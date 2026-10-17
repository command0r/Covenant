using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Covenant.Ui.Logic;
using Covenant.Ui.Models;

namespace Covenant.Ui.Services;

/// <summary>What the console observed for one governed request. Response text is console-only: it lives in
/// memory for the operator who submitted it and is never persisted.</summary>
public sealed class InferenceResult
{
    public int? HttpStatus { get; set; }
    /// <summary>X-Covenant-Request-Id (or request_id from an error body as a fallback). Null only when no
    /// response arrived at all.</summary>
    public string? RequestId { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorReason { get; set; }
    public string Text { get; set; } = "";
    public string? Model { get; set; }
    public bool StreamTerminatedWithError { get; set; }
    /// <summary>Stream ended without [DONE] and without an error line (connection dropped).</summary>
    public bool StreamIncomplete { get; set; }
    public bool Cancelled { get; set; }
    /// <summary>Network-level failure text (no HTTP response).</summary>
    public string? TransportError { get; set; }
    /// <summary>Wall time measured in the browser — labelled "client-measured" wherever shown.</summary>
    public double ClientMs { get; set; }

    public OutcomeInput ToOutcomeInput(AuditEntryView? record) => new(
        HttpStatus, ErrorCode, StreamTerminatedWithError || StreamIncomplete, Cancelled, record, HasPartialText: Text.Length > 0);
}

/// <summary>Console inference (ADR-0012): POST /admin/playground/requests on the ADMIN HttpClient with the admin
/// token. The Host resolves the selected test application server-side and runs the same governed pipeline as /v1;
/// the browser never holds an application credential. The response contract is exactly /v1/chat/completions
/// (buffered JSON or SSE, X-Covenant-Request-Id). A 401 here means the ADMIN token was rejected → the usual
/// disconnect. 400 = rejected before governance (no audit record); 403 = governed refusal (audited);
/// 409 = duplicate client_request_id (not run again).</summary>
public sealed class ConsoleInferenceApi(HttpClient http, AdminApi admin)
{
    public const string Path = "/admin/playground/requests";
    public const string RequestIdHeader = "X-Covenant-Request-Id";

    /// <param name="onDelta">Each streamed text delta.</param>
    /// <param name="onRequestId">Called once, as soon as the request id is known (streaming: at the response
    /// headers, before any text) so the trace can be followed live.</param>
    public Task<InferenceResult> SendAsync(RequestSnapshot snapshot, Action<string>? onDelta, Action<string>? onRequestId, CancellationToken ct)
        => snapshot.Streaming ? StreamAsync(snapshot, onDelta, onRequestId, ct) : BufferedAsync(snapshot, onRequestId, ct);

    private HttpRequestMessage Build(RequestSnapshot s, bool stream)
    {
        var body = new PlaygroundRequestDto
        {
            ApplicationId = s.ApplicationId ?? "",
            ProfileId = s.ProfileId,
            Messages = [new ChatMessageDto { Role = "user", Content = s.Prompt }],
            Model = s.RequestedModel,
            MaxTokens = s.MaxTokens,
            Stream = stream,
            Workflow = s.Workflow,
            UseCase = s.UseCase,
            ClientRequestId = s.ClientRequestId,
            WebSearch = WebSourcesText.Wire(s.WebSources),
            Location = s.WebSources == WebSourcesMode.Off ? null : s.Location,
        };
        var request = admin.NewRequest(HttpMethod.Post, Path);
        request.Content = JsonContent.Create(body, UiJsonContext.Default.PlaygroundRequestDto);
        return request;
    }

    private async Task<InferenceResult> BufferedAsync(RequestSnapshot s, Action<string>? onRequestId, CancellationToken ct)
    {
        var result = new InferenceResult();
        var sw = Stopwatch.StartNew();
        try
        {
            using var request = Build(s, stream: false);
            using var response = await http.SendAsync(request, ct);
            Capture(result, response, onRequestId);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (response.IsSuccessStatusCode)
                ReadChatResponse(result, text);
            else
                ReadError(result, text, onRequestId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            result.Cancelled = true;
        }
        catch (HttpRequestException ex)
        {
            result.TransportError = "Could not reach Covenant: " + ex.Message;
        }
        catch (OperationCanceledException)
        {
            // Not our token: the client-side timeout elapsed. The request may still complete on the server.
            result.TransportError = "Stopped waiting after the console's " + http.Timeout.TotalMinutes.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
                                    + "-minute timeout — the request may have completed or been charged.";
        }
        finally
        {
            result.ClientMs = sw.Elapsed.TotalMilliseconds;
        }
        return result;
    }

    private async Task<InferenceResult> StreamAsync(RequestSnapshot s, Action<string>? onDelta, Action<string>? onRequestId, CancellationToken ct)
    {
        var result = new InferenceResult();
        var sw = Stopwatch.StartNew();
        var text = new StringBuilder();
        try
        {
            using var request = Build(s, stream: true);
            request.Headers.Accept.ParseAdd("text/event-stream");
            Microsoft.AspNetCore.Components.WebAssembly.Http.WebAssemblyHttpRequestMessageExtensions
                .SetBrowserResponseStreamingEnabled(request, true);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            Capture(result, response, onRequestId);

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!response.IsSuccessStatusCode || !string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                // Refused before the first byte: the same JSON error contract as buffered mode.
                var body = await response.Content.ReadAsStringAsync(ct);
                if (response.IsSuccessStatusCode) ReadChatResponse(result, body);
                else ReadError(result, body, onRequestId);
                return result;
            }

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            bool done = false;
            while (!done)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var payload = line.AsSpan(5).Trim();
                if (payload.IsEmpty) continue;
                if (payload is "[DONE]") { done = true; break; }

                StreamFrameDto? frame;
                try { frame = JsonSerializer.Deserialize(payload, UiJsonContext.Default.StreamFrameDto); }
                catch (JsonException) { continue; }
                if (frame is null) continue;

                if (frame.Error is { Length: > 0 })
                {
                    result.StreamTerminatedWithError = true;
                    result.ErrorCode = frame.Error;
                    result.ErrorReason = frame.Reason;
                    SetRequestId(result, frame.RequestId, onRequestId);
                    continue; // [DONE] follows
                }

                SetRequestId(result, frame.Id, onRequestId);
                if (frame.Model is { Length: > 0 } m) result.Model = m;
                if (frame.Choices is { Count: > 0 } choices && choices[0].Delta?.Content is { Length: > 0 } delta)
                {
                    text.Append(delta);
                    result.Text = text.ToString();
                    onDelta?.Invoke(delta);
                }
            }
            if (!done && !result.StreamTerminatedWithError) result.StreamIncomplete = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            result.Cancelled = true;
        }
        catch (HttpRequestException ex)
        {
            if (result.HttpStatus is null) result.TransportError = "Could not reach Covenant: " + ex.Message;
            else result.StreamIncomplete = true;
        }
        catch (IOException)
        {
            result.StreamIncomplete = true;
        }
        catch (OperationCanceledException)
        {
            // Client-side timeout (not our token).
            if (result.HttpStatus is null) result.TransportError = "Stopped waiting for response headers (console timeout).";
            else result.StreamIncomplete = true;
        }
        finally
        {
            result.Text = text.Length > 0 ? text.ToString() : result.Text;
            result.ClientMs = sw.Elapsed.TotalMilliseconds;
        }
        return result;
    }

    private void Capture(InferenceResult result, HttpResponseMessage response, Action<string>? onRequestId)
    {
        result.HttpStatus = (int)response.StatusCode;
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            result.ErrorCode = "admin_token_rejected";
            admin.ReportUnauthorized();
            return;
        }
        if (response.Headers.TryGetValues(RequestIdHeader, out var values))
            foreach (var v in values)
                if (!string.IsNullOrWhiteSpace(v)) { SetRequestId(result, v.Trim(), onRequestId); break; }
    }

    private static void SetRequestId(InferenceResult result, string? id, Action<string>? onRequestId)
    {
        if (result.RequestId is not null || string.IsNullOrWhiteSpace(id)) return;
        result.RequestId = id;
        onRequestId?.Invoke(id);
    }

    private static void ReadChatResponse(InferenceResult result, string body)
    {
        ChatResponseDto? dto;
        try { dto = JsonSerializer.Deserialize(body, UiJsonContext.Default.ChatResponseDto); }
        catch (JsonException) { return; }
        if (dto is null) return;
        result.RequestId ??= dto.Id;
        result.Model = dto.Model;
        if (dto.Choices.Count > 0 && dto.Choices[0].Message?.Content is { } content)
            result.Text = ContentText(content);
    }

    private static string ContentText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
        if (content.ValueKind != JsonValueKind.Array) return "";
        var sb = new StringBuilder();
        foreach (var part in content.EnumerateArray())
            if (part.ValueKind == JsonValueKind.Object && part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                sb.Append(t.GetString());
        return sb.ToString();
    }

    private static void ReadError(InferenceResult result, string body, Action<string>? onRequestId)
    {
        if (string.IsNullOrWhiteSpace(body)) return;
        try
        {
            var err = JsonSerializer.Deserialize(body, UiJsonContext.Default.ErrorResponse);
            if (err is null) return;
            result.ErrorCode ??= err.Error;
            result.ErrorReason = err.Reason;
            // 409 duplicate_submission carries the ORIGINAL request id: evidence and trace follow that one.
            SetRequestId(result, err.RequestId, onRequestId);
        }
        catch (JsonException)
        {
            // Not our error contract (e.g. a framework 400/404 page): keep the status, no reason.
        }
    }
}

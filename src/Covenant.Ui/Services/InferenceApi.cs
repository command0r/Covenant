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
        HttpStatus, ErrorCode, StreamTerminatedWithError || StreamIncomplete, Cancelled, record);
}

/// <summary>POST /v1/chat/completions through the governed pipeline with an APPLICATION key. Uses its own
/// HttpClient and never sees the admin token. An empty key sends no Authorization header at all, so the server
/// answers with its real 401 (and the record says classification was not evaluated). Identity and team are
/// resolved by the server from the key; the console only sends workflow/use-case labels.</summary>
public sealed class InferenceApi(HttpClient http, AppState state)
{
    public const string RequestIdHeader = "X-Covenant-Request-Id";

    public Task<InferenceResult> SendAsync(RequestSnapshot snapshot, Action<string>? onDelta, CancellationToken ct)
        => snapshot.Streaming ? StreamAsync(snapshot, onDelta, ct) : BufferedAsync(snapshot, ct);

    private HttpRequestMessage Build(RequestSnapshot s, bool stream)
    {
        var body = new ChatRequestDto
        {
            Model = s.RequestedModel,
            Messages = [new ChatMessageDto { Role = "user", Content = s.Prompt }],
            Stream = stream,
            MaxTokens = s.MaxTokens,
        };
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = JsonContent.Create(body, UiJsonContext.Default.ChatRequestDto),
        };
        if (state.ApplicationKey is { Length: > 0 } key)
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key.Trim());
        AddLabel(request, "X-Covenant-Workflow", s.Workflow);
        AddLabel(request, "X-Covenant-UseCase", s.UseCase);
        return request;
    }

    /// <summary>Labels are plain attribution text; anything outside printable ASCII is dropped so the browser's
    /// fetch never rejects the header.</summary>
    private static void AddLabel(HttpRequestMessage request, string header, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var sb = new StringBuilder(value.Length);
        foreach (var c in value.Trim())
            if (c >= 0x20 && c < 0x7F) sb.Append(c);
        if (sb.Length > 0 && sb.Length <= 128) request.Headers.TryAddWithoutValidation(header, sb.ToString());
    }

    private async Task<InferenceResult> BufferedAsync(RequestSnapshot s, CancellationToken ct)
    {
        var result = new InferenceResult();
        var sw = Stopwatch.StartNew();
        try
        {
            using var request = Build(s, stream: false);
            using var response = await http.SendAsync(request, ct);
            Capture(result, response);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (response.IsSuccessStatusCode)
                ReadChatResponse(result, text);
            else
                ReadError(result, text);
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

    private async Task<InferenceResult> StreamAsync(RequestSnapshot s, Action<string>? onDelta, CancellationToken ct)
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
            Capture(result, response);

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!response.IsSuccessStatusCode || !string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                // Refused before the first byte: the same JSON error contract as buffered mode.
                var body = await response.Content.ReadAsStringAsync(ct);
                if (response.IsSuccessStatusCode) ReadChatResponse(result, body);
                else ReadError(result, body);
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
                    result.RequestId ??= frame.RequestId;
                    continue; // [DONE] follows
                }

                result.RequestId ??= frame.Id;
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

    private static void Capture(InferenceResult result, HttpResponseMessage response)
    {
        result.HttpStatus = (int)response.StatusCode;
        if (response.Headers.TryGetValues(RequestIdHeader, out var values))
            foreach (var v in values)
                if (!string.IsNullOrWhiteSpace(v)) { result.RequestId = v.Trim(); break; }
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

    private static void ReadError(InferenceResult result, string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return;
        try
        {
            var err = JsonSerializer.Deserialize(body, UiJsonContext.Default.ErrorResponse);
            if (err is null) return;
            result.ErrorCode = err.Error;
            result.ErrorReason = err.Reason;
            result.RequestId ??= err.RequestId;
        }
        catch (JsonException)
        {
            // Not our error contract (e.g. the binder's 400): keep the status, no reason.
        }
    }
}

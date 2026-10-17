using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Covenant.Core;
using Covenant.Governance;

namespace Covenant.Host;

/// <summary>The OpenAI chat-completions response contract (buffered JSON or SSE), shared by <c>/v1/chat/completions</c>
/// and the console endpoint <c>POST /admin/playground/requests</c> (ADR-0012) so both speak exactly the same wire and run
/// exactly the same governed pipeline. The caller has already built the canonical request and bound the configuration
/// snapshot (and, for the console, the server-resolved identity).</summary>
public static class ChatCompletions
{
    /// <summary>Runs the pipeline for <paramref name="ctx"/> and writes the response. The per-request trace buffer is
    /// created BEFORE the pipeline starts (ADR-0013) so a subscriber that connects after submission still gets the
    /// earliest events; it is completed when the pipeline has finished.</summary>
    public static async Task<IResult> RunAsync(HttpContext http, InferencePipeline pipe, InferenceContext ctx, TraceHub? traces, CancellationToken ct)
    {
        var request = ctx.Request;
        var buffer = traces?.Begin(request.RequestId!, ctx.ConfigVersion, ctx.Configuration?.Config.Settings.TraceRetentionMinutes ?? 10);
        if (buffer is not null) ctx.TraceSink = buffer.Append;
        try
        {
            return await RunCoreAsync(http, pipe, ctx, ct);
        }
        finally
        {
            buffer?.Complete(traces!.Clock.GetUtcNow());
        }
    }

    private static async Task<IResult> RunCoreAsync(HttpContext http, InferencePipeline pipe, InferenceContext ctx, CancellationToken ct)
    {
        var request = ctx.Request;

        // --- Streamed mode (ADR-0002): SSE headers commit lazily on the FIRST delta, so a pre-flight denial still returns a plain JSON error below. ---
        if (request.Stream)
        {
            var resp = http.Response;
            var chunkId = request.RequestId!;
            bool sseStarted = false;

            async ValueTask WriteChunkAsync(OpenAiChatChunk chunk, CancellationToken token)
            {
                var json = JsonSerializer.Serialize(chunk, CovenantJsonContext.Default.OpenAiChatChunk);
                await resp.WriteAsync($"data: {json}\n\n", token);
                await resp.Body.FlushAsync(token);
            }

            // The model of the attempt actually streaming (an approved fallback may serve, ADR-0013).
            OpenAiChatChunk Chunk(string? role, string? content, string? finish, OpenAiUsage? usage) => new()
            {
                Id = chunkId,
                Model = ctx.Response?.ServedByModel ?? ctx.AttemptedModel ?? ctx.Policy?.Route?.ModelId ?? "",
                Choices = [new OpenAiChunkChoice { Index = 0, Delta = new OpenAiDelta { Role = role, Content = content }, FinishReason = finish }],
                Usage = usage,
            };

            ctx.DeltaSink = async (delta, token) =>
            {
                if (!sseStarted)
                {
                    sseStarted = true;
                    resp.StatusCode = StatusCodes.Status200OK;
                    resp.ContentType = "text/event-stream";
                    resp.Headers.CacheControl = "no-cache";
                    await WriteChunkAsync(Chunk("assistant", null, null, null), token);
                }
                await WriteChunkAsync(Chunk(null, delta.Content, null, null), token);
            };

            await pipe.ExecuteAsync(ctx, ct);

            if (!sseStarted)
            {
                // Denied before the first byte — same JSON error contract as buffered mode.
                return DenialResult(ctx);
            }

            if (ctx.IsDenied)
            {
                // Mid-stream failure after a committed 200: terminate with an error event (audited already).
                var err = JsonSerializer.Serialize(
                    new ErrorResponse { Error = "upstream_error", Reason = ctx.DenialReason ?? "stream failed", RequestId = request.RequestId },
                    CovenantJsonContext.Default.ErrorResponse);
                await resp.WriteAsync($"data: {err}\n\n", ct);
            }
            else if (ctx.Response is { } finalResp)
            {
                await WriteChunkAsync(Chunk(null, null, "stop", new OpenAiUsage
                {
                    PromptTokens = finalResp.Usage.InputTokens,
                    CompletionTokens = finalResp.Usage.OutputTokens,
                    TotalTokens = finalResp.Usage.TotalTokens,
                }), ct);
            }

            await resp.WriteAsync("data: [DONE]\n\n", ct);
            await resp.Body.FlushAsync(ct);
            return Results.Empty;
        }

        await pipe.ExecuteAsync(ctx, ct);

        if (ctx.IsDenied || ctx.Response is null)
            return DenialResult(ctx);

        var r = ctx.Response;
        var dto = new OpenAiChatResponse
        {
            Id = request.RequestId,
            Model = r.ServedByModel,
            Choices = [new OpenAiChoice { Index = 0, Message = new OpenAiMessage { Role = "assistant", Content = OpenAiWire.TextElement(r.Message.Content) } }],
            Usage = new OpenAiUsage
            {
                PromptTokens = r.Usage.InputTokens,
                CompletionTokens = r.Usage.OutputTokens,
                TotalTokens = r.Usage.TotalTokens
            }
        };
        return Results.Json(dto, CovenantJsonContext.Default.OpenAiChatResponse);
    }

    /// <summary>No credentials → 401. Governance said no → 403. Upstream broke, refused fail-closed → 502.</summary>
    public static IResult DenialResult(InferenceContext ctx)
    {
        var (error, status) = ctx.DenialKind switch
        {
            DenialKind.Unauthenticated => ("unauthenticated", StatusCodes.Status401Unauthorized),
            DenialKind.UpstreamFailure => ("upstream_error", StatusCodes.Status502BadGateway),
            DenialKind.RateLimited => ("rate_limited", StatusCodes.Status429TooManyRequests),
            DenialKind.Unsupported => ("unsupported", StatusCodes.Status400BadRequest),
            DenialKind.Cancelled => ("cancelled", 499),
            _ => ("denied", StatusCodes.Status403Forbidden),
        };
        return Results.Json(
            new ErrorResponse { Error = error, Reason = ctx.DenialReason ?? "no response", RequestId = ctx.Request.RequestId },
            CovenantJsonContext.Default.ErrorResponse, statusCode: status);
    }
}

// ---- Console inference (ADR-0012) ----

public sealed class PlaygroundMessage
{
    [JsonPropertyName("role")] public string? Role { get; set; }
    [JsonPropertyName("content")] public string? Content { get; set; }
}

/// <summary>POST /admin/playground/requests body (docs/ui-api-contract.md v2 "Console inference").</summary>
public sealed class PlaygroundRequest
{
    [JsonPropertyName("application_id")] public string? ApplicationId { get; set; }
    [JsonPropertyName("profile_id")] public string? ProfileId { get; set; }
    [JsonPropertyName("messages")] public List<PlaygroundMessage?>? Messages { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("max_tokens")] public int? MaxTokens { get; set; }
    [JsonPropertyName("stream")] public bool Stream { get; set; }
    [JsonPropertyName("workflow")] public string? Workflow { get; set; }
    [JsonPropertyName("use_case")] public string? UseCase { get; set; }
    [JsonPropertyName("client_request_id")] public string? ClientRequestId { get; set; }
}

/// <summary>client_request_id de-duplication (ADR-0012): a resubmission within 10 minutes is answered 409 with the ORIGINAL
/// request id — a browser reload never replays inference. Claimed atomically before the pipeline runs (two concurrent
/// submissions: exactly one runs). Bounded memory.</summary>
public sealed class ConsoleSubmissions(TimeProvider? clock = null, TimeSpan? window = null, int maxEntries = 10_000)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly TimeSpan _window = window ?? TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, (string RequestId, DateTimeOffset At)> _seen = new(StringComparer.Ordinal);

    /// <summary>True ⇒ claimed for <paramref name="requestId"/>; false ⇒ duplicate of <paramref name="originalRequestId"/>.</summary>
    public bool TryClaim(string clientRequestId, string requestId, out string originalRequestId)
    {
        var now = _clock.GetUtcNow();
        if (_seen.Count >= maxEntries) Sweep(now);
        while (true)
        {
            if (_seen.TryAdd(clientRequestId, (requestId, now))) { originalRequestId = requestId; return true; }
            if (_seen.TryGetValue(clientRequestId, out var existing))
            {
                if (now - existing.At <= _window) { originalRequestId = existing.RequestId; return false; }
                if (_seen.TryUpdate(clientRequestId, (requestId, now), existing)) { originalRequestId = requestId; return true; }
            }
        }
    }

    private void Sweep(DateTimeOffset now)
    {
        foreach (var kv in _seen)
            if (now - kv.Value.At > _window) _seen.TryRemove(kv.Key, out _);
        if (_seen.Count < maxEntries) return;
        foreach (var kv in _seen.OrderBy(kv => kv.Value.At).Take(_seen.Count - maxEntries + 1).ToList())
            _seen.TryRemove(kv.Key, out _);
    }
}

public static class PlaygroundApi
{
    public const int MaxMessages = 200;
    public const int MaxContentChars = 200_000;
    public const int MaxClientRequestIdChars = 128;
    private const int MaxTagChars = 64;

    /// <summary>Body validation BEFORE governance (no audit record, 400). Returns the reason or null when valid.</summary>
    public static string? Validate(PlaygroundRequest? body)
    {
        if (body is null) return "request body is required";
        if (string.IsNullOrWhiteSpace(body.ApplicationId)) return "application_id is required";
        if (body.ApplicationId.Length > 64) return "application_id is too long";
        if (body.ProfileId is { Length: > 64 }) return "profile_id is too long";
        if (body.Messages is not { Count: > 0 } messages) return "messages must be a non-empty array";
        if (messages.Count > MaxMessages) return $"at most {MaxMessages} messages";
        long chars = 0;
        foreach (var m in messages)
        {
            if (m is null) return "a message is null";
            if (m.Role is not ("system" or "user" or "assistant")) return "message role must be system, user or assistant";
            if (m.Content is null) return "message content must be a string";
            chars += m.Content.Length;
        }
        if (chars > MaxContentChars) return $"messages exceed {MaxContentChars} characters";
        if (body.MaxTokens is < 1) return "max_tokens must be a positive integer";
        if (body.Model is { Length: > 200 }) return "model is too long";
        if (body.ClientRequestId is { } crid
            && (crid.Length is 0 or > MaxClientRequestIdChars || !crid.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':')))
            return $"client_request_id must be 1–{MaxClientRequestIdChars} chars of [A-Za-z0-9-_.:]";
        return null;
    }

    private static string Tag(string? value, string fallback)
    {
        var v = Evidence.Bounded(value);
        if (v.Length == 0) return fallback;
        return v.Length <= MaxTagChars ? v : v[..MaxTagChars];
    }

    private static ChatRole Role(string role) => role switch
    {
        "system" => ChatRole.System,
        "assistant" => ChatRole.Assistant,
        _ => ChatRole.User,
    };

    private static IResult Error(string error, string reason, int status, string? requestId = null)
        => Results.Json(new ErrorResponse { Error = error, Reason = reason, RequestId = requestId }, CovenantJsonContext.Default.ErrorResponse, statusCode: status);

    /// <summary>POST /admin/playground/requests (ADR-0012): admin-authenticated; the application is resolved SERVER-SIDE and
    /// bound with <see cref="InferenceContext.BindConsoleIdentity"/> (a field the /v1 ingress can never set); the same
    /// governed pipeline as /v1 then enforces enabled + console testing + profile grant (governed 403, audited). The
    /// browser never holds or receives an application credential.</summary>
    public static void MapPlayground(this WebApplication app, IRuntimeConfig runtime, TraceHub traces, ConsoleSubmissions submissions,
        Func<HttpContext, bool> authorized)
    {
        app.MapPost("/admin/playground/requests", async Task<IResult> (HttpContext http, InferencePipeline pipe, CancellationToken ct) =>
        {
            if (!authorized(http)) return Error("unauthorized", "invalid admin token", StatusCodes.Status401Unauthorized);
            var requestId = RequestCorrelation.NewId();
            http.Response.Headers[RequestCorrelation.Header] = requestId;

            PlaygroundRequest? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync(http.Request.Body, CovenantJsonContext.Default.PlaygroundRequest, ct);
            }
            catch (JsonException)
            {
                return Error("invalid_request", "malformed JSON — rejected before governance; no evidence record", StatusCodes.Status400BadRequest);
            }
            var invalid = Validate(body);
            if (invalid is not null || body is null)
                return Error("invalid_request", (invalid ?? "request body is required") + " — rejected before governance; no evidence record",
                    StatusCodes.Status400BadRequest);

            if (body.ClientRequestId is { } clientRequestId && !submissions.TryClaim(clientRequestId, requestId, out var original))
            {
                http.Response.Headers[RequestCorrelation.Header] = original;
                return Error("duplicate_submission", "this client_request_id was already submitted; it is not run again", StatusCodes.Status409Conflict, original);
            }

            var appId = body.ApplicationId!.Trim();
            var request = new InferenceRequest(
                Principal: "console:" + appId,
                Messages: body.Messages!.Select(m => new ChatMessage(Role(m!.Role!), m.Content!)).ToList(),
                RequestedModel: string.IsNullOrWhiteSpace(body.Model) ? null : body.Model.Trim(),
                Attribution: new AttributionTags("unknown", Tag(body.Workflow, "console"), Tag(body.UseCase, "playground")),
                Stream: body.Stream,
                Credential: null,
                MaxOutputTokens: body.MaxTokens,
                RequestId: requestId,
                RequestedProfile: string.IsNullOrWhiteSpace(body.ProfileId) ? null : body.ProfileId.Trim());
            var ctx = new InferenceContext(request);
            using var lease = SnapshotLease.Bind(runtime, ctx);   // ADR-0011: one configuration version for the whole request
            var team = ctx.Configuration?.FindApplication(appId)?.Team ?? "unknown";
            ctx.BindConsoleIdentity(new PreAuthenticatedIdentity(appId, team, "console:" + appId), ConfigAuditEvents.SharedAdminOperator);
            return await ChatCompletions.RunAsync(http, pipe, ctx, traces, ct);
        });
    }
}

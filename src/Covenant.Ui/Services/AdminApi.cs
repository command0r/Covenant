using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Covenant.Ui.Models;

namespace Covenant.Ui.Services;

/// <summary>A failed admin call, with the HTTP status, the server's error code (<c>error</c>, e.g. "cursor_expired") and
/// its reason when it sent one. The message is safe to render: it never includes the token or the request body.</summary>
public sealed class ApiException(HttpStatusCode? status, string message, string? errorCode = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
    public string? ErrorCode { get; } = errorCode;
}

/// <summary>Status, body text and ETag of a raw admin call (body never contains a secret: config payloads carry
/// secret references only).</summary>
public sealed record RawResponse(int Status, string Body, string? ETag)
{
    public bool IsSuccess => Status is >= 200 and < 300;
}

/// <summary>Typed client for /admin/*. Every call carries X-Covenant-Admin-Token from AppState; a 401 raises
/// Unauthorized (ConsoleSession then disconnects and routes to /connect with "Token rejected") and throws.
/// Admin and application credentials are never mixed: this class knows nothing about application keys.</summary>
public sealed class AdminApi(HttpClient http, AppState state)
{
    public const string TokenHeader = "X-Covenant-Admin-Token";

    public event Action? Unauthorized;

    public Task<StatusReport> GetStatusAsync(CancellationToken ct = default)
        => GetAsync("/admin/status", UiJsonContext.Default.StatusReport, ct);

    public Task<EvidenceReport> GetEvidenceAsync(CancellationToken ct = default)
        => GetAsync("/admin/evidence", UiJsonContext.Default.EvidenceReport, ct);

    public Task<RequestsResponse> GetRequestsAsync(int limit = 200, CancellationToken ct = default)
        => GetAsync($"/admin/requests?limit={limit}", UiJsonContext.Default.RequestsResponse, ct);

    public Task<AuditEntryView> GetRequestAsync(string id, CancellationToken ct = default)
        => GetAsync("/admin/requests/" + Uri.EscapeDataString(id), UiJsonContext.Default.AuditEntryView, ct);

    public Task<CatalogResponse> GetCatalogAsync(CancellationToken ct = default)
        => GetAsync("/admin/catalog", UiJsonContext.Default.CatalogResponse, ct);

    public Task<KillSwitchState> SetKillSwitchAsync(bool engaged, string? reason, CancellationToken ct = default)
        => PostAsync("/admin/kill-switch", new KillSwitchRequest { Engaged = engaged, Reason = reason },
            UiJsonContext.Default.KillSwitchRequest, UiJsonContext.Default.KillSwitchState, ct);

    public Task<ResetResponse> ResetAsync(CancellationToken ct = default)
        => PostAsync<object?, ResetResponse>("/admin/reset", null, null, UiJsonContext.Default.ResetResponse, ct);

    public Task<ProbeResponse> ProbeAsync(string endpoint, CancellationToken ct = default)
        => PostAsync("/admin/providers/probe", new ProbeRequest { Endpoint = endpoint },
            UiJsonContext.Default.ProbeRequest, UiJsonContext.Default.ProbeResponse, ct);

    // ---- v2 (docs/ui-api-contract.md "v2 additions") ----

    public Task<List<ApplicationSummaryDto>> GetApplicationsAsync(CancellationToken ct = default)
        => GetAsync("/admin/applications", UiJsonContext.Default.ListApplicationSummaryDto, ct);

    public Task<ConfigEnvelopeDto> GetConfigAsync(CancellationToken ct = default)
        => GetAsync("/admin/config", UiJsonContext.Default.ConfigEnvelopeDto, ct);

    public Task<List<BudgetStateDto>> GetBudgetsAsync(CancellationToken ct = default)
        => GetAsync("/admin/budgets", UiJsonContext.Default.ListBudgetStateDto, ct);

    public Task<TraceSnapshotDto> GetTraceAsync(string requestId, CancellationToken ct = default)
        => GetAsync("/admin/requests/" + Uri.EscapeDataString(requestId) + "/trace", UiJsonContext.Default.TraceSnapshotDto, ct);

    /// <summary>Opens the per-request trace SSE stream (replay then live). <paramref name="lastEventId"/> &gt; 0 is
    /// sent as Last-Event-ID so a reconnect resumes after the last applied seq. Throws ApiException with the status
    /// (404 unknown/expired, 410 expired) on a non-success answer.</summary>
    public async Task<HttpResponseMessage> OpenTraceStreamAsync(string requestId, long lastEventId, CancellationToken ct)
    {
        var request = NewRequest(HttpMethod.Get, "/admin/requests/" + Uri.EscapeDataString(requestId) + "/trace/stream");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (lastEventId > 0)
            request.Headers.TryAddWithoutValidation("Last-Event-ID", lastEventId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Microsoft.AspNetCore.Components.WebAssembly.Http.WebAssemblyHttpRequestMessageExtensions
            .SetBrowserResponseStreamingEnabled(request, true);
        // Not disposed here (same as the status stream): the browser keeps reading the body after headers.
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        try
        {
            await EnsureSuccessAsync(response, ct);
        }
        catch
        {
            response.Dispose();
            throw;
        }
        return response;
    }

    // ---- T08: configuration editing, budgets drill-down, audit archive, request export ----

    /// <summary>POST /admin/config/validate. Never mutates. Raw answer: 200 carries the result (valid or not);
    /// 400/404/5xx are reported by the caller.</summary>
    public Task<RawResponse> ValidateConfigRawAsync(ConfigValidateRequestDto body, CancellationToken ct = default)
        => SendRawAsync(HttpMethod.Post, "/admin/config/validate", JsonContent.Create(body, UiJsonContext.Default.ConfigValidateRequestDto), null, ct);

    /// <summary>PUT /admin/config with If-Match = the ETag of the version the draft was taken from (never "*").</summary>
    public Task<RawResponse> PutConfigRawAsync(ConfigPutRequestDto body, string ifMatch, CancellationToken ct = default)
        => SendRawAsync(HttpMethod.Put, "/admin/config", JsonContent.Create(body, UiJsonContext.Default.ConfigPutRequestDto), ifMatch, ct);

    /// <summary>POST /admin/config/rollback — activates the target's content as a NEW version (412 when the active moved).</summary>
    public Task<RawResponse> RollbackConfigRawAsync(int targetVersion, int expectedActiveVersion, CancellationToken ct = default)
        => SendRawAsync(HttpMethod.Post, "/admin/config/rollback",
            JsonContent.Create(new ConfigRollbackRequestDto { TargetVersion = targetVersion, ExpectedActiveVersion = expectedActiveVersion },
                UiJsonContext.Default.ConfigRollbackRequestDto), null, ct);

    public Task<List<ConfigVersionDto>> GetConfigVersionsAsync(int limit = 50, CancellationToken ct = default)
        => GetAsync("/admin/config/versions?limit=" + limit.ToString(System.Globalization.CultureInfo.InvariantCulture),
            UiJsonContext.Default.ListConfigVersionDto, ct);

    public Task<CovenantConfigDto> GetConfigVersionAsync(int version, CancellationToken ct = default)
        => GetAsync("/admin/config/versions/" + version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            UiJsonContext.Default.CovenantConfigDto, ct);

    /// <summary>POST /admin/policies/simulate — dry run with the real engine; no provider call, no audit, no spend.
    /// With <see cref="SimulateRequestDto.Config"/> set, the Host validates that candidate (422 when invalid).</summary>
    public Task<SimulateResponseDto> SimulateAsync(SimulateRequestDto body, CancellationToken ct = default)
        => PostAsync("/admin/policies/simulate", body, UiJsonContext.Default.SimulateRequestDto, UiJsonContext.Default.SimulateResponseDto, ct);

    /// <summary>GET /admin/budgets/{id}/requests — settled contributions of the budget's scope in its current period.</summary>
    public Task<BudgetRequestsDto> GetBudgetRequestsAsync(string budgetId, int limit = 100, CancellationToken ct = default)
        => GetAsync("/admin/budgets/" + Uri.EscapeDataString(budgetId) + "/requests?limit="
                    + limit.ToString(System.Globalization.CultureInfo.InvariantCulture), UiJsonContext.Default.BudgetRequestsDto, ct);

    /// <summary>GET /admin/playground/submissions/{client_request_id} (review round 2, B7). Returns the HTTP status and,
    /// on 200, the submission (request id + running|completed). 404 = not registered yet / expired. A 401 raises
    /// <see cref="Unauthorized"/> like every admin call. Transport failures throw.</summary>
    public async Task<(int Status, PlaygroundSubmissionDto? Submission)> GetPlaygroundSubmissionAsync(string clientRequestId, CancellationToken ct = default)
    {
        var raw = await SendRawAsync(HttpMethod.Get, "/admin/playground/submissions/" + Uri.EscapeDataString(clientRequestId), null, null, ct);
        if (raw.Status != 200) return (raw.Status, null);
        try { return (raw.Status, JsonSerializer.Deserialize(raw.Body, UiJsonContext.Default.PlaygroundSubmissionDto)); }
        catch (JsonException) { return (raw.Status, null); }
    }

    /// <summary>GET /admin/web-search (ADR-0015): readiness and setup steps; never the key.</summary>
    public Task<WebSearchStatusDto> GetWebSearchStatusAsync(CancellationToken ct = default)
        => GetAsync("/admin/web-search", UiJsonContext.Default.WebSearchStatusDto, ct);

    /// <summary>GET /admin/requests/{id}/search (ADR-0015): the sources actually retrieved. 404 = the request did not ask
    /// for web sources, or its transient record expired.</summary>
    public async Task<(int Status, RequestSearchDto? Search)> GetRequestSearchAsync(string requestId, CancellationToken ct = default)
    {
        var raw = await SendRawAsync(HttpMethod.Get, "/admin/requests/" + Uri.EscapeDataString(requestId) + "/search", null, null, ct);
        if (raw.Status != 200) return (raw.Status, null);
        try { return (raw.Status, JsonSerializer.Deserialize(raw.Body, UiJsonContext.Default.RequestSearchDto)); }
        catch (JsonException) { return (raw.Status, null); }
    }

    /// <summary>GET /admin/audit/segments — the current segment plus archived siblings (ids only).</summary>
    public Task<List<AuditSegmentDto>> GetSegmentsAsync(CancellationToken ct = default)
        => GetAsync("/admin/audit/segments", UiJsonContext.Default.ListAuditSegmentDto, ct);

    /// <summary>One newest-first page: <paramref name="cursor"/> from the previous page's <c>next_cursor</c>;
    /// <paramref name="segment"/> = a segment id from GET /admin/audit/segments (null = current segment).</summary>
    public Task<RequestsResponse> GetRequestsPageAsync(int limit, string? cursor, string? segment, CancellationToken ct = default)
    {
        var path = "/admin/requests?limit=" + limit.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(cursor)) path += "&cursor=" + Uri.EscapeDataString(cursor);
        if (!string.IsNullOrWhiteSpace(segment)) path += "&segment=" + Uri.EscapeDataString(segment);
        return GetAsync(path, UiJsonContext.Default.RequestsResponse, ct);
    }

    /// <summary>GET /admin/requests/{id}/export — the per-request evidence report (no prompt/response content),
    /// byte-for-byte as the server produced it.</summary>
    public Task<string> GetRequestExportJsonAsync(string id, string? segment = null, CancellationToken ct = default)
        => GetTextAsync("/admin/requests/" + Uri.EscapeDataString(id) + "/export"
                        + (string.IsNullOrWhiteSpace(segment) ? "" : "?segment=" + Uri.EscapeDataString(segment)), ct);

    /// <summary>Raw send for calls whose non-2xx bodies carry meaning (config writes: 412/422/428). A 401 still
    /// raises <see cref="Unauthorized"/>. Transport failures throw.</summary>
    public async Task<RawResponse> SendRawAsync(HttpMethod method, string path, HttpContent? content, string? ifMatch, CancellationToken ct)
    {
        using var request = NewRequest(method, path);
        if (content is not null) request.Content = content;
        if (!string.IsNullOrWhiteSpace(ifMatch)) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized) Unauthorized?.Invoke();
        string? etag = response.Headers.ETag?.Tag;
        return new RawResponse((int)response.StatusCode, text, etag);
    }

    private async Task<string> GetTextAsync(string path, CancellationToken ct)
    {
        using var request = NewRequest(HttpMethod.Get, path);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>For callers that send their own admin requests (console inference): a 401 there means the admin
    /// token was rejected, which is handled exactly like any other admin 401.</summary>
    public void ReportUnauthorized() => Unauthorized?.Invoke();

    /// <summary>Raw GET returning the body text — used by Evidence export so the downloaded file is byte-for-byte
    /// what the server produced, not a re-serialization.</summary>
    public async Task<string> GetEvidenceJsonAsync(CancellationToken ct = default)
    {
        using var request = NewRequest(HttpMethod.Get, "/admin/evidence");
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>Opens the SSE status stream. The caller owns the response and reads lines from it.</summary>
    public async Task<HttpResponseMessage> OpenStatusStreamAsync(CancellationToken ct)
    {
        var request = NewRequest(HttpMethod.Get, "/admin/status/stream");
        request.Headers.Accept.ParseAdd("text/event-stream");
        Microsoft.AspNetCore.Components.WebAssembly.Http.WebAssemblyHttpRequestMessageExtensions
            .SetBrowserResponseStreamingEnabled(request, true);
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        try
        {
            await EnsureSuccessAsync(response, ct);
        }
        catch
        {
            response.Dispose();
            throw;
        }
        return response;
    }

    public HttpRequestMessage NewRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        if (state.AdminToken is { Length: > 0 } token)
            request.Headers.TryAddWithoutValidation(TokenHeader, token);
        return request;
    }

    private async Task<T> GetAsync<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        using var request = NewRequest(HttpMethod.Get, path);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        var body = await response.Content.ReadFromJsonAsync(typeInfo, ct);
        return body ?? throw new ApiException(response.StatusCode, "Empty response from " + path);
    }

    private async Task<TOut> PostAsync<TIn, TOut>(
        string path, TIn? body,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TIn>? inType,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TOut> outType,
        CancellationToken ct)
    {
        using var request = NewRequest(HttpMethod.Post, path);
        if (body is not null && inType is not null)
            request.Content = JsonContent.Create(body, inType);
        else
            request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        var result = await response.Content.ReadFromJsonAsync(outType, ct);
        return result ?? throw new ApiException(response.StatusCode, "Empty response from " + path);
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            Unauthorized?.Invoke();
            throw new ApiException(response.StatusCode, "Token rejected");
        }

        string? reason = null;
        string? code = null;
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (text.Length > 0)
            {
                var err = JsonSerializer.Deserialize(text, UiJsonContext.Default.ErrorResponse);
                code = err?.Error;
                reason = err?.Reason ?? err?.Error;
                if (err?.Error == "not_found" && err.Scanned is { } scanned)
                    reason = $"not found (scanned {scanned} records)";
            }
        }
        catch (JsonException) { /* non-JSON error body: fall through to the status line */ }

        throw new ApiException(response.StatusCode,
            reason is { Length: > 0 } ? $"{(int)response.StatusCode}: {reason}" : $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}", code);
    }
}

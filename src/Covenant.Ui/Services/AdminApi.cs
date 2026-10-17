using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Covenant.Ui.Models;

namespace Covenant.Ui.Services;

/// <summary>A failed admin call, with the HTTP status and the server's reason when it sent one. The message is
/// safe to render: it never includes the token or the request body.</summary>
public sealed class ApiException(HttpStatusCode? status, string message) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
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
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (text.Length > 0)
            {
                var err = JsonSerializer.Deserialize(text, UiJsonContext.Default.ErrorResponse);
                reason = err?.Reason ?? err?.Error;
                if (err?.Error == "not_found" && err.Scanned is { } scanned)
                    reason = $"not found (scanned {scanned} records)";
            }
        }
        catch (JsonException) { /* non-JSON error body: fall through to the status line */ }

        throw new ApiException(response.StatusCode,
            reason is { Length: > 0 } ? $"{(int)response.StatusCode}: {reason}" : $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
    }
}

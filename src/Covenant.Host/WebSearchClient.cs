using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Covenant.Core;
using Covenant.Governance;

namespace Covenant.Host;

// Tavily search client (ADR-0015). Fixed endpoint, Bearer key from the server's secret store, source-generated JSON,
// redirects disabled, per-call deadline, 256 KiB response cap, exactly one request — no retries, no crawling.
// https://docs.tavily.com/documentation/api-reference/endpoint/search

public sealed class TavilySearchRequest
{
    [JsonPropertyName("query")] public required string Query { get; init; }
    [JsonPropertyName("search_depth")] public string SearchDepth { get; init; } = "basic";
    [JsonPropertyName("max_results")] public required int MaxResults { get; init; }
    [JsonPropertyName("include_answer")] public bool IncludeAnswer { get; init; }
    [JsonPropertyName("include_raw_content")] public bool IncludeRawContent { get; init; }
    [JsonPropertyName("include_usage")] public bool IncludeUsage { get; init; } = true;
    [JsonPropertyName("auto_parameters")] public bool AutoParameters { get; init; }
}

public sealed class TavilySearchResponse
{
    [JsonPropertyName("results")] public List<TavilyResult?>? Results { get; set; }
    [JsonPropertyName("usage")] public TavilyUsage? Usage { get; set; }
}

public sealed class TavilyResult
{
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("content")] public string? Content { get; set; }
}

public sealed class TavilyUsage
{
    [JsonPropertyName("credits")] public int? Credits { get; set; }
}

/// <summary>GET /admin/web-search: configured/missing state and setup instructions. Never contains the key.</summary>
public sealed class WebSearchStatusView
{
    [JsonPropertyName("provider")] public string Provider { get; init; } = "tavily";
    [JsonPropertyName("endpoint")] public string Endpoint { get; init; } = WebSearchEgress.Endpoint;
    [JsonPropertyName("key_configured")] public required bool KeyConfigured { get; init; }
    [JsonPropertyName("egress_approved")] public required bool EgressApproved { get; init; }
    [JsonPropertyName("mode")] public required string Mode { get; init; }
    [JsonPropertyName("mode_allows_search")] public required bool ModeAllowsSearch { get; init; }
    [JsonPropertyName("enabled")] public required bool Enabled { get; init; }
    [JsonPropertyName("price_configured")] public required bool PriceConfigured { get; init; }
    [JsonPropertyName("price_per_credit_usd")] public decimal? PricePerCreditUsd { get; init; }
    [JsonPropertyName("profiles_allowed")] public required List<string> ProfilesAllowed { get; init; }
    [JsonPropertyName("config_version")] public required int ConfigVersion { get; init; }
    /// <summary>True only when every condition holds; the console still shows each one separately.</summary>
    [JsonPropertyName("ready")] public required bool Ready { get; init; }
    [JsonPropertyName("setup")] public required List<string> Setup { get; init; }
}

/// <summary>GET /admin/requests/{id}/search: the actually retrieved sources of one console request (transient, same TTL as
/// traces). Distinct from any citations the model wrote.</summary>
public sealed class RequestSearchView
{
    [JsonPropertyName("request_id")] public required string RequestId { get; init; }
    [JsonPropertyName("mode")] public required string Mode { get; init; }
    [JsonPropertyName("triggered")] public required bool Triggered { get; init; }
    [JsonPropertyName("cue")] public string? Cue { get; init; }
    [JsonPropertyName("outcome")] public required string Outcome { get; init; }
    [JsonPropertyName("reason_code")] public string? ReasonCode { get; init; }
    [JsonPropertyName("retrieved_utc")] public DateTimeOffset? RetrievedUtc { get; init; }
    [JsonPropertyName("duration_ms")] public double? DurationMs { get; init; }
    [JsonPropertyName("sources")] public required List<RequestSearchSourceView> Sources { get; init; }
}

public sealed class RequestSearchSourceView
{
    [JsonPropertyName("title")] public required string Title { get; init; }
    [JsonPropertyName("url")] public required string Url { get; init; }
    [JsonPropertyName("retrieved_utc")] public required DateTimeOffset RetrievedUtc { get; init; }
}

[JsonSerializable(typeof(TavilySearchRequest))]
[JsonSerializable(typeof(TavilySearchResponse))]
[JsonSerializable(typeof(WebSearchStatusView))]
[JsonSerializable(typeof(RequestSearchView))]
public partial class WebSearchJsonContext : JsonSerializerContext;

public sealed class TavilySearchClient : IWebSearchClient, IDisposable
{
    public const int MaxResponseBytes = 256 * 1024;
    private static readonly Uri EndpointUri = new(WebSearchEgress.Endpoint);

    private readonly string? _apiKey;
    private readonly HttpClient _http;
    private readonly TimeProvider _clock;

    /// <param name="handler">Test seam (fake transport). Production: a SocketsHttpHandler with redirects disabled.</param>
    public TavilySearchClient(string? apiKey, bool egressApproved, HttpMessageHandler? handler = null, TimeProvider? clock = null)
    {
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        _clock = clock ?? TimeProvider.System;
        UnavailableReason = _apiKey is null
            ? $"{WebSearchEgress.SecretKey} is not configured on the server"
            : !egressApproved
                ? $"{WebSearchEgress.Authority} is not on the startup egress allow-list (Providers:ProbeAllowList)"
                : null;
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        }, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,   // the per-call deadline below is the only timeout
        };
    }

    public string? UnavailableReason { get; }

    public async Task<WebSearchResult> SearchAsync(WebSearchQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var watch = Stopwatch.StartNew();
        if (UnavailableReason is not null || _apiKey is null)
            return WebSearchResult.Failed(WebSearchOutcome.NotConfigured, sent: false, 0);

        var body = new TavilySearchRequest { Query = query.Query, MaxResults = Math.Clamp(query.MaxResults, 1, 3) };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(query.Timeout);
        bool sent = false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, EndpointUri)
            {
                Content = JsonContent(body),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            sent = true;   // from here the request may reach the provider (conservatively billable)
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            var status = (int)response.StatusCode;
            if (status is >= 300 and < 400)
                return WebSearchResult.Failed(WebSearchOutcome.HttpError, true, watch.Elapsed.TotalMilliseconds, status);   // redirects never followed
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return WebSearchResult.Failed(WebSearchOutcome.RateLimited, true, watch.Elapsed.TotalMilliseconds, status);
            if (!response.IsSuccessStatusCode)
                return WebSearchResult.Failed(WebSearchOutcome.HttpError, true, watch.Elapsed.TotalMilliseconds, status);

            var bytes = await ReadCappedAsync(response, deadline.Token);
            if (bytes is null)
                return WebSearchResult.Failed(WebSearchOutcome.Oversized, true, watch.Elapsed.TotalMilliseconds, status);

            TavilySearchResponse? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize(bytes, WebSearchJsonContext.Default.TavilySearchResponse);
            }
            catch (JsonException)
            {
                return WebSearchResult.Failed(WebSearchOutcome.InvalidResponse, true, watch.Elapsed.TotalMilliseconds, status);
            }
            if (parsed?.Results is null)
                return WebSearchResult.Failed(WebSearchOutcome.InvalidResponse, true, watch.Elapsed.TotalMilliseconds, status);

            var retrieved = _clock.GetUtcNow();
            var raw = parsed.Results.Where(r => r is not null)
                .Select(r => new WebSource(r!.Title ?? "", r.Url ?? "", r.Content ?? "", retrieved));
            var sources = WebSources.Validate(raw, body.MaxResults);
            return new WebSearchResult(WebSearchOutcome.Ok, true, sources, parsed.Usage?.Credits, watch.Elapsed.TotalMilliseconds, status);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return WebSearchResult.Failed(WebSearchOutcome.Timeout, sent, watch.Elapsed.TotalMilliseconds);
        }
        catch (HttpRequestException ex)
        {
            // A connection failure can occur AFTER dispatch (e.g. a reset while reading headers).
            // Only name resolution proves that the provider was not reached.
            bool reached = ex.HttpRequestError != HttpRequestError.NameResolutionError;
            return WebSearchResult.Failed(WebSearchOutcome.ConnectFailed, sent && reached, watch.Elapsed.TotalMilliseconds);
        }
        catch (IOException)
        {
            return WebSearchResult.Failed(WebSearchOutcome.ConnectFailed, sent, watch.Elapsed.TotalMilliseconds);
        }
    }

    private static ByteArrayContent JsonContent(TavilySearchRequest body)
    {
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, WebSearchJsonContext.Default.TavilySearchRequest));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    private static async Task<byte[]?> ReadCappedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is > MaxResponseBytes) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[MaxResponseBytes + 1];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total), ct);
            if (read == 0) break;
            total += read;
        }
        if (total > MaxResponseBytes) return null;
        return buffer.AsSpan(0, total).ToArray();
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Admin APIs for web search (ADR-0015): status/setup and the transient per-request sources.</summary>
public static class WebSearchApi
{
    public static WebSearchStatusView Status(IRequestConfiguration cfg, IWebSearchClient client, bool keyConfigured, bool egressApproved)
    {
        var ws = cfg.Config.WebSearch;
        bool hybrid = cfg.Config.Mode == ProviderMode.Hybrid;
        var profiles = cfg.Config.Profiles.Where(p => p.WebSearchAllowed).Select(p => p.Id).ToList();
        var setup = new List<string>();
        if (!keyConfigured) setup.Add("Set Tavily__ApiKey in the server environment and restart. Development-only user-secrets are also supported. Never enter the key in the console.");
        if (!egressApproved) setup.Add($"Add {WebSearchEgress.Authority} to Providers:ProbeAllowList (startup egress allow-list) and restart. The console cannot widen egress.");
        if (!hybrid) setup.Add("Switch the configuration to Hybrid mode (Policies → Mode). Local-only never makes external calls.");
        if (ws?.PricePerCreditUsd is null) setup.Add("Enter the price per Tavily credit from your Tavily plan (Settings → Web search). An unknown price is not $0.");
        if (ws is not { Enabled: true }) setup.Add("Enable web search (Settings → Web search) and apply.");
        if (profiles.Count == 0) setup.Add("Allow web search on a Public-baseline workload profile (Policies → Applications & workload profiles).");
        bool ready = keyConfigured && egressApproved && hybrid && ws is { Enabled: true, PricePerCreditUsd: not null } && profiles.Count > 0
                     && client.UnavailableReason is null;
        return new WebSearchStatusView
        {
            KeyConfigured = keyConfigured,
            EgressApproved = egressApproved,
            Mode = StartupEgress.ModeString(cfg.Config.Mode),
            ModeAllowsSearch = hybrid,
            Enabled = ws?.Enabled ?? false,
            PriceConfigured = ws?.PricePerCreditUsd is not null,
            PricePerCreditUsd = ws?.PricePerCreditUsd,
            ProfilesAllowed = profiles,
            ConfigVersion = cfg.Version,
            Ready = ready,
            Setup = setup,
        };
    }

    public static RequestSearchView View(string requestId, WebSearchState state) => new()
    {
        RequestId = requestId,
        Mode = state.Mode.ToString().ToLowerInvariant(),
        Triggered = state.Triggered,
        Cue = state.Cue,
        Outcome = state.Outcome,
        ReasonCode = state.ReasonCode,
        RetrievedUtc = state.RetrievedUtc,
        DurationMs = state.DurationMs is { } d ? Math.Round(d, 3) : null,
        Sources = state.Sources.Select(s => new RequestSearchSourceView { Title = s.Title, Url = s.Url, RetrievedUtc = s.RetrievedUtc }).ToList(),
    };
}

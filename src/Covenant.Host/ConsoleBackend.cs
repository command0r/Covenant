using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Covenant.Adapters;
using Covenant.Core;

namespace Covenant.Host;

/// <summary>ADR-0009: one server-generated correlation id per /v1 request. It is the response id,
/// the X-Covenant-Request-Id header, and the audit record Id. Never taken from the caller.</summary>
public static class RequestCorrelation
{
    public const string Header = "X-Covenant-Request-Id";
    public const string ItemKey = "covenant.request-id";

    public static string NewId() => $"req_{Guid.NewGuid():n}";

    public static string Get(HttpContext http)
        => http.Items.TryGetValue(ItemKey, out var v) && v is string s ? s : NewId();

    /// <summary>Lookup ids are bounded and shape-checked before touching the log.</summary>
    public static bool IsWellFormed(string? id)
        => id is { Length: > 0 and <= 64 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
}

/// <summary>Observed provider health. A later failure always supersedes an earlier success for the
/// current state; the last success time is kept so the UI can say "last worked at …".</summary>
public sealed class ProviderHealth : IProviderHealth
{
    private readonly ConcurrentDictionary<string, HealthView> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _clock;

    public ProviderHealth(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    public void RecordSuccess(string adapterKey, string modelId) => Success($"{adapterKey}:{modelId}");
    public void RecordFailure(string adapterKey, string modelId, string error) => Failure($"{adapterKey}:{modelId}", error);

    public void Success(string key)
        => _byKey.AddOrUpdate(key,
            _ => new HealthView { State = "ok", LastSuccessUtc = _clock.GetUtcNow() },
            (_, h) => new HealthView { State = "ok", LastSuccessUtc = _clock.GetUtcNow(), LastFailureUtc = h.LastFailureUtc, LastError = h.LastError });

    public void Failure(string key, string error)
        => _byKey.AddOrUpdate(key,
            _ => new HealthView { State = "failed", LastFailureUtc = _clock.GetUtcNow(), LastError = error },
            (_, h) => new HealthView { State = "failed", LastSuccessUtc = h.LastSuccessUtc, LastFailureUtc = _clock.GetUtcNow(), LastError = error });

    public HealthView Get(string key) => _byKey.TryGetValue(key, out var h) ? h : new HealthView { State = "unknown" };
}

public sealed class HealthView
{
    [JsonPropertyName("state")] public string State { get; init; } = "unknown";
    [JsonPropertyName("last_success_utc")] public DateTimeOffset? LastSuccessUtc { get; init; }
    [JsonPropertyName("last_failure_utc")] public DateTimeOffset? LastFailureUtc { get; init; }
    [JsonPropertyName("last_error")] public string? LastError { get; init; }
}

/// <summary>Admin-only, host-side probe of an OpenAI-compatible endpoint (Ollama /v1/models). NOT a
/// general URL fetcher: the destination must be on the operator allow-list (Providers:ProbeAllowList ∪
/// the configured Local:Endpoint authority); http/https only; no userinfo/query/fragment; redirects
/// disabled; the TCP connection is made only to the validated host; 5 s timeout; 64 KiB response cap;
/// no Covenant credential is ever forwarded.</summary>
public static class ProviderProbe
{
    public const int MaxResponseBytes = 64 * 1024;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>Allow-list entries are "host:port" (port defaults by scheme when omitted in config).</summary>
    public static HashSet<string> BuildAllowList(string? configured, string? localEndpoint)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (configured ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
            set.Add(raw.Contains(':') ? raw.Trim() : $"{raw.Trim()}:11434");
        if (localEndpoint is not null && Uri.TryCreate(localEndpoint, UriKind.Absolute, out var u))
            set.Add($"{u.Host}:{u.Port}");
        return set;
    }

    /// <summary>Validates and normalizes a draft endpoint. Returns null + error when refused.</summary>
    public static (Uri? Endpoint, string? Error) Validate(string? raw, IReadOnlySet<string> allowList)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 300) return (null, "endpoint is required (max 300 chars)");
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var u)) return (null, "not an absolute URL");
        if (u.Scheme is not ("http" or "https")) return (null, "only http and https are allowed");
        if (!string.IsNullOrEmpty(u.UserInfo)) return (null, "credentials in the URL are not allowed");
        if (!string.IsNullOrEmpty(u.Query) || !string.IsNullOrEmpty(u.Fragment)) return (null, "query strings and fragments are not allowed");
        var authority = $"{u.Host}:{u.Port}";
        if (!allowList.Contains(authority))
            return (null, $"destination {authority} is not on the operator allow-list (Providers:ProbeAllowList)");
        var path = u.AbsolutePath.TrimEnd('/');
        return (new Uri($"{u.Scheme}://{u.Host}:{u.Port}{path}/"), null);
    }

    public static Task<ProbeResult> RunAsync(string? raw, IReadOnlySet<string> allowList, CancellationToken ct)
        => RunAsync(raw, allowList, Timeout, ct);

    /// <summary>Overall deadline covers connect, headers AND body read (a server that sends headers
    /// then stalls cannot hold the admin request open). Overload exists so tests can shorten it.</summary>
    public static async Task<ProbeResult> RunAsync(string? raw, IReadOnlySet<string> allowList, TimeSpan timeout, CancellationToken ct)
    {
        var (endpoint, error) = Validate(raw, allowList);
        if (endpoint is null)
            return new ProbeResult { Allowed = false, Reachable = false, Error = error, Endpoint = raw?.Trim() };

        var started = DateTimeOffset.UtcNow;
        var host = endpoint.Host;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(3),
            MaxResponseHeadersLength = 32,
            // Connect only to the validated host name (the request URI cannot be swapped underneath).
            ConnectCallback = async (context, token) =>
            {
                if (!string.Equals(context.DnsEndPoint.Host, host, StringComparison.OrdinalIgnoreCase))
                    throw new HttpRequestException("connection target differs from the validated host");
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(context.DnsEndPoint, token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        using var client = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, "models"));
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            var ms = (DateTimeOffset.UtcNow - started).TotalMilliseconds;
            if ((int)resp.StatusCode is >= 300 and < 400)
                return Fail(endpoint, ms, (int)resp.StatusCode, "redirects are not followed");
            if (!resp.IsSuccessStatusCode)
                return Fail(endpoint, ms, (int)resp.StatusCode, $"endpoint answered HTTP {(int)resp.StatusCode}");

            var body = await ReadCappedAsync(resp, deadline.Token);
            if (body is null) return Fail(endpoint, ms, (int)resp.StatusCode, $"response exceeded {MaxResponseBytes} bytes");
            var models = ParseModelIds(body);
            if (models is null) return Fail(endpoint, ms, (int)resp.StatusCode, "response was not an OpenAI-compatible model list");
            return new ProbeResult
            {
                Allowed = true, Reachable = true, HttpStatus = (int)resp.StatusCode, Models = models,
                DurationMs = (DateTimeOffset.UtcNow - started).TotalMilliseconds, Endpoint = endpoint.ToString(),
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail(endpoint, (DateTimeOffset.UtcNow - started).TotalMilliseconds, null,
                $"timed out after {timeout.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} s");
        }
        catch (HttpRequestException ex)
        {
            return Fail(endpoint, (DateTimeOffset.UtcNow - started).TotalMilliseconds, null,
                ex.InnerException is SocketException se ? $"connection failed: {se.SocketErrorCode}" : "connection failed");
        }
        catch (IOException)
        {
            // Connection reset / closed while reading the body.
            return Fail(endpoint, (DateTimeOffset.UtcNow - started).TotalMilliseconds, null, "connection failed while reading the response");
        }
    }

    private static ProbeResult Fail(Uri endpoint, double ms, int? status, string error) => new()
    {
        Allowed = true, Reachable = false, HttpStatus = status, Error = error, DurationMs = ms, Endpoint = endpoint.ToString(),
    };

    private static async Task<byte[]?> ReadCappedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.Content.Headers.ContentLength is > MaxResponseBytes) return null;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[MaxResponseBytes + 1];
        int total = 0, read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct)) > 0)
        {
            total += read;
            if (total > MaxResponseBytes) return null;
        }
        return buffer[..total];
    }

    /// <summary>{"data":[{"id":"qwen3:8b"},…]} → ids (max 200, each ≤ 200 chars). Null if not that shape.</summary>
    public static List<string>? ParseModelIds(byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return null;
            var ids = new List<string>();
            foreach (var m in data.EnumerateArray())
                if (m.ValueKind == JsonValueKind.Object && m.TryGetProperty("id", out var id)
                    && id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 and <= 200 } s
                    && ids.Count < 200)
                    ids.Add(s);
            return ids;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed class ProbeRequest
{
    [JsonPropertyName("endpoint")] public string? Endpoint { get; set; }
}

public sealed class ProbeResult
{
    [JsonPropertyName("allowed")] public bool Allowed { get; init; }
    [JsonPropertyName("reachable")] public bool Reachable { get; init; }
    [JsonPropertyName("http_status")] public int? HttpStatus { get; init; }
    [JsonPropertyName("models")] public List<string> Models { get; init; } = [];
    [JsonPropertyName("error")] public string? Error { get; init; }
    [JsonPropertyName("duration_ms")] public double? DurationMs { get; init; }
    [JsonPropertyName("endpoint")] public string? Endpoint { get; init; }
    [JsonPropertyName("checked_utc")] public DateTimeOffset CheckedUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Anchor file facts for the console (ADR-0007). Counts what exists; "configured" is not
/// "verified" — verification status comes from the chain verifier.</summary>
public static class AnchorInfo
{
    public static (long Count, DateTimeOffset? LastUtc) Read(string? anchorPath)
    {
        if (anchorPath is null || !File.Exists(anchorPath)) return (0, null);
        long count = 0;
        DateTimeOffset? last = null;
        foreach (var line in File.ReadLines(anchorPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            count++;
            var parts = line.Split(AuditChain.Separator, 3);
            if (parts.Length == 3 && DateTimeOffset.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var t))
                last = t;
        }
        return (count, last);
    }
}

// ---- Console contracts ----

public sealed class RequestsPage
{
    [JsonPropertyName("segment")] public required SegmentInfo Segment { get; init; }
    [JsonPropertyName("items")] public required List<AuditEntry> Items { get; init; }
    /// <summary>Opaque newest-first position of the next page of the SAME segment; null on the last page.</summary>
    [JsonPropertyName("next_cursor")] public string? NextCursor { get; init; }
}

public sealed class SegmentInfo
{
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("entries")] public required int Entries { get; init; }
    [JsonPropertyName("chain_valid")] public required bool ChainValid { get; init; }
    [JsonPropertyName("first_utc")] public DateTimeOffset? FirstUtc { get; init; }
    [JsonPropertyName("last_utc")] public DateTimeOffset? LastUtc { get; init; }
    /// <summary>Control-plane events in this segment (config.activated, …), excluded from Entries/items (v3).</summary>
    [JsonPropertyName("events")] public int Events { get; init; }
    /// <summary>Opaque segment id (GET /admin/audit/segments): "current" or an archived segment's id.</summary>
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("label")] public string? Label { get; init; }
    /// <summary>Always present (review round 2, B8): whether this page belongs to the current segment — the UI never infers
    /// the segment from the first row.</summary>
    [JsonPropertyName("is_current")] public bool IsCurrent { get; init; }
}

public sealed class CatalogReport
{
    [JsonPropertyName("mode")] public required string Mode { get; init; }
    [JsonPropertyName("models")] public required List<CatalogModel> Models { get; init; }
    [JsonPropertyName("local")] public required LocalConfigView Local { get; init; }
    [JsonPropertyName("probe_allow_list")] public required List<string> ProbeAllowList { get; init; }
    // v2 (ADR-0011): multiple connections, keyed by connection id; models keyed "connectionId:modelId".
    [JsonPropertyName("config_version")] public int? ConfigVersion { get; init; }
    [JsonPropertyName("connections")] public List<CatalogConnection>? Connections { get; init; }
    [JsonPropertyName("egress_allow_list")] public List<string>? EgressAllowList { get; init; }
}

public sealed class CatalogModel
{
    [JsonPropertyName("adapter")] public required string Adapter { get; init; }
    [JsonPropertyName("model_id")] public required string ModelId { get; init; }
    [JsonPropertyName("classifications")] public required List<string> Classifications { get; init; }
    /// <summary>An adapter is actually registered for this exact adapter:model (not just named by policy).</summary>
    [JsonPropertyName("registered")] public required bool Registered { get; init; }
    [JsonPropertyName("price_basis")] public required string PriceBasis { get; init; }
    [JsonPropertyName("in_per_1m")] public decimal? InPer1M { get; init; }
    [JsonPropertyName("out_per_1m")] public decimal? OutPer1M { get; init; }
    [JsonPropertyName("endpoint_host")] public string? EndpointHost { get; init; }
    [JsonPropertyName("health")] public required HealthView Health { get; init; }
    // v2 additive: connection-scoped identity.
    [JsonPropertyName("connection_id")] public string? ConnectionId { get; init; }
    [JsonPropertyName("boundary")] public string? Boundary { get; init; }
    [JsonPropertyName("key")] public string? Key { get; init; }
}

public sealed class LocalConfigView
{
    [JsonPropertyName("configured")] public required bool Configured { get; init; }
    /// <summary>scheme://host:port/path only — never credentials.</summary>
    [JsonPropertyName("endpoint")] public string? Endpoint { get; init; }
    [JsonPropertyName("model_id")] public string? ModelId { get; init; }
    [JsonPropertyName("registered")] public required bool Registered { get; init; }
    [JsonPropertyName("endpoint_health")] public required HealthView EndpointHealth { get; init; }
    /// <summary>Review round 2 (B11): the local-boundary connection this view describes (connection id) and the CURRENT
    /// runtime health of exactly that connection + model, observed from real governed attempts (key
    /// "connection_id:model_id"). Use this — not a legacy "local" adapter name — for the header.</summary>
    [JsonPropertyName("connection_id")] public string? ConnectionId { get; init; }
    [JsonPropertyName("health")] public HealthView? Health { get; init; }
}

/// <summary>Current runtime health of one configured connection + model (review round 2, B11): /admin/status
/// <c>connection_health</c>. Observed from real attempts and probes; "unknown" until observed.</summary>
public sealed class ConnectionHealthView
{
    [JsonPropertyName("key")] public required string Key { get; init; }
    [JsonPropertyName("connection_id")] public required string ConnectionId { get; init; }
    [JsonPropertyName("model_id")] public required string ModelId { get; init; }
    [JsonPropertyName("boundary")] public required string Boundary { get; init; }
    [JsonPropertyName("enabled")] public required bool Enabled { get; init; }
    [JsonPropertyName("health")] public required HealthView Health { get; init; }
}

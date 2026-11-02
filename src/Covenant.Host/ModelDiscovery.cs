using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Covenant.Core;
using Covenant.Governance;

namespace Covenant.Host;

// ADR-0017 cloud model catalog discovery. Admin-only, fixed per-kind URLs (no caller-supplied host/path), startup egress
// allow-list, server-side secret by allowed reference, redirects disabled, bounded deadlines/pages/bytes, bounded error
// codes with fixed messages (never provider free text, never the key). Discovery is NOT approval and NOT pricing.
//   Anthropic: GET https://api.anthropic.com/v1/models?limit=100[&after_id=…] → {data:[{id, display_name, created_at}], has_more, last_id}
//   OpenAI:    GET https://api.openai.com/v1/models → {data:[{id, created, owned_by}]}

public sealed class DiscoverModelsRequest
{
    /// <summary>"Anthropic" | "OpenAi".</summary>
    public string? Kind { get; init; }
    /// <summary>Secret reference (configuration key) whose value the server uses; never a value.</summary>
    public string? SecretRef { get; init; }
}

public sealed class DiscoveredModel
{
    public required string Id { get; init; }
    public string? DisplayName { get; init; }
    public DateTimeOffset? CreatedUtc { get; init; }
    public string? OwnedBy { get; init; }
}

public sealed class DiscoverModelsResponse
{
    public required string Kind { get; init; }
    /// <summary>The fixed URL that was (or would have been) called.</summary>
    public required string Endpoint { get; init; }
    public required bool Ok { get; init; }
    /// <summary>Null when ok; else unauthorized | rate_limited | provider_error | timeout | invalid_response |
    /// egress_not_approved | secret_missing | secret_not_allowed | unsupported_kind | connect_failed.</summary>
    public string? Code { get; init; }
    public string? Message { get; init; }
    public required List<DiscoveredModel> Models { get; init; }
    public required bool Truncated { get; init; }
    public required string Note { get; init; }
}

// ---- provider wire shapes (minimal; unknown members ignored) ----
public sealed class AnthropicModelsPage
{
    [JsonPropertyName("data")] public List<AnthropicModelInfo?>? Data { get; set; }
    [JsonPropertyName("has_more")] public bool HasMore { get; set; }
    [JsonPropertyName("last_id")] public string? LastId { get; set; }
}

public sealed class AnthropicModelInfo
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
    [JsonPropertyName("created_at")] public DateTimeOffset? CreatedAt { get; set; }
}

public sealed class OpenAiModelsList
{
    [JsonPropertyName("data")] public List<OpenAiModelInfo?>? Data { get; set; }
}

public sealed class OpenAiModelInfo
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("created")] public long? Created { get; set; }
    [JsonPropertyName("owned_by")] public string? OwnedBy { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = false)]
[JsonSerializable(typeof(DiscoverModelsRequest))]
[JsonSerializable(typeof(DiscoverModelsResponse))]
[JsonSerializable(typeof(AnthropicModelsPage))]
[JsonSerializable(typeof(OpenAiModelsList))]
public partial class ModelDiscoveryJsonContext : JsonSerializerContext;

/// <summary>The discovery client (ADR-0017). One instance per Host; tests substitute the transport.</summary>
public sealed class ModelCatalogDiscovery : IDisposable
{
    public const string AnthropicUrl = "https://api.anthropic.com/v1/models";
    public const string OpenAiUrl = "https://api.openai.com/v1/models";
    /// <summary>Stable Messages API version header accepted for the Models list (sent explicitly; see ADR-0017).</summary>
    public const string AnthropicVersion = "2023-06-01";
    public const int MaxResponseBytes = 512 * 1024;
    public const int MaxPages = 5;
    public const int PageSize = 100;
    public const int MaxModels = 500;
    public const int MaxIdLength = 200;
    public static readonly TimeSpan PageTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(20);

    public const string NotApproval =
        "Discovery lists what this credential can see. It is not approval and not pricing: add a model explicitly, enter its "
        + "price from the provider's price list, then preview and save a new configuration version.";

    private readonly HttpClient _http;

    public ModelCatalogDiscovery(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        }, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public static string? UrlFor(ProviderKind kind) => kind switch
    {
        ProviderKind.Anthropic => AnthropicUrl,
        ProviderKind.OpenAi => OpenAiUrl,
        _ => null,
    };

    /// <summary>Validates the request against the startup facts, resolves the secret server-side and lists models.</summary>
    public async Task<DiscoverModelsResponse> DiscoverAsync(DiscoverModelsRequest body, Func<string, string?> secretValue,
        IReadOnlySet<string> egressAllowList, CancellationToken ct)
    {
        ProviderKind? parsedKind = body.Kind switch { "Anthropic" => ProviderKind.Anthropic, "OpenAi" => ProviderKind.OpenAi, _ => null };
        if (parsedKind is not { } kind || UrlFor(kind) is not { } url)
            return Fail(body.Kind ?? "", "", "unsupported_kind", "Model discovery is available for Anthropic and OpenAI connections only. Ollama uses Test (probe).");
        if (ConfigValidator.DefaultAuthority(kind) is not { } authority || !egressAllowList.Contains(authority))
            return Fail(kind.ToString(), url, "egress_not_approved",
                $"{ConfigValidator.DefaultAuthority(kind)} is not on the startup egress allow-list (Providers:ProbeAllowList); add it and restart.");
        if (body.SecretRef is not { Length: > 0 } secretRef || !ConfigValidator.IsAllowedSecretRef(secretRef))
            return Fail(kind.ToString(), url, "secret_not_allowed", "Choose the credential reference of this connection (e.g. Anthropic:ApiKey).");
        if (secretValue(secretRef) is not { Length: > 0 } key)
            return Fail(kind.ToString(), url, "secret_missing", "The credential is not set on the server. Set it in the server environment and restart.");

        using var total = CancellationTokenSource.CreateLinkedTokenSource(ct);
        total.CancelAfter(TotalTimeout);
        try
        {
            return kind == ProviderKind.Anthropic
                ? await AnthropicAsync(key, total.Token, ct)
                : await OpenAiAsync(key, total.Token, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail(kind.ToString(), url, "timeout", "The provider did not answer in time. Enter the model ID manually or try again.");
        }
        catch (HttpRequestException)
        {
            return Fail(kind.ToString(), url, "connect_failed", "The provider could not be reached. Enter the model ID manually or try again.");
        }
        catch (IOException)
        {
            return Fail(kind.ToString(), url, "connect_failed", "The provider connection failed. Enter the model ID manually or try again.");
        }
    }

    private async Task<DiscoverModelsResponse> AnthropicAsync(string key, CancellationToken total, CancellationToken caller)
    {
        var models = new List<DiscoveredModel>();
        string? after = null;
        bool truncated = false;
        for (int page = 0; ; page++)
        {
            if (page >= MaxPages) { truncated = true; break; }
            var uri = AnthropicUrl + "?limit=" + PageSize + (after is null ? "" : "&after_id=" + Uri.EscapeDataString(after));
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.TryAddWithoutValidation("x-api-key", key);
            req.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
            var (bytes, failure) = await SendAsync(req, ProviderKind.Anthropic, total);
            if (failure is not null) return failure;
            AnthropicModelsPage? parsed;
            try { parsed = JsonSerializer.Deserialize(bytes!, ModelDiscoveryJsonContext.Default.AnthropicModelsPage); }
            catch (JsonException) { return Invalid(ProviderKind.Anthropic); }
            if (parsed?.Data is null) return Invalid(ProviderKind.Anthropic);
            foreach (var m in parsed.Data)
                if (m?.Id is { } id && SafeId(id) && models.Count < MaxModels)
                    models.Add(new DiscoveredModel { Id = id, DisplayName = SafeText(m.DisplayName), CreatedUtc = m.CreatedAt });
            if (models.Count >= MaxModels) { truncated = parsed.HasMore || parsed.Data.Count > 0; break; }
            if (!parsed.HasMore || parsed.LastId is not { } last || !SafeId(last) || last == after) break;
            after = last;
        }
        caller.ThrowIfCancellationRequested();
        return Ok(ProviderKind.Anthropic, AnthropicUrl, models, truncated);
    }

    private async Task<DiscoverModelsResponse> OpenAiAsync(string key, CancellationToken total, CancellationToken caller)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, OpenAiUrl);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        var (bytes, failure) = await SendAsync(req, ProviderKind.OpenAi, total);
        if (failure is not null) return failure;
        OpenAiModelsList? parsed;
        try { parsed = JsonSerializer.Deserialize(bytes!, ModelDiscoveryJsonContext.Default.OpenAiModelsList); }
        catch (JsonException) { return Invalid(ProviderKind.OpenAi); }
        if (parsed?.Data is null) return Invalid(ProviderKind.OpenAi);
        var models = parsed.Data
            .Where(m => m?.Id is { } id && SafeId(id))
            .Select(m => new DiscoveredModel
            {
                Id = m!.Id!,
                OwnedBy = SafeText(m.OwnedBy),
                CreatedUtc = m.Created is { } s && s > 0 && s < 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(s) : null,
            })
            .OrderByDescending(m => m.CreatedUtc)
            .ToList();
        bool truncated = models.Count > MaxModels;
        caller.ThrowIfCancellationRequested();
        return Ok(ProviderKind.OpenAi, OpenAiUrl, models.Take(MaxModels).ToList(), truncated);
    }

    private async Task<(byte[]? Bytes, DiscoverModelsResponse? Failure)> SendAsync(HttpRequestMessage req, ProviderKind kind, CancellationToken total)
    {
        using var page = CancellationTokenSource.CreateLinkedTokenSource(total);
        page.CancelAfter(PageTimeout);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, page.Token);
        int status = (int)resp.StatusCode;
        var url = UrlFor(kind)!;
        if (status is 401 or 403) return (null, Fail(kind.ToString(), url, "unauthorized", "The provider rejected the credential (401/403). Check the server's key."));
        if (resp.StatusCode == HttpStatusCode.TooManyRequests) return (null, Fail(kind.ToString(), url, "rate_limited", "The provider rate-limited the request (429). Try again shortly."));
        if (status is >= 300 and < 400 || !resp.IsSuccessStatusCode)
            return (null, Fail(kind.ToString(), url, "provider_error", $"The provider answered HTTP {status}. Enter the model ID manually or try again."));
        var bytes = await ReadCappedAsync(resp, page.Token);
        return bytes is null ? (null, Invalid(kind)) : (bytes, null);
    }

    private static async Task<byte[]?> ReadCappedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is > MaxResponseBytes) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[MaxResponseBytes + 1];
        int read, total = 0;
        while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), ct)) > 0) total += read;
        return total > MaxResponseBytes ? null : buffer.AsSpan(0, total).ToArray();
    }

    public static bool SafeId(string id)
        => id.Length is > 0 and <= MaxIdLength && id.All(ch => !char.IsControl(ch) && !char.IsWhiteSpace(ch));

    private static string? SafeText(string? s)
        => s is null ? null : new string(s.Where(ch => !char.IsControl(ch)).Take(120).ToArray());

    private static DiscoverModelsResponse Ok(ProviderKind kind, string url, List<DiscoveredModel> models, bool truncated) => new()
    {
        Kind = kind.ToString(), Endpoint = url, Ok = true, Models = models, Truncated = truncated, Note = NotApproval,
    };

    private static DiscoverModelsResponse Invalid(ProviderKind kind)
        => Fail(kind.ToString(), UrlFor(kind)!, "invalid_response", "The provider's answer could not be read. Enter the model ID manually.");

    private static DiscoverModelsResponse Fail(string kind, string url, string code, string message) => new()
    {
        Kind = kind, Endpoint = url, Ok = false, Code = code, Message = message, Models = [], Truncated = false, Note = NotApproval,
    };

    public void Dispose() => _http.Dispose();
}

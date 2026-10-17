using System.ClientModel;
using Anthropic;
using Covenant.Adapters;
using Covenant.Core;
using Covenant.Governance;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Covenant.Host;

/// <summary>The provider clients compiled for ONE configuration version, keyed "connectionId:modelId" (the
/// ChatClientRegistry key format — legacy-imported connections are named openai/anthropic/local, so pre-v2 keys
/// keep matching). Owned by the snapshot; disposed when the retired snapshot's last request releases it.</summary>
public sealed class ProviderClientSet : IChatClientRegistry, IDisposable
{
    private readonly Dictionary<string, IChatClient> _clients;
    private int _disposed;

    public ProviderClientSet(Dictionary<string, IChatClient> clients, IReadOnlyList<string>? skipped = null)
    {
        _clients = clients;
        Skipped = skipped ?? Array.Empty<string>();
    }

    /// <summary>"connection: reason" for connections that were NOT built (disabled, secret missing, egress, mode).</summary>
    public IReadOnlyList<string> Skipped { get; }

    public IReadOnlyCollection<string> Keys => _clients.Keys;

    public bool IsDisposed => Volatile.Read(ref _disposed) == 1;

    public bool TryResolve(string adapterKey, string modelId, out IChatClient client)
        => _clients.TryGetValue($"{adapterKey}:{modelId}", out client!);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        foreach (var c in _clients.Values.Distinct()) c.Dispose();
    }
}

/// <summary>Builds the provider clients for a configuration version (Host-only: it knows the SDKs).</summary>
public interface IProviderClientFactory
{
    ProviderClientSet Build(CovenantConfig config);
}

/// <summary>OpenAI SDK for OpenAi / OpenAiCompatible / Ollama (endpoint + secret), Anthropic SDK for Anthropic —
/// the same construction the pre-v2 Program.cs used. Execution-time revalidation (ADR-0011): a connection whose
/// endpoint is not on the startup egress allow-list, whose secret is missing, or which is cloud in local-only
/// mode gets NO client (the provider stage then refuses fail-closed). Secret values are read here and never
/// leave this method.</summary>
public sealed class SdkProviderClientFactory(IConfiguration configuration, IReadOnlySet<string> egressAllowList) : IProviderClientFactory
{
    public ProviderClientSet Build(CovenantConfig config)
    {
        var clients = new Dictionary<string, IChatClient>(StringComparer.Ordinal);
        var skipped = new List<string>();
        foreach (var conn in config.Connections)
        {
            if (!conn.Enabled) { skipped.Add($"{conn.Id}: disabled"); continue; }
            if (conn.Boundary == Boundary.Cloud && config.Mode == ProviderMode.LocalOnly) { skipped.Add($"{conn.Id}: cloud in local-only mode"); continue; }

            var authority = conn.Endpoint is null ? ConfigValidator.DefaultAuthority(conn.Kind) : ConfigValidator.EndpointAuthority(conn.Endpoint);
            if (authority is null || !egressAllowList.Contains(authority)) { skipped.Add($"{conn.Id}: endpoint not on the egress allow-list"); continue; }

            string? secret = conn.SecretRef is { } sr && ConfigValidator.IsAllowedSecretRef(sr) ? configuration[sr] : null;
            if (string.IsNullOrWhiteSpace(secret))
            {
                if (conn.Kind is ProviderKind.OpenAi or ProviderKind.Anthropic || conn.SecretRef is not null)
                {
                    skipped.Add($"{conn.Id}: secret unavailable");
                    continue;
                }
                secret = "not-needed";   // keyless in-perimeter servers (Ollama/vLLM) — never a Covenant credential
            }

            try
            {
                if (conn.Kind == ProviderKind.Anthropic)
                {
                    foreach (var m in conn.Models)
                    {
                        var anthropic = conn.Endpoint is { Length: > 0 } baseUrl
                            ? new AnthropicClient { ApiKey = secret, BaseUrl = baseUrl }
                            : new AnthropicClient { ApiKey = secret };
                        clients[$"{conn.Id}:{m.ModelId}"] = anthropic.AsIChatClient(m.ModelId);
                    }
                }
                else
                {
                    var root = conn.Endpoint is { Length: > 0 } endpoint
                        ? new OpenAIClient(new ApiKeyCredential(secret), new OpenAIClientOptions { Endpoint = new Uri(endpoint) })
                        : new OpenAIClient(secret);
                    foreach (var m in conn.Models)
                        clients[$"{conn.Id}:{m.ModelId}"] = root.GetChatClient(m.ModelId).AsIChatClient();
                }
            }
            catch (Exception ex) when (ex is ArgumentException or UriFormatException or InvalidOperationException)
            {
                skipped.Add($"{conn.Id}: client construction failed ({ex.GetType().Name})");
            }
        }
        return new ProviderClientSet(clients, skipped);
    }
}

/// <summary>The Host's default <see cref="IChatClientRegistry"/> DI registration: resolves against the CURRENT
/// snapshot's clients (catalog "registered" checks). Its presence tells the provider stage to resolve against
/// each request's OWN snapshot instead (ADR-0011 isolation); a test-substituted registry replaces it entirely.</summary>
public sealed class SnapshotChatClientRegistry(IRuntimeConfig runtime) : IChatClientRegistry
{
    public bool TryResolve(string adapterKey, string modelId, out IChatClient client)
    {
        if (runtime.Current.GetResource<IChatClientRegistry>() is { } set)
            return set.TryResolve(adapterKey, modelId, out client);
        client = null!;
        return false;
    }
}

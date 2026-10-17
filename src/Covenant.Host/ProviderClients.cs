using System.ClientModel;
using System.ClientModel.Primitives;
using Anthropic;
using Covenant.Adapters;
using Covenant.Core;
using Covenant.Governance;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Covenant.Host;

/// <summary>A connection for which NO client was built, and why (disabled, egress, secret, mode, construction).</summary>
public sealed record SkippedConnection(string ConnectionId, string Reason)
{
    public override string ToString() => $"{ConnectionId}: {Reason}";
}

/// <summary>The provider clients compiled for ONE configuration version, keyed "connectionId:modelId" (the
/// ChatClientRegistry key format — legacy-imported connections are named openai/anthropic/local, so pre-v2 keys
/// keep matching). Owned by the snapshot; disposed (clients AND their HttpClients) when the retired snapshot's last
/// request releases it.</summary>
public sealed class ProviderClientSet : IChatClientRegistry, IDisposable
{
    private readonly Dictionary<string, IChatClient> _clients;
    private readonly IReadOnlyList<IDisposable> _owned;
    private int _disposed;

    public ProviderClientSet(Dictionary<string, IChatClient> clients, IReadOnlyList<SkippedConnection>? skipped = null,
        IReadOnlyList<IDisposable>? owned = null)
    {
        _clients = clients;
        Skipped = skipped ?? Array.Empty<SkippedConnection>();
        _owned = owned ?? Array.Empty<IDisposable>();
    }

    /// <summary>Connections that were NOT built (disabled, secret missing, egress, mode, construction failure).</summary>
    public IReadOnlyList<SkippedConnection> Skipped { get; }

    public IReadOnlyCollection<string> Keys => _clients.Keys;

    public bool IsDisposed => Volatile.Read(ref _disposed) == 1;

    public bool TryResolve(string adapterKey, string modelId, out IChatClient client)
        => _clients.TryGetValue($"{adapterKey}:{modelId}", out client!);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        foreach (var c in _clients.Values.Distinct()) c.Dispose();
        foreach (var o in _owned) o.Dispose();
    }

    /// <summary>ADR-0011: a target whose client cannot be built makes the candidate INVALID — never a silently skipped
    /// target. Every enabled connection referenced by an enabled policy must have been built; otherwise one
    /// <c>client_unbuildable</c> error per such connection (path = the connection).</summary>
    public static List<ConfigIssue> UnbuildableIssues(CovenantConfig config, ProviderClientSet clients)
    {
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in config.Policies)
            if (p.Enabled)
                foreach (var t in p.Targets) referenced.Add(t.ConnectionId);
        var issues = new List<ConfigIssue>();
        for (int i = 0; i < config.Connections.Count; i++)
        {
            var conn = config.Connections[i];
            if (!conn.Enabled || !referenced.Contains(conn.Id)) continue;
            var skip = clients.Skipped.FirstOrDefault(x => x.ConnectionId == conn.Id);
            bool missing = skip is not null || conn.Models.Any(m => !clients.TryResolve(conn.Id, m.ModelId, out _));
            if (missing)
                issues.Add(new ConfigIssue($"connections[{i}]", "client_unbuildable",
                    $"no provider client could be built for enabled connection '{conn.Id}' ({skip?.Reason ?? "model client missing"}); it is referenced by an enabled policy"));
        }
        return issues;
    }
}

/// <summary>Builds the provider clients for a configuration version (Host-only: it knows the SDKs).</summary>
public interface IProviderClientFactory
{
    ProviderClientSet Build(CovenantConfig config);
}

/// <summary>OpenAI SDK for OpenAi / OpenAiCompatible / Ollama (endpoint + secret), Anthropic SDK for Anthropic.
/// ADR-0011 transport rules, per connection:
/// <list type="bullet">
/// <item>automatic redirects OFF — a 3xx can never move a request off the approved egress endpoint
/// (<see cref="SocketsHttpHandler.AllowAutoRedirect"/> = false on a handler owned by this connection);</item>
/// <item>SDK-internal retries OFF — attempts are Covenant's own, counted and accounted (ADR-0013/0014): OpenAI via
/// <c>ClientPipelineOptions.RetryPolicy = new ClientRetryPolicy(0)</c>, Anthropic via <c>MaxRetries = 0</c>;</item>
/// <item>the connection's <c>timeout_seconds</c> is the effective network timeout: OpenAI
/// <c>ClientPipelineOptions.NetworkTimeout</c>, Anthropic <c>Timeout</c> (the HttpClient's own timeout is infinite
/// so it never pre-empts the configured one).</item>
/// </list>
/// API names verified against the referenced assemblies' metadata (OpenAI 2.12.0 / System.ClientModel 1.14.0:
/// <c>OpenAIClientOptions : ClientPipelineOptions</c> { Transport, RetryPolicy, NetworkTimeout };
/// <c>HttpClientPipelineTransport(HttpClient)</c>; <c>ClientRetryPolicy(int maxRetries)</c>. Anthropic 12.8.0:
/// <c>AnthropicClient</c> init properties { ApiKey, BaseUrl, HttpClient, MaxRetries (int?), Timeout (TimeSpan?) }).
/// Execution-time revalidation: a connection whose endpoint is not on the startup egress allow-list, whose secret
/// is missing, or which is cloud in local-only mode gets NO client and is reported in <see cref="ProviderClientSet.Skipped"/>.
/// Secret values are read here and never leave this method.</summary>
public sealed class SdkProviderClientFactory(IConfiguration configuration, IReadOnlySet<string> egressAllowList) : IProviderClientFactory
{
    /// <summary>The per-connection handler: no redirects, bounded connect time. Public for regression tests.</summary>
    public static SocketsHttpHandler CreateHandler(ConnectionConfig connection) => new()
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(Math.Clamp(connection.TimeoutSeconds, 1, 30)),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        UseCookies = false,
    };

    /// <summary>HttpClient over <see cref="CreateHandler"/>; its own Timeout is infinite — the SDK option enforces
    /// the connection's timeout_seconds.</summary>
    public static HttpClient CreateHttpClient(ConnectionConfig connection)
        => new(CreateHandler(connection), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };

    public static OpenAIClientOptions OpenAiOptions(ConnectionConfig connection, HttpClient http)
    {
        var options = new OpenAIClientOptions
        {
            Transport = new HttpClientPipelineTransport(http),
            RetryPolicy = new ClientRetryPolicy(0),
            NetworkTimeout = TimeSpan.FromSeconds(connection.TimeoutSeconds),
        };
        if (connection.Endpoint is { Length: > 0 } endpoint) options.Endpoint = new Uri(endpoint);
        return options;
    }

    public ProviderClientSet Build(CovenantConfig config)
    {
        var clients = new Dictionary<string, IChatClient>(StringComparer.Ordinal);
        var skipped = new List<SkippedConnection>();
        var owned = new List<IDisposable>();
        foreach (var conn in config.Connections)
        {
            if (!conn.Enabled) { skipped.Add(new(conn.Id, "disabled")); continue; }
            if (conn.Boundary == Boundary.Cloud && config.Mode == ProviderMode.LocalOnly) { skipped.Add(new(conn.Id, "cloud in local-only mode")); continue; }

            var authority = ConfigValidator.AuthorityOf(conn);
            if (authority is null || !egressAllowList.Contains(authority)) { skipped.Add(new(conn.Id, "endpoint not on the egress allow-list")); continue; }

            string? secret = conn.SecretRef is { } sr && ConfigValidator.IsAllowedSecretRef(sr) ? configuration[sr] : null;
            if (string.IsNullOrWhiteSpace(secret))
            {
                if (conn.Kind is ProviderKind.OpenAi or ProviderKind.Anthropic || conn.SecretRef is not null)
                {
                    skipped.Add(new(conn.Id, "secret unavailable"));
                    continue;
                }
                secret = "not-needed";   // keyless in-perimeter servers (Ollama/vLLM) — never a Covenant credential
            }

            HttpClient? http = null;
            try
            {
                http = CreateHttpClient(conn);
                if (conn.Kind == ProviderKind.Anthropic)
                {
                    foreach (var m in conn.Models)
                    {
                        var anthropic = conn.Endpoint is { Length: > 0 } baseUrl
                            ? new AnthropicClient
                            {
                                ApiKey = secret, BaseUrl = baseUrl, HttpClient = http, MaxRetries = 0,
                                Timeout = TimeSpan.FromSeconds(conn.TimeoutSeconds),
                            }
                            : new AnthropicClient
                            {
                                ApiKey = secret, HttpClient = http, MaxRetries = 0,
                                Timeout = TimeSpan.FromSeconds(conn.TimeoutSeconds),
                            };
                        clients[$"{conn.Id}:{m.ModelId}"] = anthropic.AsIChatClient(m.ModelId);
                    }
                }
                else
                {
                    var root = new OpenAIClient(new ApiKeyCredential(secret), OpenAiOptions(conn, http));
                    foreach (var m in conn.Models)
                        clients[$"{conn.Id}:{m.ModelId}"] = root.GetChatClient(m.ModelId).AsIChatClient();
                }
                owned.Add(http);
            }
            catch (Exception ex) when (ex is ArgumentException or UriFormatException or InvalidOperationException)
            {
                http?.Dispose();
                foreach (var m in conn.Models) clients.Remove($"{conn.Id}:{m.ModelId}");
                skipped.Add(new(conn.Id, $"client construction failed ({ex.GetType().Name})"));
            }
        }
        return new ProviderClientSet(clients, skipped, owned);
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

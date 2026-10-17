using System.Diagnostics;
using Covenant.Core;
using Covenant.Governance;
using Covenant.Host;
using Microsoft.Extensions.Configuration;
using Xunit;
using MEAI = Microsoft.Extensions.AI;

namespace Covenant.Tests;

/// <summary>Review fix 11 (ADR-0011 "Provider clients"): SDK clients are built with automatic redirects OFF,
/// SDK-internal retries OFF and the connection's timeout as the network timeout — exercised against real loopback
/// sockets through the real OpenAI and Anthropic SDK clients. And a client that cannot be built for an enabled,
/// referenced connection is a validation error (client_unbuildable), never a silently skipped target.</summary>
public sealed class ProviderClientConstructionTests
{
    private static IConfiguration Secrets() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Anthropic:ApiKey"] = "ak-test-not-real",
        ["OpenAI:ApiKey"] = "sk-test-not-real",
    }).Build();

    private static ConnectionConfig Compat(string endpoint, int timeoutSeconds = 30) => new()
    {
        Id = "compat", DisplayName = "loopback", Kind = ProviderKind.OpenAiCompatible, Boundary = Boundary.Local,
        Endpoint = endpoint, Enabled = true, TimeoutSeconds = timeoutSeconds,
        Models = [new ModelConfig { ModelId = "m1", Price = new PriceConfig { InputPer1M = 0, OutputPer1M = 0, Basis = "zero-marginal-local" } }],
    };

    private static ConnectionConfig Claude(string baseUrl, int timeoutSeconds = 30) => new()
    {
        Id = "claude", DisplayName = "loopback anthropic", Kind = ProviderKind.Anthropic, Boundary = Boundary.Cloud,
        Endpoint = baseUrl, Enabled = true, SecretRef = "Anthropic:ApiKey", TimeoutSeconds = timeoutSeconds,
        Models = [new ModelConfig { ModelId = "claude-test", Price = new PriceConfig { InputPer1M = 1, OutputPer1M = 1, Basis = "x" } }],
    };

    private static CovenantConfig With(params ConnectionConfig[] connections) => V2.Hybrid() with
    {
        Connections = connections,
        Policies = [V2.Policy("all", [DataClassification.Public, DataClassification.Internal],
            connections.Select(c => V2.T(c.Id, c.Models[0].ModelId)).ToArray())],
    };

    private static MEAI.IChatClient Build(ConnectionConfig conn, params string[] egress)
    {
        var set = new SdkProviderClientFactory(Secrets(), new HashSet<string>(egress, StringComparer.OrdinalIgnoreCase)).Build(With(conn));
        Assert.Empty(set.Skipped);
        Assert.True(set.TryResolve(conn.Id, conn.Models[0].ModelId, out var client));
        return client;
    }

    private static Task Call(MEAI.IChatClient client)
        => client.GetResponseAsync([new MEAI.ChatMessage(MEAI.ChatRole.User, "hi")]);

    [Fact]
    public void Handler_never_follows_redirects()
    {
        using var handler = SdkProviderClientFactory.CreateHandler(Compat("http://127.0.0.1:1/v1"));
        Assert.False(handler.AllowAutoRedirect);
        using var http = SdkProviderClientFactory.CreateHttpClient(Compat("http://127.0.0.1:1/v1"));
        Assert.Equal(Timeout.InfiniteTimeSpan, http.Timeout);   // the SDK option (connection timeout) governs
    }

    [Fact]
    public async Task OpenAi_compatible_client_does_not_retry_a_503_internally()
    {
        using var server = new LoopbackHttpServer(_ => LoopbackHttpServer.Http(503, """{"error":{"message":"busy"}}"""));
        var client = Build(Compat(server.V1), server.Authority);
        await Assert.ThrowsAnyAsync<Exception>(() => Call(client));
        await Task.Delay(300);                                   // a default SDK retry would arrive well within this
        Assert.Single(server.Requests);                          // exactly one attempt: retries are Covenant's own
    }

    [Fact]
    public async Task OpenAi_compatible_client_does_not_follow_a_redirect_off_the_approved_endpoint()
    {
        using var elsewhere = new LoopbackHttpServer(_ => LoopbackHttpServer.Http(200, """{"id":"x","choices":[]}"""));
        using var server = new LoopbackHttpServer(_ => LoopbackHttpServer.Http(302, "",
            extraHeaders: $"Location: http://127.0.0.1:{elsewhere.Port}/v1/chat/completions\r\n"));
        var client = Build(Compat(server.V1), server.Authority);
        await Assert.ThrowsAnyAsync<Exception>(() => Call(client));
        Assert.Single(server.Requests);
        Assert.Equal(0, elsewhere.Connections);                  // the redirect target was never contacted
    }

    [Fact]
    public async Task OpenAi_compatible_client_enforces_the_connection_timeout()
    {
        using var server = new LoopbackHttpServer(_ => null);     // accepts, never answers
        var client = Build(Compat(server.V1, timeoutSeconds: 1), server.Authority);
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<Exception>(() => Call(client));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"took {sw.Elapsed}");
        Assert.Single(server.Requests);                          // and no retry after the timeout
    }

    [Fact]
    public async Task Anthropic_client_does_not_retry_and_does_not_follow_redirects()
    {
        using var busy = new LoopbackHttpServer(_ => LoopbackHttpServer.Http(503, """{"type":"error","error":{"type":"overloaded_error","message":"busy"}}"""));
        var c1 = Build(Claude($"http://127.0.0.1:{busy.Port}"), busy.Authority);
        await Assert.ThrowsAnyAsync<Exception>(() => Call(c1));
        await Task.Delay(300);
        Assert.Single(busy.Requests);

        using var elsewhere = new LoopbackHttpServer(_ => LoopbackHttpServer.Http(200, "{}"));
        using var redirecting = new LoopbackHttpServer(_ => LoopbackHttpServer.Http(307, "",
            extraHeaders: $"Location: http://127.0.0.1:{elsewhere.Port}/v1/messages\r\n"));
        var c2 = Build(Claude($"http://127.0.0.1:{redirecting.Port}"), redirecting.Authority);
        await Assert.ThrowsAnyAsync<Exception>(() => Call(c2));
        Assert.Single(redirecting.Requests);
        Assert.Equal(0, elsewhere.Connections);
    }

    /// <summary>An enabled connection referenced by an enabled policy that gets no client (here: its endpoint is not
    /// on the execution-time egress list) makes the candidate invalid — client_unbuildable — instead of being skipped.</summary>
    [Fact]
    public void Unbuildable_referenced_connection_is_a_validation_error_not_a_silent_skip()
    {
        var conn = Compat("http://127.0.0.1:11434/v1");
        var cfg = With(conn, V2.Ollama(id: "spare") with { Enabled = false });
        var set = new SdkProviderClientFactory(Secrets(), new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Build(cfg);
        Assert.Contains(set.Skipped, s => s.ConnectionId == "compat");
        var issue = Assert.Single(ProviderClientSet.UnbuildableIssues(cfg, set));
        Assert.Equal("client_unbuildable", issue.Code);
        Assert.Equal("connections[0]", issue.Path);

        // Disabled or unreferenced connections are not errors; a fully built set has none.
        var ok = new SdkProviderClientFactory(Secrets(), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "127.0.0.1:11434" }).Build(cfg);
        Assert.Empty(ProviderClientSet.UnbuildableIssues(cfg, ok));
        ok.Dispose();
        set.Dispose();
    }
}

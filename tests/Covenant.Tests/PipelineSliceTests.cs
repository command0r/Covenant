using Covenant.Adapters;
using Covenant.Core;
using Covenant.Governance;
using Xunit;
using MEAI = Microsoft.Extensions.AI;

namespace Covenant.Tests;

public class PipelineSliceTests
{
    // Stub IChatClient so governance is tested with no live model call (tests/CLAUDE.md rule).
    private sealed class StubChatClient(string reply) : MEAI.IChatClient
    {
        public Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> messages, MEAI.ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, reply))
            {
                Usage = new MEAI.UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 }
            });

        public IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<MEAI.ChatMessage> messages, MEAI.ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    // Tripwire: if governance ever routes restricted data here, the test fails loudly.
    private sealed class TripwireChatClient : MEAI.IChatClient
    {
        public Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> messages, MEAI.ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new Xunit.Sdk.XunitException("a public provider was called for restricted data");

        public IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<MEAI.ChatMessage> messages, MEAI.ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new Xunit.Sdk.XunitException("a public provider was called for restricted data");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    // Simulates an upstream provider failure (quota, network, 5xx) at the adapter boundary.
    private sealed class ThrowingChatClient : MEAI.IChatClient
    {
        public Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> messages, MEAI.ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new HttpRequestException("HTTP 429 (insufficient_quota)");

        public IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<MEAI.ChatMessage> messages, MEAI.ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new HttpRequestException("HTTP 429 (insufficient_quota)");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class CollectingAuditSink : IAuditSink
    {
        public List<AuditEntry> Entries { get; } = [];
        public ValueTask EnqueueAsync(AuditEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return ValueTask.CompletedTask;
        }
    }

    private static InferencePipeline BuildPipeline(
        IAuditSink sink,
        MEAI.IChatClient openAi,
        IKillSwitch? killSwitch = null,
        ISpendLedger? ledger = null,
        BudgetConfig? budget = null,
        MEAI.IChatClient? local = null)
    {
        var clients = new Dictionary<string, MEAI.IChatClient>
        {
            ["openai"] = openAi
            // "local" only when a test supplies one → by default PII/PHI cannot be served (fail-closed)
        };
        if (local is not null) clients["local"] = local;
        var registry = new ChatClientRegistry(clients);
        var policy = new PolicyConfig
        {
            AllowedRoutes = new Dictionary<DataClassification, IReadOnlyList<RouteTarget>>
            {
                [DataClassification.Public]   = [new("openai", "gpt-4o-mini")],
                [DataClassification.Internal] = [new("openai", "gpt-4o-mini")],
                [DataClassification.Pii]      = [new("local", "llama-3.1-8b-instruct")],
                [DataClassification.Phi]      = [new("local", "llama-3.1-8b-instruct")],
            }
        };
        var prices = new PriceBook(new Dictionary<string, (decimal, decimal)>
        {
            ["gpt-4o-mini"] = (0.15m, 0.60m)
        });
        return new InferencePipeline(
        [
            new AuditStage(sink),
            new ClassifyStage(new RegexDataClassifier()),
            new PolicyStage(new PolicyEngine(policy)),
            new BudgetStage(
                killSwitch ?? new KillSwitch(),
                ledger ?? new InMemorySpendLedger(),
                budget ?? new BudgetConfig { GlobalCapUsd = 1_000m }),
            new ProviderCallStage(registry),
            new AttributionStage(prices),
        ]);
    }

    private static InferenceContext Ctx(string content, string team = "unknown") =>
        new(new InferenceRequest("tester", [new ChatMessage(ChatRole.User, content)], RequestedModel: null,
            new AttributionTags(team, "test-workflow", "test-case")));

    [Fact]
    public async Task Internal_request_is_served_attributed_and_audited()
    {
        var sink = new CollectingAuditSink();
        var pipeline = BuildPipeline(sink, new StubChatClient("hello from the model"));
        var ctx = Ctx("just a normal internal question");

        await pipeline.ExecuteAsync(ctx, default);

        Assert.False(ctx.IsDenied);
        Assert.NotNull(ctx.Response);
        Assert.Equal("gpt-4o-mini", ctx.Response!.ServedByModel);
        Assert.True(ctx.Response.Usage.CostUsd > 0m);            // cost was computed
        Assert.NotNull(ctx.Attribution);
        Assert.Single(sink.Entries);                              // exactly one audit entry
        Assert.Equal(PolicyEffect.Allow, sink.Entries[0].Effect);
    }

    [Fact]
    public async Task Pii_request_is_denied_never_reaches_public_provider_and_is_audited()
    {
        var sink = new CollectingAuditSink();
        var pipeline = BuildPipeline(sink, new TripwireChatClient());   // throws if ever called
        var ctx = Ctx("my SSN is 123-45-6789, please summarize my account");

        await pipeline.ExecuteAsync(ctx, default);                       // must not throw

        Assert.True(ctx.IsDenied);
        // Must be the governance denial — if the tripwire had fired, the reason would be the
        // swallowed provider failure instead, and this assertion would catch it.
        Assert.Equal(DenialKind.Governance, ctx.DenialKind);
        Assert.Contains("no adapter registered for key 'local'", ctx.DenialReason!);
        Assert.Null(ctx.Response);
        Assert.Equal(DataClassification.Pii, ctx.Classification);
        Assert.Single(sink.Entries);                                     // denials are audited too
        Assert.Equal(PolicyEffect.Deny, sink.Entries[0].Effect);
    }

    [Fact]
    public async Task Provider_failure_is_a_governed_denial_not_an_unhandled_exception()
    {
        var sink = new CollectingAuditSink();
        var pipeline = BuildPipeline(sink, new ThrowingChatClient());   // upstream blows up (429, network, …)
        var ctx = Ctx("just a normal internal question", team: "platform");

        await pipeline.ExecuteAsync(ctx, default);                       // must not throw

        Assert.True(ctx.IsDenied);
        Assert.Equal(DenialKind.UpstreamFailure, ctx.DenialKind);
        Assert.Contains("provider 'openai' call failed", ctx.DenialReason!);
        Assert.Null(ctx.Response);
        Assert.Single(sink.Entries);                                      // upstream failures are audited too
        Assert.Equal(PolicyEffect.Deny, sink.Entries[0].Effect);
    }

    [Fact]
    public async Task Pii_request_with_local_adapter_is_served_in_perimeter_never_by_public_provider()
    {
        var sink = new CollectingAuditSink();
        // Public provider is the tripwire; the in-perimeter target is the stub. Routing PII anywhere
        // but "local" fails this test loudly.
        var pipeline = BuildPipeline(sink, new TripwireChatClient(), local: new StubChatClient("served in perimeter"));
        var ctx = Ctx("my SSN is 123-45-6789, please summarize my account", team: "platform");

        await pipeline.ExecuteAsync(ctx, default);

        Assert.False(ctx.IsDenied);
        Assert.Equal(DataClassification.Pii, ctx.Classification);
        Assert.Equal("llama-3.1-8b-instruct", ctx.Response!.ServedByModel);
        Assert.Single(sink.Entries);
        Assert.Equal(PolicyEffect.Allow, sink.Entries[0].Effect);
    }

    [Fact]
    public async Task Tripped_kill_switch_denies_never_reaches_a_provider_and_is_audited()
    {
        var sink = new CollectingAuditSink();
        var killSwitch = new KillSwitch();
        killSwitch.Trip("incident drill");
        var pipeline = BuildPipeline(sink, new TripwireChatClient(), killSwitch);   // throws if ever called
        var ctx = Ctx("just a normal internal question", team: "platform");

        await pipeline.ExecuteAsync(ctx, default);                                  // must not throw

        Assert.True(ctx.IsDenied);
        Assert.Contains("kill switch", ctx.DenialReason!);
        Assert.Null(ctx.Response);
        Assert.Single(sink.Entries);
    }

    [Fact]
    public async Task Exhausted_team_budget_blocks_the_request_and_never_reaches_a_provider()
    {
        var sink = new CollectingAuditSink();
        var ledger = new InMemorySpendLedger();
        ledger.Record("payments", 5m);                                   // team already at its cap
        var budget = new BudgetConfig
        {
            GlobalCapUsd = 1_000m,
            TeamCapsUsd = new Dictionary<string, decimal> { ["payments"] = 5m }
        };
        var pipeline = BuildPipeline(sink, new TripwireChatClient(), ledger: ledger, budget: budget);
        var ctx = Ctx("summarize this quarter", team: "payments");

        await pipeline.ExecuteAsync(ctx, default);

        Assert.True(ctx.IsDenied);
        Assert.Contains("payments", ctx.DenialReason!);
        Assert.Single(sink.Entries);
    }

    [Fact]
    public async Task Exhausted_global_budget_blocks_teams_without_their_own_cap()
    {
        var sink = new CollectingAuditSink();
        var ledger = new InMemorySpendLedger();
        ledger.Record("some-other-team", 10m);                           // appliance-wide ceiling reached
        var budget = new BudgetConfig { GlobalCapUsd = 10m };            // no team caps at all
        var pipeline = BuildPipeline(sink, new TripwireChatClient(), ledger: ledger, budget: budget);
        var ctx = Ctx("anything at all", team: "uncapped-team");

        await pipeline.ExecuteAsync(ctx, default);

        Assert.True(ctx.IsDenied);
        Assert.Contains("global budget", ctx.DenialReason!);
        Assert.Single(sink.Entries);
    }

    [Fact]
    public async Task Served_request_records_its_attributed_cost_in_the_ledger()
    {
        var sink = new CollectingAuditSink();
        var ledger = new InMemorySpendLedger();
        var pipeline = BuildPipeline(sink, new StubChatClient("hello"), ledger: ledger);
        var ctx = Ctx("just a normal internal question", team: "platform");

        await pipeline.ExecuteAsync(ctx, default);

        Assert.False(ctx.IsDenied);
        Assert.NotNull(ctx.Attribution);
        Assert.True(ctx.Attribution!.Usage.CostUsd > 0m);
        Assert.Equal(ctx.Attribution.Usage.CostUsd, ledger.TeamSpendUsd("platform"));
        Assert.Equal(ctx.Attribution.Usage.CostUsd, ledger.GlobalSpendUsd);
    }

    [Fact]
    public async Task Denied_request_consumes_no_budget()
    {
        var sink = new CollectingAuditSink();
        var ledger = new InMemorySpendLedger();
        var pipeline = BuildPipeline(sink, new TripwireChatClient(), ledger: ledger);
        var ctx = Ctx("my SSN is 123-45-6789", team: "platform");        // policy-denied before budget

        await pipeline.ExecuteAsync(ctx, default);

        Assert.True(ctx.IsDenied);
        Assert.Equal(0m, ledger.GlobalSpendUsd);
    }
}

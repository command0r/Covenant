using Covenant.Core;
using Covenant.Governance;
using Xunit;
using MEAI = Microsoft.Extensions.AI;

namespace Covenant.Tests;

/// <summary>Cache stage guarantees: a hit costs $0 and never reaches a provider; keys are team-scoped; entries expire; denials and disabled config never cache.</summary>
public class CacheTests
{
    private sealed class CountingChatClient : MEAI.IChatClient
    {
        public int Calls;

        public Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> messages, MEAI.ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, "fresh answer"))
            {
                Usage = new MEAI.UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 }
            });
        }

        public IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<MEAI.ChatMessage> messages, MEAI.ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class NullSink : IAuditSink
    {
        public List<AuditEntry> Entries { get; } = [];
        public ValueTask EnqueueAsync(AuditEntry entry, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return ValueTask.CompletedTask;
        }
    }

    private static (InferencePipeline Pipeline, CountingChatClient Client, NullSink Sink) Build(
        ResponseCache cache, int ttlSeconds)
    {
        var client = new CountingChatClient();
        var sink = new NullSink();
        var registry = new Covenant.Adapters.ChatClientRegistry(
            new Dictionary<string, MEAI.IChatClient> { ["openai"] = client });
        var policy = new PolicyConfig
        {
            AllowedRoutes = new Dictionary<DataClassification, IReadOnlyList<RouteTarget>>
            {
                [DataClassification.Internal] = [new("openai", "gpt-4o-mini")],
            }
        };
        var pipeline = new InferencePipeline(
        [
            new AuditStage(sink),
            new ClassifyStage(new RegexDataClassifier()),
            new PolicyStage(new PolicyEngine(policy)),
            new CacheStage(cache, new CacheConfig { TtlSeconds = ttlSeconds }),
            new BudgetStage(new KillSwitch(), new InMemorySpendLedger(), new BudgetConfig { GlobalCapUsd = 1_000m }),
            new Covenant.Adapters.ProviderCallStage(registry),
            new AttributionStage(new PriceBook(new Dictionary<string, (decimal, decimal)> { ["gpt-4o-mini"] = (0.15m, 0.60m) })),
        ]);
        return (pipeline, client, sink);
    }

    private static InferenceContext Ctx(string content, string team = "platform") =>
        new(new InferenceRequest("tester", [new ChatMessage(ChatRole.User, content)], null,
            new AttributionTags(team, "w", "u")));

    [Fact]
    public async Task Identical_request_hits_cache_costs_zero_and_never_reaches_the_provider_again()
    {
        var (pipeline, client, sink) = Build(new ResponseCache(), ttlSeconds: 60);

        var first = Ctx("what's the weather like");
        await pipeline.ExecuteAsync(first, default);
        var second = Ctx("what's the weather like");
        await pipeline.ExecuteAsync(second, default);

        Assert.Equal(1, client.Calls);                                   // one provider call, ever
        Assert.False(first.ServedFromCache);
        Assert.True(second.ServedFromCache);
        Assert.True(first.Response!.Usage.CostUsd > 0m);
        Assert.Equal(0m, second.Response!.Usage.CostUsd);                // hits are free
        Assert.Equal(first.Response.Message.Content, second.Response.Message.Content);
        Assert.True(sink.Entries[1].CacheHit);                           // and audited as a hit
    }

    [Fact]
    public async Task Cache_is_team_scoped_so_responses_never_cross_teams()
    {
        var (pipeline, client, _) = Build(new ResponseCache(), ttlSeconds: 60);

        await pipeline.ExecuteAsync(Ctx("same question", team: "payments"), default);
        var other = Ctx("same question", team: "risk");
        await pipeline.ExecuteAsync(other, default);

        Assert.Equal(2, client.Calls);                                   // no cross-team reuse
        Assert.False(other.ServedFromCache);
    }

    [Fact]
    public async Task Expired_entries_are_not_served()
    {
        var clock = new ManualClock();
        var (pipeline, client, _) = Build(new ResponseCache(clock), ttlSeconds: 30);

        await pipeline.ExecuteAsync(Ctx("hello"), default);
        clock.Now += TimeSpan.FromSeconds(31);
        var late = Ctx("hello");
        await pipeline.ExecuteAsync(late, default);

        Assert.Equal(2, client.Calls);
        Assert.False(late.ServedFromCache);
    }

    [Fact]
    public async Task Cache_is_disabled_by_default_ttl_zero()
    {
        var (pipeline, client, _) = Build(new ResponseCache(), ttlSeconds: 0);

        await pipeline.ExecuteAsync(Ctx("hello"), default);
        await pipeline.ExecuteAsync(Ctx("hello"), default);

        Assert.Equal(2, client.Calls);                                   // opt-in means opt-in
    }

    [Fact]
    public async Task Denials_are_never_cached()
    {
        var (pipeline, client, sink) = Build(new ResponseCache(), ttlSeconds: 60);

        var denied = Ctx("my SSN is 123-45-6789");                       // PII: no permitted route here
        await pipeline.ExecuteAsync(denied, default);
        var again = Ctx("my SSN is 123-45-6789");
        await pipeline.ExecuteAsync(again, default);

        Assert.Equal(0, client.Calls);                                   // never served at all
        Assert.True(denied.IsDenied);
        Assert.True(again.IsDenied);
        Assert.All(sink.Entries, e => Assert.False(e.CacheHit));
    }
}

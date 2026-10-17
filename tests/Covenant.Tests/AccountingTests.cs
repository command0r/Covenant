using System.Runtime.CompilerServices;
using Covenant.Adapters;
using Covenant.Core;
using Covenant.Governance;
using Covenant.Host;
using Xunit;
using MEAI = Microsoft.Extensions.AI;

namespace Covenant.Tests;

/// <summary>ADR-0009 §4–5 / Task 03: honest usage and fail-closed accounting through the REAL stages.
/// Unreported usage is never a real zero; a started-but-failed call is never $0; unknown cost never
/// becomes a false saving.</summary>
public sealed class AccountingTests
{
    private const string Cloud = "gpt-4o-mini";
    private const string Strong = "gpt-4o";
    private const string Local = "qwen3:8b";
    private const decimal InPer1K = 0.01m;
    private const decimal OutPer1K = 0.03m;

    private enum UsageMode { Full, None, InputOnly }

    private sealed class AccountingClient(string reply, UsageMode usage = UsageMode.Full, bool fail = false, string? streamPartialThenFail = null) : MEAI.IChatClient
    {
        public int Calls;

        private MEAI.UsageDetails? Usage() => usage switch
        {
            UsageMode.Full => new MEAI.UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 },
            UsageMode.InputOnly => new MEAI.UsageDetails { InputTokenCount = 10 },
            _ => null,
        };

        public Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> messages, MEAI.ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            if (fail) throw new HttpRequestException("simulated provider 500");
            return Task.FromResult(new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, reply)) { Usage = Usage() });
        }

        public async IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<MEAI.ChatMessage> messages, MEAI.ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            await Task.Yield();
            if (streamPartialThenFail is { } partial)
            {
                yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, partial);
                throw new HttpRequestException("stream interrupted");
            }
            yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, reply);
            if (Usage() is { } u) yield return new MEAI.ChatResponseUpdate { Contents = [new MEAI.UsageContent(u)] };
            yield return new MEAI.ChatResponseUpdate { FinishReason = MEAI.ChatFinishReason.Stop };   // terminal completion (real providers send one)
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class CollectingSink : IAuditSink
    {
        public List<AuditEntry> Entries { get; } = [];
        public ValueTask EnqueueAsync(AuditEntry entry, CancellationToken cancellationToken = default)
        {
            lock (Entries) Entries.Add(entry);
            return ValueTask.CompletedTask;
        }
    }

    private static Dictionary<string, (decimal, decimal)> DefaultPrices() => new()
    {
        [Cloud] = (InPer1K, OutPer1K),
        [Strong] = (0.1m, 0.3m),
        [Local] = (0m, 0m),
    };

    private sealed record Rig(InferencePipeline Pipeline, CollectingSink Sink, InMemorySpendLedger Ledger);

    private static Rig Build(MEAI.IChatClient cloud, MEAI.IChatClient? local = null,
        Dictionary<string, (decimal, decimal)>? prices = null, int cacheTtlSeconds = 0)
    {
        var sink = new CollectingSink();
        var ledger = new InMemorySpendLedger();
        var priceBook = new PriceBook(prices ?? DefaultPrices());
        var clients = new Dictionary<string, MEAI.IChatClient> { [$"openai:{Cloud}"] = cloud };
        if (local is not null) clients[$"local:{Local}"] = local;
        var policy = new PolicyConfig
        {
            AllowedRoutes = new Dictionary<DataClassification, IReadOnlyList<RouteTarget>>
            {
                [DataClassification.Public] = [new("openai", Cloud)],
                [DataClassification.Internal] = [new("openai", Cloud)],
                [DataClassification.Pii] = [new("local", Local)],
                [DataClassification.Phi] = [new("local", Local)],
            }
        };
        var pipeline = new InferencePipeline(
        [
            new AuditStage(sink),
            new AuthStage(new AuthConfig { AllowAnonymous = true, Keys = [] }),
            new ClassifyStage(new RegexDataClassifier()),
            new PolicyStage(new PolicyEngine(policy)),
            new CacheStage(new ResponseCache(), new CacheConfig { TtlSeconds = cacheTtlSeconds }),
            new BudgetStage(new KillSwitch(), ledger, new BudgetConfig { GlobalCapUsd = 1_000m }, priceBook),
            new ProviderCallStage(new ChatClientRegistry(clients)),
            new AttributionStage(priceBook),
        ]);
        return new Rig(pipeline, sink, ledger);
    }

    private static async Task<(InferenceContext Ctx, AuditEntry Entry)> RunAsync(Rig rig, string prompt, bool stream = false, string? model = null)
    {
        var ctx = new InferenceContext(new InferenceRequest("tester", [new ChatMessage(ChatRole.User, prompt)], model,
            new AttributionTags("acct", "wf", "uc"), Stream: stream, RequestId: $"req_{Guid.NewGuid():n}"));
        if (stream) ctx.DeltaSink = (_, _) => ValueTask.CompletedTask;
        int before = rig.Sink.Entries.Count;
        await rig.Pipeline.ExecuteAsync(ctx, CancellationToken.None);
        Assert.Equal(before + 1, rig.Sink.Entries.Count);
        return (ctx, rig.Sink.Entries[^1]);
    }

    private static decimal Estimate(long inChars, long outChars)
        => TokenEstimate.FromChars(inChars) / 1000m * InPer1K + TokenEstimate.FromChars(outChars) / 1000m * OutPer1K;

    [Fact]
    public async Task Complete_usage_is_reported_and_priced_from_tokens()
    {
        var rig = Build(new AccountingClient("reply"));
        var (_, e) = await RunAsync(rig, "a normal question");

        Assert.Equal("reported", e.CostBasis);
        Assert.True(e.UsageReported);
        Assert.Equal(10, e.Usage.InputTokens);
        Assert.Equal(5, e.Usage.OutputTokens);
        Assert.Equal(10 / 1000m * InPer1K + 5 / 1000m * OutPer1K, e.Usage.CostUsd);
        Assert.Equal(e.Usage.CostUsd, rig.Ledger.GlobalSpendUsd);
    }

    [Fact]
    public async Task Missing_usage_on_priced_model_is_estimated_from_text_and_charged()
    {
        const string prompt = "a question with no usage reported back";
        const string reply = "an answer of some length here";
        var rig = Build(new AccountingClient(reply, UsageMode.None));
        var (_, e) = await RunAsync(rig, prompt);

        Assert.Equal(PolicyEffect.Allow, e.Effect);
        Assert.Equal("estimated_from_text", e.CostBasis);
        Assert.False(e.UsageReported);
        Assert.Equal(0, e.Usage.InputTokens);                      // not reported — not invented either
        Assert.Equal(Estimate(prompt.Length, reply.Length), e.Usage.CostUsd);
        Assert.True(e.Usage.CostUsd > 0m);
        Assert.Equal(e.Usage.CostUsd, rig.Ledger.GlobalSpendUsd);  // cannot escape the budget
    }

    [Fact]
    public async Task Missing_usage_on_a_streamed_call_is_also_estimated()
    {
        var rig = Build(new AccountingClient("streamed answer", UsageMode.None));
        var (_, e) = await RunAsync(rig, "stream without usage", stream: true);

        Assert.Equal("estimated_from_text", e.CostBasis);
        Assert.False(e.UsageReported);
        Assert.True(e.Usage.CostUsd > 0m);
    }

    [Fact]
    public async Task Partial_usage_is_not_reported_usage()
    {
        var rig = Build(new AccountingClient("reply", UsageMode.InputOnly));
        var (ctx, e) = await RunAsync(rig, "partial usage please");

        Assert.False(ctx.Response!.UsageReported);
        Assert.False(e.UsageReported);
        Assert.Equal("estimated_from_text", e.CostBasis);
    }

    [Fact]
    public async Task Local_zero_price_is_zero_priced_not_reported_free()
    {
        var local = new AccountingClient("local reply");
        var rig = Build(new AccountingClient("never"), local);
        var (_, e) = await RunAsync(rig, "ssn 123-45-6789");

        Assert.Equal(Local, e.ServedByModel);
        Assert.Equal("zero_priced", e.CostBasis);
        Assert.Equal(0m, e.Usage.CostUsd);
        Assert.Equal(1, local.Calls);
    }

    [Fact]
    public async Task Refusal_with_no_call_is_no_call_and_zero()
    {
        var cloud = new AccountingClient("never");
        var rig = Build(cloud);
        var (_, e) = await RunAsync(rig, "hello", model: "unapproved-model");

        Assert.Equal(PolicyEffect.Deny, e.Effect);
        Assert.Equal("no_call", e.CostBasis);
        Assert.Null(e.AttemptedModel);
        Assert.Null(e.UsageReported);
        Assert.Equal(0m, e.Usage.CostUsd);
        Assert.Equal(0, cloud.Calls);
    }

    [Fact]
    public async Task Cache_hit_is_no_call_and_zero_and_charged_once()
    {
        var cloud = new AccountingClient("cached reply");
        var rig = Build(cloud, cacheTtlSeconds: 60);
        var (_, first) = await RunAsync(rig, "same question twice");
        var spentAfterFirst = rig.Ledger.GlobalSpendUsd;
        var (_, second) = await RunAsync(rig, "same question twice");

        Assert.Equal("reported", first.CostBasis);
        Assert.True(second.CacheHit);
        Assert.Equal("no_call", second.CostBasis);
        Assert.Null(second.AttemptedModel);
        Assert.Equal(0m, second.Usage.CostUsd);
        Assert.Equal(1, cloud.Calls);
        Assert.Equal(spentAfterFirst, rig.Ledger.GlobalSpendUsd);
    }

    [Fact]
    public async Task Upstream_failure_on_priced_model_is_charged_an_estimate()
    {
        const string prompt = "this call will fail upstream";
        var rig = Build(new AccountingClient("never", fail: true));
        var (ctx, e) = await RunAsync(rig, prompt);

        Assert.Equal(DenialKind.UpstreamFailure, ctx.DenialKind);
        Assert.Equal(Cloud, e.AttemptedModel);
        Assert.Equal("estimated_failed_call", e.CostBasis);
        Assert.False(e.UsageReported);
        Assert.Equal(Estimate(prompt.Length, 0), e.Usage.CostUsd);
        Assert.True(e.Usage.CostUsd > 0m);
        Assert.Equal(e.Usage.CostUsd, rig.Ledger.GlobalSpendUsd);
    }

    [Fact]
    public async Task Interrupted_stream_estimate_includes_partial_output()
    {
        const string prompt = "stream then die";
        const string partial = "twenty chars of text";   // 20 chars → 5 estimated output tokens
        var rig = Build(new AccountingClient("never", streamPartialThenFail: partial));
        var (ctx, e) = await RunAsync(rig, prompt, stream: true);

        Assert.Equal(DenialKind.UpstreamFailure, ctx.DenialKind);
        Assert.Equal(partial.Length, ctx.PartialOutputChars);
        Assert.Equal("estimated_failed_call", e.CostBasis);
        Assert.Equal(Estimate(prompt.Length, partial.Length), e.Usage.CostUsd);
        Assert.True(e.Usage.CostUsd > Estimate(prompt.Length, 0));   // output was counted, not dropped
        Assert.Equal(e.Usage.CostUsd, rig.Ledger.GlobalSpendUsd);
    }

    [Fact]
    public async Task Unpriced_model_is_flagged_unpriced_and_counted_unknown()
    {
        var prices = DefaultPrices();
        prices.Remove(Cloud);
        var rig = Build(new AccountingClient("reply"), prices: prices);
        var (_, e) = await RunAsync(rig, "price me if you can");

        Assert.Equal("unpriced", e.CostBasis);
        Assert.Equal(0m, e.Usage.CostUsd);

        var finops = FinOps.Build([e], Local, Strong, prices);
        Assert.Equal(1, finops.UnknownCostRequests);
        Assert.Equal(0m, finops.EstimatedSavingsUsd);
    }

    [Fact]
    public async Task Failed_call_on_unpriced_model_is_unknown_not_zero()
    {
        var prices = DefaultPrices();
        prices.Remove(Cloud);
        var rig = Build(new AccountingClient("never", fail: true), prices: prices);
        var (_, e) = await RunAsync(rig, "fail on an unpriced model");

        Assert.Equal("unknown_failed_call", e.CostBasis);
        Assert.Equal(Cloud, e.AttemptedModel);
        Assert.Equal(1, FinOps.Build([e], Local, Strong, prices).UnknownCostRequests);
    }

    private static AuditEntry Served(string? costBasis, string model = Cloud) => new(
        $"req_{Guid.NewGuid():n}", DateTimeOffset.UtcNow, "p", new AttributionTags("t", "w", "u"),
        DataClassification.Internal, PolicyEffect.Allow, "allowed", model, new Usage(1000, 1000, 0.04m),
        SchemaVersion: 2, CostBasis: costBasis);

    [Theory]
    [InlineData("unpriced")]
    [InlineData("estimated_from_text")]
    public void Unknown_or_estimated_cost_never_becomes_a_saving(string basis)
    {
        var prices = DefaultPrices();
        var control = FinOps.Build([Served("reported")], Local, Strong, prices);
        Assert.True(control.EstimatedSavingsUsd > 0m);           // same record, reported → a real saving vs strong

        var flagged = FinOps.Build([Served(basis)], Local, Strong, prices);
        Assert.Equal(0m, flagged.EstimatedSavingsUsd);
        Assert.Equal(0m, flagged.SavingsRouterUsd);
        Assert.Equal(1, flagged.Allowed);
        Assert.Equal(0.04m, flagged.TotalCostUsd);              // the charge still counts toward spend
        Assert.Equal(basis == "unpriced" ? 1 : 0, flagged.UnknownCostRequests);
        Assert.Equal(basis == "estimated_from_text" ? 1 : 0, flagged.EstimatedCostRequests);
    }

    [Fact]
    public void Legacy_records_without_cost_basis_keep_their_savings_treatment()
    {
        var legacy = FinOps.Build([Served(null)], Local, Strong, DefaultPrices());
        Assert.True(legacy.EstimatedSavingsUsd > 0m);
        Assert.Equal(0, legacy.UnknownCostRequests);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(4, 1)]
    [InlineData(5, 2)]
    [InlineData(400, 100)]
    public void Token_estimate_is_ceiling_chars_over_four(int chars, int tokens)
        => Assert.Equal((long)tokens, TokenEstimate.FromChars(chars));
}

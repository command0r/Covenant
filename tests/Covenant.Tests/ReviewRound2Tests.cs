using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Covenant.Core;
using Covenant.Governance;
using Covenant.Host;
using Xunit;
using MEAI = Microsoft.Extensions.AI;

namespace Covenant.Tests;

// Review round 2 (backend) regressions — docs/verification-2026-10-16/v2/CODEX-REVIEW.txt. Every test here FAILS on
// 96deed5 (behaviourally, or because the evidence field it asserts did not exist) and passes with the fix.

/// <summary>Ledger store whose appends can be made to fail (injected IOException) — B4.</summary>
internal sealed class FlakyLedgerStore : ILedgerStore
{
    public InMemoryLedgerStore Inner { get; } = new();
    public Func<IReadOnlyList<LedgerRecord>, bool> Fail { get; set; } = _ => false;
    public int Failures { get; private set; }

    public IReadOnlyList<LedgerRecord> Records => Inner.Records;

    public void Append(IReadOnlyList<LedgerRecord> records)
    {
        if (Fail(records))
        {
            Failures++;
            throw new IOException("injected ledger write failure");
        }
        Inner.Append(records);
    }

    /// <summary>Fails the next <paramref name="count"/> appends that contain a record with <paramref name="op"/>.</summary>
    public void FailNext(string op, int count = 1)
    {
        int left = count;
        Fail = recs => recs.Any(r => r.Op == op) && left-- > 0;
    }
}

internal sealed class ThrowingStage : IPipelineStage
{
    public Task InvokeAsync(InferenceContext context, PipelineDelegate next, CancellationToken cancellationToken)
        => throw new InvalidOperationException("downstream stage failure (injected)");
}

internal static class Round2
{
    public static StubChatClient Mini() => new("mini reply");

    /// <summary>Hybrid fallback config (Public → openai/gpt-4o-mini, openai/gpt-4o, ollama/qwen3:8b), cache optional.</summary>
    public static CovenantConfig Config(int maxAttempts = 3, int deadlineMs = 60_000, int cacheTtl = 0, Func<CovenantConfig, CovenantConfig>? tweak = null)
        => V2Rig.FallbackConfig(new RetrySettings { MaxAttempts = maxAttempts, DeadlineMs = deadlineMs }, tweak: c =>
        {
            var x = c with { Settings = new OperationalSettings { CacheTtlSeconds = cacheTtl } };
            return tweak is null ? x : tweak(x);
        });

    /// <summary>The Host's stage order with optional extra stages appended after attribution.</summary>
    public static (InferencePipeline Pipeline, V2Rig.ListSink Sink) Pipeline(BudgetLedger ledger, IReadOnlyDictionary<string, MEAI.IChatClient> clients,
        params IPipelineStage[] after)
    {
        var sink = new V2Rig.ListSink();
        var kill = new KillSwitch();
        var stages = new List<IPipelineStage>
        {
            new AuditStage(sink),
            new AuthStage(new AuthConfig { AllowAnonymous = false, Keys = [new ApiKeyRecord("demo-key", "demo-service", "platform", "demo-app")] }),
            new ShapeStage(),
            new ClassifyStage(new RegexDataClassifier()),
            new PolicyStage(new PolicyEngine(new PolicyConfig { AllowedRoutes = new Dictionary<DataClassification, IReadOnlyList<RouteTarget>>() })),
            new CacheStage(new ResponseCache(), new CacheConfig()),
            new RateLimitStage(new RateCounter(), new RateCounter(), new RateLimitConfig()),
            new PauseStage(kill),
            new BudgetStage(kill, ledger),
            new ProviderCallStage(new Covenant.Adapters.ChatClientRegistry(clients), new ProviderHealth(), classifier: SdkProviderFailureClassifier.Shared),
            new AttributionStage(new PriceBook(new Dictionary<string, (decimal, decimal)>())),
        };
        stages.AddRange(after);
        return (new InferencePipeline(stages), sink);
    }

    public static InferenceContext Ctx(RuntimeSnapshot snapshot, string prompt, string profile = "public-demo", bool stream = false)
    {
        var ctx = new InferenceContext(new InferenceRequest("caller", [new ChatMessage(ChatRole.User, prompt)], null,
            new AttributionTags("self-declared", "wf", "uc"), Stream: stream, Credential: "demo-key",
            RequestId: $"req_{Guid.NewGuid():n}", RequestedProfile: profile));
        ctx.Configuration = snapshot;
        ctx.ConfigVersion = snapshot.Version;
        return ctx;
    }
}

/// <summary>B1 — a stream counts as served ONLY with a terminal finish reason and no error.</summary>
public sealed class StreamCompletionTests
{
    private readonly StubChatClient _mini = new("mini reply");
    private readonly StubChatClient _strong = new("strong reply");
    private readonly StubChatClient _local = new("local reply");
    private readonly ResponseCache _cache = new();

    private V2Rig Rig() => V2Rig.Build(Round2.Config(cacheTtl: 300), new Dictionary<string, MEAI.IChatClient>
    {
        [$"openai:{V2.CloudModel}"] = _mini,
        ["openai:gpt-4o"] = _strong,
        [$"ollama:{V2.LocalTag}"] = _local,
    }, cache: _cache);

    [Fact]
    public async Task Stream_ending_without_a_finish_reason_is_an_incomplete_stream_provider_failure_never_served_cached_or_continued()
    {
        _mini.Then(StubChatClient.Mode.StreamNoFinish).Then(StubChatClient.Mode.Serve);
        var rig = Rig();
        var (ctx, e) = await rig.RunAsync(V2Rig.PublicPrompt, profile: "public-demo", stream: true);

        Assert.True(ctx.IsDenied);
        Assert.Equal(DenialKind.UpstreamFailure, ctx.DenialKind);
        Assert.Equal(TraceReasons.IncompleteStream, ctx.DenialCode);
        Assert.Null(ctx.Response);
        Assert.Equal(PolicyEffect.Deny, e.Effect);
        Assert.Equal("UpstreamFailure", e.DenialKind);
        Assert.Null(e.ServedByModel);
        Assert.Equal(0, _strong.Calls + _local.Calls);                                  // no fallback: content was emitted
        Assert.Equal("partial mini reply".Length, ctx.PartialOutputChars);
        Assert.Equal("partial mini reply".Length, e.PartialOutputChars);                // partial output recorded
        var a = Assert.Single(e.Attempts!);
        Assert.Equal("failed", a.Outcome);
        Assert.Equal("incomplete_stream", a.ErrorClass);
        Assert.Equal("estimated_failed_call", a.CostBasis);                             // charged its estimate
        Assert.True(a.CostUsd > 0m);
        Assert.Equal(a.ReservedUsd, a.CostUsd);
        Assert.Equal(0, rig.Ledger.OpenReservations);
        Assert.Equal("failed", rig.Health.Get($"openai:{V2.CloudModel}").State);
        Assert.Contains(ctx.TraceEvents, t => t.Stage == "provider" && t.AttemptId == "att_1" && t.State == TraceStates.Failed
            && t.ReasonCode == TraceReasons.IncompleteStream);

        // Never cached: the identical request calls the provider again.
        var (again, e2) = await rig.RunAsync(V2Rig.PublicPrompt, profile: "public-demo", stream: true);
        Assert.False(again.IsDenied, again.DenialReason);
        Assert.False(e2.CacheHit);
        Assert.Equal(2, _mini.Calls);
    }

    [Fact]
    public async Task Stream_throwing_mid_stream_is_terminal_partial_recorded_and_never_cached()
    {
        _mini.Then(StubChatClient.Mode.StreamThenThrow).Then(StubChatClient.Mode.Serve);
        var rig = Rig();
        var (ctx, e) = await rig.RunAsync(V2Rig.PublicPrompt, profile: "public-demo", stream: true);

        Assert.Equal(DenialKind.UpstreamFailure, ctx.DenialKind);
        Assert.Equal(0, _strong.Calls + _local.Calls);
        Assert.Equal("partial ".Length, e.PartialOutputChars);
        Assert.Equal("5xx", Assert.Single(e.Attempts!).ErrorClass);
        var (_, e2) = await rig.RunAsync(V2Rig.PublicPrompt, profile: "public-demo", stream: true);
        Assert.False(e2.CacheHit);
        Assert.Equal(2, _mini.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Finish_reason_length_is_served_and_recorded_as_truncated(bool stream)
    {
        _mini.Then(StubChatClient.Mode.ServeLength);
        var rig = Rig();
        var (ctx, e) = await rig.RunAsync(V2Rig.PublicPrompt, profile: "public-demo", stream: stream);
        Assert.False(ctx.IsDenied, ctx.DenialReason);
        Assert.Equal("length", ctx.Response!.FinishReason);
        Assert.True(Assert.Single(e.Attempts!).Truncated);
        Assert.Null(e.PartialOutputChars);
    }

    [Fact]
    public async Task Host_SSE_relays_the_incomplete_stream_as_upstream_error_and_never_finish_reason_stop()
    {
        using var factory = new RestartableHybridFactory(RestartableHybridFactory.NewAuditPath());
        var admin = factory.CreateClient();
        factory.Cloud.Then(StubChatClient.Mode.StreamNoFinish).Then(StubChatClient.Mode.Serve);
        using var app = factory.CreateClient();
        app.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "demo-key");
        app.DefaultRequestHeaders.Add("X-Covenant-Profile", "public-demo");

        using var resp = await app.PostAsync("/v1/chat/completions", CorrelationTestKit.Chat("Summarize this public announcement.", stream: true));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);                      // headers committed with the first delta
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("partial ", body, StringComparison.Ordinal);
        Assert.Contains("\"upstream_error\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"finish_reason\":\"stop\"", body, StringComparison.Ordinal);
        Assert.EndsWith("data: [DONE]\n\n", body, StringComparison.Ordinal);
        var e = await CorrelationTestKit.LookupAsync(admin, CorrelationTestKit.HeaderId(resp));
        Assert.Equal(PolicyEffect.Deny, e.Effect);
        Assert.Equal("UpstreamFailure", e.DenialKind);
        Assert.Equal(0, factory.Local.Calls);                                   // no fallback after content

        // The completed stream still ends with the provider's own finish reason.
        using var ok = await app.PostAsync("/v1/chat/completions", CorrelationTestKit.Chat("Summarize this public announcement.", stream: true));
        var okBody = await ok.Content.ReadAsStringAsync();
        Assert.Contains("\"finish_reason\":\"stop\"", okBody, StringComparison.Ordinal);
        Assert.DoesNotContain("upstream_error", okBody, StringComparison.Ordinal);
    }

    // Reviewer HTTP finding: a legitimately completed EMPTY stream was audited Allow/served but answered 403 "no response",
    // because the endpoint treated "no delta written" as "denied". It must be a 200 SSE with the terminal chunk + [DONE].
    [Fact]
    public async Task Host_SSE_completed_empty_stream_is_a_successful_200_not_a_denial()
    {
        using var factory = new RestartableHybridFactory(RestartableHybridFactory.NewAuditPath());
        var admin = factory.CreateClient();
        factory.Cloud.Then(StubChatClient.Mode.StreamEmptyComplete);
        using var app = factory.CreateClient();
        app.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "demo-key");
        app.DefaultRequestHeaders.Add("X-Covenant-Profile", "public-demo");

        using var resp = await app.PostAsync("/v1/chat/completions", CorrelationTestKit.Chat("Summarize this public announcement.", stream: true));
        var body = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);
        Assert.Contains("\"finish_reason\":\"stop\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("upstream_error", body, StringComparison.Ordinal);
        Assert.EndsWith("data: [DONE]\n\n", body, StringComparison.Ordinal);
        var e = await CorrelationTestKit.LookupAsync(admin, CorrelationTestKit.HeaderId(resp));
        Assert.Equal(PolicyEffect.Allow, e.Effect);                             // evidence and HTTP outcome agree
    }
}

/// <summary>B2 — request-level CostBasis over the charged attempts.</summary>
public sealed class AggregateCostBasisTests
{
    private static AttemptRecord A(string basis, decimal cost) => new() { AttemptId = "a", ConnectionId = "c", ModelId = "m", CostBasis = basis, CostUsd = cost };

    [Fact]
    public void Reported_only_when_every_charged_attempt_is_reported_else_the_shared_basis_else_mixed()
    {
        Assert.Equal("reported", CostBases.Aggregate([A("reported", 0.1m)], "reported"));
        Assert.Equal("estimated_failed_call", CostBases.Aggregate([A("estimated_failed_call", 0.1m), A("estimated_failed_call", 0.2m)], "estimated_failed_call"));
        Assert.Equal("mixed", CostBases.Aggregate([A("estimated_failed_call", 0.1m), A("reported", 0.2m)], "reported"));
        Assert.Equal("reported", CostBases.Aggregate([A("estimated_failed_call", 0m), A("reported", 0.2m)], "reported"));   // $0 is not a charge
        Assert.Equal("zero_priced", CostBases.Aggregate([A("zero_priced", 0m)], "zero_priced"));                          // nothing charged
    }

    [Fact]
    public async Task Failed_estimated_attempt_plus_reported_fallback_is_mixed_in_the_audit_record()
    {
        var mini = new StubChatClient("mini reply");
        var strong = new StubChatClient("strong reply");
        mini.Then(StubChatClient.Mode.Throw, () => Failures.Status(HttpStatusCode.ServiceUnavailable));
        var rig = V2Rig.Build(Round2.Config(), new Dictionary<string, MEAI.IChatClient>
        {
            [$"openai:{V2.CloudModel}"] = mini,
            ["openai:gpt-4o"] = strong,
        });
        var (ctx, e) = await rig.RunAsync(V2Rig.PublicPrompt, profile: "public-demo");
        Assert.False(ctx.IsDenied, ctx.DenialReason);
        Assert.Equal("estimated_failed_call", e.Attempts![0].CostBasis);
        Assert.Equal("reported", e.Attempts[1].CostBasis);
        Assert.Equal("mixed", e.CostBasis);                    // was "reported" for a total containing an estimate

        // FinOps counts it as an estimated-cost request and keeps it out of savings claims.
        var f = FinOps.Build([e], V2.LocalTag, V2.LocalTag, new Dictionary<string, (decimal, decimal)>(), null);
        Assert.Equal(1, f.EstimatedCostRequests);
    }
}

/// <summary>B3 — one finite deadline for the whole execution, also after content.</summary>
public sealed class DeadlineAfterContentTests
{
    [Fact]
    public async Task Deadline_after_streamed_content_is_a_timeout_with_partial_output_and_no_fallback()
    {
        var mini = new StubChatClient("mini reply") { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };   // never released
        var strong = new StubChatClient("strong reply");
        mini.Then(StubChatClient.Mode.StreamGated);
        var rig = V2Rig.Build(Round2.Config(deadlineMs: 300), new Dictionary<string, MEAI.IChatClient>
        {
            [$"openai:{V2.CloudModel}"] = mini,
            ["openai:gpt-4o"] = strong,
        });
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(5));   // pre-fix: the stream hangs until this fires
        var (ctx, e) = await rig.RunAsync(V2Rig.PublicPrompt, profile: "public-demo", stream: true, ct: safety.Token);

        Assert.False(safety.IsCancellationRequested);
        Assert.Equal(DenialKind.UpstreamFailure, ctx.DenialKind);
        var a = Assert.Single(e.Attempts!);
        Assert.Equal("failed", a.Outcome);
        Assert.Equal("timeout", a.ErrorClass);
        Assert.Equal("partial ".Length, e.PartialOutputChars);
        Assert.Equal(0, strong.Calls);
        Assert.True(a.CostUsd > 0m);
        Assert.Contains("deadline", e.Reason, StringComparison.Ordinal);
        Assert.Equal(0, rig.Ledger.OpenReservations);
    }
}

/// <summary>B4 — every admitted attempt settled/released exactly once; a slot closes only after the durable write.</summary>
public sealed class SettlementRobustnessTests
{
    private static readonly RouteTarget Mini = new("openai", V2.CloudModel);

    [Fact]
    public void A_failed_settle_write_leaves_the_slot_open_and_a_retry_settles_exactly_once()
    {
        var store = new FlakyLedgerStore();
        var ledger = new BudgetLedger(store);
        var snapshot = new RuntimeSnapshot(Round2.Config());
        var ctx = Round2.Ctx(snapshot, V2Rig.PublicPrompt);
        var gate = new BudgetAttemptGate(ledger, new KillSwitch(), snapshot, ctx);
        Assert.True(gate.ReserveFirst(ctx, Mini).Admitted);
        Assert.Equal(1, ledger.OpenReservations);

        store.FailNext(LedgerOps.Settle);
        Assert.Throws<IOException>(() => gate.Settle(ctx, "att_1", null));
        Assert.Equal(1, gate.OpenSlots);                     // NOT marked closed before the durable write succeeded
        Assert.Equal(1, ledger.OpenReservations);            // reservation still held (never silently lost)
        Assert.Null(ctx.SettledUsd);

        var charge = gate.Settle(ctx, "att_1", null);
        Assert.True(charge.Settled);
        Assert.Equal(0, gate.OpenSlots);
        Assert.Equal(0, ledger.OpenReservations);
        Assert.False(gate.Settle(ctx, "att_1", null).Settled);   // idempotent afterwards
        Assert.Equal(new[] { LedgerOps.Reserve, LedgerOps.Settle, LedgerOps.Release }, store.Records.Select(r => r.Op));
        Assert.Equal(charge.CostUsd, ctx.SettledUsd);
    }

    [Fact]
    public void A_failed_release_write_leaves_the_slot_open_and_a_retry_releases_once()
    {
        var store = new FlakyLedgerStore();
        var ledger = new BudgetLedger(store);
        var snapshot = new RuntimeSnapshot(Round2.Config());
        var ctx = Round2.Ctx(snapshot, V2Rig.PublicPrompt);
        var gate = new BudgetAttemptGate(ledger, new KillSwitch(), snapshot, ctx);
        gate.ReserveFirst(ctx, Mini);
        store.FailNext(LedgerOps.Release);
        Assert.Throws<IOException>(() => gate.Release(ctx, "att_1"));
        Assert.Equal(1, gate.OpenSlots);
        gate.Finish(ctx);                                    // the unwind backstop releases it
        Assert.Equal(0, gate.OpenSlots);
        Assert.Equal(0, ledger.OpenReservations);
        Assert.Equal(new[] { LedgerOps.Reserve, LedgerOps.Release }, store.Records.Select(r => r.Op));
    }

    [Fact]
    public async Task Pipeline_settle_failure_is_retried_by_the_unwind_and_settles_exactly_once()
    {
        var store = new FlakyLedgerStore();
        var ledger = new BudgetLedger(store);
        var (pipeline, sink) = Round2.Pipeline(ledger, new Dictionary<string, MEAI.IChatClient> { [$"openai:{V2.CloudModel}"] = new StubChatClient("mini") });
        var ctx = Round2.Ctx(new RuntimeSnapshot(Round2.Config()), V2Rig.PublicPrompt);
        store.FailNext(LedgerOps.Settle);

        await Assert.ThrowsAsync<IOException>(() => pipeline.ExecuteAsync(ctx, CancellationToken.None));
        Assert.Equal(1, store.Failures);
        Assert.Equal(0, ledger.OpenReservations);            // pre-fix: the slot was closed before the failed write ⇒ stuck open
        Assert.Equal(0m, ledger.Reserved("global"));
        Assert.Single(store.Records, r => r.Op == LedgerOps.Settle);
        Assert.Single(sink.Entries);                         // the request is still evidenced
    }

    [Fact]
    public async Task Persistent_write_failure_keeps_state_consistent_and_boot_recovery_settles_once()
    {
        var store = new FlakyLedgerStore();
        var ledger = new BudgetLedger(store);
        var (pipeline, _) = Round2.Pipeline(ledger, new Dictionary<string, MEAI.IChatClient> { [$"openai:{V2.CloudModel}"] = new StubChatClient("mini") });
        var ctx = Round2.Ctx(new RuntimeSnapshot(Round2.Config()), V2Rig.PublicPrompt);
        store.FailNext(LedgerOps.Settle, count: int.MaxValue);

        await Assert.ThrowsAsync<IOException>(() => pipeline.ExecuteAsync(ctx, CancellationToken.None));
        Assert.Equal(1, ledger.OpenReservations);            // held, uncharged: the durable state the next boot sees
        var reserved = store.Records.Single(r => r.Op == LedgerOps.Reserve).AmountUsd;

        // "Restart": replay what was durably written; recovery settles the open reservation exactly once.
        var after = new BudgetLedger(new InMemoryLedgerStore(), store.Records);
        var recovered = Assert.Single(after.RecoverUnsettled());
        Assert.Equal(LedgerOps.SettleRecovered, recovered.Op);
        Assert.Equal(reserved, after.Spent("global", BudgetPeriod.Monthly));
        Assert.Empty(after.RecoverUnsettled());
    }

    [Fact]
    public async Task Exception_after_attribution_still_settles_the_served_attempt_exactly_once_at_its_priced_cost()
    {
        var store = new FlakyLedgerStore();
        var ledger = new BudgetLedger(store);
        var (pipeline, sink) = Round2.Pipeline(ledger, new Dictionary<string, MEAI.IChatClient> { [$"openai:{V2.CloudModel}"] = new StubChatClient("mini") },
            new ThrowingStage());
        var ctx = Round2.Ctx(new RuntimeSnapshot(Round2.Config()), V2Rig.PublicPrompt);

        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.ExecuteAsync(ctx, CancellationToken.None));
        Assert.Equal(new[] { LedgerOps.Reserve, LedgerOps.Settle, LedgerOps.Release }, store.Records.Select(r => r.Op));
        Assert.Equal(0, ledger.OpenReservations);
        var a = Assert.Single(ctx.Attempts);
        Assert.Equal("reported", a.CostBasis);               // settled by the provider stage's finally with the priced cost
        Assert.Equal(10 / 1_000_000m * 0.15m + 5 / 1_000_000m * 0.60m, a.CostUsd);
        Assert.Single(sink.Entries);                         // still exactly one evidence record
    }
}

/// <summary>B5 — unambiguous cache keys and served provenance (reviewer probe cache-collision-probe.cs.txt).</summary>
public sealed class CacheKeyCollisionTests
{
    private static async Task<(int Calls, bool SecondHit)> TwoRunsAsync(Func<InferenceContext> first, Func<InferenceContext> second)
    {
        var cache = new CacheStage(new ResponseCache(), new CacheConfig { TtlSeconds = 60 });
        int calls = 0;
        Task Next(InferenceContext c, CancellationToken t)
        {
            calls++;
            c.Response = new(new(ChatRole.Assistant, "response-" + calls), Usage.Empty, "model");
            return Task.CompletedTask;
        }
        await cache.InvokeAsync(first(), Next, default);
        var b = second();
        await cache.InvokeAsync(b, Next, default);
        return (calls, b.ServedFromCache);
    }

    private static InferenceContext Legacy(params ChatMessage[] messages)
        => new(new InferenceRequest("app", messages, "model", new("team", "flow", "case"))) { Policy = PolicyOutcome.Allow(new("connection", "model")) };

    private static readonly RuntimeSnapshot Snapshot = new(V2.Hybrid(tweak: c => c with { Settings = new OperationalSettings { CacheTtlSeconds = 60 } }));

    private static InferenceContext Configured(params ChatMessage[] messages)
    {
        var ctx = new InferenceContext(new InferenceRequest("app", messages, null, new("platform", "flow", "case"))) { Configuration = Snapshot, ApplicationId = "demo-app" };
        ctx.Policy = PolicyOutcome.Allow(new("openai", V2.CloudModel)) with { Candidates = [new("openai", V2.CloudModel), new("ollama", V2.LocalTag)] };
        return ctx;
    }

    public static TheoryData<string> Shapes => new() { "delimiter-in-content", "boundary-shift", "control-chars", "role", "empty-vs-absent" };

    private static ChatMessage U(string content) => new(ChatRole.User, content);

    private static (ChatMessage[] A, ChatMessage[] B) Pair(string shape)
    {
        switch (shape)
        {
            case "delimiter-in-content": return (new[] { U("x\u001fAssistant:y") }, new[] { U("x"), new ChatMessage(ChatRole.Assistant, "y") });
            case "boundary-shift": return (new[] { U("ab"), U("c") }, new[] { U("a"), U("bc") });
            case "control-chars": return (new[] { U("a\u001e"), U("b") }, new[] { U("a"), U("\u001eb") });
            case "role": return (new[] { new ChatMessage(ChatRole.System, "x") }, new[] { U("x") });
            case "empty-vs-absent": return (new[] { U("x"), U("") }, new[] { U("x") });
            default: throw new ArgumentOutOfRangeException(nameof(shape));
        }
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task Different_message_structures_never_collide_legacy_key(string shape)
    {
        var (a, b) = Pair(shape);
        var (calls, hit) = await TwoRunsAsync(() => Legacy(a), () => Legacy(b));
        Assert.Equal(2, calls);
        Assert.False(hit);
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task Different_message_structures_never_collide_configured_key(string shape)
    {
        var (a, b) = Pair(shape);
        var (calls, hit) = await TwoRunsAsync(() => Configured(a), () => Configured(b));
        Assert.Equal(2, calls);
        Assert.False(hit);
    }

    [Fact]
    public async Task Exact_repeat_still_hits()
    {
        var (calls, hit) = await TwoRunsAsync(() => Configured(new ChatMessage(ChatRole.User, "same")), () => Configured(new ChatMessage(ChatRole.User, "same")));
        Assert.Equal(1, calls);
        Assert.True(hit);
    }

    [Fact]
    public async Task Reviewer_probe_capped_primary_and_less_capped_fallback_with_max_tokens_20_then_10_are_two_calls()
    {
        var config = new CovenantConfig
        {
            Version = 1, Origin = "probe", CreatedUtc = DateTimeOffset.UtcNow, Mode = ProviderMode.LocalOnly,
            Connections = [new ConnectionConfig { Id = "local", DisplayName = "Fixture", Kind = ProviderKind.Ollama, Boundary = Boundary.Local, Enabled = true,
                Models = [new ModelConfig { ModelId = "primary", MaxOutputTokens = 2 }, new ModelConfig { ModelId = "fallback", MaxOutputTokens = 100 }] }],
            Applications = [new ApplicationConfig { Id = "app", Team = "team", Enabled = true, ConsoleTesting = true, GrantedProfiles = [], MaxOutputTokens = 100 }],
            Profiles = [], Policies = [], Budgets = [], Settings = new OperationalSettings { CacheTtlSeconds = 60 },
        };
        var snapshot = new RuntimeSnapshot(config);
        InferenceContext B(int max) => new(new InferenceRequest("app", [new(ChatRole.User, "test")], null, new("team", "flow", "case"), MaxOutputTokens: max))
        {
            Configuration = snapshot, ApplicationId = "app",
            Policy = PolicyOutcome.Allow(new("local", "primary")) with { Candidates = [new("local", "primary"), new("local", "fallback")] },
        };
        var (calls, hit) = await TwoRunsAsync(() => B(20), () => B(10));
        Assert.Equal(2, calls);
        Assert.False(hit);
    }

    [Fact]
    public async Task A_hit_reports_the_connection_and_model_that_actually_served_not_the_primary()
    {
        var mini = new StubChatClient("mini reply");
        var strong = new StubChatClient("strong reply");
        var local = new StubChatClient("local reply");
        mini.Then(StubChatClient.Mode.Throw, () => Failures.Status(HttpStatusCode.ServiceUnavailable)).Then(StubChatClient.Mode.Serve);
        strong.Then(StubChatClient.Mode.Throw, () => Failures.Status(HttpStatusCode.ServiceUnavailable)).Then(StubChatClient.Mode.Serve);
        var rig = V2Rig.Build(Round2.Config(cacheTtl: 300), new Dictionary<string, MEAI.IChatClient>
        {
            [$"openai:{V2.CloudModel}"] = mini,
            ["openai:gpt-4o"] = strong,
            [$"ollama:{V2.LocalTag}"] = local,
        });
        var (_, first) = await rig.RunAsync(V2Rig.PublicPrompt, profile: "public-demo");
        Assert.Equal("ollama", first.ServedConnectionId);
        Assert.Equal(V2.LocalTag, first.ServedByModel);

        var (hitCtx, hit) = await rig.RunAsync(V2Rig.PublicPrompt, profile: "public-demo");
        Assert.True(hit.CacheHit);
        Assert.Equal(1, local.Calls);
        Assert.Equal(new RouteTarget("ollama", V2.LocalTag), hitCtx.ServedTarget);
        Assert.Equal("ollama", hit.ServedConnectionId);
        Assert.Equal(V2.LocalTag, hit.ServedByModel);
        Assert.Equal(V2.CloudModel, hit.SelectedModel);       // the policy's primary stays visible as "selected"
    }
}

/// <summary>B6 — identity team = the ACTIVE application's team on both entry points.</summary>
public sealed class TeamConsistencyTests
{
    private const decimal OneRequest = 10 / 1_000_000m * 0.15m + 5 / 1_000_000m * 0.60m;

    [Fact]
    public async Task Team_change_applied_through_configuration_governs_new_v1_and_console_charges_and_history_stays()
    {
        using var factory = new RestartableHybridFactory(RestartableHybridFactory.NewAuditPath());
        var admin = factory.CreateClient();
        using var app = factory.CreateClient();
        app.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "demo-key");   // key's startup team: platform
        app.DefaultRequestHeaders.Add("X-Covenant-Profile", "public-demo");

        using (var before = await app.PostAsync("/v1/chat/completions", CorrelationTestKit.Chat("Summarize this public announcement.")))
            Assert.Equal("platform", (await CorrelationTestKit.LookupAsync(admin, CorrelationTestKit.HeaderId(before))).Tags.Team);

        var (version, cfg, _) = await V2Admin.GetConfigAsync(admin);
        await V2Admin.ApplyAsync(admin, cfg with
        {
            Applications = cfg.Applications.Select(a => a.Id == "demo-app" ? a with { Team = "payments" } : a).ToList(),
        }, version, "move demo-app to payments");

        using var v1 = await app.PostAsync("/v1/chat/completions", CorrelationTestKit.Chat("Summarize this other public announcement."));
        Assert.Equal(HttpStatusCode.OK, v1.StatusCode);
        Assert.Equal("payments", (await CorrelationTestKit.LookupAsync(admin, CorrelationTestKit.HeaderId(v1))).Tags.Team);   // pre-fix: platform

        using var console = await Playground.PostAsync(admin, Playground.Body("demo-app", "Summarize a third public announcement.", profile: "public-demo"));
        Assert.Equal(HttpStatusCode.OK, console.StatusCode);
        Assert.Equal("payments", (await CorrelationTestKit.LookupAsync(admin, CorrelationTestKit.HeaderId(console))).Tags.Team);

        var (_, status, _) = await V2Admin.SendAsync(admin, HttpMethod.Get, "/admin/status");
        var teams = status.GetProperty("budget").GetProperty("teams").EnumerateArray()
            .ToDictionary(t => t.GetProperty("team").GetString()!, t => t.GetProperty("spend_usd").GetDecimal(), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(OneRequest, teams["platform"]);          // historical charge stays where it was recorded
        Assert.Equal(2 * OneRequest, teams["payments"]);      // both entry points charge the new team
    }
}

/// <summary>B7 — console request id available before the first response byte.</summary>
public sealed class EarlyRequestIdTests
{
    [Fact]
    public async Task Submission_lookup_returns_the_request_id_while_the_provider_blocks_and_the_trace_shows_provider_running()
    {
        using var factory = new RestartableHybridFactory(RestartableHybridFactory.NewAuditPath());
        var admin = factory.CreateClient();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Cloud.Gate = gate;
        factory.Cloud.Then(StubChatClient.Mode.Gated).Then(StubChatClient.Mode.Serve);
        const string crid = "crid-early-7f3a";

        var post = Playground.PostAsync(admin, Playground.Body("demo-app", "Summarize this public announcement.", profile: "public-demo",
            clientRequestId: crid, maxTokens: 256));
        await factory.Cloud.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var (status, sub, _) = await V2Admin.SendAsync(admin, HttpMethod.Get, $"/admin/playground/submissions/{crid}");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(crid, sub.GetProperty("client_request_id").GetString());
        Assert.Equal("running", sub.GetProperty("state").GetString());
        var requestId = sub.GetProperty("request_id").GetString()!;
        Assert.True(RequestCorrelation.IsWellFormed(requestId));

        var (traceStatus, trace, _) = await V2Admin.SendAsync(admin, HttpMethod.Get, $"/admin/requests/{requestId}/trace");
        Assert.Equal(HttpStatusCode.OK, traceStatus);
        Assert.False(trace.GetProperty("complete").GetBoolean());
        var events = trace.GetProperty("events").EnumerateArray().ToList();
        var running = events.Last(e => e.GetProperty("stage").GetString() == "provider");
        Assert.Equal("running", running.GetProperty("state").GetString());
        Assert.Equal("att_1", running.GetProperty("attempt_id").GetString());
        Assert.Equal(256, running.GetProperty("requested_max_output_tokens").GetInt32());     // B12 on trace attempt events
        Assert.Equal(256, running.GetProperty("effective_max_output_tokens").GetInt32());

        gate.SetResult();
        using var resp = await post;
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(requestId, CorrelationTestKit.HeaderId(resp));
        var (_, done, _) = await V2Admin.SendAsync(admin, HttpMethod.Get, $"/admin/playground/submissions/{crid}");
        Assert.Equal("completed", done.GetProperty("state").GetString());

        var (missing, missingBody, _) = await V2Admin.SendAsync(admin, HttpMethod.Get, "/admin/playground/submissions/never-submitted");
        Assert.Equal(HttpStatusCode.NotFound, missing);
        Assert.Equal("not_found", missingBody.GetProperty("error").GetString());
        using var noToken = await admin.GetAsync($"/admin/playground/submissions/{crid}");
        Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);
    }

    [Fact]
    public void Client_request_id_is_required_and_bounded()
    {
        PlaygroundRequest Body(string? crid) => new()
        {
            ApplicationId = "demo-app", Messages = [new PlaygroundMessage { Role = "user", Content = "x" }], ClientRequestId = crid,
        };
        Assert.NotNull(PlaygroundApi.Validate(Body(null)));
        Assert.NotNull(PlaygroundApi.Validate(Body("")));
        Assert.NotNull(PlaygroundApi.Validate(Body(new string('a', 65))));
        Assert.NotNull(PlaygroundApi.Validate(Body("a.b")));
        Assert.Null(PlaygroundApi.Validate(Body(new string('a', 64))));
        Assert.Null(PlaygroundApi.Validate(Body("run_7f3a-B")));
    }
}

/// <summary>B8 — stable newest-first paging anchored by absolute index; rotation ⇒ 410.</summary>
public sealed class StablePaginationTests : IDisposable
{
    private readonly RestartableHybridFactory _factory = new(RestartableHybridFactory.NewAuditPath());
    private readonly HttpClient _admin;

    public StablePaginationTests() => _admin = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private async Task<string> RunAsync(string prompt)
    {
        using var resp = await Playground.PostAsync(_admin, Playground.Body("demo-app", prompt));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var id = CorrelationTestKit.HeaderId(resp);
        await CorrelationTestKit.LookupAsync(_admin, id);
        return id;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(string path)
    {
        var (status, body, _) = await V2Admin.SendAsync(_admin, HttpMethod.Get, path);
        return (status, body);
    }

    private static List<string> Ids(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("Id").GetString()!).ToList();

    [Fact]
    public async Task Insert_between_pages_causes_no_duplicates_and_no_gaps()
    {
        var r1 = await RunAsync("first internal question");
        var r2 = await RunAsync("second internal question");
        var r3 = await RunAsync("third internal question");

        var (_, page1) = await GetAsync("/admin/requests?limit=2");
        Assert.Equal(new[] { r3, r2 }, Ids(page1));
        var segment = page1.GetProperty("segment");
        Assert.Equal("current", segment.GetProperty("id").GetString());
        Assert.True(segment.GetProperty("is_current").GetBoolean());
        Assert.False(string.IsNullOrEmpty(segment.GetProperty("label").GetString()));
        var cursor = page1.GetProperty("next_cursor").GetString()!;

        var r4 = await RunAsync("a newer internal question arrives between pages");

        var (status, page2) = await GetAsync($"/admin/requests?limit=2&cursor={Uri.EscapeDataString(cursor)}");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(new[] { r1 }, Ids(page2));              // pre-fix: r2 again (offset shifted by the insert)
        Assert.Equal(JsonValueKind.Null, page2.GetProperty("next_cursor").ValueKind);
        Assert.True(page2.GetProperty("segment").GetProperty("is_current").GetBoolean());

        var (_, fresh) = await GetAsync("/admin/requests?limit=2");
        Assert.Equal(new[] { r4, r3 }, Ids(fresh));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_cursor_issued_before_a_rotation_is_410_cursor_expired_never_redirected(bool newRecordAfterRotation)
    {
        await RunAsync("first internal question");
        await RunAsync("second internal question");
        var (_, page1) = await GetAsync("/admin/requests?limit=1");
        var cursor = page1.GetProperty("next_cursor").GetString()!;

        var (resetStatus, _, _) = await V2Admin.SendAsync(_admin, HttpMethod.Post, "/admin/reset");
        Assert.Equal(HttpStatusCode.OK, resetStatus);
        if (newRecordAfterRotation) await RunAsync("after the rotation");

        var (status, body) = await GetAsync($"/admin/requests?limit=1&cursor={Uri.EscapeDataString(cursor)}");
        Assert.Equal(HttpStatusCode.Gone, status);
        Assert.Equal("cursor_expired", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Archived_segment_pages_carry_their_descriptor()
    {
        await RunAsync("first internal question");
        await RunAsync("second internal question");
        await V2Admin.SendAsync(_admin, HttpMethod.Post, "/admin/reset");
        var (_, segments) = await GetAsync("/admin/audit/segments");
        var archivedId = segments.EnumerateArray().Single(s => !s.GetProperty("current").GetBoolean()).GetProperty("id").GetString()!;
        var (_, page1) = await GetAsync($"/admin/requests?segment={archivedId}&limit=1");
        Assert.False(page1.GetProperty("segment").GetProperty("is_current").GetBoolean());
        Assert.Equal(archivedId, page1.GetProperty("segment").GetProperty("id").GetString());
        var (status, page2) = await GetAsync($"/admin/requests?cursor={Uri.EscapeDataString(page1.GetProperty("next_cursor").GetString()!)}");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Single(Ids(page2));
    }
}

/// <summary>B9 — atomic ledger bootstrap with a completion marker; B10 — replay validation.</summary>
public sealed class LedgerBootstrapAndReplayTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"covenant-ledger-r2-{Guid.NewGuid():n}.jsonl");

    public void Dispose() => CorrelationTestKit.DeleteWithSiblings(_path);

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-17T12:00:00Z");
    private static readonly (string, decimal)[] TwoTeams = [("payments", 1.5m), ("data", 0.5m)];

    private static string Line(string op, string req, string att, string scopes, string amount = "0.1", string ts = "2026-10-17T10:00:00+00:00", bool overrun = false)
        => $"{{\"op\":\"{op}\",\"request_id\":\"{req}\",\"attempt_id\":\"{att}\",\"scopes\":[{scopes}],\"amount_usd\":{amount},\"ts_utc\":\"{ts}\"{(overrun ? ",\"overrun\":true" : "")}}}";

    private const string G = "\"global\",\"team:platform\"";
    private static readonly string Marker = Line("bootstrap-complete", "bootstrap", "bootstrap-complete", "", "0");

    [Fact]
    public void Fresh_bootstrap_imports_every_team_once_atomically_with_the_marker_and_is_never_repeated()
    {
        int calls = 0;
        var opened = LedgerBootstrap.OpenOrBootstrap(new FileLedgerStore(_path), () => { calls++; return TwoTeams; }, Now);
        Assert.Empty(opened.Errors);
        Assert.False(opened.Existed);
        Assert.Equal(new[] { LedgerOps.LegacyImport, LedgerOps.LegacyImport, LedgerOps.BootstrapComplete }, opened.Records.Select(r => r.Op));
        Assert.False(File.Exists(_path + ".bootstrap.tmp"));
        var ledger = new BudgetLedger(new FileLedgerStore(_path), opened.Records, new SettableClock(Now));
        Assert.Equal(2m, ledger.Spent("global", BudgetPeriod.Monthly));

        var again = LedgerBootstrap.OpenOrBootstrap(new FileLedgerStore(_path), () => { calls++; return TwoTeams; }, Now);
        Assert.Empty(again.Errors);
        Assert.True(again.Existed);
        Assert.Equal(1, calls);                                   // the import never runs twice
        Assert.Equal(2m, new BudgetLedger(new InMemoryLedgerStore(), again.Records, new SettableClock(Now)).Spent("global", BudgetPeriod.Monthly));
    }

    [Fact]
    public void Interruption_after_create_before_import_refuses_start_and_never_restores_capacity()
    {
        File.WriteAllText(_path, "");                             // what the pre-fix EnsureCreated left behind
        Assert.NotEmpty(new FileLedgerStore(_path).Open().Errors); // pre-fix: accepted silently ⇒ legacy spend lost
        int calls = 0;
        var opened = LedgerBootstrap.OpenOrBootstrap(new FileLedgerStore(_path), () => { calls++; return TwoTeams; }, Now);
        Assert.Contains(opened.Errors, e => e.Contains("bootstrap-complete", StringComparison.Ordinal));
        Assert.Equal(0, calls);
        Assert.Equal("", File.ReadAllText(_path));                // evidence untouched
    }

    [Fact]
    public void Partway_through_a_multi_team_import_refuses_start()
    {
        var oneOfTwo = Line("legacy-import", "legacy-import", "team:payments", "\"global\",\"team:payments\"", "1.5");
        File.WriteAllText(_path, oneOfTwo + "\n");
        Assert.NotEmpty(new FileLedgerStore(_path).Open().Errors);

        // Same with the second team's line torn mid-write: never quarantine-and-continue on a half import.
        File.WriteAllText(_path, oneOfTwo + "\n{\"op\":\"legacy-import\",\"request_id\":\"legacy-imp");
        Assert.NotEmpty(new FileLedgerStore(_path).Open().Errors);
    }

    [Fact]
    public void Interruption_before_the_rename_leaves_no_ledger_and_the_next_boot_bootstraps_once()
    {
        File.WriteAllText(_path + ".bootstrap.tmp", Line("legacy-import", "legacy-import", "team:payments", "\"global\",\"team:payments\"", "1.5") + "\n");
        var opened = LedgerBootstrap.OpenOrBootstrap(new FileLedgerStore(_path), () => TwoTeams, Now);
        Assert.Empty(opened.Errors);
        Assert.Equal(2m, new BudgetLedger(new InMemoryLedgerStore(), opened.Records, new SettableClock(Now)).Spent("global", BudgetPeriod.Monthly));
    }

    [Fact]
    public void Duplicate_legacy_import_lines_are_idempotent_per_scope()
    {
        var imp = Line("legacy-import", "legacy-import", "team:payments", "\"global\",\"team:payments\"", "1.5", "2026-10-17T10:00:00+00:00");
        File.WriteAllText(_path, imp + "\n" + imp + "\n" + Marker + "\n");
        var opened = new FileLedgerStore(_path).Open();
        Assert.Empty(opened.Errors);
        var ledger = new BudgetLedger(new InMemoryLedgerStore(), opened.Records, new SettableClock(Now));
        Assert.Equal(1.5m, ledger.Spent("team:payments", BudgetPeriod.Monthly));
        Assert.Empty(ledger.ImportLegacy([("payments", 1.5m)]));  // a replayed import adds nothing
    }

    [Fact]
    public void Pre_marker_journal_with_recorded_operations_is_accepted_once_with_the_marker_appended()
    {
        File.WriteAllText(_path, Line("reserve", "r1", "att_1", G) + "\n" + Line("settle", "r1", "att_1", G) + "\n" + Line("release", "r1", "att_1", G, "0") + "\n");
        var first = new FileLedgerStore(_path).Open();
        Assert.Empty(first.Errors);
        Assert.NotEmpty(first.Warnings);
        var second = new FileLedgerStore(_path).Open();
        Assert.Empty(second.Errors);
        Assert.Empty(second.Warnings);
        Assert.Single(second.Records, r => r.Op == LedgerOps.BootstrapComplete);
    }

    public static TheoryData<string, string, string> Violations => new()
    {
        { "unknown-op", Line("refund", "r1", "att_1", G), "unknown op" },
        { "negative", Line("reserve", "r1", "att_1", G, "-1"), "negative amount" },
        { "empty-request", Line("reserve", "", "att_1", G), "empty request_id" },
        { "empty-attempt", Line("reserve", "r1", "", G), "empty attempt_id" },
        { "bad-scope", Line("reserve", "r1", "att_1", "\"teamx\""), "invalid scope" },
        { "empty-team", Line("reserve", "r1", "att_1", "\"global\",\"team:\""), "invalid scope" },
        { "no-scopes", Line("reserve", "r1", "att_1", ""), "without scopes" },
        { "min-ts", Line("reserve", "r1", "att_1", G, ts: "0001-01-01T00:00:00+00:00"), "ts_utc" },
        { "settle-unreserved", Line("settle", "r9", "att_1", G), "settle without a reservation" },
        { "release-unreserved", Line("release", "r9", "att_1", G), "release without a reservation" },
        { "recover-unreserved", Line("settle-recovered", "r9", "att_1", G), "settle-recovered without a reservation" },
        { "double-settle", Line("settle", "r0", "att_1", G), "second settle" },
        { "double-reserve", Line("reserve", "r0", "att_1", G), "second reserve" },
        { "release-after-close", Line("release", "rc", "att_1", G, "0"), "release after the attempt was closed" },
        { "import-after-marker", Line("legacy-import", "legacy-import", "team:x", "\"global\",\"team:x\"", "1"), "after the bootstrap-complete marker" },
        { "second-marker", Marker, "duplicate bootstrap-complete" },
    };

    [Theory]
    [MemberData(nameof(Violations))]
    public void A_violation_in_a_non_tail_line_refuses_start_with_a_precise_diagnostic(string name, string bad, string expected)
    {
        Assert.NotNull(name);
        var prefix = new[]
        {
            Marker,
            Line("reserve", "r0", "att_1", G), Line("settle", "r0", "att_1", G),                        // r0 settled
            Line("reserve", "rc", "att_1", G), Line("settle-recovered", "rc", "att_1", G),               // rc closed
        };
        var lines = prefix.Append(bad).Append(Line("reserve", "tail", "att_1", G)).ToArray();          // valid tail after it
        File.WriteAllText(_path, string.Join("\n", lines) + "\n");
        var opened = new FileLedgerStore(_path).Open();
        var error = Assert.Single(opened.Errors);
        Assert.Contains($"line {prefix.Length + 1} ", error, StringComparison.Ordinal);
        Assert.Contains(expected, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Non_iso_timestamp_is_a_corrupt_line()
    {
        File.WriteAllText(_path, Marker + "\n" + Line("reserve", "r1", "att_1", G, ts: "yesterday") + "\n" + Line("reserve", "r2", "att_1", G) + "\n");
        Assert.Contains(new FileLedgerStore(_path).Open().Errors, e => e.Contains("line 2 ", StringComparison.Ordinal));
    }

    [Fact]
    public void A_valid_full_lifecycle_replays_cleanly_and_an_unknown_op_never_silently_disappears_in_memory()
    {
        File.WriteAllText(_path, string.Join("\n",
            Marker,
            Line("reserve", "r1", "att_1", G), Line("settle", "r1", "att_1", G, "0.05"), Line("release", "r1", "att_1", G, "0.05"),
            Line("reserve", "r2", "att_1", G), Line("release", "r2", "att_1", G),
            Line("settle", "r3", "att_1", G, "0.2", overrun: true),
            Line("reserve", "r4", "att_1", G), Line("settle-recovered", "r4", "att_1", G)) + "\n");
        var opened = new FileLedgerStore(_path).Open();
        Assert.Empty(opened.Errors);
        Assert.Equal(0.05m + 0.2m + 0.1m, new BudgetLedger(new InMemoryLedgerStore(), opened.Records, new SettableClock(Now)).Spent("global", BudgetPeriod.None));

        var bogus = new LedgerRecord { Op = "refund", RequestId = "r", AttemptId = "a", Scopes = ["global"], AmountUsd = 1m, TsUtc = Now };
        Assert.Throws<InvalidDataException>(() => new BudgetLedger(new InMemoryLedgerStore(), [bogus]));
    }
}

/// <summary>B11 — header local health from CURRENT runtime connection health.</summary>
public sealed class LocalHealthTests
{
    [Fact]
    public async Task After_a_successful_governed_local_call_catalog_and_status_show_that_connection_model_ok_and_a_legacy_local_connection_does_not_shadow_it()
    {
        using var factory = new RestartableHybridFactory(RestartableHybridFactory.NewAuditPath());
        var admin = factory.CreateClient();

        // A stale legacy-style connection literally named "local" (pre-fix: catalog.local always picked it).
        var (version, cfg, _) = await V2Admin.GetConfigAsync(admin);
        await V2Admin.ApplyAsync(admin, cfg with
        {
            Connections = cfg.Connections.Append(new ConnectionConfig
            {
                Id = "local", DisplayName = "Legacy local", Kind = ProviderKind.Ollama, Boundary = Boundary.Local,
                Endpoint = "http://127.0.0.1:11434/v1", Enabled = true,
                Models = [new ModelConfig { ModelId = "legacy-model", Price = new PriceConfig { InputPer1M = 0m, OutputPer1M = 0m, Basis = "zero-marginal-local" } }],
            }).ToList(),
        }, version, "legacy local connection");

        using var app = factory.CreateClient();
        app.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "demo-key");
        using var resp = await app.PostAsync("/v1/chat/completions", CorrelationTestKit.Chat("an internal question"));   // Internal → ollama
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, factory.Local.Calls);

        var (_, catalog, _) = await V2Admin.SendAsync(admin, HttpMethod.Get, "/admin/catalog");
        var local = catalog.GetProperty("local");
        Assert.Equal("ollama", local.GetProperty("connection_id").GetString());
        Assert.Equal(V2.LocalTag, local.GetProperty("model_id").GetString());
        Assert.Equal("ok", local.GetProperty("health").GetProperty("state").GetString());
        var conn = catalog.GetProperty("connections").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "ollama");
        Assert.Equal("ok", conn.GetProperty("models")[0].GetProperty("health").GetProperty("state").GetString());

        var (_, status, _) = await V2Admin.SendAsync(admin, HttpMethod.Get, "/admin/status");
        var health = status.GetProperty("connection_health").EnumerateArray().ToList();
        Assert.Equal("ok", health.Single(h => h.GetProperty("key").GetString() == $"ollama:{V2.LocalTag}").GetProperty("health").GetProperty("state").GetString());
        Assert.Equal("unknown", health.Single(h => h.GetProperty("key").GetString() == "local:legacy-model").GetProperty("health").GetProperty("state").GetString());
    }
}

/// <summary>B12 — requested vs effective output bound in audit v3 and on attempts.</summary>
public sealed class OutputBoundEvidenceTests
{
    [Fact]
    public async Task Audit_records_requested_and_effective_bounds_per_attempt_and_on_the_record()
    {
        var mini = new StubChatClient("mini reply");
        var rig = V2Rig.Build(Round2.Config(tweak: c => c with
        {
            Connections = c.Connections.Select(x => x.Id != "openai" ? x : x with
            {
                Models = x.Models.Select(m => m.ModelId == V2.CloudModel ? m with { MaxOutputTokens = 64 } : m).ToList(),
            }).ToList(),
        }), new Dictionary<string, MEAI.IChatClient> { [$"openai:{V2.CloudModel}"] = mini });

        var (ctx, e) = await rig.RunAsync(V2Rig.PublicPrompt, profile: "public-demo", maxTokens: 256);
        Assert.False(ctx.IsDenied, ctx.DenialReason);
        Assert.Equal(256, e.RequestedMaxOutputTokens);
        Assert.Equal(64, e.EffectiveMaxOutputTokens);
        Assert.Equal(64, Assert.Single(e.Attempts!).EffectiveMaxOutputTokens);
        Assert.Equal(64, mini.LastOptions?.MaxOutputTokens);
        Assert.Contains(ctx.TraceEvents, t => t.Stage == "provider" && t.AttemptId == "att_1" && t.RequestedMaxOutputTokens == 256 && t.EffectiveMaxOutputTokens == 64);

        var (_, noMax) = await rig.RunAsync(V2Rig.PublicPrompt + " again", profile: "public-demo");
        Assert.Null(noMax.RequestedMaxOutputTokens);
        Assert.Equal(64, noMax.EffectiveMaxOutputTokens);
    }
}

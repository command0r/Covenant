using System.Net;
using Covenant.Core;
using Covenant.Governance;
using Covenant.Host;
using Xunit;
using MEAI = Microsoft.Extensions.AI;

namespace Covenant.Tests;

/// <summary>ADR-0013 bounded approved fallback through the REAL stages (stub IChatClients, never a paid provider):
/// fallback only for the policy's retry classes, within maxAttempts and deadlineMs, never after content was emitted,
/// never on caller cancellation, pause or non-retryable errors; every attempt evidenced and charged (ADR-0014).</summary>
public sealed class FallbackTests
{
    private readonly StubChatClient _mini = new("mini reply");
    private readonly StubChatClient _strong = new("strong reply");
    private readonly StubChatClient _local = new("local reply");

    private V2Rig Rig(RetrySettings? retry = null, Func<CovenantConfig, CovenantConfig>? tweak = null)
        => V2Rig.Build(V2Rig.FallbackConfig(retry, tweak: tweak), new Dictionary<string, MEAI.IChatClient>
        {
            [$"openai:{V2.CloudModel}"] = _mini,
            ["openai:gpt-4o"] = _strong,
            [$"ollama:{V2.LocalTag}"] = _local,
        });

    public static TheoryData<string> RetryableFailures => new() { "429", "timeout", "5xx-500", "5xx-502", "5xx-503", "5xx-504", "connect" };

    private static Exception Make(string kind) => kind switch
    {
        "429" => Failures.Status(HttpStatusCode.TooManyRequests),
        "timeout" => Failures.ProviderTimeout(),
        "5xx-500" => Failures.Status(HttpStatusCode.InternalServerError),
        "5xx-502" => Failures.Status(HttpStatusCode.BadGateway),
        "5xx-503" => Failures.Status(HttpStatusCode.ServiceUnavailable),
        "5xx-504" => Failures.Status(HttpStatusCode.GatewayTimeout),
        "connect" => Failures.Connect(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    [Theory]
    [MemberData(nameof(RetryableFailures))]
    public async Task Retryable_failure_before_output_falls_back_to_the_next_approved_target(string kind)
    {
        _mini.Then(StubChatClient.Mode.Throw, () => Make(kind));
        var rig = Rig();
        var (ctx, e) = await rig.RunAsync(V2Rig.PublicPrompt, profile: "public-demo");

        Assert.False(ctx.IsDenied, ctx.DenialReason);
        Assert.Equal(1, _mini.Calls);
        Assert.Equal(1, _strong.Calls);
        Assert.Equal("strong reply", ctx.Response!.Message.Content);
        Assert.Equal("gpt-4o", e.ServedByModel);
        Assert.Equal(V2.CloudModel, e.SelectedModel);              // policy's primary
        Assert.Equal("gpt-4o", e.AttemptedModel);                  // last attempt started

        var attempts = Assert.IsAssignableFrom<IReadOnlyList<AuditAttempt>>(e.Attempts);
        Assert.Equal(2, attempts.Count);
        Assert.Equal(("att_1", "openai", V2.CloudModel, "failed"), (attempts[0].AttemptId, attempts[0].ConnectionId, attempts[0].ModelId, attempts[0].Outcome));
        Assert.Equal(kind.StartsWith("5xx", StringComparison.Ordinal) ? "5xx" : kind, attempts[0].ErrorClass);
        Assert.Equal(("att_2", "gpt-4o", "served"), (attempts[1].AttemptId, attempts[1].ModelId, attempts[1].Outcome));

        // Both attempts charged: the failed one at its reservation-bound estimate (possibly billed, never $0).
        Assert.Equal("estimated_failed_call", attempts[0].CostBasis);
        Assert.True(attempts[0].CostUsd > 0m);
        Assert.Equal(attempts[0].ReservedUsd, attempts[0].CostUsd);
        Assert.Equal("reported", attempts[1].CostBasis);
        Assert.Equal(attempts[0].CostUsd + attempts[1].CostUsd, e.SettledUsd);
        Assert.Equal(e.SettledUsd, e.Usage.CostUsd);               // the request's charge includes the failed attempt
        Assert.Equal(e.SettledUsd, rig.Ledger.Spent("global", BudgetPeriod.Monthly));
        Assert.Equal(0, rig.Ledger.OpenReservations);
        Assert.Equal(0m, rig.Ledger.Reserved("global"));

        // Observed health per connection:model from the real attempts.
        Assert.Equal("failed", rig.Health.Get($"openai:{V2.CloudModel}").State);
        Assert.Equal("ok", rig.Health.Get("openai:gpt-4o").State);

        // Trace: two provider attempts; the fallback's pause + budget re-checks are recorded with its attempt id.
        Assert.Contains(ctx.TraceEvents, t => t.Stage == "provider" && t.AttemptId == "att_1" && t.State == TraceStates.Failed);
        Assert.Contains(ctx.TraceEvents, t => t.Stage == "pause" && t.AttemptId == "att_2" && t.State == TraceStates.Passed);
        Assert.Contains(ctx.TraceEvents, t => t.Stage == "budget" && t.AttemptId == "att_2" && t.State == TraceStates.Passed);
        Assert.Contains(ctx.TraceEvents, t => t.Stage == "provider" && t.AttemptId == "att_2" && t.State == TraceStates.Passed);
    }

    public static TheoryData<int> NonRetryableStatuses => new() { 400, 401, 403, 404, 422 };

    [Theory]
    [MemberData(nameof(NonRetryableStatuses))]
    public async Task Non_retryable_provider_error_never_falls_back(int status)
    {
        _mini.Then(StubChatClient.Mode.Throw, () => Failures.Status((HttpStatusCode)status));
        var rig = Rig();
        var (ctx, e) = await rig.RunAsync(V2Rig.PublicPrompt, profile: "public-demo");

        Assert.Equal(DenialKind.UpstreamFailure, ctx.DenialKind);
        Assert.Equal(0, _strong.Calls + _local.Calls);
        var a = Assert.Single(e.Attempts!);
        Assert.Equal("non_retryable", a.ErrorClass);
        Assert.Equal("estimated_failed_call", e.CostBasis);
        Assert.True(e.Usage.CostUsd > 0m);
    }

    [Fact]
    public async Task Failure_class_not_in_retry_on_is_terminal()
    {
        _mini.Then(StubChatClient.Mode.Throw, () => Failures.Status(HttpStatusCode.TooManyRequests));
        var rig = Rig(new RetrySettings { MaxAttempts = 3, DeadlineMs = 60_000, On = ["5xx"] });
        var (ctx, e) = await rig.RunAsync(V2Rig.PublicPrompt, profile: "public-demo");
        Assert.Equal(DenialKind.UpstreamFailure, ctx.DenialKind);
        Assert.Equal("429", Assert.Single(e.Attempts!).ErrorClass);
        Assert.Equal(0, _strong.Calls);
    }

    [Fact]
    public async Task Max_attempts_is_respected()
    {
        var fail = () => Failures.Status(HttpStatusCode.ServiceUnavailable);
        _mini.Then(StubChatClient.Mode.Throw, fail);
        _strong.Then(StubChatClient.Mode.Throw, fail);
        var rig = Rig(new RetrySettings { MaxAttempts = 2, DeadlineMs = 60_000 });
        var (ctx, e) = await rig.RunAsync(V2Rig.PublicPrompt, profile: "public-demo");

        Assert.Equal(DenialKind.UpstreamFailure, ctx.DenialKind);
        Assert.Equal(2, e.Attempts!.Count);
        Assert.Equal(0, _local.Calls);                              // a third approved target exists but maxAttempts = 2
        Assert.All(e.Attempts!, a => Assert.True(a.CostUsd > 0m));
        Assert.Equal(e.Attempts!.Sum(a => a.CostUsd), e.SettledUsd);
    }

    [Fact]
    public async Task Deadline_cancels_a_hanging_attempt_as_timeout_and_starts_no_further_attempt()
    {
        _mini.Then(StubChatClient.Mode.Hang);
        var rig = Rig(new RetrySettings { MaxAttempts = 3, DeadlineMs = 50 });   // pure rig: below the validator minimum on purpose
        var (ctx, e) = await rig.RunAsync(V2Rig.PublicPrompt, profile: "public-demo");

        Assert.Equal(DenialKind.UpstreamFailure, ctx.DenialKind);
        var a = Assert.Single(e.Attempts!);
        Assert.Equal("timeout", a.ErrorClass);
        Assert.Equal(0, _strong.Calls + _local.Calls);
    }

    [Fact]
    public async Task Streaming_failure_after_the_first_chunk_never_continues_on_a_second_provider()
    {
        _mini.Then(StubChatClient.Mode.StreamThenThrow);
        var rig = Rig();
        var (ctx, e) = await rig.RunAsync(V2Rig.PublicPrompt, profile: "public-demo", stream: true);

        Assert.Equal(DenialKind.UpstreamFailure, ctx.DenialKind);
        Assert.Equal(0, _strong.Calls + _local.Calls);              // 503 is retryable, but content was already emitted
        Assert.Equal("partial ".Length, ctx.PartialOutputChars);   // partial output preserved as a fact
        var a = Assert.Single(e.Attempts!);
        Assert.Equal("failed", a.Outcome);
        Assert.Equal("5xx", a.ErrorClass);
        Assert.Equal(PolicyEffect.Deny, e.Effect);
        Assert.Equal("estimated_failed_call", e.CostBasis);
    }

    [Fact]
    public async Task Streaming_failure_before_any_chunk_may_fall_back()
    {
        _mini.Then(StubChatClient.Mode.Throw, () => Failures.Status(HttpStatusCode.BadGateway));
        var rig = Rig();
        var (ctx, e) = await rig.RunAsync(V2Rig.PublicPrompt, profile: "public-demo", stream: true);
        Assert.False(ctx.IsDenied, ctx.DenialReason);
        Assert.Equal("gpt-4o", e.ServedByModel);
        Assert.Equal(2, e.Attempts!.Count);
    }

    [Fact]
    public async Task Pause_tripped_between_attempts_refuses_the_second_attempt_at_pause()
    {
        var rig = Rig();
        _mini.Then(StubChatClient.Mode.Throw, () => Failures.Status(HttpStatusCode.ServiceUnavailable));
        _mini.OnCall = () => rig.Kill.Trip("operator pause mid-request");
        var (ctx, e) = await rig.RunAsync(V2Rig.PublicPrompt, profile: "public-demo");

        Assert.True(ctx.IsDenied);
        Assert.Equal("pause", e.StoppedAtStage);
        Assert.Equal("Governance", e.DenialKind);
        Assert.Contains("kill switch engaged", e.Reason, StringComparison.Ordinal);
        Assert.Equal(0, _strong.Calls);
        Assert.Single(e.Attempts!);                                 // the refused fallback never started
        Assert.Contains(ctx.TraceEvents, t => t.Stage == "pause" && t.AttemptId == "att_2" && t.State == TraceStates.Refused);
        Assert.True(e.Usage.CostUsd > 0m);                          // the failed first attempt is still charged
        Assert.Equal(0, rig.Ledger.OpenReservations);
    }

    [Fact]
    public async Task Pause_before_the_request_refuses_with_no_attempt()
    {
        var rig = Rig();
        rig.Kill.Trip("maintenance");
        var (_, e) = await rig.RunAsync(V2Rig.PublicPrompt, profile: "public-demo");
        Assert.Equal("pause", e.StoppedAtStage);
        Assert.Empty(e.Attempts!);
        Assert.Equal(0, _mini.Calls + _strong.Calls + _local.Calls);
    }

    [Fact]
    public async Task Policy_denial_makes_no_attempt()
    {
        var rig = Rig();
        var (_, e) = await rig.RunAsync(V2Rig.PublicPrompt, profile: "public-demo", model: "openai/not-a-target");
        Assert.Equal("policy", e.StoppedAtStage);
        Assert.Empty(e.Attempts!);
        Assert.Equal(0, _mini.Calls + _strong.Calls + _local.Calls);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_a_provider_failure_and_never_falls_back()
    {
        _mini.Then(StubChatClient.Mode.Hang);
        var rig = Rig();
        using var cts = new CancellationTokenSource();
        var run = rig.RunAsync(V2Rig.PublicPrompt, profile: "public-demo", ct: cts.Token);
        await _mini.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        var (ctx, e) = await run;

        Assert.Equal(DenialKind.Cancelled, ctx.DenialKind);
        Assert.Equal("Cancelled", e.DenialKind);
        Assert.Equal(0, _strong.Calls + _local.Calls);
        var a = Assert.Single(e.Attempts!);
        Assert.Equal("cancelled", a.Outcome);
        Assert.Equal("client_cancelled", a.ErrorClass);
        Assert.NotEqual("failed", rig.Health.Get($"openai:{V2.CloudModel}").State);   // no health penalty for the provider
        // Cancellation settles the (possibly billed) attempt at its estimate and releases the reservation.
        Assert.Equal(a.ReservedUsd, a.CostUsd);
        Assert.Equal(0, rig.Ledger.OpenReservations);
        Assert.Equal(0m, rig.Ledger.Reserved("global"));
        Assert.Equal(a.CostUsd, rig.Ledger.Spent("global", BudgetPeriod.Monthly));
    }

    [Fact]
    public async Task Model_override_pins_one_target_with_no_fallback()
    {
        _mini.Then(StubChatClient.Mode.Throw, () => Failures.Status(HttpStatusCode.ServiceUnavailable));
        var rig = Rig();
        var (ctx, e) = await rig.RunAsync(V2Rig.PublicPrompt, profile: "public-demo", model: $"openai/{V2.CloudModel}");
        Assert.Equal(DenialKind.UpstreamFailure, ctx.DenialKind);
        Assert.Single(e.Attempts!);
        Assert.Equal(0, _strong.Calls + _local.Calls);
    }

    [Fact]
    public async Task Sensitive_request_whose_local_primary_fails_never_reaches_a_cloud_target()
    {
        // Even a (validation-invalid) sensitive policy that lists a cloud fallback cannot make it eligible: the engine
        // excludes cloud destinations for PII/PHI, so the failing local primary is terminal.
        _local.Then(StubChatClient.Mode.Throw, () => Failures.Status(HttpStatusCode.ServiceUnavailable));
        var rig = Rig(tweak: c => c with
        {
            Policies = c.Policies.Select(p => p.Id != "sensitive" ? p : p with
            {
                Targets = [V2.T("ollama", V2.LocalTag), V2.T("openai", V2.CloudModel), V2.T("openai", "gpt-4o")],
                Retry = new RetrySettings { MaxAttempts = 3, DeadlineMs = 60_000 },
            }).ToList(),
        });
        var (ctx, e) = await rig.RunAsync("Customer SSN 000-12-3456 needs a summary.", profile: "public-demo");

        Assert.Equal(DataClassification.Pii, e.Classification);
        Assert.Equal(DenialKind.UpstreamFailure, ctx.DenialKind);
        Assert.Equal(1, _local.Calls);
        Assert.Equal(0, _mini.Calls + _strong.Calls);
        Assert.Equal("ollama", Assert.Single(e.Attempts!).ConnectionId);
        Assert.All(ctx.Policy!.Candidates, c => Assert.Equal("ollama", c.AdapterKey));
    }

    [Fact]
    public async Task The_enforced_output_bound_is_sent_to_the_provider()
    {
        var rig = Rig();
        await rig.RunAsync(V2Rig.PublicPrompt, profile: "public-demo");
        Assert.Equal(RequestBounds.DefaultOutputTokens, _mini.LastOptions?.MaxOutputTokens);   // application default 1024

        await rig.RunAsync(V2Rig.PublicPrompt + " again", profile: "public-demo", maxTokens: 64);
        Assert.Equal(64, _mini.LastOptions?.MaxOutputTokens);                                  // min(request, application)

        await rig.RunAsync(V2Rig.PublicPrompt + " once more", profile: "public-demo", maxTokens: 100_000);
        Assert.Equal(RequestBounds.DefaultOutputTokens, _mini.LastOptions?.MaxOutputTokens);   // capped by the application
    }
}

/// <summary>SDK-aware failure classification (ADR-0013) — BCL shapes the stub providers raise.</summary>
public sealed class ProviderFailureClassifierTests
{
    [Theory]
    [InlineData(429, "429")]
    [InlineData(500, "5xx")]
    [InlineData(502, "5xx")]
    [InlineData(503, "5xx")]
    [InlineData(504, "5xx")]
    [InlineData(501, "non_retryable")]
    [InlineData(400, "non_retryable")]
    [InlineData(401, "non_retryable")]
    [InlineData(404, "non_retryable")]
    public void Http_status_maps_to_the_bounded_class(int status, string expected)
        => Assert.Equal(expected, SdkProviderFailureClassifier.Shared.Classify(Failures.Status((HttpStatusCode)status)));

    [Fact]
    public void Transport_and_timeout_failures_are_classified()
    {
        var c = SdkProviderFailureClassifier.Shared;
        Assert.Equal("connect", c.Classify(Failures.Connect()));
        Assert.Equal("connect", c.Classify(new System.Net.Sockets.SocketException(10061)));
        Assert.Equal("timeout", c.Classify(new TimeoutException()));
        Assert.Equal("timeout", c.Classify(Failures.ProviderTimeout()));
        Assert.Equal("5xx", c.Classify(new InvalidOperationException("wrapped", Failures.Status(HttpStatusCode.BadGateway))));
        Assert.Equal("connect", c.Classify(new System.ClientModel.ClientResultException("no response", null, null)));
        Assert.Equal("non_retryable", c.Classify(new InvalidOperationException("anything else")));
    }
}

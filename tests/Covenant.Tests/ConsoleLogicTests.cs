using Covenant.Core;
using Covenant.Governance;
using Covenant.Ui.Logic;
using Covenant.Ui.Models;
using Xunit;

namespace Covenant.Tests;

// Console view-model logic (src/Covenant.Ui/Logic, linked into this project — see Covenant.Tests.csproj).
// These decide what the operator is TOLD happened, so every branch is pinned: transport vs final outcome,
// pause vs policy vs missing adapter (from the recorded stage, never the reason text), "Not evaluated" vs a
// detection, unknown cost never rendered as $0, and frozen request snapshots.

internal static class Rec
{
    public static AuditEntryView V2(
        string effect = "Allow",
        string? denialKind = null,
        string? stage = null,
        string? selected = "gpt-4o-mini",
        string? attempted = "gpt-4o-mini",
        string? served = "gpt-4o-mini",
        bool cacheHit = false,
        bool? classificationEvaluated = true,
        string classification = "Internal",
        string? signal = null,
        string? costBasis = "reported",
        decimal cost = 0.0012m,
        bool? usageReported = true,
        long inTok = 10, long outTok = 20,
        string id = "req_0123456789abcdef0123456789abcdef",
        string team = "payments",
        DateTimeOffset? at = null,
        string reason = "allowed") => new()
    {
        Id = id,
        CorrelationId = id,
        TimestampUtc = at ?? new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero),
        Principal = "svc-" + team,
        Tags = new AttributionTagsView { Team = team, Workflow = "playground", UseCase = "console-test" },
        Classification = classification,
        Effect = effect,
        Reason = reason,
        ServedByModel = served,
        Usage = new UsageView { InputTokens = inTok, OutputTokens = outTok, TotalTokens = inTok + outTok, CostUsd = cost },
        DurationMs = 123,
        PromptChars = 42,
        PromptSha256 = new string('a', 64),
        Signal = signal,
        CacheHit = cacheHit,
        SchemaVersion = 2,
        ClassificationEvaluated = classificationEvaluated,
        DenialKind = denialKind,
        StoppedAtStage = stage,
        SelectedModel = selected,
        AttemptedModel = attempted,
        Stream = false,
        UsageReported = usageReported,
        CostBasis = costBasis,
    };

    public static AuditEntryView Pause() => V2("Deny", "Governance", "pause", attempted: null, served: null,
        costBasis: "no_call", cost: 0m, usageReported: null, inTok: 0, outTok: 0, reason: "kill switch engaged: demo");

    public static AuditEntryView PolicyDeny() => V2("Deny", "Governance", "policy", selected: null, attempted: null, served: null,
        costBasis: "no_call", cost: 0m, usageReported: null, inTok: 0, outTok: 0,
        reason: "model 'gpt-4-unapproved-test' not permitted for classification 'Internal'");

    public static AuditEntryView MissingAdapter() => V2("Deny", "Governance", "provider", selected: "qwen3:8b", attempted: null, served: null,
        classification: "Pii", signal: "US SSN pattern", costBasis: "no_call", cost: 0m, usageReported: null, inTok: 0, outTok: 0,
        reason: "no adapter registered for 'local:qwen3:8b'");

    public static AuditEntryView AuthFail() => V2("Deny", "Unauthenticated", "auth", selected: null, attempted: null, served: null,
        classificationEvaluated: false, classification: "Phi", costBasis: "no_call", cost: 0m, usageReported: null, inTok: 0, outTok: 0,
        reason: "authentication required: no API key presented");

    public static AuditEntryView Upstream() => V2("Deny", "UpstreamFailure", "provider", served: null,
        costBasis: "estimated_failed_call", cost: 0.0004m, usageReported: false, inTok: 0, outTok: 0,
        reason: "provider 'openai' stream failed: boom");

    /// <summary>A pre-v2 line: none of the v2 fields exist.</summary>
    public static AuditEntryView Legacy(string effect = "Allow") => new()
    {
        Id = "a1b2c3",
        TimestampUtc = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero),
        Principal = "svc-legacy",
        Tags = new AttributionTagsView { Team = "legacy" },
        Classification = "Phi",
        Effect = effect,
        Reason = effect == "Allow" ? "allowed" : "denied",
        ServedByModel = effect == "Allow" ? "gpt-4o-mini" : null,
        Usage = new UsageView { InputTokens = 5, OutputTokens = 7, TotalTokens = 12, CostUsd = 0.0001m },
    };
}

public sealed class OutcomeModelTests
{
    [Fact]
    public void Served_record_is_served()
    {
        var v = OutcomeModel.Compute(new OutcomeInput(200, Record: Rec.V2()));
        Assert.Equal(DisplayOutcome.Served, v.Outcome);
        Assert.Equal("served", v.PillKind);
        Assert.False(v.TransportDiffers);
        Assert.Equal("HTTP 200", v.StatusLine);
    }

    [Fact]
    public void Http_200_before_the_record_is_served_but_awaiting_evidence()
    {
        var v = OutcomeModel.Compute(new OutcomeInput(200));
        Assert.Equal(DisplayOutcome.Served, v.Outcome);
        Assert.True(v.AwaitingRecord);
    }

    [Fact]
    public void Cache_hit_is_served_from_cache()
    {
        var v = OutcomeModel.FromRecord(Rec.V2(cacheHit: true, attempted: null, costBasis: "no_call"));
        Assert.Equal(DisplayOutcome.ServedFromCache, v.Outcome);
        Assert.Equal("served", v.PillKind);
    }

    [Fact]
    public void Mid_stream_failure_is_transport_200_but_failed_before_the_record()
    {
        var v = OutcomeModel.Compute(new OutcomeInput(200, "upstream_error", StreamTerminatedWithError: true));
        Assert.Equal(DisplayOutcome.ProviderFailedMidStream, v.Outcome);
        Assert.True(v.TransportDiffers);
        Assert.Equal("Transport 200, final outcome: failed", v.StatusLine);
        Assert.Equal("failed", v.PillKind);
    }

    [Fact]
    public void Mid_stream_failure_with_its_record_stays_failed_and_differs()
    {
        var v = OutcomeModel.Compute(new OutcomeInput(200, "upstream_error", true, Record: Rec.Upstream()));
        Assert.Equal(DisplayOutcome.ProviderFailedMidStream, v.Outcome);
        Assert.True(v.TransportDiffers);
        Assert.Equal("Transport 200, final outcome: failed", v.StatusLine);
    }

    [Fact]
    public void Upstream_failure_without_a_committed_stream_is_provider_unavailable()
    {
        Assert.Equal(DisplayOutcome.ProviderUnavailable, OutcomeModel.Compute(new OutcomeInput(502, "upstream_error", Record: Rec.Upstream())).Outcome);
        Assert.Equal(DisplayOutcome.ProviderUnavailable, OutcomeModel.Compute(new OutcomeInput(502, "upstream_error")).Outcome);
        Assert.Equal(DisplayOutcome.ProviderUnavailable, OutcomeModel.FromRecord(Rec.Upstream()).Outcome);
    }

    [Fact]
    public void Auth_failure_before_and_after_the_record()
    {
        var before = OutcomeModel.Compute(new OutcomeInput(401, "unauthenticated"));
        Assert.Equal(DisplayOutcome.AuthenticationFailed, before.Outcome);
        Assert.Contains("not evaluated", before.Explanation, StringComparison.OrdinalIgnoreCase);

        var after = OutcomeModel.Compute(new OutcomeInput(401, "unauthenticated", Record: Rec.AuthFail()));
        Assert.Equal(DisplayOutcome.AuthenticationFailed, after.Outcome);
        Assert.Equal("refused", after.PillKind);
    }

    [Fact]
    public void Auth_failure_classification_is_not_evaluated_never_the_restrictive_default()
    {
        var text = ModelFacts.Classification(Rec.AuthFail());
        Assert.Equal("Not evaluated — stopped at authentication", text);
        Assert.DoesNotContain("Phi", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Http_403_before_the_record_does_not_guess_the_stage()
    {
        var v = OutcomeModel.Compute(new OutcomeInput(403, "denied"));
        Assert.Equal(DisplayOutcome.Unknown, v.Outcome);
        Assert.Equal("Refused (HTTP 403) — awaiting evidence for the stage", v.Title);
        Assert.True(v.AwaitingRecord);
        Assert.Equal("refused", v.PillKind);
    }

    [Fact]
    public void Pause_vs_policy_vs_missing_adapter_come_from_the_recorded_stage()
    {
        Assert.Equal(DisplayOutcome.Paused, OutcomeModel.FromRecord(Rec.Pause()).Outcome);
        Assert.Equal("paused", OutcomeModel.FromRecord(Rec.Pause()).PillKind);
        Assert.Equal(DisplayOutcome.PolicyDenied, OutcomeModel.FromRecord(Rec.PolicyDeny()).Outcome);

        var missing = OutcomeModel.FromRecord(Rec.MissingAdapter());
        Assert.Equal(DisplayOutcome.ProviderUnavailable, missing.Outcome);
        Assert.Contains("no registered adapter", missing.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Pause_is_never_inferred_from_the_reason_text()
    {
        var looksPaused = Rec.V2("Deny", "Governance", "policy", attempted: null, served: null, costBasis: "no_call",
            reason: "kill switch engaged: demo");
        Assert.Equal(DisplayOutcome.PolicyDenied, OutcomeModel.FromRecord(looksPaused).Outcome);

        var paused = Rec.V2("Deny", "Governance", "pause", attempted: null, served: null, costBasis: "no_call", reason: "anything");
        Assert.Equal(DisplayOutcome.Paused, OutcomeModel.FromRecord(paused).Outcome);
    }

    [Fact]
    public void Provider_stage_with_an_attempted_model_is_not_a_missing_adapter()
    {
        var r = Rec.V2("Deny", "Governance", "provider", served: null, costBasis: "unknown_failed_call");
        var v = OutcomeModel.FromRecord(r);
        Assert.NotEqual(DisplayOutcome.ProviderUnavailable, v.Outcome);
        Assert.DoesNotContain("no registered adapter", v.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Budget_refusal_is_a_governance_denial()
    {
        var v = OutcomeModel.FromRecord(Rec.V2("Deny", "Governance", "budget", attempted: null, served: null, costBasis: "no_call",
            reason: "global budget exhausted (cap 5 USD)"));
        Assert.Equal(DisplayOutcome.PolicyDenied, v.Outcome);
        Assert.Contains("budget", v.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rate_limited_unsupported_and_rejected_before_governance()
    {
        Assert.Equal(DisplayOutcome.RateLimited, OutcomeModel.Compute(new OutcomeInput(429, "rate_limited")).Outcome);
        Assert.Equal(DisplayOutcome.RateLimited, OutcomeModel.FromRecord(Rec.V2("Deny", "RateLimited", "rate_limit", attempted: null, served: null)).Outcome);

        Assert.Equal(DisplayOutcome.Unsupported, OutcomeModel.Compute(new OutcomeInput(400, "unsupported")).Outcome);
        Assert.Equal(DisplayOutcome.Unsupported, OutcomeModel.FromRecord(Rec.V2("Deny", "Unsupported", "shape", attempted: null, served: null)).Outcome);

        var binder = OutcomeModel.Compute(new OutcomeInput(400));
        Assert.Equal(DisplayOutcome.RejectedBeforeGovernance, binder.Outcome);
        Assert.Contains("no evidence record", binder.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Cancellation_is_stopped_waiting_never_a_rollback()
    {
        var client = OutcomeModel.Compute(new OutcomeInput(Cancelled: true));
        Assert.Equal(DisplayOutcome.Cancelled, client.Outcome);
        Assert.Contains("not a rollback", client.Explanation, StringComparison.Ordinal);
        Assert.Contains("may have completed or been charged", client.Explanation, StringComparison.Ordinal);

        Assert.Equal(DisplayOutcome.Cancelled, OutcomeModel.Compute(new OutcomeInput(499, "cancelled")).Outcome);
        Assert.Equal(DisplayOutcome.Cancelled, OutcomeModel.FromRecord(Rec.V2("Deny", "Cancelled", "provider", served: null,
            costBasis: "estimated_failed_call")).Outcome);
    }

    [Fact]
    public void Cancelled_stream_after_200_is_transport_200_final_incomplete()
    {
        var v = OutcomeModel.Compute(new OutcomeInput(200, Cancelled: true));
        Assert.Equal(DisplayOutcome.Cancelled, v.Outcome);
        Assert.True(v.TransportDiffers);
        Assert.Equal("Transport 200, final outcome: incomplete", v.StatusLine);
    }

    [Fact]
    public void Record_wins_over_client_cancel_when_the_server_completed()
    {
        var v = OutcomeModel.Compute(new OutcomeInput(Cancelled: true, Record: Rec.V2()));
        Assert.Equal(DisplayOutcome.Served, v.Outcome);
        Assert.Contains("stopped waiting", v.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_response_and_unknown_effect_are_unknown()
    {
        var net = OutcomeModel.Compute(new OutcomeInput());
        Assert.Equal(DisplayOutcome.Unknown, net.Outcome);
        Assert.Equal("No HTTP response", net.StatusLine);
        Assert.Equal("unknown", net.PillKind);

        Assert.Equal(DisplayOutcome.Unknown, OutcomeModel.FromRecord(Rec.V2(effect: "Maybe")).Outcome);
    }

    [Fact]
    public void Legacy_denial_without_kind_or_stage_is_not_attributed_to_a_stage()
    {
        var v = OutcomeModel.FromRecord(Rec.Legacy("Deny"));
        Assert.Equal(DisplayOutcome.Unknown, v.Outcome);
        Assert.Equal("Refused (legacy record)", v.Title);
        Assert.Equal("refused", v.PillKind);
    }

    [Fact]
    public void Pipeline_stage_rows_map_pause_and_budget_to_one_row()
    {
        Assert.Equal("pause_budget", PipelineStages.RowKey("pause"));
        Assert.Equal("pause_budget", PipelineStages.RowKey("budget"));
        Assert.Equal("auth", PipelineStages.RowKey("auth"));
        Assert.Null(PipelineStages.RowKey(null));
        Assert.Contains(PipelineStages.Configured, s => s.Key == "pause_budget");
        Assert.Equal(9, PipelineStages.Configured.Count);
    }
}

public sealed class ModelFactsTests
{
    [Fact]
    public void Selected_before_a_pause_is_not_an_attempted_model()
    {
        var f = ModelFacts.From(Rec.Pause());
        Assert.Equal("gpt-4o-mini", f.Selected);
        Assert.Equal("None — no provider call", f.Attempted);
        Assert.Equal("None — not served", f.Served);
    }

    [Fact]
    public void Cache_hit_shows_served_from_cache_and_no_attempt()
    {
        var f = ModelFacts.From(Rec.V2(cacheHit: true, attempted: null, costBasis: "no_call"));
        Assert.StartsWith("Served from cache (no provider call)", f.Served, StringComparison.Ordinal);
        Assert.Equal("None — no provider call", f.Attempted);
    }

    [Fact]
    public void Legacy_record_nulls_are_not_recorded()
    {
        var f = ModelFacts.From(Rec.Legacy());
        Assert.Equal("Not recorded", f.Selected);
        Assert.Equal("Not recorded", f.Attempted);
        Assert.Equal("gpt-4o-mini", f.Served);
        Assert.Equal("Not recorded", f.Classification);
        Assert.Equal("Not recorded", ModelFacts.Served(NoServed(Rec.Legacy())));
    }

    private static AuditEntryView NoServed(AuditEntryView r)
    {
        r.ServedByModel = null;
        return r;
    }

    [Fact]
    public void Evaluated_classification_shows_the_signal_when_present()
    {
        Assert.Equal("Pii · US SSN pattern", ModelFacts.Classification(Rec.V2(classification: "Pii", signal: "US SSN pattern")));
        Assert.Equal("Internal · no rule matched", ModelFacts.Classification(Rec.V2()));
    }

    [Fact]
    public void Selected_to_served_cell()
    {
        Assert.Equal("gpt-4o-mini", ModelFacts.SelectedToServed(Rec.V2()));
        Assert.Equal("gpt-4o-mini → not served", ModelFacts.SelectedToServed(Rec.Pause()));
        Assert.Equal("gpt-4o-mini → cache", ModelFacts.SelectedToServed(Rec.V2(cacheHit: true)));
    }
}

public sealed class CostTextTests
{
    [Fact]
    public void Each_cost_basis_has_its_own_wording()
    {
        Assert.Equal("$0.0012 (estimated provider charge)", CostText.Cost(Rec.V2(costBasis: "reported", cost: 0.0012m)));
        Assert.Equal("$0.00 configured provider charge · infrastructure excluded", CostText.Cost(Rec.V2(costBasis: "zero_priced", cost: 0m)));
        Assert.Equal("≈ $0.0030 estimated from text length", CostText.Cost(Rec.V2(costBasis: "estimated_from_text", cost: 0.003m)));
        Assert.Equal("≈ $0.0004 estimated from text length", CostText.Cost(Rec.Upstream()));
        Assert.Equal("$0.00 — no provider call", CostText.Cost(Rec.Pause()));
        Assert.Equal("$0.0001 (legacy record)", CostText.Cost(Rec.Legacy()));
    }

    [Theory]
    [InlineData("unknown_failed_call")]
    [InlineData("unpriced")]
    [InlineData("something_new")]
    public void Unknown_cost_is_never_rendered_as_zero(string basis)
    {
        var r = Rec.V2(costBasis: basis, cost: 0m);
        Assert.Equal("Not reported", CostText.Cost(r));
        Assert.Equal("Not reported", CostText.CostShort(r));
        Assert.DoesNotContain("$0", CostText.Cost(r), StringComparison.Ordinal);
    }

    [Fact]
    public void Tokens_are_not_reported_when_usage_was_not_reported()
    {
        Assert.Equal("Not reported", CostText.Tokens(Rec.V2(usageReported: false, inTok: 0, outTok: 0)));
        Assert.Equal("Not reported", CostText.Tokens(Rec.V2(usageReported: null, inTok: 0, outTok: 0)));
        Assert.Equal("10 in · 20 out · 30 total", CostText.Tokens(Rec.V2()));
    }

    [Fact]
    public void Tokens_without_a_provider_call_or_legacy()
    {
        Assert.Equal("None — no provider call", CostText.Tokens(Rec.Pause()));
        Assert.Equal("None — served from cache", CostText.Tokens(Rec.V2(cacheHit: true, attempted: null)));
        Assert.Equal("5 in · 7 out · 12 total (legacy record)", CostText.Tokens(Rec.Legacy()));
    }
}

public sealed class RequestFilterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 30, 0, TimeSpan.Zero);

    private static List<AuditEntryView> Items() =>
    [
        Rec.V2(id: "req_aaaa1111", team: "payments", at: Now.AddMinutes(-1)),
        Rec.V2("Deny", "Governance", "pause", attempted: null, served: null, costBasis: "no_call", id: "req_bbbb2222", team: "risk", at: Now.AddMinutes(-5)),
        Rec.V2(selected: "local-qwen", attempted: "local-qwen", served: "local-qwen", id: "req_cccc3333", team: "risk", at: Now.AddMinutes(-30)),
        Rec.V2("Deny", "Unauthenticated", "auth", selected: null, attempted: null, served: null, classificationEvaluated: false,
            id: "req_dddd4444", team: "unknown", at: Now.AddHours(-3)),
    ];

    [Fact]
    public void No_criteria_returns_everything()
    {
        Assert.Equal(4, RequestFilter.Apply(Items(), RequestFilterCriteria.None, Now).Count);
        Assert.False(RequestFilterCriteria.None.IsActive);
    }

    [Fact]
    public void Filters_by_outcome_model_team_id_and_window()
    {
        var items = Items();
        Assert.Equal("req_bbbb2222", Assert.Single(RequestFilter.Apply(items, new RequestFilterCriteria { Outcome = DisplayOutcome.Paused }, Now)).Id);
        Assert.Equal("req_cccc3333", Assert.Single(RequestFilter.Apply(items, new RequestFilterCriteria { Model = "LOCAL-QWEN" }, Now)).Id);
        Assert.Equal(2, RequestFilter.Apply(items, new RequestFilterCriteria { Team = "risk" }, Now).Count);
        Assert.Equal("req_dddd4444", Assert.Single(RequestFilter.Apply(items, new RequestFilterCriteria { IdSearch = "DDDD" }, Now)).Id);
        Assert.Equal(2, RequestFilter.Apply(items, new RequestFilterCriteria { Window = TimeWindow.Last15Minutes }, Now).Count);
        Assert.Equal(3, RequestFilter.Apply(items, new RequestFilterCriteria { Window = TimeWindow.LastHour }, Now).Count);
    }

    [Fact]
    public void Model_filter_matches_selected_even_when_nothing_was_served()
    {
        var hit = RequestFilter.Apply(Items(), new RequestFilterCriteria { Model = "gpt-4o-mini" }, Now);
        Assert.Contains(hit, r => r.Id == "req_bbbb2222"); // selected before the pause, never attempted
    }

    [Fact]
    public void Criteria_combine_and_report_active()
    {
        var c = new RequestFilterCriteria { Team = "risk", Window = TimeWindow.Last15Minutes };
        Assert.True(c.IsActive);
        Assert.Equal("req_bbbb2222", Assert.Single(RequestFilter.Apply(Items(), c, Now)).Id);
        Assert.Empty(RequestFilter.Apply(Items(), c with { IdSearch = "zzzz" }, Now));
    }

    [Fact]
    public void Options_are_distinct_and_sorted()
    {
        Assert.Equal(new[] { "gpt-4o-mini", "local-qwen" }, RequestFilter.Models(Items()));
        Assert.Equal(new[] { "payments", "risk", "unknown" }, RequestFilter.Teams(Items()));
        Assert.Contains(DisplayOutcome.AuthenticationFailed, RequestFilter.Outcomes(Items()));
    }
}

public sealed class SnapshotAndPresetTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Snapshot_is_frozen_against_later_form_and_preset_edits()
    {
        var form = new PlaygroundForm();
        ScenarioPresets.ApplyTo(ScenarioPresets.Find('A')!, form);
        var snap = RequestSnapshot.Freeze(1, form, T0);
        Assert.True(snap.MatchesForm(form));

        // Edit the form, then re-fill it from another preset: the snapshot must not move.
        form.Prompt = "edited";
        form.Streaming = true;
        ScenarioPresets.ApplyTo(ScenarioPresets.Find('B')!, form);

        Assert.Equal(ScenarioPresets.NormalPrompt, snap.Prompt);
        Assert.Null(snap.RequestedModel);
        Assert.False(snap.Streaming);
        Assert.Equal("Scenario A · Permitted request", snap.ScenarioLabel);
        Assert.Equal("Request #1 · Scenario A · Permitted request", snap.Title);
        Assert.False(snap.MatchesForm(form));
    }

    [Fact]
    public void Editing_a_preset_copy_does_not_change_the_catalogue()
    {
        var a = ScenarioPresets.Find('A')!;
        var edited = a with { Prompt = "changed" };
        Assert.NotEqual(edited.Prompt, ScenarioPresets.Find('A')!.Prompt);
        Assert.Equal(ScenarioPresets.NormalPrompt, ScenarioPresets.All[0].Prompt);
    }

    [Fact]
    public void Edited_preset_prompt_is_labelled_custom()
    {
        var form = new PlaygroundForm();
        ScenarioPresets.ApplyTo(ScenarioPresets.Find('A')!, form);
        form.Prompt += " and more";
        var snap = RequestSnapshot.Freeze(2, form, T0);
        Assert.Equal("Custom", snap.ScenarioLabel);
        Assert.Null(snap.Scenario);
    }

    [Fact]
    public void Scenario_B_requests_the_unapproved_test_model_and_A_lets_policy_choose()
    {
        var form = new PlaygroundForm();
        ScenarioPresets.ApplyTo(ScenarioPresets.Find("b")!, form);
        var b = RequestSnapshot.Freeze(1, form, T0);
        Assert.Equal("gpt-4-unapproved-test", b.RequestedModel);
        Assert.Equal("gpt-4-unapproved-test", b.RoutingLabel);

        ScenarioPresets.ApplyTo(ScenarioPresets.Find('A')!, form);
        var a = RequestSnapshot.Freeze(2, form, T0);
        Assert.Null(a.RequestedModel);
        Assert.Equal("Let policy choose", a.RoutingLabel);
    }

    [Fact]
    public void Scenario_D_appends_a_fresh_nonce_on_every_run()
    {
        var form = new PlaygroundForm();
        ScenarioPresets.ApplyTo(ScenarioPresets.Find('D')!, form);
        var one = RequestSnapshot.Freeze(1, form, T0);
        var two = RequestSnapshot.Freeze(2, form, T0);

        Assert.NotEqual(one.Prompt, two.Prompt);
        Assert.StartsWith(ScenarioPresets.NormalPrompt + " [run ", one.Prompt, StringComparison.Ordinal);
        Assert.Equal(ScenarioPresets.NormalPrompt, one.BasePrompt);
        Assert.Equal("Scenario D · Pause and resume", one.ScenarioLabel);
        Assert.True(one.MatchesForm(form)); // the nonce is not a form change

        var nonces = Enumerable.Range(0, 200).Select(_ => ScenarioPresets.NewNonce()).ToHashSet();
        Assert.Equal(200, nonces.Count);
    }

    [Fact]
    public void Form_problems_block_run()
    {
        var form = new PlaygroundForm();
        Assert.NotNull(form.Problem);
        form.Prompt = "hi";
        Assert.Null(form.Problem);
        form.Mode = RouteMode.Explicit;
        Assert.NotNull(form.Problem);
        form.ExplicitModel = "  ";
        Assert.NotNull(form.Problem);
        form.ExplicitModel = "x";
        Assert.Null(form.Problem);
        form.MaxTokens = 0;
        Assert.NotNull(form.Problem);
    }

    // The presets must exercise the real classifier the way their labels claim.
    private static DataClassification Classify(string text) => new RegexDataClassifier().Classify(
        new InferenceRequest("tester", [new ChatMessage(ChatRole.User, text)], null, new AttributionTags("t", "w", "u"))).Classification;

    [Fact]
    public void Scenario_C_and_the_connection_test_trigger_the_PII_classifier()
    {
        Assert.Equal(DataClassification.Pii, Classify(ScenarioPresets.SensitivePrompt));
        Assert.Equal(DataClassification.Pii, Classify(ConnectionLogic.GovernedTestPrompt()));
    }

    [Fact]
    public void Scenarios_A_B_D_are_not_sensitive()
    {
        var form = new PlaygroundForm();
        ScenarioPresets.ApplyTo(ScenarioPresets.Find('D')!, form);
        Assert.Equal(DataClassification.Internal, Classify(ScenarioPresets.NormalPrompt));
        Assert.Equal(DataClassification.Internal, Classify(RequestSnapshot.Freeze(1, form, T0).Prompt));
    }
}

public sealed class ConnectionLogicTests
{
    [Theory]
    [InlineData("http://Ollama-Host:11434/v1/", "http://ollama-host:11434/v1")]
    [InlineData("http://10.0.0.5:11434/v1", "http://10.0.0.5:11434/v1")]
    [InlineData("http://localhost/v1", "http://localhost:80/v1")]
    public void Normalize_matches_the_catalog_shape(string raw, string expected)
        => Assert.Equal(expected, ConnectionLogic.Normalize(raw));

    [Theory]
    [InlineData("")]
    [InlineData("ollama:11434")]
    [InlineData("ftp://host:21/")]
    [InlineData("http://user:pw@host:11434/v1")]
    [InlineData("http://host:11434/v1?x=1")]
    public void Endpoint_problems_are_reported(string raw) => Assert.NotNull(ConnectionLogic.EndpointProblem(raw));

    [Fact]
    public void Authority_is_host_and_port()
        => Assert.Equal("ollama-host:11434", ConnectionLogic.Authority("http://ollama-host:11434/v1"));

    private static CatalogResponse Catalog(string? endpoint, string? model, bool registered, bool configured = true) => new()
    {
        Mode = "cloud",
        Local = new LocalRouteInfo { Configured = configured, Endpoint = endpoint, ModelId = model, Registered = registered },
    };

    [Fact]
    public void Applied_check_needs_same_endpoint_exact_tag_and_server_registration()
    {
        const string draft = "http://ollama-host:11434/v1/";
        Assert.True(ConnectionLogic.CheckApplied(Catalog("http://ollama-host:11434/v1", "qwen3:8b", true), draft, "qwen3:8b").Pass);
        Assert.False(ConnectionLogic.CheckApplied(Catalog("http://other:11434/v1", "qwen3:8b", true), draft, "qwen3:8b").Pass);
        Assert.False(ConnectionLogic.CheckApplied(Catalog("http://ollama-host:11434/v1", "qwen3:4b", true), draft, "qwen3:8b").Pass);
        Assert.False(ConnectionLogic.CheckApplied(Catalog("http://ollama-host:11434/v1", "qwen3:8b", false), draft, "qwen3:8b").Pass);
        Assert.False(ConnectionLogic.CheckApplied(Catalog(null, null, false, configured: false), draft, "qwen3:8b").Pass);
        Assert.False(ConnectionLogic.CheckApplied(null, draft, "qwen3:8b").Pass);
    }

    [Fact]
    public void Config_block_has_names_and_values_but_no_secret()
    {
        var block = ConnectionLogic.ConfigBlock("http://ollama-host:11434/v1", "qwen3:8b", localOnly: true);
        Assert.Contains("Providers__Mode=local-only", block, StringComparison.Ordinal);
        Assert.Contains("Local__Endpoint=http://ollama-host:11434/v1", block, StringComparison.Ordinal);
        Assert.Contains("Local__ModelId=qwen3:8b", block, StringComparison.Ordinal);
        Assert.Contains("dotnet user-secrets set \"Local:ModelId\" \"qwen3:8b\"", block, StringComparison.Ordinal);
        Assert.DoesNotContain("ApiKey=", block, StringComparison.Ordinal);

        var cloud = ConnectionLogic.ConfigBlock("http://ollama-host:11434/v1", "qwen3:8b", localOnly: false);
        Assert.DoesNotContain("\nProviders__Mode=local-only", "\n" + cloud, StringComparison.Ordinal);
    }

    [Fact]
    public void Route_review_text_depends_on_mode()
    {
        Assert.Contains("PII and PHI requests route to local:qwen3:8b", ConnectionLogic.RouteReview("cloud", "qwen3:8b"), StringComparison.Ordinal);
        Assert.Contains("every classification", ConnectionLogic.RouteReview("local-only", "qwen3:8b"), StringComparison.Ordinal);
    }

    [Fact]
    public void Governed_connection_test_prompt_is_fresh_every_run_so_it_cannot_hit_the_cache()
        => Assert.NotEqual(ConnectionLogic.GovernedTestPrompt(), ConnectionLogic.GovernedTestPrompt());

    [Fact]
    public void Launch_link_reads_credentials_only_from_the_fragment()
    {
        var c = LaunchLink.Parse("http://localhost:5201/ui/connect#token=abc%2B1&appkey=k-2");
        Assert.NotNull(c);
        Assert.Equal("abc+1", c!.AdminToken);
        Assert.Equal("k-2", c.ApplicationKey);

        Assert.Null(LaunchLink.Parse("http://localhost:5201/ui/connect?token=abc"));   // query is sent to the server: never accepted
        Assert.Null(LaunchLink.Parse("http://localhost:5201/ui/connect"));
        Assert.Null(LaunchLink.Parse("http://localhost:5201/ui/connect#"));
        Assert.Null(LaunchLink.Parse("http://localhost:5201/ui/connect#other=1"));
    }
}

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

    internal static List<ApplicationSummaryDto> Apps() =>
    [
        new() { Id = "demo-app", Team = "payments", Enabled = true, ConsoleTesting = true, GrantedProfiles = ["public-tickets", "internal"], DefaultProfile = "internal" },
        new() { Id = "disabled-app", Team = "risk", Enabled = false, ConsoleTesting = true, GrantedProfiles = ["internal"], DefaultProfile = "internal" },
        new() { Id = "prod-app", Team = "risk", Enabled = true, ConsoleTesting = false, GrantedProfiles = ["internal"], DefaultProfile = "internal" },
    ];

    internal static List<WorkloadProfileDto> Profiles() =>
    [
        new() { Id = "public-tickets", Name = "Public tickets", Baseline = "Public" },
        new() { Id = "internal", Name = "Internal", Baseline = "Internal" },
    ];

    [Fact]
    public void Snapshot_is_frozen_against_later_form_and_preset_edits()
    {
        var form = new PlaygroundForm();
        ScenarioPresets.ApplyTo(ScenarioPresets.Find('A')!, form, Apps(), Profiles());
        var snap = RequestSnapshot.Freeze(1, form, T0, clientRequestId: "crid-1");
        Assert.True(snap.MatchesForm(form));
        Assert.Equal("demo-app", snap.ApplicationId);
        Assert.Equal("public-tickets", snap.ProfileId);
        Assert.Equal("crid-1", snap.ClientRequestId);

        form.Prompt = "edited";
        form.Streaming = !form.Streaming;
        ScenarioPresets.ApplyTo(ScenarioPresets.Find('B')!, form, Apps(), Profiles());

        Assert.Equal(ScenarioPresets.TicketPrompt, snap.Prompt);
        Assert.Null(snap.RequestedModel);
        Assert.Equal("Scenario A · Public → cloud", snap.ScenarioLabel);
        Assert.Equal("Request #1 · Scenario A · Public → cloud", snap.Title);
        Assert.False(snap.MatchesForm(form));
    }

    [Fact]
    public void Every_run_gets_a_fresh_client_request_id()
    {
        var form = new PlaygroundForm { Prompt = "x", ApplicationId = "demo-app" };
        var ids = Enumerable.Range(1, 50).Select(i => RequestSnapshot.Freeze(i, form, T0).ClientRequestId).ToHashSet();
        Assert.Equal(50, ids.Count);
        Assert.All(ids, id => Assert.Equal(32, id.Length));
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
    public void Presets_only_populate_the_form()
    {
        var form = new PlaygroundForm { Streaming = false, MaxTokens = 77, Workflow = "wf" };
        var result = ScenarioPresets.ApplyTo(ScenarioPresets.Find('B')!, form, Apps(), Profiles());

        Assert.Null(result.Note);
        Assert.Equal('B', form.Scenario);
        Assert.Equal(ScenarioPresets.SensitiveTicketPrompt, form.Prompt);
        Assert.Equal(RouteMode.Policy, form.Mode);
        Assert.Equal("demo-app", form.ApplicationId);          // first console-testable app with a Public profile
        Assert.Equal("public-tickets", form.ProfileId);         // the granted Public-baseline profile
        // Operator settings untouched; nothing frozen or sent — a preset has no submission side.
        Assert.False(form.Streaming);
        Assert.Equal(77, form.MaxTokens);
        Assert.Equal("wf", form.Workflow);
    }

    [Fact]
    public void Preset_without_a_matching_granted_profile_says_so_and_keeps_the_choice_to_the_operator()
    {
        var apps = new List<ApplicationSummaryDto>
        {
            new() { Id = "only-internal", Team = "t", Enabled = true, ConsoleTesting = true, GrantedProfiles = ["internal"], DefaultProfile = "internal" },
        };
        var form = new PlaygroundForm();
        var result = ScenarioPresets.ApplyTo(ScenarioPresets.Find('A')!, form, apps, Profiles());
        Assert.NotNull(result.Note);
        Assert.Contains("Public", result.Note, StringComparison.Ordinal);
        Assert.Equal("internal", form.ProfileId); // the app default, not a fabricated grant
    }

    [Fact]
    public void Preset_is_ready_only_when_it_still_matches_and_nothing_blocks_run()
    {
        var preset = ScenarioPresets.Find('A')!;
        var form = new PlaygroundForm();
        ScenarioPresets.ApplyTo(preset, form); // no applications known
        Assert.False(ScenarioPresets.IsReady(preset, form, null));

        ScenarioPresets.ApplyTo(preset, form, Apps(), Profiles());
        Assert.True(ScenarioPresets.IsReady(preset, form, Apps()));

        form.Prompt += "!";
        Assert.False(ScenarioPresets.IsReady(preset, form, Apps()));
    }

    [Fact]
    public void Scenario_D_appends_a_fresh_nonce_on_every_run()
    {
        var form = new PlaygroundForm();
        ScenarioPresets.ApplyTo(ScenarioPresets.Find('D')!, form, Apps(), Profiles());
        var one = RequestSnapshot.Freeze(1, form, T0);
        var two = RequestSnapshot.Freeze(2, form, T0);

        Assert.NotEqual(one.Prompt, two.Prompt);
        Assert.NotEqual(one.ClientRequestId, two.ClientRequestId);
        Assert.StartsWith(ScenarioPresets.TicketPrompt + " [run ", one.Prompt, StringComparison.Ordinal);
        Assert.Equal(ScenarioPresets.TicketPrompt, one.BasePrompt);
        Assert.Equal("Scenario D · Pause demo", one.ScenarioLabel);
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
    public void Scenarios_B_C_and_the_connection_test_trigger_the_PII_classifier()
    {
        Assert.Equal(DataClassification.Pii, Classify(ScenarioPresets.Find('B')!.Prompt));
        Assert.Equal(DataClassification.Pii, Classify(ScenarioPresets.Find('C')!.Prompt));
        Assert.Equal(DataClassification.Pii, Classify(ConnectionLogic.GovernedTestPrompt()));
    }

    [Fact]
    public void Scenarios_A_and_D_carry_nothing_sensitive()
    {
        var form = new PlaygroundForm();
        ScenarioPresets.ApplyTo(ScenarioPresets.Find('D')!, form);
        Assert.True(Classify(ScenarioPresets.TicketPrompt) < DataClassification.Pii);
        Assert.True(Classify(RequestSnapshot.Freeze(1, form, T0).Prompt) < DataClassification.Pii);
    }
}

public sealed class ComposerRulesTests
{
    private static readonly List<ApplicationSummaryDto> Apps = SnapshotAndPresetTests.Apps();

    [Fact]
    public void Without_applications_nothing_can_run()
    {
        var form = new PlaygroundForm { Prompt = "hello" };
        Assert.Equal("Applications are not loaded yet.", ComposerRules.Problem(form, null));
        Assert.Equal("No application is enabled for console testing.", ComposerRules.Problem(form, []));
        Assert.Equal("Choose an application to run as.", ComposerRules.Problem(form, Apps));

        Assert.True(ComposerRules.SelectApplication(form, Apps, "demo-app"));
        Assert.Null(ComposerRules.Problem(form, Apps));
    }

    [Fact]
    public void Disabled_and_non_test_applications_are_listed_but_not_selectable()
    {
        var choices = ComposerRules.Applications(Apps);
        Assert.Equal(3, choices.Count);
        Assert.True(choices[0].Selectable);                 // selectable first
        Assert.Equal("demo-app", choices[0].App.Id);
        Assert.Contains(choices, c => c.App.Id == "disabled-app" && !c.Selectable && c.Reason == "disabled");
        Assert.Contains(choices, c => c.App.Id == "prod-app" && !c.Selectable && c.Reason == "console testing not allowed");

        var form = new PlaygroundForm { Prompt = "x" };
        Assert.False(ComposerRules.SelectApplication(form, Apps, "prod-app"));
        Assert.False(ComposerRules.SelectApplication(form, Apps, "disabled-app"));
        Assert.False(ComposerRules.SelectApplication(form, Apps, "nope"));
        Assert.Null(form.ApplicationId);

        form.ApplicationId = "prod-app"; // forced (e.g. stale form): Run is still refused, with the reason
        Assert.Contains("console testing not allowed", ComposerRules.Problem(form, Apps), StringComparison.Ordinal);
    }

    [Fact]
    public void Only_granted_profiles_are_selectable_and_the_default_follows_the_application()
    {
        var form = new PlaygroundForm { Prompt = "x" };
        ComposerRules.SelectApplication(form, Apps, "demo-app");
        Assert.Equal("internal", form.ProfileId); // default_profile
        Assert.Equal(new[] { "public-tickets", "internal" }, ComposerRules.GrantedProfiles(ComposerRules.Find(Apps, "demo-app")));

        Assert.False(ComposerRules.SelectProfile(form, Apps, "phi-records"));
        Assert.Equal("internal", form.ProfileId);
        Assert.True(ComposerRules.SelectProfile(form, Apps, "public-tickets"));
        Assert.Equal("public-tickets", form.ProfileId);

        form.ProfileId = "phi-records"; // ungranted value forced into the form
        Assert.Equal("Profile phi-records is not granted to demo-app.", ComposerRules.Problem(form, Apps));
    }

    [Fact]
    public void Default_profile_falls_back_to_the_first_granted_one_when_the_default_is_not_granted()
    {
        var app = new ApplicationSummaryDto { Id = "a", Enabled = true, ConsoleTesting = true, GrantedProfiles = ["p1", "p2"], DefaultProfile = "zzz" };
        Assert.Equal("p1", ComposerRules.DefaultProfile(app));
        Assert.Null(ComposerRules.DefaultProfile(new ApplicationSummaryDto { Id = "b", Enabled = true, ConsoleTesting = true }));
    }

    [Fact]
    public void Eligible_overrides_follow_policies_profile_baseline_and_mode()
    {
        var config = new CovenantConfigDto
        {
            Mode = "Hybrid",
            Connections =
            [
                new() { Id = "ollama", DisplayName = "Ollama", Boundary = "Local", Enabled = true, Models = [new() { ModelId = "qwen3:8b", DisplayName = "Qwen" }] },
                new() { Id = "openai", DisplayName = "OpenAI", Boundary = "Cloud", Enabled = true, Models = [new() { ModelId = "gpt-x-mini" }] },
                new() { Id = "off", Boundary = "Cloud", Enabled = false, Models = [new() { ModelId = "m" }] },
            ],
            Profiles = SnapshotAndPresetTests.Profiles(),
            Policies =
            [
                new() { Id = "public", Enabled = true, Classifications = ["Public"],
                        Targets = [new() { ConnectionId = "openai", ModelId = "gpt-x-mini" }, new() { ConnectionId = "off", ModelId = "m" }, new() { ConnectionId = "ollama", ModelId = "qwen3:8b" }] },
                new() { Id = "internal", Enabled = true, Classifications = ["Internal"], Targets = [new() { ConnectionId = "ollama", ModelId = "qwen3:8b" }] },
                new() { Id = "paused", Enabled = true, Paused = true, Classifications = ["Public"], Targets = [new() { ConnectionId = "ollama", ModelId = "other" }] },
            ],
        };

        var pub = ComposerRules.EligibleOverrides(config, "public-tickets", null);
        Assert.Equal(new[] { "openai/gpt-x-mini", "ollama/qwen3:8b" }, pub.Select(o => o.Value));
        Assert.Equal("Cloud", pub[0].Boundary);
        Assert.Equal("Local", pub[1].Boundary);

        Assert.Equal(new[] { "ollama/qwen3:8b" }, ComposerRules.EligibleOverrides(config, "internal", null).Select(o => o.Value));

        config.Mode = "LocalOnly";
        Assert.Equal(new[] { "ollama/qwen3:8b" }, ComposerRules.EligibleOverrides(config, "public-tickets", null).Select(o => o.Value));
    }
}

public sealed class SafeMarkdownTests
{
    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("hello <img src=x onerror=alert(1)>")]
    [InlineData("<a href=\"javascript:alert(1)\">x</a>")]
    [InlineData("<iframe src=//evil></iframe>")]
    [InlineData("**<svg onload=alert(1)>**")]
    [InlineData("`<script>`")]
    [InlineData("```\n<script>alert(1)</script>\n```")]
    [InlineData("- <b onmouseover=x>item</b>")]
    public void Raw_html_is_always_encoded(string input)
    {
        var html = SafeMarkdown.ToHtml(input);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<svg", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<b ", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<a href=\"javascript", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;", html, StringComparison.Ordinal);
        AssertOnlyAllowedTags(html);
    }

    [Theory]
    [InlineData("[click](javascript:alert(1))")]
    [InlineData("[click](JAVASCRIPT:alert(1))")]
    [InlineData("[click](data:text/html;base64,PHNjcmlwdD4=)")]
    [InlineData("[click](vbscript:msgbox)")]
    [InlineData("[click](//evil.example/x)")]
    [InlineData("[click](jav&#x61;script:alert(1))")]
    public void Non_http_links_stay_literal_text(string input)
    {
        var html = SafeMarkdown.ToHtml(input);
        Assert.DoesNotContain("<a ", html, StringComparison.Ordinal);
        Assert.Contains("[click]", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Http_links_open_safely_and_cannot_break_out_of_the_attribute()
    {
        var html = SafeMarkdown.ToHtml("see [docs](https://example.com/a?b=1&c=2) now");
        Assert.Contains("<a href=\"https://example.com/a?b=1&amp;c=2\" target=\"_blank\" rel=\"noopener noreferrer\">docs</a>", html, StringComparison.Ordinal);

        var tricky = SafeMarkdown.ToHtml("[x](https://e.com/\"onmouseover=\"alert(1))");
        Assert.DoesNotContain("\"onmouseover", tricky, StringComparison.Ordinal);
        Assert.DoesNotContain(" onmouseover=\"", tricky, StringComparison.Ordinal);
        AssertOnlyAllowedTags(tricky);
    }

    [Fact]
    public void Supported_formatting_renders()
    {
        Assert.Equal("<h3>Title</h3>", SafeMarkdown.ToHtml("# Title"));
        Assert.Equal("<h4>Sub</h4>", SafeMarkdown.ToHtml("## Sub"));
        Assert.Equal("<h5>Small</h5>", SafeMarkdown.ToHtml("### Small"));
        Assert.Equal("<p>#### four</p>", SafeMarkdown.ToHtml("#### four"));
        Assert.Equal("<p><strong>bold</strong> and <em>it</em> and <strong>b2</strong> and <em>i2</em></p>",
            SafeMarkdown.ToHtml("**bold** and *it* and __b2__ and _i2_"));
        Assert.Equal("<p>use <code>a*b*c</code> here</p>", SafeMarkdown.ToHtml("use `a*b*c` here"));
        Assert.Equal("<ul><li>one</li><li>two</li></ul>", SafeMarkdown.ToHtml("- one\n* two"));
        Assert.Equal("<ol><li>first</li><li>second</li></ol>", SafeMarkdown.ToHtml("1. first\n2) second"));
        Assert.Equal("<p>line one<br>line two</p><p>para two</p>", SafeMarkdown.ToHtml("line one\r\nline two\n\npara two"));
        Assert.Equal("<pre><code>x &lt; 1\n**not bold**</code></pre>", SafeMarkdown.ToHtml("```csharp\nx < 1\n**not bold**\n```"));
        Assert.Equal("<p>snake_case_name stays</p>", SafeMarkdown.ToHtml("snake_case_name stays"));
        Assert.Equal("{&quot;priority&quot;:&quot;low&quot;}", StripP(SafeMarkdown.ToHtml("{\"priority\":\"low\"}")));
        Assert.Equal("", SafeMarkdown.ToHtml(null));
        Assert.Equal("", SafeMarkdown.ToHtml(""));
    }

    [Fact]
    public void Unclosed_fence_while_streaming_renders_as_code()
        => Assert.Equal("<pre><code>partial &lt;b&gt;</code></pre>", SafeMarkdown.ToHtml("```\npartial <b>"));

    private static string StripP(string html) => html.StartsWith("<p>", StringComparison.Ordinal) ? html[3..^4] : html;

    /// <summary>Every tag in the output is one the renderer itself writes.</summary>
    private static void AssertOnlyAllowedTags(string html)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "p", "br", "h3", "h4", "h5", "strong", "em", "code", "pre", "ul", "ol", "li", "a" };
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(html, "<\\/?([a-zA-Z0-9]+)"))
            Assert.Contains(m.Groups[1].Value, allowed);
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(html, "<a ([^>]*)>"))
            Assert.Matches("^href=\"https?://[^\"<>]*\" target=\"_blank\" rel=\"noopener noreferrer\"$", m.Groups[1].Value);
    }
}

public sealed class TraceModelTests
{
    private static TraceEventDto E(long seq, string stage, string state, double? ms = null, string? attempt = null, string? reason = null, string? target = null)
        => new() { Seq = seq, Stage = stage, State = state, DurationMs = ms, AttemptId = attempt, ReasonCode = reason, Target = target };

    private static TraceStepView Step(TraceView v, string stage) => v.Steps.Single(s => s.Stage == stage);

    [Fact]
    public void Steps_follow_the_canonical_order_and_unreported_stages_are_pending_until_complete()
    {
        var open = TraceModel.Reduce([E(1, "auth", "passed", 2)], complete: false);
        Assert.Equal(TraceModel.Stages, open.Steps.Select(s => s.Stage));
        Assert.Equal(TraceStepState.Passed, Step(open, "auth").State);
        Assert.Equal(2, Step(open, "auth").DurationMs);
        Assert.Equal(TraceStepState.Pending, Step(open, "policy").State);

        var done = TraceModel.Reduce([E(1, "auth", "passed", 2)], complete: true);
        Assert.Equal(TraceStepState.NotReported, Step(done, "policy").State); // never invented as passed/skipped
    }

    [Fact]
    public void Duplicates_are_dropped_by_seq_and_out_of_order_delivery_cannot_regress_a_state()
    {
        var events = new[]
        {
            E(3, "policy", "passed", 4, reason: "allowed"),
            E(1, "auth", "running"),
            E(2, "auth", "passed", 1.5),
            E(2, "auth", "failed"),       // duplicate seq (replay): ignored, first copy wins
            E(3, "policy", "refused"),    // duplicate seq
            E(4, "policy", "running"),    // stray later event wins only because its seq is higher
        };
        var v = TraceModel.Reduce(events, complete: false);
        Assert.Equal(TraceStepState.Passed, Step(v, "auth").State);
        Assert.Equal(1.5, Step(v, "auth").DurationMs);
        Assert.Equal(TraceStepState.Running, Step(v, "policy").State);
        Assert.Equal("allowed", Step(v, "policy").ReasonCode);
        Assert.Equal(4, v.LastSeq);
    }

    [Fact]
    public void Skipped_stages_are_reported_as_skipped_with_their_reason()
    {
        var v = TraceModel.Reduce(
        [
            E(1, "cache", "passed", 1, reason: "cache_hit"),
            E(2, "rate_limit", "skipped", reason: "cache_hit"),
            E(3, "pause", "skipped", reason: "cache_hit"),
            E(4, "provider", "skipped", reason: "cache_hit"),
        ], complete: true);
        Assert.Equal(TraceStepState.Skipped, Step(v, "rate_limit").State);
        Assert.Equal(TraceStepState.Skipped, Step(v, "provider").State);
        Assert.Empty(Step(v, "provider").Attempts);
        Assert.Equal("cache_hit", Step(v, "provider").ReasonCode);
        Assert.Null(Step(v, "rate_limit").DurationMs);
    }

    [Fact]
    public void Provider_events_are_grouped_into_attempts_in_order_of_first_appearance()
    {
        var v = TraceModel.Reduce(
        [
            E(10, "provider", "running", attempt: "a1", target: "openai/gpt-x"),
            E(12, "provider", "running", attempt: "a2", target: "ollama/qwen"),
            E(11, "provider", "failed", 5000, attempt: "a1", reason: "timeout"),
            E(13, "provider", "passed", 800, attempt: "a2"),
        ], complete: true);
        var p = Step(v, "provider");
        Assert.Equal(2, p.Attempts.Count);
        Assert.Equal("a1", p.Attempts[0].AttemptId);
        Assert.Equal(TraceStepState.Failed, p.Attempts[0].State);
        Assert.Equal("openai/gpt-x", p.Attempts[0].Target);
        Assert.Equal("timeout", p.Attempts[0].ReasonCode);
        Assert.Equal(5000, p.Attempts[0].DurationMs);
        Assert.Equal("a2", p.Attempts[1].AttemptId);
        Assert.Equal(TraceStepState.Passed, p.Attempts[1].State);
        Assert.Equal("ollama/qwen", p.Attempts[1].Target);
        Assert.Equal(TraceStepState.Passed, p.State);   // last attempt decides
        Assert.Equal(5800, p.DurationMs);                // sum of reported attempt durations
        Assert.Equal(new[] { 1, 2 }, p.Attempts.Select(a => a.Number));
    }

    [Fact]
    public void Provider_is_running_while_any_attempt_runs()
    {
        var v = TraceModel.Reduce([E(1, "provider", "running", attempt: "a1")], complete: false);
        Assert.Equal(TraceStepState.Running, Step(v, "provider").State);
    }

    [Fact]
    public void Accumulator_dedupes_tracks_last_seq_and_merges_snapshots()
    {
        var acc = new TraceAccumulator();
        Assert.True(acc.Add(E(1, "auth", "passed")));
        Assert.False(acc.Add(E(1, "auth", "passed")));
        Assert.True(acc.Add(E(5, "policy", "passed")));
        Assert.Equal(5, acc.LastSeq);

        acc.ApplySnapshot(new TraceSnapshotDto { Complete = true, ConfigVersion = 7, Events = [E(1, "auth", "passed"), E(6, "attribution", "passed")] });
        Assert.Equal(3, acc.Count);
        Assert.True(acc.Complete);
        var view = acc.View();
        Assert.Equal(7, view.ConfigVersion);
        Assert.Equal(TraceStepState.NotReported, Step(view, "budget").State);
    }

    [Fact]
    public void Unknown_states_and_stages_are_kept_honestly()
    {
        var v = TraceModel.Reduce([E(1, "auth", "weird"), E(2, "future_stage", "passed")], complete: true);
        Assert.Equal(TraceStepState.NotReported, Step(v, "auth").State);
        Assert.Equal("future_stage", v.Steps[^1].Stage);
    }

    [Fact]
    public void Audit_summary_fallback_reads_objects_and_compact_strings()
    {
        var summary = System.Text.Json.JsonDocument.Parse(
            "[{\"Stage\":\"auth\",\"State\":\"passed\",\"DurationMs\":1},\"policy:passed:3\",\"provider:failed:900\",\"provider:passed:400\",{\"nonsense\":1}]")
            .RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
        var attempts = new List<AuditAttemptView>
        {
            new() { AttemptId = "a1", ConnectionId = "openai", ModelId = "gpt-x" },
            new() { AttemptId = "a2", ConnectionId = "ollama", ModelId = "qwen" },
        };
        var v = TraceModel.FromAuditSummary(summary, attempts, 3)!;
        Assert.Equal("audit summary", v.Source);
        Assert.True(v.Complete);
        Assert.Equal(TraceStepState.Passed, Step(v, "auth").State);
        Assert.Equal(3, Step(v, "policy").DurationMs);
        var p = Step(v, "provider");
        Assert.Equal(2, p.Attempts.Count);
        Assert.Equal("openai/gpt-x", p.Attempts[0].Target);
        Assert.Equal(TraceStepState.Failed, p.Attempts[0].State);
        Assert.Equal(TraceStepState.Passed, p.State);
        Assert.Null(TraceModel.FromAuditSummary(null));
        Assert.Null(TraceModel.FromAuditSummary([]));
    }

    [Fact]
    public void Sse_parser_dispatches_on_blank_lines_and_joins_data()
    {
        var p = new SseParser();
        Assert.Null(p.Feed(": comment"));
        Assert.Null(p.Feed("id: 7"));
        Assert.Null(p.Feed("data: {\"a\":"));
        Assert.Null(p.Feed("data: 1}"));
        var ev = p.Feed("");
        Assert.NotNull(ev);
        Assert.Equal("7", ev!.Id);
        Assert.Equal("message", ev.Event);
        Assert.Equal("{\"a\":\n1}", ev.Data);

        Assert.Null(p.Feed("event: end"));
        var end = p.Feed("");
        Assert.Equal("end", end!.Event);
        Assert.Null(p.Feed("")); // nothing pending
    }
}

public sealed class ShellStatusTests
{
    [Theory]
    [InlineData("local-only", "Local-only", "local")]
    [InlineData("LocalOnly", "Local-only", "local")]
    [InlineData("hybrid", "Hybrid", "hybrid")]
    [InlineData("Hybrid", "Hybrid", "hybrid")]
    [InlineData("legacy-cloud", "Legacy cloud", "legacy")]
    [InlineData("LegacyCloud", "Legacy cloud", "legacy")]
    [InlineData("cloud", "Legacy cloud", "legacy")]
    [InlineData(null, "Mode not reported", "unknown")]
    [InlineData("", "Mode not reported", "unknown")]
    [InlineData("quantum", "Mode not reported", "unknown")]
    public void Mode_badge_mapping(string? mode, string label, string tone)
    {
        var b = ShellStatus.Mode(mode);
        Assert.Equal(label, b.Label);
        Assert.Equal(tone, b.Tone);
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Live_shows_no_counter_and_the_exact_time_goes_in_the_title()
    {
        var f = ShellStatus.Freshness(true, false, Now.AddSeconds(-1), Now, null);
        Assert.Equal("Live", f.Label);
        Assert.Equal("ok", f.Tone);
        Assert.False(f.Stale);
        Assert.Equal("Last status update 2026-10-16 11:59:59 UTC", f.Title);
        Assert.DoesNotMatch("\\d+ s", f.Label);
    }

    [Fact]
    public void Stale_shows_age_and_the_feed_problem()
    {
        var f = ShellStatus.Freshness(true, true, Now.AddSeconds(-12), Now, "Connection lost — retrying");
        Assert.Equal("Stale · 12 s", f.Label);
        Assert.Equal("warn", f.Tone);
        Assert.Contains("Connection lost", f.Title, StringComparison.Ordinal);
        Assert.Equal("Stale · 3 min", ShellStatus.Freshness(true, true, Now.AddSeconds(-200), Now, null).Label);
        Assert.Equal("Stale · no update", ShellStatus.Freshness(true, true, null, Now, null).Label);
        Assert.Equal("Disconnected", ShellStatus.Freshness(false, false, null, Now, null).Label);
    }

    [Fact]
    public void Announcement_depends_on_state_changes_only()
    {
        var a = ShellStatus.Announcement(true, false, false);
        Assert.Equal("Console connected. New provider calls accepted.", a);
        Assert.Equal("Console connected. New provider calls paused. Status is stale.", ShellStatus.Announcement(true, true, true));
        Assert.Equal("Console disconnected.", ShellStatus.Announcement(false, true, true));
        // Same state, any time → same text: nothing to re-announce while the clock ticks.
        Assert.Equal(a, ShellStatus.Announcement(true, false, false));
    }
}

public sealed class AttemptAndDestinationTests
{
    private static AuditEntryView Fallback() => Set(Rec.V2(selected: "openai/gpt-x", attempted: "ollama/qwen", served: "ollama/qwen"),
    [
        new AuditAttemptView { AttemptId = "a1", ConnectionId = "openai", ModelId = "gpt-x", Outcome = "failed", ErrorClass = "timeout", DurationMs = 5000, CostUsd = 0.0001m, CostBasis = "estimated_failed_call" },
        new AuditAttemptView { AttemptId = "a2", ConnectionId = "ollama", ModelId = "qwen", Outcome = "served", DurationMs = 800, CostUsd = 0m, CostBasis = "zero_priced" },
    ]);

    private static AuditEntryView Set(AuditEntryView r, List<AuditAttemptView> attempts) { r.Attempts = attempts; return r; }

    private static readonly List<ConnectionDto> Connections =
    [
        new() { Id = "openai", Boundary = "Cloud", Enabled = true },
        new() { Id = "ollama", Boundary = "Local", Enabled = true },
    ];

    [Fact]
    public void Fallback_is_explained_attempt_by_attempt()
    {
        var r = Fallback();
        var v = OutcomeModel.FromRecord(r);
        Assert.Equal(DisplayOutcome.Served, v.Outcome);
        Assert.Equal("Served by approved fallback", v.Title);
        Assert.Contains("Attempt 1 (openai/gpt-x) failed: timeout; attempt 2 (ollama/qwen) served.", v.Explanation, StringComparison.Ordinal);

        var lines = AttemptFacts.Lines(r, Connections);
        Assert.Equal(2, lines.Count);
        Assert.Equal("openai/gpt-x", lines[0].Target);
        Assert.Equal("Cloud", lines[0].Boundary);
        Assert.Equal("timeout", lines[0].ErrorClass);
        Assert.Equal("≈ $0.0001 estimated", lines[0].Cost);
        Assert.Equal("ollama/qwen", lines[1].Target);
        Assert.Equal("Local", lines[1].Boundary);
    }

    [Fact]
    public void Destination_is_the_serving_attempt_with_its_boundary()
    {
        var d = ModelFacts.Destination(Fallback(), Connections);
        Assert.Equal("ollama/qwen", d.Text);
        Assert.Equal("Local", d.Boundary);
        Assert.True(d.Served);

        var unknown = ModelFacts.Destination(Fallback(), null);
        Assert.Null(unknown.Boundary); // never guessed from the name

        var refused = ModelFacts.Destination(Rec.PolicyDeny());
        Assert.Equal(ModelFacts.NoProviderCall, refused.Text);
        Assert.False(refused.Served);

        var cache = ModelFacts.Destination(Rec.V2(cacheHit: true, attempted: null, served: "ollama/qwen"), Connections);
        Assert.StartsWith("Cache — no provider call", cache.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Single_attempt_is_not_called_a_fallback()
    {
        var r = Set(Rec.V2(), [new AuditAttemptView { AttemptId = "a1", ConnectionId = "openai", ModelId = "gpt-x", Outcome = "served" }]);
        Assert.Null(AttemptFacts.FallbackSentence(r));
        Assert.Equal("Served", OutcomeModel.FromRecord(r).Title);
    }

    [Fact]
    public void Cancel_with_partial_text_says_so_truthfully()
    {
        var v = OutcomeModel.Compute(new OutcomeInput(200, Cancelled: true, HasPartialText: true));
        Assert.Equal(DisplayOutcome.Cancelled, v.Outcome);
        Assert.Equal("Cancelled — partial response", v.Title);
        Assert.Contains("the provider may have charged for work done", v.Explanation, StringComparison.Ordinal);
        Assert.Equal("Cancelled — no response received", OutcomeModel.Compute(new OutcomeInput(Cancelled: true)).Title);
    }

    [Fact]
    public void Duplicate_submission_is_not_a_success()
    {
        var v = OutcomeModel.Compute(new OutcomeInput(409, "duplicate_submission"));
        Assert.Equal(DisplayOutcome.DuplicateSubmission, v.Outcome);
        Assert.Equal("unknown", v.PillKind);
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
    public void Launch_link_reads_only_the_admin_token_from_the_fragment_and_ignores_appkey()
    {
        var c = LaunchLink.Parse("http://localhost:5201/ui/connect#token=abc%2B1&appkey=k-2");
        Assert.NotNull(c);
        Assert.Equal("abc+1", c!.AdminToken);

        Assert.Null(LaunchLink.Parse("http://localhost:5201/ui/connect#appkey=k-2"));        // application keys are never accepted
        Assert.Null(LaunchLink.Parse("http://localhost:5201/ui/connect?token=abc"));   // query is sent to the server: never accepted
        Assert.Null(LaunchLink.Parse("http://localhost:5201/ui/connect"));
        Assert.Null(LaunchLink.Parse("http://localhost:5201/ui/connect#"));
        Assert.Null(LaunchLink.Parse("http://localhost:5201/ui/connect#other=1"));
    }
}

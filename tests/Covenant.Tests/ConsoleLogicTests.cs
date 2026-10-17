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
        Assert.Equal("≈ $0.003 estimated from text length", CostText.Cost(Rec.V2(costBasis: "estimated_from_text", cost: 0.003m)));
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
    private static ClassificationResult Detect(string text) => new RegexDataClassifier().Classify(
        new InferenceRequest("tester", [new ChatMessage(ChatRole.User, text)], null, new AttributionTags("t", "w", "u")));

    private static DataClassification Classify(string text) => Detect(text).Classification;

    [Fact]
    public void Scenarios_B_C_and_the_connection_test_trigger_the_PII_classifier()
    {
        Assert.Equal(DataClassification.Pii, Classify(ScenarioPresets.Find('B')!.Prompt));
        Assert.Equal(DataClassification.Pii, Classify(ScenarioPresets.Find('C')!.Prompt));
        Assert.Equal(DataClassification.Pii, Classify(ConnectionLogic.GovernedTestPrompt()));
        // The synthetic marker is the published sample SSN, and B/C are the same request (C differs only in the
        // operator's route state), so both escalate their Public baseline the same way.
        Assert.Contains("078-05-1120", ScenarioPresets.SensitiveTicketPrompt, StringComparison.Ordinal);
        Assert.Equal(ScenarioPresets.Find('B')!.Prompt, ScenarioPresets.Find('C')!.Prompt);
        Assert.Equal("US SSN pattern", Detect(ScenarioPresets.SensitiveTicketPrompt).Signal);
        Assert.Equal("Public", ScenarioPresets.Find('B')!.PreferredBaseline);
        Assert.Equal("Public", ScenarioPresets.Find('C')!.PreferredBaseline);
    }

    [Fact]
    public void Scenario_A_detects_nothing_so_its_Public_baseline_is_not_escalated()
    {
        // The detector's no-match result is Internal with NO signal = "no escalation"; the final classification is
        // max(baseline, detected-if-signalled), so A stays at its Public profile baseline.
        var a = ScenarioPresets.Find('A')!;
        Assert.Equal("Public", a.PreferredBaseline);
        var r = Detect(a.Prompt);
        Assert.Null(r.Signal);
        Assert.True(r.Classification < DataClassification.Pii);
        Assert.Equal(ScenarioPresets.TicketPrompt, a.Prompt);
        Assert.False(a.FreshNonce);
    }

    [Fact]
    public void Scenario_D_carries_nothing_sensitive_and_keeps_the_operator_profile()
    {
        var d = ScenarioPresets.Find('D')!;
        Assert.True(d.FreshNonce);
        Assert.Null(d.PreferredBaseline); // pause demo: no baseline is forced

        var form = new PlaygroundForm();
        ScenarioPresets.ApplyTo(d, form, Apps(), Profiles());
        Assert.Equal("internal", form.ProfileId); // the app default stays — D never picks a profile
        Assert.True(form.FreshNonce);
        var frozen = RequestSnapshot.Freeze(1, form, T0).Prompt;
        Assert.Null(Detect(ScenarioPresets.TicketPrompt).Signal);
        Assert.Null(Detect(frozen).Signal);  // the nonce never looks like PII/PHI
        Assert.True(Classify(frozen) < DataClassification.Pii);
    }

    [Fact]
    public void Every_preset_only_populates_and_never_touches_the_operator_settings()
    {
        Assert.Equal(new[] { 'A', 'B', 'C', 'D' }, ScenarioPresets.All.Select(p => p.Letter));
        foreach (var p in ScenarioPresets.All)
        {
            var form = new PlaygroundForm { Streaming = false, MaxTokens = 33, Workflow = "wf", UseCase = "uc" };
            ScenarioPresets.ApplyTo(p, form, Apps(), Profiles());
            Assert.Equal(p.Letter, form.Scenario);
            Assert.Equal(p.Prompt, form.Prompt);
            Assert.Equal(RouteMode.Policy, form.Mode);
            Assert.Equal(p.FreshNonce, form.FreshNonce);
            Assert.False(form.Streaming);
            Assert.Equal(33, form.MaxTokens);
            Assert.Equal("wf", form.Workflow);
            Assert.Equal("uc", form.UseCase);
            Assert.Equal(ScenarioPresets.ExpectedLow, p.ExpectedAnswer);
            Assert.True(p.StillMatches(form));
        }
        Assert.Same(ScenarioPresets.Find('b'), ScenarioPresets.Find("B"));
        Assert.Null(ScenarioPresets.Find('Z'));
        Assert.Null(ScenarioPresets.Find("AB"));
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
            Mode = "hybrid", // v2 wire spelling (KebabEnumConverter<ProviderMode>)
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

        config.Mode = "local-only";
        Assert.Equal(new[] { "ollama/qwen3:8b" }, ComposerRules.EligibleOverrides(config, "public-tickets", null).Select(o => o.Value));
        // One contract spelling only: an enum-name spelling is not "local-only" (the Host never sends it).
        Assert.False(PolicyRoutes.IsLocalOnly("LocalOnly"));
        Assert.True(PolicyRoutes.IsLocalOnly("local-only"));
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
    public void Audit_summary_fallback_reads_the_typed_compact_trace()
    {
        // Core AuditTraceStep shape only ({Stage, State, DurationMs, AttemptId?, ReasonCode?}); provider steps are matched
        // to Attempts[] by AttemptId.
        var summary = new List<AuditTraceStepView>
        {
            new() { Stage = "auth", State = "passed", DurationMs = 1 },
            new() { Stage = "policy", State = "passed", DurationMs = 3 },
            new() { Stage = "provider", State = "failed", DurationMs = 900, AttemptId = "a1", ReasonCode = "timeout" },
            new() { Stage = "provider", State = "passed", DurationMs = 400, AttemptId = "a2" },
            new() { Stage = "", State = "passed" },
        };
        var attempts = new List<AuditAttemptView>
        {
            new() { AttemptId = "a1", ConnectionId = "openai", ModelId = "gpt-x", Outcome = "failed" },
            new() { AttemptId = "a2", ConnectionId = "ollama", ModelId = "qwen", Outcome = "served" },
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
        Assert.Equal("timeout", p.Attempts[0].ReasonCode);
        Assert.Equal("ollama/qwen", p.Attempts[1].Target);
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
    [InlineData("hybrid", "Hybrid", "hybrid")]
    [InlineData("legacy-cloud", "Legacy cloud", "legacy")]
    // One explicit contract (docs/ui-api-contract.md "Wire spellings"): other spellings are not guessed.
    [InlineData("LocalOnly", "Mode not reported", "unknown")]
    [InlineData("Hybrid", "Mode not reported", "unknown")]
    [InlineData("LegacyCloud", "Mode not reported", "unknown")]
    [InlineData("cloud", "Mode not reported", "unknown")]
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

    [Fact]
    public void Launch_link_token_rides_only_in_the_fragment()
    {
        Assert.Equal("t-1", LaunchLink.Parse("http://localhost:5201/ui/connect#token=t-1")!.AdminToken);
        Assert.Equal("t-2", LaunchLink.Parse("http://localhost:5201/ui/connect#appkey=k&token=t-2")!.AdminToken);
        Assert.Equal("t-3", LaunchLink.Parse("http://localhost:5201/ui/connect#token=%20t-3%20")!.AdminToken);

        // Query-string tokens are sent to the server (and its logs): never accepted, even beside a fragment.
        Assert.Null(LaunchLink.Parse("http://localhost:5201/ui/connect?token=abc"));
        Assert.Null(LaunchLink.Parse("http://localhost:5201/ui/connect?token=abc#appkey=k"));
        Assert.Equal("frag", LaunchLink.Parse("http://localhost:5201/ui/connect?token=query#token=frag")!.AdminToken);
        Assert.Null(LaunchLink.Parse("http://localhost:5201/ui/connect#token="));
        Assert.Null(LaunchLink.Parse("http://localhost:5201/ui/connect#token=" + new string('x', 513)));
        Assert.Null(LaunchLink.Parse(null));
    }

    [Fact]
    public void Launch_link_credentials_expose_no_application_key()
    {
        // Console v2 (ADR-0012): the console never holds an application key — the parsed credentials carry the
        // admin token and nothing else, whatever the launcher put in the fragment.
        var props = typeof(LaunchLink.Credentials).GetProperties().Select(p => p.Name).ToList();
        Assert.Equal(new[] { "AdminToken" }, props);

        var c = LaunchLink.Parse("http://localhost:5201/ui/connect#appkey=secret-app-key&token=adm");
        Assert.NotNull(c);
        Assert.DoesNotContain("secret-app-key", c!.ToString(), StringComparison.Ordinal);
    }
}

// ---- T08: editable admin pages and evidence workflows ----

internal static class Cfg
{
    /// <summary>A small hybrid configuration: local Ollama + cloud OpenAI, Public → cloud with local fallback,
    /// Internal → local, PII/PHI → local.</summary>
    public static CovenantConfigDto Hybrid() => new()
    {
        Version = 3,
        Origin = "console",
        CreatedUtc = new DateTimeOffset(2026, 10, 16, 9, 0, 0, TimeSpan.Zero),
        Mode = "hybrid",
        Connections =
        [
            new ConnectionDto
            {
                Id = "ollama", DisplayName = "Ollama", Kind = "Ollama", Boundary = "Local", Endpoint = "http://ollama-host:11434/v1", Enabled = true,
                Models = [new ModelDto { ModelId = "qwen2.5-coder:7b", Price = new PriceDto { InputPer1M = 0m, OutputPer1M = 0m, Basis = "zero-marginal-local" } }],
            },
            new ConnectionDto
            {
                Id = "openai", DisplayName = "OpenAI", Kind = "OpenAi", Boundary = "Cloud", Enabled = true, SecretRef = "Secrets:openai",
                Models = [new ModelDto { ModelId = "gpt-4o-mini", Price = new PriceDto { InputPer1M = 0.15m, OutputPer1M = 0.60m, Basis = "provider-list" } }],
            },
        ],
        Policies =
        [
            new PolicyDto
            {
                Id = "public", Name = "Public", Enabled = true, Classifications = ["Public"], Strategy = "fixed",
                Targets = [new PolicyTargetDto { ConnectionId = "openai", ModelId = "gpt-4o-mini" }, new PolicyTargetDto { ConnectionId = "ollama", ModelId = "qwen2.5-coder:7b" }],
            },
            new PolicyDto
            {
                Id = "internal", Name = "Internal", Enabled = true, Classifications = ["Internal"], Strategy = "fixed",
                Targets = [new PolicyTargetDto { ConnectionId = "ollama", ModelId = "qwen2.5-coder:7b" }],
            },
            new PolicyDto
            {
                Id = "sensitive", Name = "PII/PHI", Enabled = true, Classifications = ["Pii", "Phi"], Strategy = "fixed",
                Targets = [new PolicyTargetDto { ConnectionId = "ollama", ModelId = "qwen2.5-coder:7b" }],
            },
        ],
        Budgets = [new BudgetConfigDto { Id = "global", Scope = "global", CapUsd = 25m, Period = "monthly", WarnAt = [0.8m, 0.95m], Enforcement = "hard" }],
        Settings = new OperationalSettingsDto { CacheTtlSeconds = 300, CacheMaxEntries = 1000, PromptPreviewChars = 0, TraceRetentionMinutes = 10 },
    };
}

public sealed class MoneyTests
{
    [Theory]
    [InlineData("0.000184", "$0.000184")]
    [InlineData("0.0012", "$0.0012")]
    [InlineData("0.003", "$0.003")]
    [InlineData("0.00012345", "$0.000123")]
    [InlineData("0.0123", "$0.0123")]
    [InlineData("0.5", "$0.50")]
    [InlineData("1.234", "$1.23")]
    [InlineData("1234.5", "$1,234.50")]
    [InlineData("0", "$0.00")]
    [InlineData("-1.23", "-$1.23")]
    [InlineData("-0.000184", "-$0.000184")]
    [InlineData("0.0000001", "<$0.000001")]
    public void Usd_uses_significant_digits_for_tiny_amounts(string value, string expected)
        => Assert.Equal(expected, Money.Usd(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData("0.000184")]
    [InlineData("0.0000049")]
    [InlineData("0.004999")]
    [InlineData("0.0000000001")]
    public void A_non_zero_charge_is_never_rendered_as_zero(string value)
    {
        var text = Money.Usd(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.NotEqual("$0.00", text);
        Assert.NotEqual("$0", text);
        Assert.DoesNotContain("$0.00 ", text + " ", StringComparison.Ordinal);
    }

    [Fact]
    public void Null_is_not_reported_and_never_zero()
    {
        Assert.Equal("Not reported", Money.Usd((decimal?)null));
        Assert.Equal("$0.15", Money.Usd((decimal?)0.15m));
        Assert.Equal("Unpriced", Money.PerMillion(null));
        Assert.Equal("$0.15 / 1M", Money.PerMillion(0.15m));
        Assert.Equal("Unpriced — cost unknown, not zero", Money.Price(0.15m, null));
        Assert.Equal("$0 provider charge (infrastructure excluded)", Money.Price(0m, 0m));
        Assert.Equal("in $0.15 · out $0.60 per 1M tokens", Money.Price(0.15m, 0.60m));
    }

    [Fact]
    public void Caps_have_two_decimals_and_tiny_caps_stay_visible()
    {
        Assert.Equal("$250.00", Money.Cap(250m));
        Assert.Equal("$0.00", Money.Cap(0m));
        Assert.Equal("$0.005", Money.Cap(0.005m));
        Assert.Equal("62%", Money.Share(6.2m, 10m));
        Assert.Equal("n/a", Money.Share(1m, 0m));
    }

    [Fact]
    public void Cost_text_now_uses_significant_digits()
        => Assert.Equal("$0.000184 (estimated provider charge)", CostText.Cost(Rec.V2(costBasis: "reported", cost: 0.000184m)));
}

public sealed class ConfigDraftTests
{
    [Fact]
    public void Draft_is_a_deep_copy_and_never_aliases_the_applied_configuration()
    {
        var applied = Cfg.Hybrid();
        var session = new ConfigDraft(3, "\"v3\"", applied);

        session.Draft.Policies[0].Targets.Add(new PolicyTargetDto { ConnectionId = "x", ModelId = "y" });
        session.Draft.Connections[0].Models[0].Price!.InputPer1M = 9m;
        session.Draft.Settings!.CacheTtlSeconds = 1;

        Assert.Equal(2, applied.Policies[0].Targets.Count);
        Assert.Equal(0m, applied.Connections[0].Models[0].Price!.InputPer1M);
        Assert.Equal(300, applied.Settings!.CacheTtlSeconds);
        Assert.Equal(2, session.Base.Policies[0].Targets.Count);
        Assert.NotSame(applied.Policies[0].Targets, session.Draft.Policies[0].Targets);
    }

    [Fact]
    public void Dirty_tracking_follows_content_not_touches()
    {
        var session = new ConfigDraft(3, null, Cfg.Hybrid());
        Assert.False(session.IsDirty);
        Assert.Equal("\"v3\"", session.BaseEtag);

        session.Draft.Policies[0].Paused = true;
        Assert.True(session.IsDirty);
        session.Draft.Policies[0].Paused = false;
        Assert.False(session.IsDirty); // toggled back: nothing to save

        Assert.True(PolicyRoutes.MoveDown(session.Draft.Policies[0].Targets, 0));
        Assert.True(session.IsDirty);   // order is content
        session.Reset();
        Assert.False(session.IsDirty);
        Assert.Equal("openai", session.Draft.Policies[0].Targets[0].ConnectionId);
    }

    [Fact]
    public void Change_summary_names_what_changed_without_values()
    {
        var before = Cfg.Hybrid();
        var after = ConfigCopy.Deep(before);
        after.Mode = "local-only";
        after.Connections[1].Enabled = false;
        after.Budgets.Add(new BudgetConfigDto { Id = "team-risk", Scope = "team:risk", CapUsd = 5m });
        after.Policies.RemoveAt(2);
        PolicyRoutes.MoveDown(after.Policies[0].Targets, 0);

        var lines = ChangeSummary.Lines(before, after);
        Assert.Contains("mode hybrid → local-only", lines);
        Assert.Contains("connection openai changed", lines);
        Assert.Contains("budget team-risk added", lines);
        Assert.Contains("policy sensitive removed", lines);
        Assert.Contains("policy public changed", lines);
        Assert.DoesNotContain(lines, l => l.Contains("Secrets:", StringComparison.Ordinal));
        Assert.StartsWith("Console: ", ChangeSummary.Text(before, after), StringComparison.Ordinal);
        Assert.Equal("Console: no effective change", ChangeSummary.Text(before, ConfigCopy.Deep(before)));
    }
}

public sealed class EditOutcomeTests
{
    [Fact]
    public void Only_a_2xx_with_a_version_is_success()
    {
        var ok = EditOutcomes.FromResponse(200, "{\"version\":8,\"etag\":\"\\\"v8\\\"\"}");
        Assert.Equal(EditOutcomeKind.Applied, ok.Kind);
        Assert.True(ok.Succeeded);
        Assert.Equal(8, ok.Version);
        Assert.Equal("Applied version v8", ok.Message);

        Assert.Equal("Rollback applied as new version v9", EditOutcomes.FromResponse(200, "{\"version\":9}", rollback: true).Message);

        var noVersion = EditOutcomes.FromResponse(200, "{}");
        Assert.Equal(EditOutcomeKind.Failed, noVersion.Kind);
        Assert.False(noVersion.Succeeded);
        Assert.False(EditOutcomes.FromResponse(200, "not json").Succeeded);
    }

    [Fact]
    public void Stale_edit_412_keeps_the_draft_and_names_the_new_version()
    {
        var stale = EditOutcomes.FromResponse(412, "{\"error\":\"stale\",\"reason\":\"the active configuration is v8\",\"active_version\":8}");
        Assert.Equal(EditOutcomeKind.Stale, stale.Kind);
        Assert.Equal(8, stale.Version);
        Assert.StartsWith("Configuration changed since you started editing (now v8).", stale.Message, StringComparison.Ordinal);
        Assert.Contains("kept", stale.Message, StringComparison.Ordinal);
        Assert.False(stale.Succeeded);

        Assert.Equal(EditOutcomeKind.Stale, EditOutcomes.FromResponse(409, "").Kind);
        Assert.Equal("Configuration changed since you started editing. Your draft was not applied and is kept.", EditOutcomes.StaleMessage(null));
    }

    [Fact]
    public void Validation_422_maps_errors_to_fields_and_reports_the_active_version_unchanged()
    {
        var body = "{\"error\":\"invalid_configuration\",\"active_version\":7,\"errors\":[{\"path\":\"connections[1].secret_ref\",\"code\":\"secret_unavailable\",\"message\":\"secret not set\"},{\"path\":\"policies[0].targets[1]\",\"code\":\"sensitive_cloud_target\",\"message\":\"cloud target for PII\"}],\"warnings\":[{\"path\":\"policies\",\"code\":\"no_route_for_classification\",\"message\":\"Phi has no route\"}]}";
        var invalid = EditOutcomes.FromResponse(422, body);
        Assert.Equal(EditOutcomeKind.Invalid, invalid.Kind);
        Assert.Equal(7, invalid.Version);
        Assert.Equal(2, invalid.Errors!.Count);
        Assert.Single(invalid.Warnings!);
        Assert.Contains("active version is unchanged", invalid.Message, StringComparison.Ordinal);

        var index = new IssueIndex(invalid.Errors, invalid.Warnings!);
        Assert.Single(index.ErrorsAt("connections[1]"));
        Assert.Single(index.ErrorsAt("connections[1].secret_ref"));
        Assert.Empty(index.ErrorsAt("connections[0]"));
        Assert.True(index.HasErrorAt("policies[0]"));
        Assert.Single(index.WarningsAt("policies"));
        Assert.Single(index.Unclaimed(["connections[1]"]));
        Assert.NotNull(IssueHelp.For("secret_unavailable"));
    }

    [Theory]
    [InlineData(428, EditOutcomeKind.PreconditionRequired)]
    [InlineData(401, EditOutcomeKind.Unauthorized)]
    [InlineData(404, EditOutcomeKind.NotFound)]
    [InlineData(400, EditOutcomeKind.Failed)]
    [InlineData(500, EditOutcomeKind.Failed)]
    public void Other_answers_are_never_success(int status, EditOutcomeKind kind)
    {
        var o = EditOutcomes.FromResponse(status, "{\"error\":\"x\",\"reason\":\"y\"}");
        Assert.Equal(kind, o.Kind);
        Assert.False(o.Succeeded);
        Assert.Null(o.Errors);
    }

    [Fact]
    public void Issue_paths_match_on_member_and_element_boundaries_only()
    {
        Assert.True(IssueIndex.Under("connections[1]", "connections[1]"));
        Assert.True(IssueIndex.Under("connections[1].endpoint", "connections[1]"));
        Assert.True(IssueIndex.Under("connections[1].models[0].price", "connections[1].models[0]"));
        Assert.False(IssueIndex.Under("connections[10].endpoint", "connections[1"));
        Assert.False(IssueIndex.Under("connections[10]", "connections[1]"));
        Assert.False(IssueIndex.Under("settings.cache_ttl_seconds_x", "settings.cache_ttl_seconds_"));
        Assert.False(IssueIndex.Under(null, "settings"));
        Assert.Equal("policies[2].targets[0]", IssueIndex.Target(2, 0));
        Assert.Equal("connections[0].models[1]", IssueIndex.Model(0, 1));
        Assert.Equal("settings.prompt_preview_chars", IssueIndex.Setting("prompt_preview_chars"));
    }
}

public sealed class ImpactTextTests
{
    private static ConfigImpactDto Impact() => new()
    {
        Classifications =
        [
            new ConfigImpactRowDto { Classification = "Public", Profile = "*", Before = "openai/gpt-4o-mini", After = "ollama/qwen", Changed = true },
            new ConfigImpactRowDto { Classification = "Internal", Profile = "", Before = "ollama/qwen", After = "ollama/qwen", Changed = false },
            new ConfigImpactRowDto { Classification = "Pii", Profile = "customer", Before = "ollama/qwen", After = "refused: no eligible target" }, // no flag, text differs
        ],
        NewlyRefused = ["Pii/customer"],
        PricingGaps = ["vllm/llama"],
    };

    [Fact]
    public void Only_changed_routes_are_listed_with_refusals_marked()
    {
        var lines = ImpactText.ChangedRoutes(Impact());
        Assert.Equal(2, lines.Count);
        Assert.Equal("Public", lines[0].Scope);
        Assert.Equal("openai/gpt-4o-mini", lines[0].Before);
        Assert.Equal("PII · profile customer", lines[1].Scope);
        Assert.True(lines[1].NowRefused);
        Assert.False(lines[1].WasRefused);
        Assert.Empty(ImpactText.ChangedRoutes(null));
    }

    [Fact]
    public void Headline_counts_each_kind_of_impact()
    {
        Assert.Equal("2 routes change · 1 newly refused · 1 pricing gap", ImpactText.Headline(Impact()));
        Assert.Equal("No routing change for any classification or profile.", ImpactText.Headline(new ConfigImpactDto()));
        Assert.StartsWith("Impact not computed", ImpactText.Headline(null), StringComparison.Ordinal);
        Assert.True(ImpactText.NeedsAttention(Impact()));
        Assert.False(ImpactText.NeedsAttention(new ConfigImpactDto()));
    }
}

public sealed class PolicyRoutesTests
{
    [Fact]
    public void Summary_lines_state_the_effective_route_never_may_use()
    {
        var cfg = Cfg.Hybrid();
        Assert.Equal("Public → openai/gpt-4o-mini · fallback: ollama/qwen2.5-coder:7b", PolicyRoutes.SummaryLine(cfg.Policies[0], cfg));
        Assert.Equal("Internal → ollama/qwen2.5-coder:7b · no fallback", PolicyRoutes.SummaryLine(cfg.Policies[1], cfg));
        Assert.Equal("PII + PHI → ollama/qwen2.5-coder:7b · no fallback", PolicyRoutes.SummaryLine(cfg.Policies[2], cfg));
        foreach (var p in cfg.Policies)
            Assert.DoesNotContain("may use", PolicyRoutes.SummaryLine(p, cfg), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Paused_route_is_skipped_and_paused_policy_refuses()
    {
        var cfg = Cfg.Hybrid();
        cfg.Policies[0].Targets[0].Paused = true;
        Assert.Equal("Public → ollama/qwen2.5-coder:7b · no fallback", PolicyRoutes.SummaryLine(cfg.Policies[0], cfg));
        Assert.Equal("route paused — skipped", PolicyRoutes.Targets(cfg.Policies[0], cfg)[0].ExcludedReason);

        cfg.Policies[0].Targets[1].Paused = true;
        Assert.Equal("Public → refused (no eligible target)", PolicyRoutes.SummaryLine(cfg.Policies[0], cfg));

        cfg.Policies[1].Paused = true;
        Assert.Equal("Internal → refused (policy paused)", PolicyRoutes.SummaryLine(cfg.Policies[1], cfg));
        Assert.Equal("Paused — refuses its traffic", PolicyText.StateText(cfg.Policies[1]));
        Assert.Contains(PolicyRoutes.RefusalConditions(cfg.Policies[1], cfg), l => l.Contains("paused", StringComparison.Ordinal));
    }

    [Fact]
    public void Cloud_targets_are_never_eligible_or_offered_for_sensitive_policies_or_local_only()
    {
        var cfg = Cfg.Hybrid();
        cfg.Policies[2].Targets.Add(new PolicyTargetDto { ConnectionId = "openai", ModelId = "gpt-4o-mini" });
        var states = PolicyRoutes.Targets(cfg.Policies[2], cfg);
        Assert.False(states[1].Eligible);
        Assert.Contains("PII/PHI", states[1].ExcludedReason!, StringComparison.Ordinal);

        cfg.Policies[2].Targets.RemoveAt(1);
        var options = PolicyRoutes.Options(cfg.Policies[2], cfg);
        var cloud = Assert.Single(options);
        Assert.Equal("openai/gpt-4o-mini", cloud.Value);
        Assert.Equal(PolicyText.SensitiveNeverCloud, cloud.DisabledReason);

        cfg.Mode = "local-only";
        Assert.Equal("Internal → ollama/qwen2.5-coder:7b · no fallback", PolicyRoutes.SummaryLine(cfg.Policies[1], cfg));
        Assert.Equal("Public → ollama/qwen2.5-coder:7b · no fallback", PolicyRoutes.SummaryLine(cfg.Policies[0], cfg));
        Assert.NotNull(Assert.Single(PolicyRoutes.Options(cfg.Policies[1], cfg)).DisabledReason);
    }

    [Fact]
    public void Disabled_or_unknown_connections_and_unpermitted_models_are_excluded()
    {
        var cfg = Cfg.Hybrid();
        cfg.Connections[1].Enabled = false;
        cfg.Policies[1].Targets.Add(new PolicyTargetDto { ConnectionId = "ghost", ModelId = "m" });
        cfg.Policies[1].Targets.Add(new PolicyTargetDto { ConnectionId = "ollama", ModelId = "not-permitted" });
        Assert.Equal("Public → ollama/qwen2.5-coder:7b · no fallback", PolicyRoutes.SummaryLine(cfg.Policies[0], cfg));
        var states = PolicyRoutes.Targets(cfg.Policies[1], cfg);
        Assert.Contains("not configured", states[1].ExcludedReason!, StringComparison.Ordinal);
        Assert.Contains("not permitted", states[2].ExcludedReason!, StringComparison.Ordinal);
        Assert.Empty(PolicyRoutes.Options(cfg.Policies[0], cfg)); // disabled connection offers nothing; ollama already listed
    }

    [Fact]
    public void Length_heuristic_is_named_honestly_with_its_threshold()
    {
        var cfg = Cfg.Hybrid();
        var p = cfg.Policies[0];
        p.Strategy = "length-heuristic";
        p.ThresholdTokens = 750;
        Assert.Equal("Length-based heuristic (threshold 750 tokens)", PolicyText.StrategyName(p));
        Assert.Equal("Public → ≤ 750 tokens: openai/gpt-4o-mini · above: ollama/qwen2.5-coder:7b", PolicyRoutes.SummaryLine(p, cfg));
        Assert.Equal("Fixed + fallback", PolicyText.StrategyName(cfg.Policies[1]));
    }

    [Fact]
    public void Fallback_reorder_moves_one_place_and_stops_at_the_ends()
    {
        var list = new List<PolicyTargetDto>
        {
            new() { ConnectionId = "a", ModelId = "1" }, new() { ConnectionId = "b", ModelId = "2" }, new() { ConnectionId = "c", ModelId = "3" },
        };
        Assert.False(PolicyRoutes.MoveUp(list, 0));
        Assert.False(PolicyRoutes.MoveDown(list, 2));
        Assert.False(PolicyRoutes.MoveUp(list, 7));
        Assert.True(PolicyRoutes.MoveUp(list, 2));
        Assert.Equal(new[] { "a", "c", "b" }, list.Select(t => t.ConnectionId));
        Assert.True(PolicyRoutes.MoveDown(list, 0));
        Assert.Equal(new[] { "c", "a", "b" }, list.Select(t => t.ConnectionId));
        Assert.Equal("Primary", PolicyRoutes.RoleLabel(0));
        Assert.Equal("Fallback 2", PolicyRoutes.RoleLabel(2));
    }

    [Fact]
    public void Hybrid_defaults_build_a_restrictive_draft_and_need_a_local_model()
    {
        var cfg = Cfg.Hybrid();
        cfg.Mode = "legacy-cloud";
        cfg.Policies = [new PolicyDto { Id = "legacy-general", Enabled = true, Classifications = ["Public", "Internal"], Targets = [new PolicyTargetDto { ConnectionId = "openai", ModelId = "gpt-4o-mini" }] }];
        Assert.True(PolicyRoutes.IsLegacy(cfg.Mode));

        Assert.Null(HybridDefaults.ApplyTo(cfg));
        Assert.Equal("hybrid", cfg.Mode);
        Assert.Equal(new[] { "public", "internal", "sensitive" }, cfg.Policies.Select(p => p.Id));
        Assert.Equal(new[] { "openai/gpt-4o-mini", "ollama/qwen2.5-coder:7b" }, cfg.Policies[0].Targets.Select(PolicyRoutes.Key));
        Assert.All(cfg.Policies.Skip(1), p => Assert.Equal(new[] { "ollama/qwen2.5-coder:7b" }, p.Targets.Select(PolicyRoutes.Key)));
        Assert.Equal(new[] { "Pii", "Phi" }, cfg.Policies[2].Classifications);

        var noLocal = Cfg.Hybrid();
        noLocal.Connections.RemoveAt(0);
        var before = ConfigCopy.Canonical(noLocal);
        Assert.NotNull(HybridDefaults.ApplyTo(noLocal));
        Assert.Equal(before, ConfigCopy.Canonical(noLocal)); // untouched when it cannot comply
    }
}

public sealed class SegmentScopeTests
{
    [Fact]
    public void Current_segment_is_the_hosts_explicit_flag_only()
    {
        Assert.True(SegmentScope.IsCurrent(new AuditSegmentDto { Id = "seg-2", Current = true }));
        // Never inferred from the id or from the list order.
        Assert.False(SegmentScope.IsCurrent(new AuditSegmentDto { Id = "current", Current = false }));
        Assert.False(SegmentScope.IsCurrent(new AuditSegmentDto { Id = "a-0123456789abcdef" }));
    }

    [Fact]
    public void Page_scope_comes_from_the_segment_descriptor()
    {
        var first = new DateTimeOffset(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);
        Assert.Equal("Current segment · since 2026-10-16 09:00:00 UTC",
            SegmentScope.Label(new SegmentInfo { Id = "current", Label = "Current segment", IsCurrent = true, FirstUtc = first }));
        Assert.Equal("Archived segment Archived 2026-10-01 09:00:00 UTC · read-only",
            SegmentScope.Label(new SegmentInfo { Id = "a-0123456789abcdef", Label = "Archived 2026-10-01 09:00:00 UTC", IsCurrent = false }));
        // An archived page whose id happens to be "current" is still archived: the flag decides.
        Assert.StartsWith("Archived segment", SegmentScope.Label(new SegmentInfo { Id = "current", IsCurrent = false }), StringComparison.Ordinal);
    }

    [Fact]
    public void Labels_say_which_segment_and_that_filters_cover_only_loaded_records()
    {
        var first = new DateTimeOffset(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);
        Assert.Equal("Current segment · since 2026-10-16 09:00:00 UTC", SegmentScope.Label(new AuditSegmentDto { Id = "current", Current = true, FirstUtc = first }));
        Assert.Equal("Archived segment Oct 1 · read-only", SegmentScope.Label(new AuditSegmentDto { Id = "a1", Label = "Oct 1" }));
        Assert.Equal("Archived · a1 (1,200 records)", SegmentScope.Option(new AuditSegmentDto { Id = "a1", Entries = 1200 }));

        var cov = SegmentScope.Coverage("Current segment", 200, 1500, moreAvailable: true, filtersActive: true);
        Assert.Contains("200 of 1,500 records loaded", cov, StringComparison.Ordinal);
        Assert.Contains("Load older", cov, StringComparison.Ordinal);
        Assert.Contains("only to these loaded records", cov, StringComparison.Ordinal);
        Assert.DoesNotContain("all history", cov, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Load older", SegmentScope.Coverage("Current segment", 200, 1500, moreAvailable: false, filtersActive: false), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, null, true, EvidenceCompletenessState.Pending)]
    [InlineData(false, true, false, EvidenceCompletenessState.Unavailable)]
    [InlineData(true, true, false, EvidenceCompletenessState.Verified)]
    [InlineData(true, false, false, EvidenceCompletenessState.Unverifiable)]
    [InlineData(true, null, true, EvidenceCompletenessState.Recorded)]
    public void Evidence_is_verified_only_when_present_in_a_verified_chain(bool found, bool? chainValid, bool pending, EvidenceCompletenessState expected)
    {
        var s = EvidenceCompleteness.State(found, chainValid, pending);
        Assert.Equal(expected, s);
        var (label, tone) = EvidenceCompleteness.Text(s);
        Assert.Equal(expected == EvidenceCompletenessState.Verified, label.StartsWith("Audit integrity verified", StringComparison.Ordinal));
        Assert.Equal(expected == EvidenceCompletenessState.Verified, tone == "ok");
    }
}

public sealed class BudgetTextTests
{
    [Fact]
    public void Thresholds_parse_as_percentages_and_reject_garbage()
    {
        Assert.Equal(new[] { 0.8m, 0.95m }, BudgetText.ParseThresholds("95, 80"));
        Assert.Equal(new[] { 0.5m }, BudgetText.ParseThresholds("50%"));
        Assert.Empty(BudgetText.ParseThresholds("")!);
        Assert.Null(BudgetText.ParseThresholds("0"));
        Assert.Null(BudgetText.ParseThresholds("120"));
        Assert.Null(BudgetText.ParseThresholds("eighty"));
        Assert.Equal("80, 95", BudgetText.FormatThresholds([0.8m, 0.95m]));
        Assert.Equal("80%, 95%", BudgetText.Thresholds([0.8m, 0.95m]));
    }

    [Fact]
    public void Server_states_are_worded_and_overrun_is_never_truncated()
    {
        Assert.Equal(("Cap reached — new paid work blocked", "bad"), BudgetText.Warning("exceeded"));
        Assert.Equal("warn", BudgetText.Warning("critical").Tone);
        Assert.Equal("Exceeded by $0.000184", BudgetText.Remaining(-0.000184m));
        Assert.Equal("$1.50", BudgetText.Remaining(1.5m));
        Assert.Equal("Monthly (UTC) · 2026-10", BudgetText.Period("monthly", "2026-10"));
        Assert.Equal("Team risk", BudgetText.Scope("team:risk"));
        Assert.Equal("Application demo-app", BudgetText.Scope("application:demo-app"));
        Assert.Equal(100, BudgetText.MeterPercent(5m, 1m, 4m));
        Assert.Equal(100, BudgetText.MeterPercent(0.01m, 0m, 0m));
        Assert.Equal(25, BudgetText.MeterPercent(0.5m, 0.5m, 4m));
        Assert.StartsWith("Warn only", BudgetText.Enforcement("warn-only"), StringComparison.Ordinal);
    }

    [Fact]
    public void Local_only_zero_spend_is_detected_for_the_explanatory_empty_state()
    {
        Assert.True(BudgetText.NoSpendAnywhere([new BudgetStateDto { Id = "g", CapUsd = 10m }]));
        Assert.False(BudgetText.NoSpendAnywhere([new BudgetStateDto { Id = "g", CapUsd = 10m, ReservedUsd = 0.0001m }]));
        Assert.False(BudgetText.NoSpendAnywhere(null));
    }
}

public sealed class ConnectionFormTests
{
    private static readonly Dictionary<string, SecretAvailabilityDto> Secrets = new()
    {
        ["Secrets:openai"] = new SecretAvailabilityDto { Available = true },
        ["Secrets:anthropic"] = new SecretAvailabilityDto { Available = false },
    };

    [Fact]
    public void Discovery_never_approves_and_local_prices_default_to_explicit_zero_marginal()
    {
        var f = ConnectionForm.NewOllama(["ollama"]);
        Assert.Equal("ollama-2", f.Id);
        f.Endpoint = "http://ollama-host:11434/v1";
        f.ApplyDiscovery(["qwen2.5-coder:7b", "llama3:8b"]);
        Assert.Equal(2, f.Models.Count);
        Assert.All(f.Models, m => Assert.False(m.Selected));
        Assert.All(f.Models, m => Assert.Equal(ConnectionForm.BasisLocal, m.Basis));
        Assert.All(f.Models, m => Assert.Equal(0m, m.InputPer1M));

        Assert.Contains(f.Problems([], null), p => p.StartsWith("Select at least one", StringComparison.Ordinal));
        f.Models[1].Selected = true;
        Assert.Empty(f.Problems(["ollama"], null));

        var dto = f.ToConnection();
        var model = Assert.Single(dto.Models);       // unselected tags are not saved
        Assert.Equal("qwen2.5-coder:7b", model.ModelId);
        Assert.Equal("zero-marginal-local", model.Price!.Basis);
        Assert.Equal("Local", dto.Boundary);
        Assert.Null(dto.SecretRef);
    }

    [Fact]
    public void Editing_prefills_actual_values_and_marks_what_the_endpoint_no_longer_reports()
    {
        var conn = Cfg.Hybrid().Connections[0];
        var f = ConnectionForm.FromConnection(conn);
        Assert.False(f.IsNew);
        Assert.Equal("http://ollama-host:11434/v1", f.Endpoint);
        var row = Assert.Single(f.Models);
        Assert.True(row.Selected);
        Assert.True(row.Configured);

        f.ApplyDiscovery(["other:1b"]);
        Assert.False(f.Models[0].ReportedByEndpoint);
        Assert.False(f.Models[1].Selected);
        Assert.Equal(0, f.SentIndex(f.Models[0]));
        Assert.Null(f.SentIndex(f.Models[1]));
    }

    [Fact]
    public void Cloud_needs_prices_and_an_available_secret_reference()
    {
        var f = ConnectionForm.NewCloud("Anthropic", []);
        Assert.Equal("Cloud", f.Boundary);
        f.SetKind("OpenAi");
        Assert.Equal("Cloud", f.Boundary);
        var m = f.AddModel("gpt-4o-mini");
        Assert.True(m.Selected);
        Assert.False(m.Priced);
        Assert.Null(m.InputPer1M); // cloud prices are never assumed

        var problems = f.Problems([], Secrets);
        Assert.Contains(problems, p => p.Contains("never treated as $0", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("Choose the server-side secret", StringComparison.Ordinal));

        m.Priced = true;
        Assert.Contains(f.Problems([], Secrets), p => p.Contains("enter both input and output prices", StringComparison.Ordinal));

        m.InputPer1M = 0.15m;
        m.OutputPer1M = 0.6m;
        f.SecretRef = "Secrets:anthropic";
        Assert.Contains(f.Problems([], Secrets), p => p.Contains("not available on the server", StringComparison.Ordinal));
        f.SecretRef = "Secrets:openai";
        Assert.Empty(f.Problems([], Secrets));

        m.Priced = false;
        Assert.Contains(f.Problems([], Secrets), p => p.Contains("never treated as $0", StringComparison.Ordinal));
        Assert.Null(f.ToConnection().Models[0].Price); // unpriced stays unknown, never $0
    }

    [Fact]
    public void Apply_to_draft_replaces_its_own_earlier_copy_even_after_an_id_change()
    {
        var draft = Cfg.Hybrid();
        var f = ConnectionForm.NewOllama(draft.Connections.Select(c => c.Id));
        f.Endpoint = "http://second:11434/v1";
        f.AddModel("phi3:mini");
        int i = f.ApplyTo(draft);
        Assert.Equal(2, i);
        f.Id = "ollama-b";
        Assert.Equal(2, f.ApplyTo(draft));
        Assert.Equal(new[] { "ollama", "openai", "ollama-b" }, draft.Connections.Select(c => c.Id));

        var edit = ConnectionForm.FromConnection(draft.Connections[0]);
        edit.Enabled = false;
        Assert.Equal(0, edit.ApplyTo(draft));
        Assert.False(draft.Connections[0].Enabled);
        Assert.Equal(3, draft.Connections.Count);
    }

    [Fact]
    public void Removing_a_routed_model_is_called_out_before_preview()
    {
        var cfg = Cfg.Hybrid();
        var f = ConnectionForm.FromConnection(cfg.Connections[0]);
        f.Models[0].Selected = false;
        Assert.Equal(new[] { "ollama/qwen2.5-coder:7b" }, f.RemovedModelsStillRouted(cfg));
    }

    [Theory]
    [InlineData("Secrets:openai", true)]
    [InlineData("OpenAI:ApiKey", true)]
    [InlineData("Secrets:", false)]
    [InlineData("Env:OPENAI", false)]
    [InlineData("Secrets:bad name", false)]
    public void Secret_references_are_restricted(string r, bool ok) => Assert.Equal(ok, ConnectionRules.IsValidSecretRef(r));

    [Fact]
    public void Connection_rules_helpers()
    {
        Assert.Equal("Secrets__openai", ConnectionRules.EnvVarFor("Secrets:openai"));
        Assert.Equal("api.openai.com:443", ConnectionRules.AuthorityOf("OpenAi", null));
        Assert.Equal("host:8000", ConnectionRules.AuthorityOf("OpenAiCompatible", "http://host:8000/v1"));
        Assert.False(ConnectionRules.IsValidId("has space"));
        Assert.True(ConnectionRules.EndpointRequired("Ollama"));
        Assert.False(ConnectionRules.EndpointRequired("Anthropic"));
    }
}

public sealed class ConnectionHealthTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_newer_failure_wins_and_both_facts_are_stated()
    {
        var h = new ModelHealth { State = "failed", LastSuccessUtc = Now.AddMinutes(-30), LastFailureUtc = Now.AddMinutes(-2), LastError = "timeout" };
        var v = ConnectionHealth.Summarize(true, [h], null, null, null, Now);
        Assert.Equal("bad", v.Tone);
        Assert.Equal("Last governed call failed 2 min ago · last success 30 min ago", v.Summary);
        Assert.Contains(v.Details, d => d.Label == "Last failed governed call" && d.Value.Contains("timeout", StringComparison.Ordinal));
    }

    [Fact]
    public void A_reachable_probe_is_not_called_ready_when_no_call_succeeded()
    {
        var probe = new ProbeResponse { Allowed = true, Reachable = true, CheckedUtc = Now.AddSeconds(-5) };
        var v = ConnectionHealth.Summarize(true, [new ModelHealth { State = "unknown" }], probe, null, null, Now);
        Assert.Equal("Endpoint reachable (probe 5 s ago) · no governed call observed yet", v.Summary);
        Assert.DoesNotContain("ready", v.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("hollow", v.Tone);
        Assert.Contains(v.Details, d => d.Label == "Last successful governed call" && d.Value == "None observed");
    }

    [Fact]
    public void Probe_failure_disabled_and_unobserved_states()
    {
        var refused = new ProbeResponse { Allowed = false, Reachable = false };
        var v = ConnectionHealth.Summarize(true, [], refused, Now.AddMinutes(-1), null, Now);
        Assert.Equal("bad", v.Tone);
        Assert.Contains(v.Details, d => d.Value.Contains("not on the probe allow-list", StringComparison.Ordinal));

        Assert.StartsWith("Disabled", ConnectionHealth.Summarize(false, [], null, null, null, Now).Summary, StringComparison.Ordinal);
        Assert.StartsWith("Not observed yet", ConnectionHealth.Summarize(true, [], null, null, null, Now).Summary, StringComparison.Ordinal);

        var ok = ConnectionHealth.Summarize(true, [new ModelHealth { State = "ok", LastSuccessUtc = Now.AddHours(-3) }], null, null,
            new ModelHealth { LastFailureUtc = Now.AddHours(-1), LastError = "refused" }, Now);
        Assert.Equal("Last successful governed call 3 h ago", ok.Summary); // inference fact first; probe is a separate detail
        Assert.Contains(ok.Details, d => d.Label == "Last endpoint probe" && d.Value.StartsWith("Failed", StringComparison.Ordinal));
    }
}

public sealed class RequestFilterApplicationTests
{
    [Fact]
    public void Application_filter_matches_v3_application_id_only()
    {
        var a = Rec.V2();
        a.ApplicationId = "demo-app";
        var legacy = Rec.Legacy();
        var c = RequestFilterCriteria.None with { Application = "DEMO-APP" };
        Assert.True(c.IsActive);
        Assert.True(RequestFilter.Matches(a, c, DateTimeOffset.UtcNow));
        Assert.False(RequestFilter.Matches(legacy, c, DateTimeOffset.UtcNow));
        Assert.Equal(new[] { "demo-app" }, RequestFilter.Applications([a, legacy]));
    }
}

// ---- Review round 2 (UI): pure logic behind U2/U4/U6/U7/U8/U9/U10 ----

public sealed class SubmissionPollTests
{
    private const string Client = "0123456789abcdef0123456789abcdef";
    private const string Req = "req_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static PlaygroundSubmissionDto Found(string state = "running") => new() { ClientRequestId = Client, RequestId = Req, State = state };

    [Fact]
    public void Not_registered_yet_polls_again_then_subscribes_exactly_once()
    {
        var p = new SubmissionPoll(Client);
        Assert.True(p.ShouldPoll);
        Assert.Equal(SubmissionPollAction.PollAgain, p.OnPollResult(404, null));
        Assert.Equal(SubmissionPollAction.PollAgain, p.OnPollResult(null, null));   // transport hiccup: bounded retry
        Assert.Equal(SubmissionPollAction.Subscribe, p.OnPollResult(200, Found()));
        Assert.Equal(Req, p.RequestId);
        Assert.Equal("submission lookup", p.Source);
        Assert.False(p.ShouldPoll);
        Assert.Equal(3, p.Polls);

        // Later answers and the finished POST never start a second subscription.
        Assert.Equal(SubmissionPollAction.Stop, p.OnPollResult(200, Found("completed")));
        Assert.Equal(SubmissionPollAction.Stop, p.OnSendCompleted(Req));
        Assert.True(p.Stopped);
    }

    [Fact]
    public void A_body_for_another_submission_is_not_accepted()
    {
        var p = new SubmissionPoll(Client);
        var other = new PlaygroundSubmissionDto { ClientRequestId = "ffff", RequestId = "req_x", State = "running" };
        Assert.Equal(SubmissionPollAction.PollAgain, p.OnPollResult(200, other));
        Assert.Null(p.RequestId);
    }

    [Fact]
    public void Response_id_is_the_backstop_when_polling_found_nothing()
    {
        var p = new SubmissionPoll(Client);
        Assert.Equal(SubmissionPollAction.PollAgain, p.OnPollResult(404, null));
        Assert.Equal(SubmissionPollAction.Subscribe, p.OnSendCompleted(Req));
        Assert.Equal("response", p.Source);
        Assert.False(p.ShouldPoll);

        var none = new SubmissionPoll(Client);
        Assert.Equal(SubmissionPollAction.Stop, none.OnSendCompleted(null));   // 400 before governance: no id, no trace
        Assert.Null(none.RequestId);
    }

    [Fact]
    public void Unauthorized_and_the_bound_stop_polling()
    {
        var p = new SubmissionPoll(Client);
        Assert.Equal(SubmissionPollAction.Stop, p.OnPollResult(401, null));
        Assert.False(p.ShouldPoll);

        var b = new SubmissionPoll(Client);
        for (int i = 1; i < SubmissionPoll.MaxPolls; i++) Assert.Equal(SubmissionPollAction.PollAgain, b.OnPollResult(404, null));
        Assert.Equal(SubmissionPollAction.Stop, b.OnPollResult(404, null));
        Assert.False(b.ShouldPoll);
        Assert.Equal(TimeSpan.FromMilliseconds(250), SubmissionPoll.Interval);
    }

    [Fact]
    public void Client_request_ids_are_guid_n()
    {
        var id = RequestSnapshot.NewClientRequestId();
        Assert.Matches("^[0-9a-f]{32}$", id);
    }
}

public sealed class LiveTraceStartTests
{
    private static TraceEventDto E(long seq, string stage, string state, string? attempt = null, double? ms = null)
        => new() { Seq = seq, Stage = stage, State = state, AttemptId = attempt, DurationMs = ms, RequestId = "req_1" };

    [Fact]
    public void Provider_shows_running_while_waiting_with_its_output_bounds()
    {
        var acc = new TraceAccumulator();
        acc.AddRange([E(1, "auth", "passed", ms: 0.4), E(2, "budget", "passed", ms: 0.2),
            new TraceEventDto { Seq = 3, Stage = "provider", State = "running", AttemptId = "att_1", Target = "ollama/qwen",
                RequestedMaxOutputTokens = 256, EffectiveMaxOutputTokens = 64 }]);
        var v = acc.View();
        var provider = v.Steps.Single(s => s.Stage == "provider");
        Assert.Equal(TraceStepState.Running, provider.State);
        Assert.Null(provider.DurationMs);                   // no invented timing while it runs
        var a = Assert.Single(provider.Attempts);
        Assert.Equal(256, a.RequestedMaxOutputTokens);
        Assert.Equal(64, a.EffectiveMaxOutputTokens);
        Assert.True(v.HasEvents);
        Assert.Equal(TraceStepState.Pending, v.Steps.Single(s => s.Stage == "attribution").State);
    }

    [Fact]
    public void Unknown_state_spellings_are_not_guessed()
        => Assert.Equal(TraceStepState.NotReported, TraceModel.ParseState("canceled"));
}

public sealed class PolicyEditingTests
{
    private static CovenantConfigDto AllClasses()
    {
        var cfg = Cfg.Hybrid();
        cfg.Mode = "legacy-cloud";
        cfg.Profiles = [new WorkloadProfileDto { Id = "internal", Name = "Internal", Baseline = "Internal" }];
        cfg.Policies =
        [
            new PolicyDto
            {
                Id = "legacy", Name = "Imported", Enabled = true, Classifications = ["Public", "Internal", "Pii", "Phi"], Strategy = "fixed",
                Targets = [new PolicyTargetDto { ConnectionId = "openai", ModelId = "gpt-4o-mini" }, new PolicyTargetDto { ConnectionId = "ollama", ModelId = "qwen2.5-coder:7b" }],
                Retry = new RetrySettingsDto { MaxAttempts = 2, DeadlineMs = 60_000, On = ["timeout"] },
            },
        ];
        return cfg;
    }

    [Fact]
    public void Split_makes_public_cloud_with_local_fallback_and_never_cloud_for_sensitive()
    {
        var cfg = AllClasses();
        Assert.True(PolicyEditing.CanSplit(cfg.Policies[0]));
        Assert.Null(PolicyEditing.ApplySplit(cfg, "legacy"));
        Assert.Equal(2, cfg.Policies.Count);

        var pub = cfg.Policies[0];
        Assert.Equal(new[] { "Public" }, pub.Classifications);
        Assert.Equal(new[] { "openai/gpt-4o-mini", "ollama/qwen2.5-coder:7b" }, pub.Targets.Select(PolicyRoutes.Key));
        Assert.Equal(60_000, pub.Retry!.DeadlineMs);

        var restricted = cfg.Policies[1];
        Assert.Equal(new[] { "Internal", "Pii", "Phi" }, restricted.Classifications);
        Assert.Equal(new[] { "ollama/qwen2.5-coder:7b" }, restricted.Targets.Select(PolicyRoutes.Key));
        Assert.DoesNotContain(restricted.Targets, t => t.ConnectionId == "openai");
        Assert.All(PolicyRoutes.Targets(restricted, cfg), t => Assert.Equal("Local", t.Boundary));
        Assert.NotEqual(pub.Id, restricted.Id);
        Assert.DoesNotContain(cfg.Policies, p => p.Id == "legacy");
    }

    [Fact]
    public void Split_without_a_cloud_target_proposes_a_priced_cloud_model_and_local_only_mode_keeps_public_local()
    {
        var cfg = AllClasses();
        cfg.Policies[0].Targets = [new PolicyTargetDto { ConnectionId = "ollama", ModelId = "qwen2.5-coder:7b" }];
        var plan = PolicyEditing.PlanSplit(cfg.Policies[0], cfg);
        Assert.Null(plan.Problem);
        Assert.Equal("openai/gpt-4o-mini", PolicyRoutes.Key(plan.PublicPolicy!.Targets[0]));
        Assert.Contains(plan.Notes, n => n.Contains("first enabled, priced cloud model", StringComparison.Ordinal));

        cfg.Mode = "local-only";
        var local = PolicyEditing.PlanSplit(cfg.Policies[0], cfg);
        Assert.Equal(new[] { "ollama/qwen2.5-coder:7b" }, local.PublicPolicy!.Targets.Select(PolicyRoutes.Key));
        Assert.DoesNotContain(local.RestrictedPolicy!.Targets, t => t.ConnectionId == "openai");
    }

    [Fact]
    public void Split_needs_a_local_model_and_a_mixed_policy()
    {
        var cfg = AllClasses();
        cfg.Connections.RemoveAll(c => c.Boundary == "Local");
        var before = ConfigCopy.Canonical(cfg);
        Assert.NotNull(PolicyEditing.ApplySplit(cfg, "legacy"));
        Assert.Equal(before, ConfigCopy.Canonical(cfg));   // untouched

        Assert.False(PolicyEditing.CanSplit(Cfg.Hybrid().Policies[0]));   // Public only
    }

    [Fact]
    public void Create_policy_never_accepts_a_cloud_target_for_pii_or_phi()
    {
        var cfg = Cfg.Hybrid();
        var form = new NewPolicyForm { Id = "pii-cloud", Name = "PII", Classifications = ["Pii"], Targets = ["openai/gpt-4o-mini"] };
        var problems = PolicyEditing.Problems(form, cfg);
        Assert.Contains(problems, p => p.Contains(PolicyText.SensitiveNeverCloud, StringComparison.Ordinal));
        Assert.Contains(PolicyEditing.TargetOptions(["Pii"], cfg), o => o.Value == "openai/gpt-4o-mini" && o.DisabledReason is not null);
        Assert.NotEmpty(PolicyEditing.Add(cfg, form));
        Assert.Equal(3, cfg.Policies.Count);

        var ok = new NewPolicyForm { Id = "public-2", Name = "Public tickets", Classifications = ["Public"], Profiles = [], Targets = ["openai/gpt-4o-mini", "ollama/qwen2.5-coder:7b"] };
        Assert.Empty(PolicyEditing.Add(cfg, ok));
        var added = cfg.Policies[^1];
        Assert.Equal("public-2", added.Id);
        Assert.True(added.Enabled);
        Assert.Equal("fixed", added.Strategy);
        Assert.Equal(new[] { "openai/gpt-4o-mini", "ollama/qwen2.5-coder:7b" }, added.Targets.Select(PolicyRoutes.Key));

        Assert.Contains(PolicyEditing.Problems(new NewPolicyForm { Id = "public-2", Name = "x", Classifications = ["Public"], Targets = ["ollama/qwen2.5-coder:7b"] }, cfg),
            p => p.Contains("already exists", StringComparison.Ordinal));
        Assert.Contains(PolicyEditing.Problems(new NewPolicyForm { Id = "p", Name = "x", Classifications = ["Public"], Profiles = ["nope"], Targets = ["ollama/qwen2.5-coder:7b"] }, cfg),
            p => p.Contains("does not exist", StringComparison.Ordinal));
    }

    [Fact]
    public void Applicability_edits_refuse_sensitive_classes_behind_a_cloud_target()
    {
        var cfg = Cfg.Hybrid();
        var pub = cfg.Policies[0];   // openai first
        Assert.NotNull(PolicyEditing.SetClassifications(pub, ["Public", "Pii"], cfg));
        Assert.Equal(new[] { "Public" }, pub.Classifications);
        Assert.NotNull(PolicyEditing.SetClassifications(pub, [], cfg));
        Assert.Null(PolicyEditing.SetClassifications(cfg.Policies[1], ["Internal", "Public"], cfg));
        Assert.Equal(new[] { "Public", "Internal" }, cfg.Policies[1].Classifications);   // contract order

        cfg.Profiles = [new WorkloadProfileDto { Id = "internal", Name = "Internal", Baseline = "Internal" }];
        Assert.Null(PolicyEditing.SetProfiles(cfg.Policies[1], ["internal"], cfg));
        Assert.Equal(new[] { "internal" }, cfg.Policies[1].Profiles);
        Assert.NotNull(PolicyEditing.SetProfiles(cfg.Policies[1], ["ghost"], cfg));
    }

    [Fact]
    public void Removing_the_only_policy_for_a_class_says_it_will_be_refused()
    {
        var cfg = Cfg.Hybrid();
        var lines = PolicyEditing.RemovalImpact(cfg.Policies[2], cfg);
        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.Contains("REFUSED", l, StringComparison.Ordinal));
        Assert.True(PolicyEditing.Remove(cfg, "sensitive"));
        Assert.Equal(new[] { "public", "internal" }, cfg.Policies.Select(p => p.Id));
        Assert.False(PolicyEditing.Remove(cfg, "sensitive"));
    }
}

public sealed class RoundTwoCostAndFactsTests
{
    [Fact]
    public void Mixed_cost_basis_is_labelled_as_including_estimates()
    {
        var r = Rec.V2(costBasis: "mixed", cost: 0.0021m);
        Assert.Equal("≈ $0.0021 · Mixed (includes estimates)", CostText.Cost(r));
        Assert.Equal("≈ $0.0021 mixed", CostText.CostShort(r));
        Assert.Contains("includes estimates", CostText.BasisDescription("mixed"), StringComparison.Ordinal);
        Assert.Equal("≈ $0.0021 Mixed (includes estimates)", CostText.BasisCost("mixed", 0.0021m));
        Assert.DoesNotContain("reported", CostText.Cost(r), StringComparison.Ordinal);
    }

    [Fact]
    public void Requested_and_effective_output_bounds_are_labelled_separately()
    {
        var r = Rec.V2();
        r.RequestedMaxOutputTokens = 256;
        r.EffectiveMaxOutputTokens = 64;
        r.ServedConnectionId = "ollama";
        Assert.Equal("256", RecordFacts.RequestedMaxOutputTokens(r));
        Assert.Equal("64", RecordFacts.EffectiveMaxOutputTokens(r));
        Assert.Equal("Requested 256 · enforced 64", RecordFacts.BoundsLine(r));
        Assert.Equal("ollama", RecordFacts.ServedConnection(r));

        var none = Rec.V2();
        none.EffectiveMaxOutputTokens = 1024;
        Assert.StartsWith("None requested", RecordFacts.RequestedMaxOutputTokens(none), StringComparison.Ordinal);

        var old = Rec.V2();   // older record: neither field
        Assert.Equal("Not recorded", RecordFacts.RequestedMaxOutputTokens(old));
        Assert.Equal("Not recorded", RecordFacts.EffectiveMaxOutputTokens(old));
        Assert.Equal("Not recorded", RecordFacts.ServedConnection(old));
        Assert.Equal("Not recorded", RecordFacts.TruncatedText(old));
    }

    [Fact]
    public void Truncation_and_incomplete_streams_are_stated_clearly()
    {
        var t = Rec.V2();
        t.EffectiveMaxOutputTokens = 64;
        t.Attempts = [new AuditAttemptView { AttemptId = "att_1", ConnectionId = "ollama", ModelId = "qwen", Outcome = "served", EffectiveMaxOutputTokens = 64, Truncated = true }];
        Assert.True(RecordFacts.Truncated(t));
        Assert.StartsWith("Yes", RecordFacts.TruncatedText(t), StringComparison.Ordinal);
        var line = Assert.Single(AttemptFacts.Lines(t));
        Assert.True(line.Truncated);
        Assert.Equal(64, line.EffectiveMaxOutputTokens);
        Assert.EndsWith("· bound 64 · truncated (length)", AttemptFacts.Detail(line), StringComparison.Ordinal);

        var inc = Rec.Upstream();
        inc.PartialOutputChars = 120;
        inc.Attempts = [new AuditAttemptView { AttemptId = "att_1", ConnectionId = "openai", ModelId = "gpt-x", Outcome = "failed", ErrorClass = "incomplete_stream", EffectiveMaxOutputTokens = 256 }];
        Assert.True(RecordFacts.IsIncompleteStream(inc));
        var text = RecordFacts.IncompleteStreamText(inc)!;
        Assert.Contains("NOT served", text, StringComparison.Ordinal);
        Assert.Contains("120 characters", text, StringComparison.Ordinal);
        Assert.Equal("256 (last attempt — not served)", RecordFacts.EffectiveMaxOutputTokens(inc));
        Assert.Null(RecordFacts.IncompleteStreamText(t));
    }

    [Fact]
    public void Attempt_success_is_the_contract_word_served_only()
    {
        Assert.True(AttemptFacts.IsSuccess(new AuditAttemptView { Outcome = "served" }));
        Assert.False(AttemptFacts.IsSuccess(new AuditAttemptView { Outcome = "ok" }));
        Assert.False(AttemptFacts.IsSuccess(new AuditAttemptView { Outcome = "succeeded" }));
    }

    [Theory]
    [InlineData("Ollama")]
    [InlineData("OpenAi")]
    [InlineData("Anthropic")]
    [InlineData("OpenAiCompatible")]
    public void New_connections_start_with_a_120_second_timeout(string kind)
    {
        var f = kind == "Ollama" ? ConnectionForm.NewOllama([]) : ConnectionForm.NewCloud(kind, []);
        Assert.Equal(120, f.TimeoutSeconds);
        Assert.Equal(ConnectionForm.DefaultTimeoutSeconds, f.TimeoutSeconds);
        Assert.Equal(120, f.ToConnection().TimeoutSeconds);
        Assert.DoesNotContain(f.Problems([], null), p => p.Contains("Timeout", StringComparison.Ordinal));
    }
}

public sealed class RequestPagingTests
{
    private static RequestsResponse Page(string id, bool current, string? cursor, int items = 2)
        => new()
        {
            Segment = new SegmentInfo { Id = id, Label = current ? "Current segment" : "Archived 2026-10-01 09:00:00 UTC", IsCurrent = current, Entries = 10, ChainValid = true },
            Items = Enumerable.Range(0, items).Select(i => Rec.V2(id: "req_" + id + i.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToList(),
            NextCursor = cursor,
        };

    [Fact]
    public void Current_pages_follow_next_cursor_until_the_last_page()
    {
        var p = new RequestPaging();
        p.Reset(null);
        Assert.False(p.CanLoadOlder);
        Assert.Equal(PageResult.Accepted, p.ApplyPage(Page("current", true, "c2-a"), older: false));
        Assert.True(p.CanLoadOlder);
        Assert.Equal("c2-a", p.NextCursor);
        Assert.Equal("Current segment", p.Segment!.Label);
        Assert.Equal(PageResult.Accepted, p.ApplyPage(Page("current", true, null), older: true));
        Assert.False(p.CanLoadOlder);
        Assert.True(p.Loaded);
    }

    [Fact]
    public void Scope_is_the_descriptor_never_the_request()
    {
        var p = new RequestPaging();
        p.Reset(null);
        // Asked for the current segment, but the Host says this page is archived: refused, nothing shown.
        Assert.Equal(PageResult.SegmentMismatch, p.ApplyPage(Page("a-0123456789abcdef", false, "x"), older: false));
        Assert.False(p.Loaded);
        Assert.NotNull(p.Problem);

        var a = new RequestPaging();
        a.Reset("a-0123456789abcdef");
        Assert.Equal(PageResult.SegmentMismatch, a.ApplyPage(Page("current", true, "x"), older: false));
        Assert.Equal(PageResult.Accepted, a.ApplyPage(Page("a-0123456789abcdef", false, "y"), older: false));
        Assert.Equal(PageResult.SegmentMismatch, a.ApplyPage(Page("a-ffffffffffffffff", false, null), older: true));   // older page of another segment
        Assert.Null(a.NextCursor);
    }

    [Fact]
    public void Cursor_expired_410_stops_paging_and_asks_for_a_reload()
    {
        var p = new RequestPaging();
        p.Reset(null);
        p.ApplyPage(Page("current", true, "c2-a"), older: false);
        Assert.Equal(PageResult.CursorExpired, p.ApplyError(410, "cursor_expired", "410: the segment was rotated"));
        Assert.True(p.Expired);
        Assert.False(p.CanLoadOlder);
        Assert.Null(p.NextCursor);
        Assert.Equal("The audit segment changed (archived) — reload the list.", p.Problem);

        Assert.Equal(PageResult.Failed, p.ApplyError(400, "invalid_cursor", "400: bad"));
        Assert.Contains("invalid_cursor", p.Problem, StringComparison.Ordinal);
        Assert.Equal(PageResult.Failed, p.ApplyError(410, "expired", "410: other"));   // only cursor_expired means "reload"

        p.Reset(null);
        Assert.False(p.Expired);
        Assert.Null(p.Problem);
        Assert.Equal(PageResult.Accepted, p.ApplyPage(Page("current", true, "c2-b"), older: false));
        Assert.True(p.CanLoadOlder);
    }

    [Fact]
    public void An_empty_page_has_no_older_cursor()
    {
        var p = new RequestPaging();
        p.Reset(null);
        Assert.Equal(PageResult.Accepted, p.ApplyPage(Page("current", true, "c2-a", items: 0), older: false));
        Assert.False(p.CanLoadOlder);
    }
}

public sealed class HeaderLocalHealthTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    private static CatalogResponse Catalog(ModelHealth? health, ModelHealth? endpoint = null) => new()
    {
        Mode = "hybrid",
        Local = new LocalRouteInfo
        {
            Configured = true, Registered = true, ConnectionId = "ollama-2", ModelId = "qwen2.5-coder:7b", Endpoint = "http://localhost:11434/v1",
            Health = health, EndpointHealth = endpoint ?? new ModelHealth(),
        },
        // A stale legacy connection literally named "local" must never be consulted.
        Models = [new CatalogModel { Adapter = "local", ModelId = "qwen2.5-coder:7b", Registered = true, Health = new ModelHealth { State = "failed", LastFailureUtc = Now } }],
    };

    private static ConnectionHealthDto Row(string key, ModelHealth h) => new()
    {
        Key = key, ConnectionId = key.Split(':')[0], ModelId = key[(key.IndexOf(':') + 1)..], Boundary = "Local", Enabled = true, Health = h,
    };

    [Fact]
    public void A_successful_governed_local_call_is_ready_from_status_connection_health()
    {
        var v = LocalHealth.Derive(Catalog(new ModelHealth()), [Row("ollama-2:qwen2.5-coder:7b", new ModelHealth { State = "ok", LastSuccessUtc = Now })]);
        Assert.Equal(LocalRouteState.Ready, v.State);
        Assert.Equal("status connection_health", v.Source);
        Assert.Equal("ollama-2", v.ConnectionId);
    }

    [Fact]
    public void Catalog_local_health_is_used_when_status_has_no_row_and_the_legacy_local_adapter_is_ignored()
    {
        var v = LocalHealth.Derive(Catalog(new ModelHealth { State = "ok", LastSuccessUtc = Now }), null);
        Assert.Equal(LocalRouteState.Ready, v.State);
        Assert.Equal("catalog local.health", v.Source);

        var unobserved = LocalHealth.Derive(Catalog(new ModelHealth()), [Row("local:qwen2.5-coder:7b", new ModelHealth { State = "failed", LastFailureUtc = Now })]);
        Assert.Equal(LocalRouteState.NotObserved, unobserved.State);   // another connection's row is not this one's health
    }

    [Fact]
    public void Endpoint_health_only_when_runtime_health_is_unknown_and_newer_failures_degrade()
    {
        var probe = LocalHealth.Derive(Catalog(new ModelHealth { State = "unknown" }, new ModelHealth { State = "ok", LastSuccessUtc = Now }), []);
        Assert.Equal(LocalRouteState.Ready, probe.State);
        Assert.Equal("catalog local.endpoint_health", probe.Source);

        var degraded = LocalHealth.Derive(Catalog(new ModelHealth { State = "failed", LastSuccessUtc = Now.AddMinutes(-5), LastFailureUtc = Now, LastError = "timeout" },
            new ModelHealth { State = "ok", LastSuccessUtc = Now }), null);
        Assert.Equal(LocalRouteState.Degraded, degraded.State);
        Assert.Equal("timeout", degraded.Health!.LastError);
    }

    [Fact]
    public void Not_configured_not_registered_and_unknown()
    {
        Assert.Equal(LocalRouteState.Unknown, LocalHealth.Derive(null, null).State);
        Assert.Equal(LocalRouteState.NotConfigured, LocalHealth.Derive(new CatalogResponse { Local = new LocalRouteInfo { Configured = false } }, null).State);
        Assert.Equal(LocalRouteState.NotRegistered,
            LocalHealth.Derive(new CatalogResponse { Local = new LocalRouteInfo { Configured = true, Registered = false, ConnectionId = "ollama" } }, null).State);
    }
}

// ---- Review round 3 (UI) ----

public sealed class ClassificationFactTests
{
    private static AuditEntryView With(string classification, string? baseline, string? signal)
    {
        var r = Rec.V2(classification: classification, signal: signal);
        r.BaselineClassification = baseline;
        return r;
    }

    [Fact]
    public void Escalation_names_baseline_and_detector()
        => Assert.Equal("PII (baseline Public, escalated: US SSN pattern)", ModelFacts.ClassificationFact(With("Pii", "Public", "US SSN pattern")));

    [Fact]
    public void Escalation_without_a_recorded_signal_still_says_escalated()
        => Assert.Equal("PHI (baseline Internal, escalated)", ModelFacts.ClassificationFact(With("Phi", "Internal", null)));

    [Fact]
    public void Same_as_baseline_is_not_escalated()
    {
        Assert.Equal("Internal (baseline Internal, not escalated)", ModelFacts.ClassificationFact(With("Internal", "Internal", null)));
        Assert.Equal("PII (baseline PII, not escalated; detector: US SSN pattern)", ModelFacts.ClassificationFact(With("Pii", "Pii", "US SSN pattern")));
    }

    [Fact]
    public void Missing_baseline_is_said_not_guessed()
    {
        Assert.Equal("PII (detector: US SSN pattern; baseline not recorded)", ModelFacts.ClassificationFact(With("Pii", null, "US SSN pattern")));
        Assert.Equal("Internal (no detector rule matched; baseline not recorded)", ModelFacts.ClassificationFact(With("Internal", null, null)));
    }

    [Fact]
    public void Not_evaluated_and_legacy_records_never_show_the_restrictive_default_as_a_detection()
    {
        Assert.StartsWith("Not evaluated — stopped at", ModelFacts.ClassificationFact(Rec.AuthFail()), StringComparison.Ordinal);
        Assert.DoesNotContain("PHI", ModelFacts.ClassificationFact(Rec.AuthFail()), StringComparison.Ordinal);
        Assert.Equal(ModelFacts.NotRecorded, ModelFacts.ClassificationFact(Rec.Legacy()));
    }

    [Fact]
    public void Cache_provenance_uses_the_served_connection_when_the_model_lacks_it()
    {
        var r = Rec.V2(cacheHit: true, attempted: null, served: "stub-slow");
        r.ServedConnectionId = "local";
        Assert.Equal("local/stub-slow", ModelFacts.CacheOrigin(r));
        Assert.Equal("Cache — no provider call (originally local/stub-slow)", ModelFacts.Destination(r).Text);
        Assert.Equal("Served from cache (no provider call) · local/stub-slow", ModelFacts.Served(r));

        var full = Rec.V2(cacheHit: true, attempted: null, served: "ollama/qwen");
        full.ServedConnectionId = "ollama";
        Assert.Equal("ollama/qwen", ModelFacts.CacheOrigin(full));
        Assert.Null(ModelFacts.CacheOrigin(Rec.V2(cacheHit: true, attempted: null, served: null)));
    }
}

public sealed class ProfileEditingTests
{
    /// <summary>Hybrid config + two profiles and two existing applications (demo grants both, default internal).</summary>
    private static CovenantConfigDto WithProfiles()
    {
        var cfg = Cfg.Hybrid();
        cfg.Profiles =
        [
            new WorkloadProfileDto { Id = "public-demo", Name = "Public demo", Baseline = "Public" },
            new WorkloadProfileDto { Id = "internal-default", Name = "Internal", Baseline = "Internal" },
        ];
        cfg.Applications =
        [
            new ApplicationDto { Id = "demo", Team = "payments", Enabled = true, ConsoleTesting = true, GrantedProfiles = ["public-demo", "internal-default"], DefaultProfile = "internal-default", MaxOutputTokens = 256 },
            new ApplicationDto { Id = "batch", Team = "risk", Enabled = true, ConsoleTesting = false, GrantedProfiles = ["internal-default"], DefaultProfile = "internal-default" },
        ];
        return cfg;
    }

    [Fact]
    public void Create_profile_validates_id_name_baseline_and_is_granted_to_nobody()
    {
        var cfg = WithProfiles();
        Assert.Contains(ProfileEditing.Problems(new NewProfileForm { Id = "bad id!", Name = "x", Baseline = "Public" }, cfg), p => p.Contains("Profile id", StringComparison.Ordinal));
        Assert.Contains(ProfileEditing.Problems(new NewProfileForm { Id = "public-demo", Name = "x", Baseline = "Public" }, cfg), p => p.Contains("already exists", StringComparison.Ordinal));
        Assert.Contains(ProfileEditing.Problems(new NewProfileForm { Id = ProfileEditing.LegacyInternal, Name = "x", Baseline = "Internal" }, cfg), p => p.Contains("built-in", StringComparison.Ordinal));
        Assert.Contains(ProfileEditing.Problems(new NewProfileForm { Id = "p", Name = " ", Baseline = "Public" }, cfg), p => p.Contains("name", StringComparison.Ordinal));
        Assert.Contains(ProfileEditing.Problems(new NewProfileForm { Id = "p", Name = "x", Baseline = "Secret" }, cfg), p => p.Contains("baseline", StringComparison.Ordinal));
        Assert.Contains(ProfileEditing.Problems(new NewProfileForm { Id = "p", Name = "x", Baseline = "pii" }, cfg), p => p.Contains("baseline", StringComparison.Ordinal));   // contract spelling only

        var before = ConfigCopy.Canonical(cfg);
        Assert.NotEmpty(ProfileEditing.Add(cfg, new NewProfileForm { Id = "", Name = "x" }));
        Assert.Equal(before, ConfigCopy.Canonical(cfg));   // refused ⇒ draft untouched

        Assert.Empty(ProfileEditing.Add(cfg, new NewProfileForm { Id = " customer-pii ", Name = " Customer PII ", Baseline = "Pii", Description = "  " }));
        var added = cfg.Profiles[^1];
        Assert.Equal("customer-pii", added.Id);
        Assert.Equal("Customer PII", added.Name);
        Assert.Equal("Pii", added.Baseline);
        Assert.Null(added.Description);
        Assert.Empty(ProfileEditing.Usage(cfg, "customer-pii").GrantedTo);
        Assert.NotEqual("customer-pii", ProfileEditing.UniqueId("customer-pii", cfg));
    }

    [Fact]
    public void Edit_name_and_baseline_refuse_blank_or_unknown_values()
    {
        var cfg = WithProfiles();
        var p = cfg.Profiles[0];
        Assert.NotNull(ProfileEditing.SetName(p, "  "));
        Assert.Equal("Public demo", p.Name);
        Assert.Null(ProfileEditing.SetName(p, " Public tickets "));
        Assert.Equal("Public tickets", p.Name);
        Assert.NotNull(ProfileEditing.SetBaseline(p, "Confidential"));
        Assert.Equal("Public", p.Baseline);
        Assert.Null(ProfileEditing.SetBaseline(p, "Internal"));
        Assert.Equal("Internal", p.Baseline);
        ProfileEditing.SetDescription(p, " ");
        Assert.Null(p.Description);
    }

    [Fact]
    public void Removing_a_default_profile_is_refused_client_side_with_the_reason()
    {
        var cfg = WithProfiles();
        var blockers = ProfileEditing.RemovalBlockers(cfg, "internal-default");
        Assert.Equal(2, blockers.Count);   // default of demo and of batch
        Assert.All(blockers, b => Assert.Contains("default profile", b, StringComparison.Ordinal));
        var before = ConfigCopy.Canonical(cfg);
        Assert.NotNull(ProfileEditing.Remove(cfg, "internal-default"));
        Assert.Equal(before, ConfigCopy.Canonical(cfg));
        Assert.NotEmpty(ProfileEditing.RemovalBlockers(cfg, ProfileEditing.LegacyInternal));
        Assert.NotEmpty(ProfileEditing.RemovalBlockers(cfg, "ghost"));
    }

    [Fact]
    public void Removing_the_only_profile_of_a_policy_is_refused_because_it_would_widen_the_policy()
    {
        var cfg = WithProfiles();
        cfg.Policies[0].Profiles = ["public-demo"];
        Assert.Contains(ProfileEditing.RemovalBlockers(cfg, "public-demo"), b => b.Contains("widen", StringComparison.Ordinal));
        Assert.NotNull(ProfileEditing.Remove(cfg, "public-demo"));
        Assert.Equal(new[] { "public-demo" }, cfg.Policies[0].Profiles);
    }

    [Fact]
    public void Removal_impact_lists_grants_and_multi_profile_policies_then_remove_cleans_every_reference()
    {
        var cfg = WithProfiles();
        cfg.Policies[0].Profiles = ["public-demo", "internal-default"];
        var impact = ProfileEditing.RemovalImpact(cfg, "public-demo");
        Assert.Contains(impact, l => l.Contains("'demo' loses the grant", StringComparison.Ordinal));
        Assert.Contains(impact, l => l.Contains("Policy 'public' stops listing", StringComparison.Ordinal));
        Assert.Empty(ProfileEditing.RemovalBlockers(cfg, "public-demo"));

        Assert.Null(ProfileEditing.Remove(cfg, "public-demo"));
        Assert.DoesNotContain(cfg.Profiles, p => p.Id == "public-demo");
        Assert.Equal(new[] { "internal-default" }, cfg.Applications[0].GrantedProfiles);
        Assert.Equal(new[] { "internal-default" }, cfg.Policies[0].Profiles);
        Assert.All(cfg.Applications, a => Assert.Empty(ProfileEditing.ApplicationProblems(a, cfg)));

        var unused = WithProfiles();
        unused.Profiles.Add(new WorkloadProfileDto { Id = "spare", Name = "Spare", Baseline = "Phi" });
        Assert.Contains(ProfileEditing.RemovalImpact(unused, "spare"), l => l.Contains("changes no route", StringComparison.Ordinal));
    }

    [Fact]
    public void Grants_and_default_profile_stay_consistent()
    {
        var cfg = WithProfiles();
        var demo = cfg.Applications[0];

        // The default cannot be revoked; another default first.
        Assert.NotNull(ProfileEditing.SetGrant(demo, "internal-default", false, cfg));
        Assert.Contains("internal-default", demo.GrantedProfiles);
        Assert.NotNull(ProfileEditing.SetDefault(cfg.Applications[1], "public-demo"));   // not granted to batch
        Assert.Equal("internal-default", cfg.Applications[1].DefaultProfile);
        Assert.Null(ProfileEditing.SetDefault(demo, "public-demo"));
        Assert.Null(ProfileEditing.SetGrant(demo, "internal-default", false, cfg));
        Assert.Equal(new[] { "public-demo" }, demo.GrantedProfiles);

        // Unknown profiles are never granted; the built-in one may be.
        Assert.NotNull(ProfileEditing.SetGrant(demo, "ghost", true, cfg));
        Assert.Null(ProfileEditing.SetGrant(demo, ProfileEditing.LegacyInternal, true, cfg));
        Assert.Contains(ProfileEditing.LegacyInternal, ProfileEditing.GrantOptions(cfg, demo));

        // No default ⇒ the built-in legacy-internal (baseline Internal) applies.
        Assert.Null(ProfileEditing.SetDefault(demo, ""));
        Assert.Null(demo.DefaultProfile);
        Assert.Contains(ProfileEditing.LegacyInternal, ProfileEditing.DefaultText(cfg, demo), StringComparison.Ordinal);
        Assert.Empty(ProfileEditing.ApplicationProblems(demo, cfg));

        // What the Host rejects is reported.
        demo.DefaultProfile = "internal-default";
        Assert.Contains(ProfileEditing.ApplicationProblems(demo, cfg), p => p.Contains("not granted", StringComparison.Ordinal));
        demo.GrantedProfiles.Add("ghost");
        Assert.Contains(ProfileEditing.ApplicationProblems(demo, cfg), p => p.Contains("does not exist", StringComparison.Ordinal));
    }

    [Fact]
    public void Team_and_output_bound_are_validated()
    {
        var app = WithProfiles().Applications[0];
        Assert.NotNull(ProfileEditing.SetTeam(app, " "));
        Assert.Equal("payments", app.Team);
        Assert.Null(ProfileEditing.SetTeam(app, " risk "));
        Assert.Equal("risk", app.Team);
        Assert.NotNull(ProfileEditing.SetMaxOutputTokens(app, 0));
        Assert.NotNull(ProfileEditing.SetMaxOutputTokens(app, null));
        Assert.Equal(256, app.MaxOutputTokens);
        Assert.Null(ProfileEditing.SetMaxOutputTokens(app, 64));
        Assert.Equal(64, app.MaxOutputTokens);
    }

    [Fact]
    public void Labels_name_the_baseline_and_the_built_in_profile()
    {
        var cfg = WithProfiles();
        Assert.Equal("Public demo (baseline Public)", ProfileEditing.Label(cfg, "public-demo"));
        Assert.Equal("legacy-internal (built-in, baseline Internal)", ProfileEditing.Label(cfg, ProfileEditing.LegacyInternal));
        Assert.Equal("ghost (not configured)", ProfileEditing.Label(cfg, "ghost"));
        Assert.True(ProfileEditing.Exists(cfg, ProfileEditing.LegacyInternal));
        Assert.False(ProfileEditing.IsDeclared(cfg, ProfileEditing.LegacyInternal));
    }
}

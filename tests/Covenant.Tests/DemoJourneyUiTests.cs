using System.Text.Json;
using Covenant.Core;
using Covenant.Governance;
using Covenant.Host;
using Xunit;
using UiLogic = Covenant.Ui.Logic;
using UiModels = Covenant.Ui.Models;

namespace Covenant.Tests;

/// <summary>Demo journey UI rules (pure): journey chips separate classification/why, workload and destination; explicit
/// escalation never grants Internal content external permission; the sanitized route needs a current, confirmed preview;
/// UI and Host stay in step on the structured-record contract; the policy editor round-trips the redaction contract.</summary>
public sealed class DemoJourneyUiTests
{
    private static UiModels.CovenantConfigDto Ui(Func<CovenantConfig, CovenantConfig>? tweak = null)
        => JsonSerializer.Deserialize(JsonSerializer.Serialize(V2.Hybrid(tweak: tweak), ConfigJsonContext.Default.CovenantConfig), UiModels.UiJsonContext.Default.CovenantConfigDto)!;

    private static CovenantConfig WithCoding(CovenantConfig c) => c with
    {
        Profiles = [.. c.Profiles, new WorkloadProfileConfig { Id = "public-coding", Name = "Public coding", Baseline = DataClassification.Public }],
        Policies = [.. c.Policies, V2.Policy("public-coding", [DataClassification.Public], [V2.T("ollama", V2.LocalTag), V2.T("openai", V2.CloudModel)], profiles: ["public-coding"])],
    };

    private static UiModels.ApplicationSummaryDto App(params string[] granted) => new() { Id = "demo-app", Team = "platform", Enabled = true, ConsoleTesting = true, GrantedProfiles = [.. granted] };

    private static UiModels.AuditEntryView Served(string cls, string baseline, string conn, string model, string? policy, string? profile, string? signal = null)
        => new()
        {
            Effect = "Allow", Classification = cls, BaselineClassification = baseline, Signal = signal, PolicyId = policy, ProfileId = profile, SchemaVersion = 2,
            Attempts = [new UiModels.AuditAttemptView { AttemptId = "att_1", ConnectionId = conn, ModelId = model, Outcome = "served", CostUsd = 0.001m, CostBasis = "reported" }],
        };

    // ---------------------------------------------------------------- journey chips

    [Fact]
    public void Chips_separate_classification_and_why_from_destination_and_show_sanitization_and_retrieval()
    {
        var cfg = Ui();
        var sanitized = Served("Pii", "Pii", "openai", V2.CloudModel, "sanitized", "support-records");
        sanitized.Redaction = new UiModels.AuditRedactionView { Outcome = "applied", OriginalClassification = "Pii", Counts = [new() { Type = "email", Count = 1 }, new() { Type = "us_ssn", Count = 1 }] };
        var chips = UiLogic.JourneyChips.For(sanitized, cfg.Connections, cfg.Profiles).Select(c => (c.Label, c.Tone)).ToList();
        Assert.Contains(("Sensitive · PII (workload label)", "sensitive"), chips);
        Assert.Contains(chips, c => c.Label.StartsWith("External · openai/", StringComparison.Ordinal) && c.Tone == "external");
        Assert.Contains(("Sanitized payload · 2 identifiers replaced", "external"), chips);

        var refused = new UiModels.AuditEntryView { Effect = "Deny", DenialKind = "Governance", StoppedAtStage = "policy", Classification = "Pii", BaselineClassification = "Public", Signal = "US SSN pattern", SchemaVersion = 2, Attempts = [] };
        var r = UiLogic.JourneyChips.For(refused, cfg.Connections, cfg.Profiles);
        Assert.Equal(("Blocked · sensitive data cannot go external", "sensitive"), (r[0].Label, r[0].Tone));               // red only because sensitive data was blocked
        Assert.Contains(r, c => c.Label == "Sensitive · PII (detected: US SSN pattern)");

        var research = Served("Public", "Public", "ollama", V2.LocalTag, "public", "public-demo");
        research.ToolCalls = [new UiModels.AuditToolCallView { Provider = "tavily", Outcome = "succeeded", CostUsd = 0.008m, CostBasis = "reported" }];
        var rc = UiLogic.JourneyChips.For(research, cfg.Connections, cfg.Profiles);
        Assert.Contains(rc, c => c.Tone == "local" && c.Label.StartsWith("Local · ", StringComparison.Ordinal));
        Assert.Contains(rc, c => c.Label == "External retrieval · tavily");                      // retrieval ≠ model boundary
        Assert.Contains(rc, c => c.Label.Contains("search $0.008", StringComparison.Ordinal));

        research.Attempts![0].CostUsd = null;
        Assert.Contains(UiLogic.JourneyChips.For(research, cfg.Connections, cfg.Profiles), c => c.Label.StartsWith("Inference unknown", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- explicit escalation

    [Fact]
    public void Public_escalation_reruns_on_an_approved_external_target_of_the_same_policy()
    {
        var cfg = Ui(WithCoding);
        var plan = UiLogic.Escalation.Plan(Served("Public", "Public", "ollama", V2.LocalTag, "public-coding", "public-coding"), cfg, App("public-coding"));
        Assert.Equal(UiLogic.EscalationKind.SamePolicy, plan.Kind);
        Assert.Equal("openai/" + V2.CloudModel, plan.Target);
        Assert.Equal("Cloud", plan.Boundary);
        Assert.Equal("public-coding", plan.ProfileId);
    }

    [Fact]
    public void Internal_content_never_gains_external_permission_without_explicit_reclassification()
    {
        var cfg = Ui(WithCoding);
        var internalRun = Served("Internal", "Internal", "ollama", V2.LocalTag, "internal", "internal-default");

        var withGrant = UiLogic.Escalation.Plan(internalRun, cfg, App("internal-default", "public-coding"));
        Assert.Equal(UiLogic.EscalationKind.Reclassify, withGrant.Kind);       // needs a confirmed switch to a GRANTED Public workload
        Assert.Equal("public-coding", withGrant.ProfileId);
        Assert.Contains("grants do not change", withGrant.Message, StringComparison.Ordinal);

        var noGrant = UiLogic.Escalation.Plan(internalRun, cfg, App("internal-default"));
        Assert.Equal(UiLogic.EscalationKind.Unavailable, noGrant.Kind);
        Assert.Null(noGrant.Target);

        var pii = UiLogic.Escalation.Plan(Served("Pii", "Public", "ollama", V2.LocalTag, "sensitive", "public-demo", "US SSN pattern"), cfg, App("public-demo", "public-coding"));
        Assert.Equal(UiLogic.EscalationKind.Unavailable, pii.Kind);
        Assert.Contains("not an automatic quality judgement", UiLogic.Escalation.NotQuality, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- sanitized route gating

    [Fact]
    public void A_sanitized_workload_needs_a_current_confirmed_preview_before_run()
    {
        var form = new UiLogic.PlaygroundForm { ApplicationId = "demo-app", ProfileId = "support-records", Prompt = "{}" };
        Assert.Null(UiLogic.SanitizedRoute.RunProblem(false, null, form, null));
        Assert.NotNull(UiLogic.SanitizedRoute.RunProblem(true, null, form, null));

        var key = UiLogic.RoutePreview.KeyOf(form);
        var applied = new UiLogic.RoutePreview(key, new UiModels.SimulateResponseDto { Redaction = new() { Outcome = "applied" } }, null, false);
        Assert.NotNull(UiLogic.SanitizedRoute.RunProblem(true, applied, form, null));
        Assert.Null(UiLogic.SanitizedRoute.RunProblem(true, applied, form, key));

        form.Prompt = "{ }";                                                     // any edit invalidates preview and confirmation
        Assert.NotNull(UiLogic.SanitizedRoute.RunProblem(true, applied, form, key));

        var refused = new UiLogic.RoutePreview(UiLogic.RoutePreview.KeyOf(form), new UiModels.SimulateResponseDto { Redaction = new() { Outcome = "refused" } }, null, false);
        Assert.Null(UiLogic.SanitizedRoute.RunProblem(true, refused, form, null));   // may run to record the refusal; nothing leaves
    }

    [Fact]
    public void The_policy_editor_offers_external_models_for_PII_only_with_the_sanitized_contract()
    {
        var cfg = Ui(c => c with { Profiles = [.. c.Profiles, new WorkloadProfileConfig { Id = "support-records", Name = "Support records", Baseline = DataClassification.Pii }] });
        var route = new UiModels.PolicyDto { Id = "sanitized", Classifications = ["Pii"], Profiles = ["support-records"] };
        Assert.All(UiLogic.PolicyEditing.TargetOptionsFor(route, cfg).Where(o => o.Boundary == "Cloud"), o => Assert.NotNull(o.DisabledReason));

        UiLogic.PolicyEditing.SetSanitized(route, true);
        Assert.True(UiLogic.PolicyText.IsSanitized(route));
        Assert.All(UiLogic.PolicyEditing.TargetOptionsFor(route, cfg).Where(o => o.Boundary == "Cloud"), o => Assert.Null(o.DisabledReason));
        Assert.Null(UiLogic.PolicyEditing.SanitizedProblem(route, cfg));

        route.Classifications = ["Pii", "Phi"];
        Assert.False(UiLogic.PolicyText.IsSanitized(route));
        Assert.NotNull(UiLogic.PolicyEditing.SanitizedProblem(route, cfg));
        route.Classifications = ["Pii"];
        route.Profiles = ["public-demo"];                                        // baseline Public: not a dedicated workload
        Assert.Contains("baseline PII", UiLogic.PolicyEditing.SanitizedProblem(route, cfg), StringComparison.Ordinal);
    }

    [Fact]
    public void The_redaction_contract_round_trips_through_the_console_DTO()
    {
        var core = V2.Hybrid(tweak: c => c with
        {
            Profiles = [.. c.Profiles, new WorkloadProfileConfig { Id = "support-records", Name = "Support", Baseline = DataClassification.Pii }],
            Policies = [.. c.Policies, V2.Policy("sanitized", [DataClassification.Pii], [V2.T("openai", V2.CloudModel)], profiles: ["support-records"])
                with { Redaction = new RedactionPolicy { Types = ["email", "us_ssn"], Tasks = ["triage_case"] } }],
        });
        var ui = JsonSerializer.Deserialize(JsonSerializer.Serialize(core, ConfigJsonContext.Default.CovenantConfig), UiModels.UiJsonContext.Default.CovenantConfigDto)!;
        var back = JsonSerializer.Deserialize(JsonSerializer.Serialize(ui, UiModels.UiJsonContext.Default.CovenantConfigDto), ConfigJsonContext.Default.CovenantConfig)!;
        var p = back.Policies.Single(x => x.Id == "sanitized");
        Assert.Equal(new[] { "email", "us_ssn" }, p.Redaction!.Types);
        Assert.Equal(new[] { "triage_case" }, p.Redaction.Tasks);
        Assert.Null(back.Policies.Single(x => x.Id == "public").Redaction);       // absent stays absent
    }

    [Fact]
    public void Console_copies_of_the_structured_record_contract_match_the_Host()
    {
        Assert.Equal(StructuredRecordSchema.SyntheticExample, UiLogic.DemoExamples.SyntheticRecord);
        Assert.Equal(StructuredRecordSchema.Tasks.Keys.Order(StringComparer.Ordinal), UiLogic.SanitizedRoute.Tasks.Select(t => t.Value).Order(StringComparer.Ordinal));
        Assert.Equal(RedactionTypes.Supported.Order(StringComparer.Ordinal), UiLogic.SanitizedRoute.IdentifierTypes.Select(t => t.Value).Order(StringComparer.Ordinal));
        // The synthetic record really is accepted by the Host contract (and only synthetic markers are used).
        var (record, refusal) = StructuredRecord.Parse([new ChatMessage(ChatRole.User, UiLogic.DemoExamples.SyntheticRecord)], RedactionTypes.Supported, ["triage_case"]);
        Assert.Null(refusal);
        Assert.StartsWith("000-", record!.Fields["us_ssn"], StringComparison.Ordinal);
        Assert.EndsWith("@example.test", record.Fields["email"], StringComparison.Ordinal);
    }

    [Fact]
    public void Usage_charge_line_never_shows_unknown_as_zero()
    {
        var row = new UiModels.UsageRowDto { TotalChargeUsd = 0.02m, EstimatedChargeRequests = 1, UnknownChargeRequests = 2 };
        var line = UiLogic.UsagePresentation.ChargeLine(row);
        Assert.Contains("2 unknown (not counted)", line, StringComparison.Ordinal);
        Assert.Contains("1 estimated", line, StringComparison.Ordinal);
        Assert.Contains("detected", UiLogic.UsagePresentation.Sensitive(row), StringComparison.Ordinal);
        Assert.Empty(UiLogic.UsagePresentation.Trend(row));
    }

    // ---------------------------------------------------------------- escalation uses the SOURCE request

    [Fact]
    public void Escalation_resends_the_source_snapshot_not_the_edited_composer_and_keeps_the_payer()
    {
        var cfg = Ui(WithCoding);
        var apps = new List<UiModels.ApplicationSummaryDto> { App("public-coding", "public-demo"), new() { Id = "batch-app", Team = "data", Enabled = true, ConsoleTesting = true, GrantedProfiles = ["public-demo"] } };
        var form = new UiLogic.PlaygroundForm
        {
            ApplicationId = "demo-app", ProfileId = "public-coding", Prompt = "Write slugify() — code only.", MaxTokens = 2048,
            Workflow = "coding", UseCase = "public-code-compare",
        };
        var older = UiLogic.RequestSnapshot.Freeze(1, form, DateTimeOffset.UnixEpoch);
        form.Prompt = "A second, different request.";
        var newer = UiLogic.RequestSnapshot.Freeze(2, form, DateTimeOffset.UnixEpoch);

        // The operator then edits the composer: other caller, other prompt, web search on, other bound.
        form.ApplicationId = "batch-app"; form.ProfileId = "public-demo"; form.Prompt = "edited later";
        form.WebSources = UiLogic.WebSourcesMode.Required; form.Location = "Toronto"; form.MaxTokens = 64;

        var plan = UiLogic.Escalation.Plan(Served("Public", "Public", "ollama", V2.LocalTag, "public-coding", "public-coding"), cfg, apps[0]);
        UiLogic.Escalation.ApplySource(form, older, plan);                    // the OLD selected run, after another run
        Assert.Null(UiLogic.Escalation.Problem(form, older, apps));
        var sent = UiLogic.RequestSnapshot.Freeze(3, form, DateTimeOffset.UnixEpoch);

        Assert.Equal(older.BasePrompt, sent.Prompt);                          // same prompt as the source, not the edit nor the newer run
        Assert.NotEqual(newer.BasePrompt, sent.Prompt);
        Assert.Equal("demo-app", sent.ApplicationId);                         // same caller ⇒ same paying team
        Assert.Equal("public-coding", sent.ProfileId);
        Assert.Equal(UiLogic.WebSourcesMode.Off, sent.WebSources);
        Assert.Null(sent.Location);
        Assert.Equal(2048, sent.MaxTokens);
        Assert.Equal("coding", sent.Workflow);
        Assert.Equal("openai/" + V2.CloudModel, sent.RequestedModel);       // only the explicit target changed

        // Latest grants are re-checked: a revoked workload refuses the comparison instead of switching caller.
        apps[0].GrantedProfiles = ["public-demo"];
        Assert.NotNull(UiLogic.Escalation.Problem(form, older, apps));
    }
}

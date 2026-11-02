using Covenant.Governance;
using Xunit;
using UiLogic = Covenant.Ui.Logic;
using UiModels = Covenant.Ui.Models;

namespace Covenant.Tests;

/// <summary>Phase 2 route scheme (Sender → Covenant → Destination): expected only from a CURRENT server preview (pending,
/// stale and version-mismatch states show no route), actual only from one run's snapshot + record; refusals say the model
/// was not contacted without hiding a search or an earlier attempt; cache hits and local inference with search are explicit.</summary>
public sealed class RouteSchemeTests
{
    private static readonly List<UiModels.ConnectionDto> Connections =
    [
        new() { Id = "ollama", Boundary = "Local", Enabled = true, Models = [new() { ModelId = "qwen" }] },
        new() { Id = "anthropic", Boundary = "Cloud", Enabled = true, Models = [new() { ModelId = "claude" }] },
    ];

    private static readonly List<UiModels.ApplicationSummaryDto> Apps =
        [new() { Id = "demo-app", Team = "platform", Enabled = true, ConsoleTesting = true, GrantedProfiles = ["public-demo"] }];

    private static UiLogic.PlaygroundForm Form(string prompt = "Summarize the release notes.", UiLogic.WebSourcesMode web = UiLogic.WebSourcesMode.Off)
        => new() { ApplicationId = "demo-app", ProfileId = "public-demo", Prompt = prompt, WebSources = web };

    private static UiModels.SimulateResponseDto Route(string primary, string boundary, string decision = "route", string? signal = null, string cls = "Public", int version = 7)
        => new()
        {
            DryRun = true, ConfigVersion = version, ApplicationId = "demo-app", Profile = "public-demo", Classification = cls, Baseline = "Public",
            DetectedSignal = signal, PolicyId = "public", Decision = decision, Primary = decision == "route" ? primary : null,
            Eligible = [new() { Target = primary, Boundary = boundary }], Reason = decision == "route" ? "allowed" : "PII is never routed to a cloud destination",
        };

    private static UiLogic.RouteSchemeView Expected(UiLogic.RoutePreview? p, UiLogic.PlaygroundForm f, int? active = 7)
        => UiLogic.RouteSchemes.Expected(p, f, active, Apps, null);

    // ---------------------------------------------------------------- expected

    [Fact]
    public void Expected_is_never_invented_before_a_current_preview_exists()
    {
        var form = Form();
        var none = Expected(null, form);
        Assert.Equal("none", none.State);
        Assert.Equal("pending", none.Destination.Tone);
        Assert.DoesNotContain("detected", none.Covenant.Line, StringComparison.OrdinalIgnoreCase);   // never a fake detection

        var key = UiLogic.RoutePreview.KeyOf(form);
        Assert.Equal("pending", Expected(new UiLogic.RoutePreview(key, null, null, Loading: true), form).State);
        Assert.Equal("unknown", Expected(new UiLogic.RoutePreview(key, null, "Route preview unavailable: 503", Loading: false), form).State);
    }

    [Fact]
    public void Expected_turns_stale_on_form_change_or_configuration_mismatch()
    {
        var form = Form();
        var preview = new UiLogic.RoutePreview(UiLogic.RoutePreview.KeyOf(form), Route("ollama/qwen", "Local"), null, false);
        var ready = Expected(preview, form);
        Assert.Equal("ready", ready.State);
        Assert.Equal("local", ready.Destination.Tone);
        Assert.StartsWith("Local · ollama/qwen", ready.Destination.Line, StringComparison.Ordinal);

        form.Prompt += " (edited)";
        var stale = Expected(preview, form);
        Assert.Equal("stale", stale.State);
        Assert.Equal("pending", stale.Destination.Tone);                         // the old destination is not shown as the answer

        var versionMismatch = Expected(new UiLogic.RoutePreview(UiLogic.RoutePreview.KeyOf(form), Route("ollama/qwen", "Local", version: 6), null, false), form, active: 7);
        Assert.Equal("stale", versionMismatch.State);
        Assert.Contains("v6 → v7", versionMismatch.StateText, StringComparison.Ordinal);
    }

    [Fact]
    public void Expected_refusal_says_the_destination_would_not_be_contacted_and_names_the_detection()
    {
        var form = Form("Summarize. SSN 000-12-3456");
        var v = Expected(new UiLogic.RoutePreview(UiLogic.RoutePreview.KeyOf(form),
            Route("anthropic/claude", "Cloud", decision: "refuse", signal: "US SSN pattern", cls: "Pii"), null, false), form);
        Assert.Equal("blocked", v.Destination.Tone);
        Assert.Contains("would not be contacted", v.Destination.Line, StringComparison.Ordinal);
        Assert.Contains("detected: US SSN pattern", v.Covenant.Line, StringComparison.Ordinal);
        Assert.Equal("blocked", v.Covenant.Tone);
    }

    [Fact]
    public void Expected_local_inference_with_web_search_shows_the_external_retrieval_badge()
    {
        var form = Form("What is the latest .NET release?", UiLogic.WebSourcesMode.Required);
        var v = Expected(new UiLogic.RoutePreview(UiLogic.RoutePreview.KeyOf(form), Route("ollama/qwen", "Local"), null, false), form);
        Assert.Equal("local", v.Destination.Tone);
        Assert.Contains(v.Badges, b => b.Contains("Tavily", StringComparison.Ordinal) && b.Contains("separately", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- actual

    private static UiLogic.RequestSnapshot Snapshot(UiLogic.PlaygroundForm f) => UiLogic.RequestSnapshot.Freeze(1, f, DateTimeOffset.UnixEpoch);

    private static UiModels.AuditEntryView Rec(string effect = "Allow", bool cache = false, string cls = "Public", string? signal = null,
        List<UiModels.AuditAttemptView>? attempts = null, List<UiModels.AuditToolCallView>? tools = null, string? stopped = null, string? denial = null)
        => new()
        {
            Effect = effect, CacheHit = cache, Classification = cls, BaselineClassification = "Public", Signal = signal, ApplicationId = "demo-app",
            ProfileId = "public-demo", PolicyId = "public", SchemaVersion = 2, ConfigVersion = 7, Attempts = attempts ?? [], ToolCalls = tools,
            StoppedAtStage = stopped, DenialKind = denial, Tags = new() { Team = "platform" },
        };

    private static UiModels.AuditAttemptView Attempt(string conn, string model, string outcome = "served")
        => new() { AttemptId = "att_1", ConnectionId = conn, ModelId = model, Outcome = outcome, CostUsd = 0m, CostBasis = "zero_priced" };

    private static UiLogic.RouteSchemeView Actual(UiModels.AuditEntryView? r, UiLogic.PlaygroundForm? f = null, bool inFlight = false)
        => UiLogic.RouteSchemes.Actual(Snapshot(f ?? Form()), r, inFlight, Apps, Connections, null);

    [Fact]
    public void Actual_uses_the_run_record_not_the_current_form()
    {
        var form = Form();
        var snapshot = Snapshot(form);
        form.ApplicationId = "someone-else";                                   // the live form changes afterwards
        var v = UiLogic.RouteSchemes.Actual(snapshot, Rec(attempts: [Attempt("ollama", "qwen")]), false, Apps, Connections, null);
        Assert.Contains("demo-app", v.Sender.Line + v.Sender.Sub, StringComparison.Ordinal);
        Assert.Equal("local", v.Destination.Tone);
        Assert.Equal("pending", Actual(null, inFlight: true).State);
        Assert.Equal("unknown", Actual(null).State);                         // no record yet: nothing assumed
    }

    [Fact]
    public void Actual_local_inference_with_a_real_search_shows_both_facts()
    {
        var v = Actual(Rec(attempts: [Attempt("ollama", "qwen")],
            tools: [new() { Provider = "tavily", Outcome = "succeeded", CostUsd = 0.008m, CostBasis = "reported" }]));
        Assert.Equal("local", v.Destination.Tone);
        Assert.Contains(v.Badges, b => b.StartsWith("External search: tavily", StringComparison.Ordinal));
    }

    [Fact]
    public void Actual_cache_hit_has_no_provider_call()
    {
        var v = Actual(Rec(cache: true));
        Assert.Equal("cache", v.Destination.Tone);
        Assert.Contains("no provider call", v.Destination.Line, StringComparison.Ordinal);
    }

    [Fact]
    public void Actual_blocked_request_never_hides_contact_that_did_happen()
    {
        var blocked = Actual(Rec(effect: "Deny", cls: "Pii", signal: "US SSN pattern", stopped: "policy", denial: "Governance"));
        Assert.Equal("blocked", blocked.Destination.Tone);
        Assert.Equal("Blocked — model not contacted", blocked.Destination.Line);
        Assert.DoesNotContain("search", blocked.Destination.Sub ?? "", StringComparison.Ordinal);

        // A search ran before a later refusal: the scheme says so (no "nothing external was contacted" claim).
        var afterSearch = Actual(Rec(effect: "Deny", stopped: "budget", denial: "Governance",
            tools: [new() { Provider = "tavily", Outcome = "succeeded", CostUsd = 0.008m, CostBasis = "reported" }]));
        Assert.Contains("a web search was made before the refusal", afterSearch.Destination.Sub, StringComparison.Ordinal);
        Assert.NotEmpty(afterSearch.Badges);

        // An earlier attempt failed and nothing served: the contact is listed.
        var failed = Actual(Rec(effect: "Deny", denial: "UpstreamFailure", attempts: [Attempt("anthropic", "claude", "failed")]));
        Assert.Equal("Provider failed — no answer", failed.Destination.Line);
        Assert.Contains("contacted earlier: anthropic/claude (failed)", failed.Destination.Sub, StringComparison.Ordinal);
    }

    [Fact]
    public void Actual_sanitized_and_fallback_runs_are_explicit()
    {
        var sanitized = Rec(cls: "Pii", attempts: [Attempt("anthropic", "claude")]);
        sanitized.Redaction = new() { Outcome = "applied", OriginalClassification = "Pii", Counts = [new() { Type = "us_ssn", Count = 1 }] };
        var s = Actual(sanitized);
        Assert.Equal("sanitized", s.Destination.Tone);
        Assert.Contains("sanitized payload", s.Destination.Line, StringComparison.Ordinal);

        var fallback = Actual(Rec(attempts: [Attempt("anthropic", "claude", "failed"), Attempt("ollama", "qwen")]));
        Assert.Equal("local", fallback.Destination.Tone);
        Assert.Contains("contacted earlier: anthropic/claude (failed)", fallback.Destination.Sub, StringComparison.Ordinal);
    }

    [Fact]
    public void Detection_coverage_text_matches_the_host_and_makes_no_guarantee()
    {
        Assert.Equal(InspectionSignals.Coverage, UiLogic.RouteSchemes.DetectionCoverage);
        Assert.Contains("not comprehensive DLP", UiLogic.RouteSchemes.DetectionCoverage, StringComparison.Ordinal);
    }
}

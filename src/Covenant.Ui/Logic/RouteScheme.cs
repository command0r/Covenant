using Covenant.Ui.Models;

namespace Covenant.Ui.Logic;

/// <summary>One box of the three-part route scheme. Tone ∈ local | external | sanitized | blocked | cache | neutral | pending —
/// always rendered with an icon AND text; colour is never the only signal.</summary>
public sealed record SchemeBox(string Title, string Line, string? Sub, string Tone, string Icon);

/// <summary>Sender → Covenant → Destination. Kind: expected (from the CURRENT server dry run only) or actual (from ONE run's
/// own snapshot and audit record — never the live form). State ∈ none | pending | stale | unknown | ready.</summary>
public sealed record RouteSchemeView(
    string Kind,
    string State,
    string? StateText,
    SchemeBox Sender,
    SchemeBox Covenant,
    SchemeBox Destination,
    IReadOnlyList<string> Badges);

/// <summary>Builds the compact route scheme (Phase 2 review). Pure. Rules:
/// <list type="bullet">
/// <item>Expected comes ONLY from a current server preview (same form key and configuration version); otherwise it says
/// pending / stale / not previewed and shows no destination or detection it does not have.</item>
/// <item>Actual comes ONLY from the selected run's snapshot and audit record.</item>
/// <item>A blocked request says the model destination was not contacted — but never "nothing external was contacted" when a
/// web search or an earlier (failed) attempt actually happened; those are listed.</item>
/// </list></summary>
public static class RouteSchemes
{
    /// <summary>Mirror of the Host's InspectionSignals.Coverage (a test keeps them equal): where the detector's limits are findable.</summary>
    public const string DetectionCoverage =
        "Local pattern inspection (ADR-0018) over every message, after decoding JSON string escapes and normalizing Unicode: US SSNs "
        + "(separated, obfuscated, masked, partial with context), standalone 9-digit numbers as possible identifiers, labelled identifier "
        + "fields in prose or structured keys (SSN, e-mail, phone, date of birth, name, account/document numbers), Luhn-valid payment "
        + "cards, medical record/patient identifiers, and health information about an identifiable person — also when identity and "
        + "health detail sit in different messages. It does not detect unlabelled free-text names or addresses, paraphrased or encoded "
        + "(e.g. base64) facts, or non-US national identifiers outside labelled fields. It is a routing safeguard, not comprehensive "
        + "DLP; no match is not proof that content is public-safe. If inspection cannot complete, the request is refused.";

    private static SchemeBox Sender(ApplicationSummaryDto? app, string? appId, string? team, string? profileName)
        => new("Sender", app is null ? appId ?? "No application" : ComposerRules.ApplicationName(app),
            Join("team " + (team ?? app?.Team ?? "?"), profileName is null ? null : "workload " + profileName), "neutral", "requests");

    private static string? Join(params string?[] parts)
    {
        var p = parts.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        return p.Count == 0 ? null : string.Join(" · ", p);
    }

    private static readonly SchemeBox CovenantPending = new("Covenant", "Not evaluated yet", null, "pending", "pending");
    private static readonly SchemeBox DestinationUnknown = new("Destination", "Unknown until previewed", null, "pending", "pending");

    // ---------------------------------------------------------------- expected (server dry run)

    public static RouteSchemeView Expected(RoutePreview? preview, PlaygroundForm form, int? activeVersion,
        IReadOnlyList<ApplicationSummaryDto>? apps, IReadOnlyList<WorkloadProfileDto>? profiles)
    {
        var app = ComposerRules.Find(apps, form.ApplicationId);
        var profile = ComposerRules.Profile(profiles, form.ProfileId);
        var sender = Sender(app, form.ApplicationId, null, profile?.Name ?? form.ProfileId);
        IReadOnlyList<string> searchBadge = form.WebSources != WebSourcesMode.Off
            ? ["Web search " + WebSourcesText.Label(form.WebSources).ToLowerInvariant() + ": an external retrieval (Tavily) when it runs — charged separately"]
            : [];

        if (preview is null)
            return new("expected", "none", "Not previewed — press Preview route for the server's answer.", sender, CovenantPending, DestinationUnknown, searchBadge);
        if (preview.Loading)
            return new("expected", "pending", "Asking the policy engine…", sender, CovenantPending, new("Destination", "Pending", null, "pending", "running"), searchBadge);
        if (preview.Error is { } err)
            return new("expected", "unknown", err, sender, new("Covenant", "Preview unavailable", null, "pending", "warn"), DestinationUnknown, searchBadge);
        if (!preview.IsCurrent(form))
            return new("expected", "stale", "Inputs changed since the preview — preview again.", sender, CovenantPending, DestinationUnknown, searchBadge);
        if (preview.Result is not { } r)
            return new("expected", "unknown", "The server returned no preview.", sender, CovenantPending, DestinationUnknown, searchBadge);
        if (activeVersion is { } v && r.ConfigVersion is { } pv && pv != v)
            return new("expected", "stale", "The configuration changed (v" + pv + " → v" + v + ") — preview again.", sender, CovenantPending, DestinationUnknown, searchBadge);

        var covenant = new SchemeBox("Covenant",
            ClassLine(r.Classification, r.Baseline, r.DetectedSignal, evaluated: r.Classification != "Not evaluated"),
            Join(r.PolicyId is { } pid ? "policy " + pid : null, r.Redaction is { Outcome: "applied" } ? "builds a sanitized structured payload" : null),
            r.DetectedSignal is not null ? "blocked" : "neutral", "policies");

        SchemeBox destination;
        if (!string.Equals(r.Decision, "route", StringComparison.Ordinal))
            destination = new("Destination", "Blocked — would not be contacted", r.Reason, "blocked", "cross");
        else if (r.Redaction is { Outcome: "refused" } red)
            destination = new("Destination", "Blocked — sanitized route refuses this input", red.Reason, "blocked", "cross");
        else
        {
            var primaryBoundary = r.Eligible.FirstOrDefault(e => e.Target == r.Primary)?.Boundary;
            bool external = string.Equals(ComposerRules.BoundaryLabel(primaryBoundary), "Cloud", StringComparison.Ordinal);
            var fallback = r.Fallbacks.Count > 0 ? "fallback " + string.Join(", ", r.Fallbacks) : "no fallback";
            destination = r.Redaction is { Outcome: "applied" }
                ? new("Destination", "External · sanitized payload only", Join(r.Primary, fallback), "sanitized", "shield")
                : external
                    ? new("Destination", "External · " + (r.Primary ?? "?"), fallback, "external", "cloud")
                    : new("Destination", "Local · " + (r.Primary ?? "?"), fallback, "local", "local");
        }
        return new("expected", "ready", "Server dry run on configuration v" + (r.ConfigVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?") + " — runtime checks still apply.",
            sender, covenant, destination, searchBadge);
    }

    private static string ClassLine(string? final, string? baseline, string? signal, bool evaluated)
    {
        if (!evaluated) return "Classification not evaluated";
        var label = JourneyChips.DataLabel(final);
        if (signal is { Length: > 0 } s) return label + " · detected: " + s;
        return string.Equals(final, baseline, StringComparison.OrdinalIgnoreCase) || baseline is null
            ? label + " · workload label (nothing detected)"
            : label + " (baseline " + PolicyText.ClassificationLabel(baseline) + ")";
    }

    // ---------------------------------------------------------------- actual (one run's evidence)

    public static RouteSchemeView Actual(RequestSnapshot snapshot, AuditEntryView? r, bool inFlight,
        IReadOnlyList<ApplicationSummaryDto>? apps, IReadOnlyList<ConnectionDto>? connections, IReadOnlyList<WorkloadProfileDto>? profiles)
    {
        var app = ComposerRules.Find(apps, snapshot.ApplicationId);
        var profileName = ComposerRules.Profile(profiles, r?.ProfileId ?? snapshot.ProfileId)?.Name ?? r?.ProfileId ?? snapshot.ProfileId;
        var sender = Sender(app, r?.ApplicationId ?? snapshot.ApplicationId, r?.Tags.Team, profileName);
        if (inFlight)
            return new("actual", "pending", "Running — the destination is shown from evidence once recorded.", sender, CovenantPending,
                new("Destination", "In progress", null, "pending", "running"), []);
        if (r is null)
            return new("actual", "unknown", "Evidence not available yet — nothing is assumed.", sender, CovenantPending,
                new("Destination", "Not recorded yet", null, "pending", "pending"), []);

        bool evaluated = r.ClassificationEvaluated != false;
        var covenant = new SchemeBox("Covenant",
            ClassLine(r.Classification, r.BaselineClassification, r.Signal, evaluated),
            Join(r.PolicyId is { } pid ? "policy " + pid : null,
                 r.Redaction is { Outcome: "applied" } ? "sanitized structured payload built" : r.Redaction is { Outcome: "refused" } ? "sanitization refused" : null,
                 r.ConfigVersion is { } cv ? "config v" + cv : null),
            !evaluated ? "blocked" : r.Signal is not null ? "blocked" : "neutral", "policies");

        var badges = new List<string>();
        var searches = r.ToolCalls?.Where(t => t.CostBasis != "no_call").ToList() ?? [];
        if (searches.Count > 0)
            badges.Add("External search: " + string.Join(", ", searches.Select(t => t.Provider).Distinct()) + " (" + Money.Usd(searches.Sum(t => t.CostUsd ?? 0m)) + ", separate charge)");

        var attempts = r.Attempts ?? [];
        var served = attempts.LastOrDefault(AttemptFacts.IsSuccess);
        var failed = attempts.Where(a => !AttemptFacts.IsSuccess(a)).Select(a => TraceModel.Target(a) + " (" + a.Outcome + ")").ToList();
        string? contacted = failed.Count > 0 ? "contacted earlier: " + string.Join(", ", failed) : null;

        SchemeBox destination;
        if (r.CacheHit)
            destination = new("Destination", "Exact cache · no provider call", ModelFacts.Destination(r, connections).Text, "cache", "history");
        else if (served is not null)
        {
            var target = TraceModel.Target(served) ?? "?";
            var boundary = ModelFacts.BoundaryOf(served.ConnectionId, connections);
            destination = r.Redaction is { Outcome: "applied" }
                ? new("Destination", "External · sanitized payload · " + target, contacted, "sanitized", "shield")
                : boundary == "Cloud"
                    ? new("Destination", "External · " + target, contacted, "external", "cloud")
                    : boundary == "Local"
                        ? new("Destination", "Local · " + target, contacted, "local", "local")
                        : new("Destination", target + " · boundary not reported", contacted, "neutral", "info");
        }
        else if (attempts.Count > 0)
            destination = new("Destination", "Provider failed — no answer", Join(contacted, r.Reason), "blocked", "cross");
        else
            destination = new("Destination", "Blocked — model not contacted", Join(
                r.StoppedAtStage is { Length: > 0 } st ? "refused at " + TraceModel.Label(st) : null,
                searches.Count > 0 ? "a web search was made before the refusal" : null), "blocked", "cross");

        return new("actual", "ready", "From this run's audit record" + (r.ConfigVersion is { } v ? " (configuration v" + v + ")" : "") + ".",
            sender, covenant, destination, badges);
    }
}

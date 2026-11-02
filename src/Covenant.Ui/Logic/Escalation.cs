using Covenant.Ui.Models;

namespace Covenant.Ui.Logic;

/// <summary>None: nothing to offer. SamePolicy: the policy that served already approves another target — re-run pinned to it.
/// Reclassify: the content was Internal; an EXTERNAL target needs an explicit switch to a granted Public workload (confirmed,
/// re-detected and re-evaluated by the Host). Unavailable: explained, with where to set it up.</summary>
public enum EscalationKind { None, SamePolicy, Reclassify, Unavailable }

public sealed record EscalationPlan(EscalationKind Kind, string? Target, string? Boundary, string? ProfileId, string? ProfileName, string Message, string? Href = null);

/// <summary>Explicit, operator-chosen escalation after a result (demo journey §5) — never automatic difficulty or quality
/// detection. Measured: what ran, where, its cost and duration. Operator choice: whether to try an approved alternative.
/// Internal content never gains external permission because it is "difficult": that needs an explicit reclassification to a
/// Public workload the application is already granted, and the Host runs detection and policy again. Pure.</summary>
public static class Escalation
{
    public const string NotQuality =
        "Operator choice, not an automatic quality judgement: Covenant measures what ran, where, how long and what it cost — not whether the answer is good.";

    public static EscalationPlan Plan(AuditEntryView r, CovenantConfigDto? cfg, ApplicationSummaryDto? app)
    {
        if (cfg is null || app is null || !string.Equals(r.Effect, "Allow", StringComparison.Ordinal) || r.CacheHit)
            return new(EscalationKind.None, null, null, null, null, "");
        var cls = (r.Classification ?? "").ToLowerInvariant();
        if (cls is "pii" or "phi")
            return new(EscalationKind.Unavailable, null, null, null, null, "Sensitive content is never escalated to an external model.");
        var served = ServedTarget(r);
        var policy = cfg.Policies.FirstOrDefault(p => string.Equals(p.Id, r.PolicyId, StringComparison.Ordinal));
        var alternatives = policy is null ? new List<Candidate>() : Eligible(policy, cfg).Where(t => !string.Equals(t.Key, served, StringComparison.Ordinal)).ToList();

        if (cls == "public")
        {
            // Prefer an approved External alternative (the "stronger" model is the operator's configured choice).
            var pick = alternatives.FirstOrDefault(t => t.Boundary == "Cloud") ?? alternatives.FirstOrDefault();
            return pick is null
                ? new(EscalationKind.Unavailable, null, null, null, null,
                    "This workload's policy approves no other target. Add an approved alternative to its route under Policies.", "policies")
                : new(EscalationKind.SamePolicy, pick.Key, pick.Boundary, r.ProfileId, null,
                    "Run the same prompt on " + pick.Key + " — an approved target of policy " + policy!.Id + ".");
        }

        // Internal (or a detected escalation above the baseline): the same policy may only offer non-external alternatives.
        bool detected = !string.Equals(r.Classification, r.BaselineClassification, StringComparison.OrdinalIgnoreCase);
        var localAlt = alternatives.FirstOrDefault(t => t.Boundary != "Cloud");
        if (detected)
            return localAlt is not null
                ? new(EscalationKind.SamePolicy, localAlt.Key, localAlt.Boundary, r.ProfileId, null, "Run on " + localAlt.Key + " (local, same policy).")
                : new(EscalationKind.Unavailable, null, null, null, null, "Detection raised this request's classification; it cannot be sent externally.");

        foreach (var pid in ComposerRules.GrantedProfiles(app))
        {
            var prof = cfg.Profiles.FirstOrDefault(p => p.Id == pid);
            if (prof is not { Baseline: "Public" }) continue;
            var route = DemoExamples.Route(cfg, "Public", pid);
            var ext = route.Eligible.FirstOrDefault(t => ComposerRules.BoundaryLabel(FindConnection(cfg, t.ConnectionId)?.Boundary) == "Cloud");
            if (route.Refusal is null && ext is not null)
                return new(EscalationKind.Reclassify, ext.ConnectionId + "/" + ext.ModelId, "Cloud", pid, prof.Name ?? pid,
                    "This content was sent as Internal. To compare externally, confirm it contains nothing internal and resend it under the Public workload "
                    + (prof.Name ?? pid) + " — detection and policy run again; the application's grants do not change.");
        }
        return localAlt is not null
            ? new(EscalationKind.SamePolicy, localAlt.Key, localAlt.Boundary, r.ProfileId, null, "Run on " + localAlt.Key + " (local, same policy).")
            : new(EscalationKind.Unavailable, null, null, null, null,
                "Internal work stays inside the perimeter. An external comparison needs a granted Public workload with an approved external route.", "policies");
    }

    private sealed record Candidate(string Key, string? Boundary);

    private static IEnumerable<Candidate> Eligible(PolicyDto policy, CovenantConfigDto cfg)
    {
        foreach (var t in policy.Targets.Where(t => !t.Paused))
        {
            var conn = FindConnection(cfg, t.ConnectionId);
            if (conn is not { Enabled: true } || !conn.Models.Any(m => m.ModelId == t.ModelId)) continue;
            if (PolicyRoutes.IsLocalOnly(cfg.Mode) && ComposerRules.BoundaryLabel(conn.Boundary) == "Cloud") continue;
            yield return new Candidate(t.ConnectionId + "/" + t.ModelId, ComposerRules.BoundaryLabel(conn.Boundary));
        }
    }

    private static ConnectionDto? FindConnection(CovenantConfigDto cfg, string id)
        => cfg.Connections.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal));

    /// <summary>"connection/model" of the served attempt.</summary>
    public static string? ServedTarget(AuditEntryView r)
        => r.Attempts?.LastOrDefault(AttemptFacts.IsSuccess) is { } a ? a.ConnectionId + "/" + a.ModelId : null;

    /// <summary>Builds the escalation request from the SOURCE run's frozen snapshot — never from the live composer, which the
    /// operator may have edited since (prompt, caller, search, output bound). Copies the immutable request fields, then
    /// applies only the explicit changes of the plan: the pinned target and, for a reclassification, the workload. Grants and
    /// configuration are re-validated against the LATEST state by <see cref="Problem"/>; the Host re-evaluates everything.</summary>
    public static void ApplySource(PlaygroundForm form, RequestSnapshot source, EscalationPlan plan)
    {
        form.Scenario = null;
        form.ExampleId = null;
        form.ExampleApplicationId = null;
        form.ExampleProfileId = null;
        form.ApplicationId = source.ApplicationId;
        form.ProfileId = plan.ProfileId ?? source.ProfileId;
        form.Prompt = source.BasePrompt;
        form.Streaming = source.Streaming;
        form.MaxTokens = source.MaxTokens;
        form.Workflow = source.Workflow ?? PlaygroundForm.DefaultWorkflow;
        form.UseCase = source.UseCase ?? PlaygroundForm.DefaultUseCase;
        form.FreshNonce = source.FreshNonce;
        form.WebSources = source.WebSources;
        form.Location = source.Location ?? "";
        form.Mode = RouteMode.Catalog;
        form.CatalogModel = plan.Target;
        form.ExplicitModel = null;
    }

    /// <summary>Why the escalation cannot be sent under the latest applications/grants (null = it may be sent). The source
    /// application must still be selectable and the target workload still granted to it — never a silent caller switch.</summary>
    public static string? Problem(PlaygroundForm escalated, RequestSnapshot source, IReadOnlyList<ApplicationSummaryDto>? apps)
    {
        if (!string.Equals(escalated.ApplicationId, source.ApplicationId, StringComparison.Ordinal))
            return "The comparison must be sent as the original application (" + (source.ApplicationId ?? "none") + ").";
        return ComposerRules.Problem(escalated, apps);
    }
}

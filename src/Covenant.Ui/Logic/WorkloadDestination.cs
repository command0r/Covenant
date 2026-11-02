using Covenant.Ui.Models;

namespace Covenant.Ui.Logic;

/// <summary>Where the selected workload is APPROVED to run under the applied configuration (shown beside the workload
/// choice). Classification is not destination: "Public" means cloud-eligible only where a policy routes it to a cloud
/// model. Mirrors the policy engine for the workload's baseline; the Host decides at run time (detection may escalate a
/// prompt, which then follows the stricter rule).</summary>
public sealed record WorkloadDestinationView(
    string? Target,
    string? Boundary,
    string? PolicyId,
    IReadOnlyList<string> Fallbacks,
    string? Note,
    string? Problem,
    string? Href,
    string? HrefLabel)
{
    public bool Ok => Problem is null && Target is not null;
}

public static class WorkloadDestination
{
    public static WorkloadDestinationView For(CovenantConfigDto? cfg, WorkloadProfileDto? profile, string? requestedModel = null)
    {
        if (cfg is null) return Fail("Configuration not loaded yet.", null, null);
        if (profile is null) return Fail("No workload selected — the server applies the application's default.", null, null);
        var baseline = profile.Baseline ?? "Internal";
        var route = DemoExamples.Route(cfg, baseline, profile.Id);
        if (route.Refusal is { } why)
            return Fail("No approved destination for " + (profile.Name ?? profile.Id) + ": " + why + ".", "policies", "Configure routes");

        var primary = route.Primary!;
        var boundary = ComposerRules.BoundaryLabel(route.PrimaryConnection?.Boundary);
        var fallbacks = route.Eligible.Skip(1).Select(t => t.ConnectionId + "/" + t.ModelId).ToList();
        string? note = null;
        bool isPublic = string.Equals(baseline, "Public", StringComparison.Ordinal);
        if (isPublic && boundary == "Local")
            note = PolicyRoutes.IsLocalOnly(cfg.Mode)
                ? "Public work runs locally: this configuration is Local-only. Cloud needs Hybrid mode and an approved cloud route."
                : "Public means cloud-eligible, not cloud: this workload's policy routes it to a local model.";
        else if (isPublic && boundary == "Cloud")
            note = "If the prompt contains sensitive data, detection raises it and the stricter (local) rule applies.";
        if (route.Policy is { } sp && PolicyText.IsSanitized(sp))
            note = "Sanitized workload: only a covenant.record.v1 structured record leaves, as placeholders; prose and PHI are refused. Preview shows the exact payload.";
        // A pinned model is announced by the composer's own pin warning (with "Let policy choose"); not repeated here.
        _ = requestedModel;

        return new WorkloadDestinationView(primary.ConnectionId + "/" + primary.ModelId, boundary, route.Policy?.Id, fallbacks, note, null,
            isPublic && boundary == "Local" ? "policies" : null, isPublic && boundary == "Local" ? "Routes" : null);
    }

    /// <summary>"Output bound: 2048 requested → 1024 enforced (application cap) — the answer may be cut." for the approved
    /// destination, or null when the requested bound is enforced in full (or nothing is requested / no destination). The
    /// Host's rule: effective = min(request, application cap, model cap). Shown beside the destination so switching the
    /// same prompt to another workload states the new bound honestly before Run.</summary>
    public static string? OutputBound(CovenantConfigDto? cfg, string? applicationId, WorkloadDestinationView dest, int? requested)
    {
        if (cfg is null || requested is not { } want || !dest.Ok || dest.Target is not { } target) return null;
        int slash = target.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0) return null;
        string connId = target[..slash], modelId = target[(slash + 1)..];
        int appCap = cfg.Applications.FirstOrDefault(a => a.Id == applicationId)?.MaxOutputTokens ?? PlaygroundForm.DefaultMaxTokens;
        int? modelCap = cfg.Connections.FirstOrDefault(c => c.Id == connId)?.Models.FirstOrDefault(m => m.ModelId == modelId)?.MaxOutputTokens;
        int effective = Math.Min(want, Math.Min(appCap, modelCap ?? int.MaxValue));
        if (effective >= want) return null;
        string by = modelCap is { } mc && mc <= appCap ? "model cap" : "application cap";
        return "Output bound: " + want.ToString(System.Globalization.CultureInfo.InvariantCulture) + " requested → "
               + effective.ToString(System.Globalization.CultureInfo.InvariantCulture) + " enforced (" + by + ") — a long answer may be cut (Truncated).";
    }

    private static WorkloadDestinationView Fail(string problem, string? href, string? label) => new(null, null, null, [], null, problem, href, label);
}

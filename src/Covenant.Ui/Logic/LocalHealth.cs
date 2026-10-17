using Covenant.Ui.Models;

namespace Covenant.Ui.Logic;

public enum LocalRouteState { Unknown, NotConfigured, NotRegistered, NotObserved, Ready, Degraded }

/// <summary>The header/Overview answer to "is the local route usable?" (review round 2, B11). <see cref="Health"/> is
/// the health actually used, <see cref="Source"/> says where it came from.</summary>
public sealed record LocalHealthView(
    LocalRouteState State,
    string? ConnectionId,
    string? ModelId,
    ModelHealth? Health,
    string Source);

/// <summary>Derives local-route health from the server only (docs/ui-api-contract.md "Header local health"): the
/// connection the catalog's <c>local</c> view names (<c>local.connection_id</c> + <c>local.model_id</c> — the one the
/// Host observed or routes, never a connection merely named "local"), with its CURRENT runtime health. The freshest
/// source wins: <c>/admin/status.connection_health</c> for exactly that key (pushed every 2 s), else
/// <c>catalog.local.health</c>; <c>local.endpoint_health</c> only when both say "unknown". A failure newer than the
/// last success is Degraded even if an older success exists. Pure.</summary>
public static class LocalHealth
{
    public static LocalHealthView Derive(CatalogResponse? catalog, IReadOnlyList<ConnectionHealthDto>? statusHealth)
    {
        if (catalog is null) return new(LocalRouteState.Unknown, null, null, null, "catalog not loaded");
        var local = catalog.Local;
        if (local is null || !local.Configured) return new(LocalRouteState.NotConfigured, local?.ConnectionId, local?.ModelId, null, "catalog");
        if (!local.Registered) return new(LocalRouteState.NotRegistered, local.ConnectionId, local.ModelId, null, "catalog");

        ModelHealth? health = null;
        string source = "none";
        if (local.ConnectionId is { Length: > 0 } cid && local.ModelId is { Length: > 0 } mid && statusHealth is not null)
        {
            var key = cid + ":" + mid;
            var row = statusHealth.FirstOrDefault(h => string.Equals(h.Key, key, StringComparison.Ordinal));
            if (row is not null && Known(row.Health)) { health = row.Health; source = "status connection_health"; }
        }
        if (health is null && Known(local.Health)) { health = local.Health; source = "catalog local.health"; }
        if (health is null && Known(local.EndpointHealth)) { health = local.EndpointHealth; source = "catalog local.endpoint_health"; }

        return new(Classify(health), local.ConnectionId, local.ModelId, health, source);
    }

    /// <summary>ok/failed (or any recorded time) is an observation; "unknown" with no times is not.</summary>
    public static bool Known(ModelHealth? h)
        => h is not null && (h.State is "ok" or "failed" || h.LastSuccessUtc is not null || h.LastFailureUtc is not null);

    public static LocalRouteState Classify(ModelHealth? h)
    {
        if (h is null || !Known(h)) return LocalRouteState.NotObserved;
        var success = h.LastSuccessUtc;
        var failure = h.LastFailureUtc;
        if (h.State == "failed" || (failure is not null && (success is null || failure > success))) return LocalRouteState.Degraded;
        return LocalRouteState.Ready;
    }
}

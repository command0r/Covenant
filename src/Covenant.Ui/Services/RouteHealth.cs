using Covenant.Ui.Logic;
using Covenant.Ui.Models;

namespace Covenant.Ui.Services;

/// <summary>One shared answer to "is the local route usable?" for the header, Overview and Models &amp;
/// connections. Built from the server's catalog and status only — never from a client checkbox.</summary>
public sealed record LocalRouteSummary(LocalRouteState State, string Text, string Tone, DateTimeOffset? LastSuccessUtc,
    DateTimeOffset? LastFailureUtc, string? LastError, string? Target);

/// <summary>Cross-checks the policy's routes (status.routes) against the provider catalog (catalog.models /
/// catalog.local). A route the policy permits but no adapter serves is "unresolved": matching requests pass
/// routing and are refused at the Provider stage. Unknown when the catalog has not loaded — never assumed healthy.</summary>
public static class RouteHealth
{
    /// <summary>Local route status from <see cref="LocalHealth.Derive"/> (catalog.local.connection_id/health and
    /// status.connection_health — the CURRENT runtime connection, review round 2 B11).</summary>
    public static LocalRouteSummary LocalRoute(CatalogResponse? catalog, StatusReport? status, DateTimeOffset nowUtc)
    {
        var v = LocalHealth.Derive(catalog, status?.ConnectionHealth);
        var target = v.ConnectionId is { Length: > 0 } c ? c + (v.ModelId is { Length: > 0 } m ? "/" + m : "") : null;
        var on = target is null ? "" : " (" + target + ")";
        var h = v.Health;
        return v.State switch
        {
            LocalRouteState.Unknown => new(v.State, "Local route: unknown (catalog not loaded)", "hollow", null, null, null, target),
            LocalRouteState.NotConfigured => new(v.State, "Local route: not configured", "hollow", null, null, null, target),
            LocalRouteState.NotRegistered => new(v.State, "Local route" + on + ": configured but not registered", "bad", null, null, null, target),
            LocalRouteState.Degraded => new(v.State,
                "Local route" + on + ": degraded (" + (h?.LastSuccessUtc is null ? "never succeeded" : "last success " + Fmt.Ago(h?.LastSuccessUtc, nowUtc)) + ")",
                "warn", h?.LastSuccessUtc, h?.LastFailureUtc, h?.LastError, target),
            LocalRouteState.Ready => new(v.State, "Local route" + on + ": ready (last success " + Fmt.Ago(h?.LastSuccessUtc, nowUtc) + ")", "ok",
                h?.LastSuccessUtc, h?.LastFailureUtc, h?.LastError, target),
            _ => new(v.State, "Local route" + on + ": registered, not yet observed", "hollow", null, null, null, target),
        };
    }

    public static IReadOnlyList<Covenant.Ui.Models.RouteView> UnresolvedRoutes(StatusReport? status, CatalogResponse? catalog)
    {
        if (status is null || catalog is null) return [];
        var registered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in catalog.Models)
            if (m.Registered) registered.Add(m.Adapter + ":" + m.ModelId);

        var unresolved = new List<Covenant.Ui.Models.RouteView>();
        foreach (var r in status.Routes)
            if (!registered.Contains(r.Adapter + ":" + r.Model)) unresolved.Add(r);
        return unresolved;
    }

    /// <summary>The catalog's local view is configured but no provider client resolves for it.</summary>
    public static bool LocalRouteMissing(StatusReport? status, CatalogResponse? catalog)
        => status is not null && catalog?.Local is { Configured: true, Registered: false };

    public static bool IsRegistered(Covenant.Ui.Models.RouteView route, CatalogResponse? catalog)
    {
        if (catalog is null) return false;
        foreach (var m in catalog.Models)
            if (m.Registered
                && string.Equals(m.Adapter, route.Adapter, StringComparison.Ordinal)
                && string.Equals(m.ModelId, route.Model, StringComparison.Ordinal))
                return true;
        return false;
    }
}

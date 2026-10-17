using Covenant.Ui.Models;

namespace Covenant.Ui.Services;

public enum LocalRouteState { Unknown, NotConfigured, NotRegistered, NotObserved, Ready, Degraded }

/// <summary>One shared answer to "is the local route usable?" for the header, Overview and Models &amp;
/// connections. Built from the server's catalog only — never from a client checkbox.</summary>
public sealed record LocalRouteSummary(LocalRouteState State, string Text, string Tone, DateTimeOffset? LastSuccessUtc,
    DateTimeOffset? LastFailureUtc, string? LastError);

/// <summary>Cross-checks the policy's routes (status.routes) against the provider catalog (catalog.models /
/// catalog.local). A route the policy permits but no adapter serves is "unresolved": matching requests pass
/// routing and are refused at the Provider stage. Unknown when the catalog has not loaded — never assumed healthy.</summary>
public static class RouteHealth
{
    /// <summary>Local route status. Health comes from the local model's observed health (real pipeline calls),
    /// falling back to the applied endpoint's probe health when the model has not been observed.
    /// A failure newer than the last success is "degraded" even if an older success exists.</summary>
    public static LocalRouteSummary LocalRoute(CatalogResponse? catalog, DateTimeOffset nowUtc)
    {
        if (catalog is null)
            return new(LocalRouteState.Unknown, "Local route: unknown (catalog not loaded)", "hollow", null, null, null);
        var local = catalog.Local;
        if (local is null || !local.Configured)
            return new(LocalRouteState.NotConfigured, "Local route: not configured", "hollow", null, null, null);
        if (!local.Registered)
            return new(LocalRouteState.NotRegistered, "Local route: configured but not registered", "bad", null, null, null);

        ModelHealth? health = null;
        foreach (var m in catalog.Models)
            if (IsLocalAdapter(m.Adapter) && string.Equals(m.ModelId, local.ModelId, StringComparison.Ordinal))
            {
                health = m.Health;
                break;
            }
        if (health is null || string.Equals(health.State, "unknown", StringComparison.OrdinalIgnoreCase) || health.State is null)
            health = local.EndpointHealth ?? health;

        var success = health?.LastSuccessUtc;
        var failure = health?.LastFailureUtc;
        var state = health?.State;
        if (string.Equals(state, "failed", StringComparison.OrdinalIgnoreCase)
            || (failure is not null && (success is null || failure > success)))
        {
            var lastOk = success is null ? "never succeeded" : "last success " + Fmt.Ago(success, nowUtc);
            return new(LocalRouteState.Degraded, $"Local route: degraded ({lastOk})", "warn", success, failure, health?.LastError);
        }
        if (string.Equals(state, "ok", StringComparison.OrdinalIgnoreCase) || success is not null)
            return new(LocalRouteState.Ready, $"Local route: ready (last success {Fmt.Ago(success, nowUtc)})", "ok", success, failure, health?.LastError);
        return new(LocalRouteState.NotObserved, "Local route: registered, not yet observed", "hollow", null, null, null);
    }

    public static bool IsLocalAdapter(string adapter) => string.Equals(adapter, "local", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<RouteView> UnresolvedRoutes(StatusReport? status, CatalogResponse? catalog)
    {
        if (status is null || catalog is null) return [];
        var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in catalog.Models)
            if (m.Registered) registered.Add(m.Adapter + ":" + m.ModelId);

        var unresolved = new List<RouteView>();
        foreach (var r in status.Routes)
            if (!registered.Contains(r.Adapter + ":" + r.Model)) unresolved.Add(r);
        return unresolved;
    }

    public static bool LocalRouteMissing(StatusReport? status, CatalogResponse? catalog)
    {
        if (status is null || catalog is null) return false;
        if (catalog.Local is { Configured: true, Registered: false }) return true;
        foreach (var r in UnresolvedRoutes(status, catalog))
            if (IsLocalAdapter(r.Adapter)) return true;
        return false;
    }

    public static bool IsRegistered(RouteView route, CatalogResponse? catalog)
    {
        if (catalog is null) return false;
        foreach (var m in catalog.Models)
            if (m.Registered
                && string.Equals(m.Adapter, route.Adapter, StringComparison.OrdinalIgnoreCase)
                && string.Equals(m.ModelId, route.Model, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}

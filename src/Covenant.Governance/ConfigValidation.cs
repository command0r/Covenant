using Covenant.Core;

namespace Covenant.Governance;

/// <summary>One validation finding. Path uses the API's snake_case field names, e.g. "policies[1].targets[0]".</summary>
public sealed record ConfigIssue(string Path, string Code, string Message);

public sealed record ConfigValidationResult(IReadOnlyList<ConfigIssue> Errors, IReadOnlyList<ConfigIssue> Warnings)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>Startup-owned facts the validator checks a candidate against (ADR-0011). None of these are
/// console-editable: a configuration edit can never widen egress or invent a secret.</summary>
public sealed class ValidationEnvironment
{
    /// <summary>Egress allow-list as "host:port" (startup Providers:ProbeAllowList ∪ bootstrap endpoints).</summary>
    public required IReadOnlySet<string> EgressAllowList { get; init; }

    /// <summary>Secret reference → is a non-empty value configured server-side. Never returns the value.</summary>
    public required Func<string, bool> SecretAvailable { get; init; }

    /// <summary>Legacy-import compatibility: the imported PII/PHI placeholder target on the unconfigured "local"
    /// connection is a WARNING (behaviour preserved, provider stage refuses it) instead of an error.</summary>
    public bool LegacyCompat { get; init; }
}

/// <summary>Pure configuration validator (ADR-0011/0013/0014). Codes are part of the API contract
/// (docs/ui-api-contract.md v2 "Configuration").</summary>
public static class ConfigValidator
{
    public static readonly IReadOnlySet<string> LegacySecretRefs =
        new HashSet<string>(StringComparer.Ordinal) { "OpenAI:ApiKey", "Anthropic:ApiKey", "Local:ApiKey" };

    private static readonly HashSet<string> RetryKinds = new(StringComparer.Ordinal) { "connect", "timeout", "429", "5xx" };

    public static bool IsValidId(string? id)
        => id is { Length: > 0 and <= 64 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    public static bool IsAllowedSecretRef(string secretRef)
        => LegacySecretRefs.Contains(secretRef)
           || (secretRef.StartsWith("Secrets:", StringComparison.Ordinal) && IsValidId(secretRef["Secrets:".Length..]));

    /// <summary>Provider-default endpoint authority when a cloud connection leaves Endpoint null.</summary>
    public static string? DefaultAuthority(ProviderKind kind) => kind switch
    {
        ProviderKind.OpenAi => "api.openai.com:443",
        ProviderKind.Anthropic => "api.anthropic.com:443",
        _ => null,
    };

    /// <summary>Normalized "host:port" of an absolute http(s) endpoint with no userinfo/query/fragment; null if invalid.</summary>
    public static string? EndpointAuthority(string endpoint)
    {
        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var u)) return null;
        if (u.Scheme is not ("http" or "https")) return null;
        if (!string.IsNullOrEmpty(u.UserInfo) || !string.IsNullOrEmpty(u.Query) || !string.IsNullOrEmpty(u.Fragment)) return null;
        return $"{u.Host}:{u.Port}";
    }

    public static ConfigValidationResult Validate(CovenantConfig c, ValidationEnvironment env)
    {
        var errors = new List<ConfigIssue>();
        var warnings = new List<ConfigIssue>();
        void Err(string path, string code, string msg) => errors.Add(new ConfigIssue(path, code, msg));
        void Warn(string path, string code, string msg) => warnings.Add(new ConfigIssue(path, code, msg));

        // ---- connections & models ----
        var connections = new Dictionary<string, ConnectionConfig>(StringComparer.Ordinal);
        for (int i = 0; i < c.Connections.Count; i++)
        {
            var conn = c.Connections[i];
            var p = $"connections[{i}]";
            if (!IsValidId(conn.Id)) { Err($"{p}.id", "invalid_id", "connection id must be 1–64 chars of [A-Za-z0-9._-]"); continue; }
            if (!connections.TryAdd(conn.Id, conn)) { Err($"{p}.id", "duplicate_id", $"connection id '{conn.Id}' is used more than once"); continue; }
            if (string.IsNullOrWhiteSpace(conn.DisplayName)) Err($"{p}.display_name", "required", "display name is required");

            if (conn.Kind is ProviderKind.OpenAi or ProviderKind.Anthropic && conn.Boundary != Boundary.Cloud)
                Err($"{p}.boundary", "invalid_boundary", $"{conn.Kind} is a cloud provider; boundary must be cloud");

            string? authority;
            if (conn.Endpoint is null)
            {
                authority = DefaultAuthority(conn.Kind);
                if (authority is null)
                    Err($"{p}.endpoint", "endpoint_required", $"{conn.Kind} connections need an explicit http(s) endpoint");
            }
            else
            {
                authority = EndpointAuthority(conn.Endpoint);
                if (authority is null)
                    Err($"{p}.endpoint", "invalid_endpoint", "endpoint must be an absolute http(s) URL without credentials, query or fragment");
            }
            if (authority is not null && !env.EgressAllowList.Contains(authority))
            {
                var msg = $"destination {authority} is not on the startup egress allow-list (Providers:ProbeAllowList); a configuration edit cannot widen egress";
                if (conn.Enabled) Err($"{p}.endpoint", "endpoint_not_allow_listed", msg);
                else Warn($"{p}.endpoint", "endpoint_not_allow_listed", msg);
            }

            if (conn.SecretRef is { } secretRef)
            {
                if (!IsAllowedSecretRef(secretRef))
                    Err($"{p}.secret_ref", "invalid_secret_ref", "secret reference must be Secrets:<name> or OpenAI:ApiKey / Anthropic:ApiKey / Local:ApiKey");
                else if (conn.Enabled && !env.SecretAvailable(secretRef))
                    Err($"{p}.secret_ref", "secret_unavailable", $"secret '{secretRef}' is not configured on the server (set it in the environment/vault and restart)");
            }
            else if (conn.Enabled && conn.Kind is ProviderKind.OpenAi or ProviderKind.Anthropic)
                Err($"{p}.secret_ref", "secret_unavailable", $"{conn.Kind} requires a server-side secret reference");

            if (conn.Enabled && conn.Boundary == Boundary.Cloud && c.Mode == ProviderMode.LocalOnly)
                Err($"{p}.enabled", "cloud_in_local_only", "local-only mode cannot have an enabled cloud connection (disable it or switch to hybrid)");

            if (conn.TimeoutSeconds is < 1 or > 600)
                Err($"{p}.timeout_seconds", "invalid_setting", "timeout must be 1–600 seconds");

            var modelIds = new HashSet<string>(StringComparer.Ordinal);
            for (int m = 0; m < conn.Models.Count; m++)
            {
                var model = conn.Models[m];
                var mp = $"{p}.models[{m}]";
                if (string.IsNullOrWhiteSpace(model.ModelId) || model.ModelId.Length > 200)
                    Err($"{mp}.model_id", "invalid_id", "model id is required (≤200 chars, the exact provider id/tag)");
                else if (!modelIds.Add(model.ModelId))
                    Err($"{mp}.model_id", "duplicate_id", $"model '{model.ModelId}' is listed twice on '{conn.Id}'");
                if (model.Price is { } price && (price.InputPer1M < 0 || price.OutputPer1M < 0))
                    Err($"{mp}.price", "invalid_price", "prices cannot be negative");
            }
        }

        // ---- profiles ----
        var profiles = new HashSet<string>(StringComparer.Ordinal) { PolicyEngineV2.LegacyInternalProfile };
        var declaredProfiles = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < c.Profiles.Count; i++)
        {
            var pr = c.Profiles[i];
            if (!IsValidId(pr.Id)) { Err($"profiles[{i}].id", "invalid_id", "profile id must be 1–64 chars of [A-Za-z0-9._-]"); continue; }
            if (!declaredProfiles.Add(pr.Id)) Err($"profiles[{i}].id", "duplicate_id", $"profile id '{pr.Id}' is used more than once");
            profiles.Add(pr.Id);
        }

        // ---- applications ----
        var applications = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < c.Applications.Count; i++)
        {
            var app = c.Applications[i];
            var p = $"applications[{i}]";
            if (!IsValidId(app.Id)) { Err($"{p}.id", "invalid_id", "application id must be 1–64 chars of [A-Za-z0-9._-]"); continue; }
            if (!applications.Add(app.Id)) Err($"{p}.id", "duplicate_id", $"application id '{app.Id}' is used more than once");
            if (string.IsNullOrWhiteSpace(app.Team)) Err($"{p}.team", "required", "team is required");
            for (int g = 0; g < app.GrantedProfiles.Count; g++)
                if (!profiles.Contains(app.GrantedProfiles[g]))
                    Err($"{p}.granted_profiles[{g}]", "profile_not_found", $"profile '{app.GrantedProfiles[g]}' does not exist");
            if (app.DefaultProfile is { } def)
            {
                if (!profiles.Contains(def)) Err($"{p}.default_profile", "profile_not_found", $"profile '{def}' does not exist");
                else if (!app.GrantedProfiles.Contains(def, StringComparer.Ordinal))
                    Err($"{p}.default_profile", "default_profile_not_granted", $"default profile '{def}' is not granted to '{app.Id}'");
            }
            if (app.MaxOutputTokens < 1) Err($"{p}.max_output_tokens", "invalid_setting", "max output tokens must be ≥ 1");
        }

        // ---- budgets ----
        bool anyHard = c.Budgets.Any(b => b.Enforcement == BudgetEnforcement.Hard);
        var budgetIds = new HashSet<string>(StringComparer.Ordinal);
        var scopes = new HashSet<string>(StringComparer.Ordinal);
        bool hasGlobal = false;
        for (int i = 0; i < c.Budgets.Count; i++)
        {
            var b = c.Budgets[i];
            var p = $"budgets[{i}]";
            if (!IsValidId(b.Id)) Err($"{p}.id", "invalid_id", "budget id must be 1–64 chars of [A-Za-z0-9._-]");
            else if (!budgetIds.Add(b.Id)) Err($"{p}.id", "duplicate_id", $"budget id '{b.Id}' is used more than once");
            if (!IsValidScope(b.Scope))
                Err($"{p}.scope", "invalid_budget_scope", "scope must be 'global', 'team:<id>' or 'application:<id>'");
            else
            {
                if (b.Scope == "global") hasGlobal = true;
                if (!scopes.Add(b.Scope))
                    Err($"{p}.scope", "invalid_budget_scope", $"more than one budget for scope '{b.Scope}' (ambiguous cap)");
                if (b.Scope.StartsWith("application:", StringComparison.Ordinal) && !applications.Contains(b.Scope["application:".Length..]))
                    Warn($"{p}.scope", "invalid_budget_scope", $"no application '{b.Scope["application:".Length..]}' is configured");
            }
            if (b.CapUsd < 0) Err($"{p}.cap_usd", "invalid_budget_cap", "cap cannot be negative");
            if (b.WarnAt.Any(w => w <= 0m || w > 1m)) Err($"{p}.warn_at", "invalid_budget_cap", "warning thresholds are fractions in (0, 1]");
        }
        if (!hasGlobal)
            Err("budgets", "missing_global_budget", "a global budget is required — nothing is unlimited (fail-closed FinOps)");

        // ---- policies ----
        var policyIds = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < c.Policies.Count; i++)
        {
            var pol = c.Policies[i];
            var p = $"policies[{i}]";
            if (!IsValidId(pol.Id)) Err($"{p}.id", "invalid_id", "policy id must be 1–64 chars of [A-Za-z0-9._-]");
            else if (!policyIds.Add(pol.Id)) Err($"{p}.id", "duplicate_id", $"policy id '{pol.Id}' is used more than once");
            if (pol.Classifications.Count == 0) Err($"{p}.classifications", "required", "a policy applies to at least one classification");
            for (int g = 0; g < pol.Profiles.Count; g++)
                if (!profiles.Contains(pol.Profiles[g]))
                    Err($"{p}.profiles[{g}]", "profile_not_found", $"profile '{pol.Profiles[g]}' does not exist");
            if (pol.Targets.Count == 0 && pol.Enabled)
                Warn($"{p}.targets", "no_route_for_classification", "policy has no targets: its matching traffic is refused");
            if (pol.Strategy == RoutingStrategy.LengthHeuristic && pol.ThresholdTokens < 0)
                Err($"{p}.threshold_tokens", "invalid_setting", "threshold must be ≥ 0");
            if (pol.Retry.MaxAttempts is < 1 or > 3) Err($"{p}.retry.max_attempts", "invalid_retry", "max attempts must be 1–3");
            if (pol.Retry.DeadlineMs is < 1_000 or > 600_000) Err($"{p}.retry.deadline_ms", "invalid_retry", "deadline must be 1 000–600 000 ms");
            if (pol.Retry.On.Any(k => !RetryKinds.Contains(k))) Err($"{p}.retry.on", "invalid_retry", "retry triggers ⊆ {connect, timeout, 429, 5xx}");

            bool sensitive = pol.Classifications.Any(x => x >= DataClassification.Pii);
            for (int t = 0; t < pol.Targets.Count; t++)
            {
                var target = pol.Targets[t];
                var tp = $"{p}.targets[{t}]";
                if (!connections.TryGetValue(target.ConnectionId, out var conn))
                {
                    var msg = $"connection '{target.ConnectionId}' does not exist";
                    if (env.LegacyCompat) Warn(tp, "unknown_connection", msg + " — imported legacy rule; requests matching it are refused at the provider stage");
                    else Err(tp, "unknown_connection", msg);
                    continue;
                }
                var model = conn.Models.FirstOrDefault(m => string.Equals(m.ModelId, target.ModelId, StringComparison.Ordinal));
                if (model is null) { Err(tp, "unknown_model", $"model '{target.ModelId}' is not permitted on connection '{conn.Id}'"); continue; }
                if (conn.Boundary == Boundary.Cloud)
                {
                    if (sensitive && !pol.AllowSensitiveCloud)
                        Warn(tp, "sensitive_cloud_without_ack", $"cloud target never serves PII/PHI under this policy (allow_sensitive_cloud is off); it is ineligible for those classifications");
                    else if (sensitive && pol.AllowSensitiveCloud)
                        Warn(tp, "sensitive_cloud_without_ack", "WARNING: this policy explicitly allows PII/PHI to reach a cloud destination");
                    if (model.Price is null && anyHard && pol.Enabled)
                        Err(tp, "unpriced_paid_target_under_hard_budget", $"{conn.Id}/{model.ModelId} has no price; a paid destination cannot run under a hard budget unpriced");
                }
            }
        }

        // Ambiguous overlap: two enabled policies at the same specificity covering one (classification, profile).
        var enabled = c.Policies.Select((pol, idx) => (pol, idx)).Where(x => x.pol.Enabled).ToList();
        for (int a = 0; a < enabled.Count; a++)
            for (int b = a + 1; b < enabled.Count; b++)
            {
                var (pa, ia) = enabled[a];
                var (pb, _) = enabled[b];
                if (!pa.Classifications.Intersect(pb.Classifications).Any()) continue;
                bool genericA = pa.Profiles.Count == 0, genericB = pb.Profiles.Count == 0;
                if (genericA != genericB) continue;   // specific beats generic: defined precedence, not an overlap
                if (!genericA && !pa.Profiles.Intersect(pb.Profiles, StringComparer.Ordinal).Any()) continue;
                Err($"policies[{ia}]", "ambiguous_policy_overlap",
                    $"policies '{pa.Id}' and '{pb.Id}' both match the same classification/profile at the same precedence");
            }

        // Coverage: every classification should have some applicable policy for the default (no-profile) path.
        foreach (var cls in Enum.GetValues<DataClassification>())
        {
            if (cls == DataClassification.Public) continue;   // Public is reachable only via an explicit Public profile
            if (!c.Policies.Any(pol => pol.Enabled && pol.Classifications.Contains(cls)))
                Warn("policies", "no_route_for_classification", $"no enabled policy covers {cls}: such requests are refused");
        }

        // ---- settings ----
        var s = c.Settings;
        if (s.CacheTtlSeconds < 0) Err("settings.cache_ttl_seconds", "invalid_setting", "must be ≥ 0");
        if (s.CacheMaxEntries < 1) Err("settings.cache_max_entries", "invalid_setting", "must be ≥ 1");
        if (s.PromptPreviewChars is < 0 or > 500) Err("settings.prompt_preview_chars", "invalid_setting", "must be 0–500");
        if (s.TraceRetentionMinutes is < 1 or > 1440) Err("settings.trace_retention_minutes", "invalid_setting", "must be 1–1440");
        if (s.RateLimitGlobalPerMinute < 0) Err("settings.rate_limit_global_per_minute", "invalid_setting", "must be ≥ 0");
        if (s.RateLimitPerTeamPerMinute < 0) Err("settings.rate_limit_per_team_per_minute", "invalid_setting", "must be ≥ 0");

        if (c.SavingsBaseline is { } baseline
            && !(connections.TryGetValue(baseline.ConnectionId, out var bc)
                 && bc.Models.Any(m => m.ModelId == baseline.ModelId && m.Price is not null)))
            Warn("savings_baseline", "invalid_savings_baseline", "savings baseline must name a priced connection/model; the metric stays hidden");

        return new ConfigValidationResult(errors, warnings);
    }

    public static bool IsValidScope(string? scope)
    {
        if (scope == "global") return true;
        if (scope is null) return false;
        foreach (var prefix in new[] { "team:", "application:" })
            // Team names come from key configuration and may be free text: any non-control text ≤128 chars.
            if (scope.StartsWith(prefix, StringComparison.Ordinal) && scope.Length > prefix.Length && scope.Length - prefix.Length <= 128
                && !scope.Any(char.IsControl) && scope[prefix.Length..].Trim().Length > 0)
                return true;
        return false;
    }
}

/// <summary>Impact preview row: effective route for one (classification, profile) before vs after.</summary>
public sealed record ImpactRow(string Classification, string Profile, string Before, string After, bool Changed);

public sealed record ConfigImpact(
    IReadOnlyList<ImpactRow> Classifications,
    IReadOnlyList<string> ApplicationsAffected,
    IReadOnlyList<string> UnavailableDestinations,
    IReadOnlyList<string> PricingGaps,
    IReadOnlyList<string> NewlyRefused);

/// <summary>Computes what a candidate would change (spec §4.1): effective route per classification × profile,
/// newly refused combinations, unavailable destinations (disabled / unknown connection / secret unavailable /
/// no client registered) and pricing gaps. Uses the real policy engine; never calls a provider.</summary>
public static class ImpactAnalyzer
{
    public static ConfigImpact Analyze(
        IRequestConfiguration? before, IRequestConfiguration after, Func<string, bool> secretAvailable,
        Func<string, string, bool>? isRegistered = null)
    {
        var profileIds = new List<string> { PolicyEngineV2.LegacyInternalProfile };
        foreach (var p in (before?.Config.Profiles ?? Array.Empty<WorkloadProfileConfig>()).Concat(after.Config.Profiles))
            if (!profileIds.Contains(p.Id)) profileIds.Add(p.Id);

        var rows = new List<ImpactRow>();
        var newlyRefused = new List<string>();
        var changedProfiles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cls in Enum.GetValues<DataClassification>())
            foreach (var profile in profileIds)
            {
                var b = before is null ? "not configured" : PolicyEngineV2.Summary(before, cls, profile);
                var a = PolicyEngineV2.Summary(after, cls, profile);
                bool changed = !string.Equals(a, b, StringComparison.Ordinal);
                rows.Add(new ImpactRow(cls.ToString(), profile, b, a, changed));
                if (changed) changedProfiles.Add(profile);
                if (changed && a.StartsWith("refused", StringComparison.Ordinal) && (before is null || !b.StartsWith("refused", StringComparison.Ordinal)))
                    newlyRefused.Add($"{cls} / {profile}: {a}");
            }

        var apps = new List<string>();
        foreach (var app in after.Config.Applications)
        {
            var prior = before?.FindApplication(app.Id);
            bool grantsHitChange = app.GrantedProfiles.Any(changedProfiles.Contains)
                || (app.GrantedProfiles.Count == 0 && changedProfiles.Contains(PolicyEngineV2.LegacyInternalProfile));
            if (prior is null || grantsHitChange || prior.Enabled != app.Enabled || prior.ConsoleTesting != app.ConsoleTesting
                || !prior.GrantedProfiles.SequenceEqual(app.GrantedProfiles) || prior.DefaultProfile != app.DefaultProfile)
                apps.Add(app.Id);
        }
        if (before is not null)
            foreach (var removed in before.Config.Applications.Where(x => after.FindApplication(x.Id) is null))
                apps.Add(removed.Id + " (removed)");

        var unavailable = new SortedSet<string>(StringComparer.Ordinal);
        var gaps = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var pol in after.Config.Policies.Where(p => p.Enabled))
            foreach (var t in pol.Targets)
            {
                var key = $"{t.ConnectionId}/{t.ModelId}";
                var conn = after.FindConnection(t.ConnectionId);
                if (conn is null) { unavailable.Add($"{key}: unknown connection"); continue; }
                if (!conn.Enabled) unavailable.Add($"{key}: connection disabled");
                else if (conn.SecretRef is { } sr && !secretAvailable(sr)) unavailable.Add($"{key}: secret unavailable");
                else if (isRegistered is not null && !isRegistered(t.ConnectionId, t.ModelId)) unavailable.Add($"{key}: no provider client registered");
                var model = after.FindModel(t.ConnectionId, t.ModelId);
                if (model is not null && model.Price is null)
                    gaps.Add(conn.Boundary == Boundary.Cloud ? $"{key} (cloud, unpriced)" : $"{key} (local, unpriced)");
            }

        return new ConfigImpact(rows, apps, unavailable.ToList(), gaps.ToList(), newlyRefused);
    }
}

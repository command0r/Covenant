using System.Globalization;
using System.Net;
using System.Text.Json;
using Covenant.Ui.Models;

namespace Covenant.Ui.Logic;

/// <summary>Deep copies and canonical comparison of configuration DTOs. Copies go through the source-generated
/// UiJsonContext (the same codec the console uses on the wire), so a copy is exactly what would be sent and no
/// list or nested object is shared with the original.</summary>
public static class ConfigCopy
{
    public static CovenantConfigDto Deep(CovenantConfigDto source)
        => JsonSerializer.Deserialize(Canonical(source), UiJsonContext.Default.CovenantConfigDto)
           ?? throw new InvalidOperationException("configuration copy failed");

    public static ConnectionDto Deep(ConnectionDto source)
    {
        var holder = new CovenantConfigDto { Connections = [source] };
        return Deep(holder).Connections[0];
    }

    /// <summary>The wire JSON of a configuration (nulls omitted) — the basis of dirty tracking.</summary>
    public static string Canonical(CovenantConfigDto config) => JsonSerializer.Serialize(config, UiJsonContext.Default.CovenantConfigDto);

    /// <summary>Canonical JSON of one connection (for "changed in draft" flags).</summary>
    public static string Canonical(ConnectionDto connection) => Canonical(new CovenantConfigDto { Connections = [connection] });

    public static string Canonical(PolicyDto policy) => Canonical(new CovenantConfigDto { Policies = [policy] });

    public static string Canonical(BudgetConfigDto budget) => Canonical(new CovenantConfigDto { Budgets = [budget] });

    public static bool SameContent(CovenantConfigDto a, CovenantConfigDto b) => string.Equals(Canonical(a), Canonical(b), StringComparison.Ordinal);
}

/// <summary>An edit session over one applied configuration version: a deep-copied, mutable DRAFT plus the
/// canonical text of the version it was taken from. Dirty = the draft's wire content differs from the base.
/// The base is never mutated, so Cancel/Reset is exact.</summary>
public sealed class ConfigDraft
{
    private readonly string _baseJson;

    public ConfigDraft(int baseVersion, string? baseEtag, CovenantConfigDto applied)
    {
        BaseVersion = baseVersion;
        BaseEtag = string.IsNullOrWhiteSpace(baseEtag) ? EtagFor(baseVersion) : baseEtag!;
        Base = ConfigCopy.Deep(applied);
        _baseJson = ConfigCopy.Canonical(Base);
        Draft = ConfigCopy.Deep(applied);
    }

    public int BaseVersion { get; }
    /// <summary>Sent as If-Match on Save: the version the operator started editing from.</summary>
    public string BaseEtag { get; }
    /// <summary>Private copy of the applied configuration (never handed out for editing).</summary>
    public CovenantConfigDto Base { get; }
    public CovenantConfigDto Draft { get; private set; }

    public bool IsDirty => !string.Equals(_baseJson, ConfigCopy.Canonical(Draft), StringComparison.Ordinal);

    /// <summary>Throws the draft away and starts again from the base version.</summary>
    public void Reset() => Draft = ConfigCopy.Deep(Base);

    public static string EtagFor(int version) => "\"v" + version.ToString(CultureInfo.InvariantCulture) + "\"";
}

/// <summary>Human change summary (sent as <c>change_summary</c>, ≤ 500 chars server-side; never secret values —
/// the configuration holds only secret REFERENCES). Lists added/removed/changed ids per section.</summary>
public static class ChangeSummary
{
    public static IReadOnlyList<string> Lines(CovenantConfigDto before, CovenantConfigDto after)
    {
        var lines = new List<string>();
        if (!string.Equals(before.Mode, after.Mode, StringComparison.Ordinal))
            lines.Add("mode " + (before.Mode ?? "unset") + " → " + (after.Mode ?? "unset"));
        Section(lines, "connection", before.Connections, after.Connections, c => c.Id, c => Json(c));
        Section(lines, "policy", before.Policies, after.Policies, p => p.Id, p => Json(p));
        Section(lines, "budget", before.Budgets, after.Budgets, b => b.Id, b => Json(b));
        Section(lines, "application", before.Applications, after.Applications, a => a.Id, a => Json(a));
        Section(lines, "profile", before.Profiles, after.Profiles, p => p.Id, p => Json(p));
        if (!string.Equals(Json(before.Settings), Json(after.Settings), StringComparison.Ordinal))
            lines.Add("operational settings changed");
        if (!string.Equals(Json(before.WebSearch), Json(after.WebSearch), StringComparison.Ordinal))
            lines.Add("web search settings changed");
        return lines;
    }

    /// <summary>One line for the PUT body: "Console: connection ollama changed; policy public changed".</summary>
    public static string Text(CovenantConfigDto before, CovenantConfigDto after)
    {
        var lines = Lines(before, after);
        var text = lines.Count == 0 ? "Console: no effective change" : "Console: " + string.Join("; ", lines);
        return text.Length <= 480 ? text : text[..479] + "…";
    }

    private static void Section<T>(List<string> lines, string noun, IReadOnlyList<T> before, IReadOnlyList<T> after,
        Func<T, string> id, Func<T, string> content)
    {
        var b = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var x in before) b[id(x)] = content(x);
        var a = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var x in after) a[id(x)] = content(x);
        foreach (var k in a.Keys.Where(k => !b.ContainsKey(k))) lines.Add(noun + " " + k + " added");
        foreach (var k in b.Keys.Where(k => !a.ContainsKey(k))) lines.Add(noun + " " + k + " removed");
        foreach (var k in a.Keys.Where(k => b.TryGetValue(k, out var old) && !string.Equals(old, a[k], StringComparison.Ordinal)))
            lines.Add(noun + " " + k + " changed");
        var orderBefore = before.Select(id).Where(a.ContainsKey).ToList();
        var orderAfter = after.Select(id).Where(b.ContainsKey).ToList();
        if (!orderBefore.SequenceEqual(orderAfter, StringComparer.Ordinal)) lines.Add(noun + " order changed");
    }

    private static string Json<T>(T value)
    {
        // Wrap in a config so every section uses the one registered source-generated type.
        var holder = new CovenantConfigDto();
        switch (value)
        {
            case ConnectionDto c: holder.Connections.Add(c); break;
            case PolicyDto p: holder.Policies.Add(p); break;
            case BudgetConfigDto b: holder.Budgets.Add(b); break;
            case ApplicationDto ap: holder.Applications.Add(ap); break;
            case WorkloadProfileDto pr: holder.Profiles.Add(pr); break;
            case OperationalSettingsDto s: holder.Settings = s; break;
            case WebSearchSettingsDto w: holder.WebSearch = w; break;
            default: return "";
        }
        return ConfigCopy.Canonical(holder);
    }
}

public enum EditOutcomeKind
{
    /// <summary>200 — the Host activated a NEW version (the only success).</summary>
    Applied,
    /// <summary>412 (or 409) — the active version moved since editing started; the draft is kept, never overwritten.</summary>
    Stale,
    /// <summary>422 — validation failed; the active version is unchanged; field errors map onto the draft.</summary>
    Invalid,
    /// <summary>428 — the Host refused a write without If-Match (a console bug, reported verbatim).</summary>
    PreconditionRequired,
    /// <summary>401 — admin token rejected (the session disconnects).</summary>
    Unauthorized,
    /// <summary>404 — endpoint or version not available on this Host.</summary>
    NotFound,
    /// <summary>Anything else (400, 5xx, transport): nothing is assumed applied.</summary>
    Failed,
}

/// <summary>The UI state after a config write. <see cref="Version"/> is the NEW active version on Applied and the
/// server-reported active version on Stale/Invalid when it sent one.</summary>
public sealed record EditOutcome(EditOutcomeKind Kind, string Message, int? Version = null,
    IReadOnlyList<ConfigIssueDto>? Errors = null, IReadOnlyList<ConfigIssueDto>? Warnings = null)
{
    public bool Succeeded => Kind == EditOutcomeKind.Applied;
}

/// <summary>Maps a config write's HTTP answer (status + body text) onto <see cref="EditOutcome"/>. Pure: the
/// service hands it the raw response, so every branch is unit-tested. Success is ONLY a 2xx carrying a version.</summary>
public static class EditOutcomes
{
    public static EditOutcome FromResponse(int status, string? body, bool rollback = false)
    {
        if (status is >= 200 and < 300)
        {
            var ok = TryParse(body, UiJsonContext.Default.ConfigPutResponseDto);
            if (ok is { Version: > 0 } put)
                return new EditOutcome(EditOutcomeKind.Applied,
                    (rollback ? "Rollback applied as new version v" : "Applied version v") + V(put.Version), put.Version);
            return new EditOutcome(EditOutcomeKind.Failed, "The Host answered " + V(status) + " without a version — not treated as applied.");
        }

        var err = TryParse(body, UiJsonContext.Default.ConfigErrorResponseDto);
        var reason = Clean(err?.Reason) ?? Clean(err?.Error);
        switch (status)
        {
            case (int)HttpStatusCode.PreconditionFailed:
            case (int)HttpStatusCode.Conflict:
                return new EditOutcome(EditOutcomeKind.Stale, StaleMessage(err?.ActiveVersion), err?.ActiveVersion);
            case (int)HttpStatusCode.UnprocessableEntity:
                var errors = err?.Errors ?? [];
                return new EditOutcome(EditOutcomeKind.Invalid,
                    errors.Count == 0
                        ? "The Host rejected the configuration (" + (reason ?? "validation failed") + "). The active version is unchanged."
                        : Plural(errors.Count, "validation error") + " — nothing was applied; the active version is unchanged.",
                    err?.ActiveVersion, errors, err?.Warnings ?? []);
            case 428:
                return new EditOutcome(EditOutcomeKind.PreconditionRequired,
                    "The Host requires the edited version (If-Match). Nothing was applied" + (reason is null ? "." : ": " + reason), err?.ActiveVersion);
            case (int)HttpStatusCode.Unauthorized:
                return new EditOutcome(EditOutcomeKind.Unauthorized, "Admin token rejected — reconnect. Nothing was applied.");
            case (int)HttpStatusCode.NotFound:
                return new EditOutcome(EditOutcomeKind.NotFound,
                    reason is not null && rollback ? "Not found: " + reason : "Not available on this Host (404) — nothing was applied.");
            default:
                return new EditOutcome(EditOutcomeKind.Failed,
                    "HTTP " + V(status) + (reason is null ? "" : ": " + reason) + " — nothing is assumed applied.");
        }
    }

    /// <summary>"Configuration changed since you started editing (now v8)."</summary>
    public static string StaleMessage(int? activeVersion)
        => "Configuration changed since you started editing" + (activeVersion is { } v ? " (now v" + V(v) + ")." : ".")
           + " Your draft was not applied and is kept.";

    private static T? TryParse<T>(string? body, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) where T : class
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try { return JsonSerializer.Deserialize(body, info); }
        catch (JsonException) { return null; }
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : (s.Length > 300 ? s[..299] + "…" : s.Trim());
    private static string V(int v) => v.ToString(CultureInfo.InvariantCulture);
    private static string Plural(int n, string noun) => V(n) + " " + noun + (n == 1 ? "" : "s");
}

/// <summary>Field-path lookup over validation issues. Paths are the Host's (<c>connections[0].models[1].price</c>,
/// <c>policies[2].targets[0]</c>, <c>settings.prompt_preview_chars</c>); a field shows the issues at its path and
/// below it.</summary>
public sealed class IssueIndex
{
    public static readonly IssueIndex Empty = new([], []);

    public IssueIndex(IReadOnlyList<ConfigIssueDto> errors, IReadOnlyList<ConfigIssueDto> warnings)
    {
        Errors = errors;
        Warnings = warnings;
    }

    public IReadOnlyList<ConfigIssueDto> Errors { get; }
    public IReadOnlyList<ConfigIssueDto> Warnings { get; }
    public bool HasErrors => Errors.Count > 0;

    public IReadOnlyList<ConfigIssueDto> ErrorsAt(string path) => Errors.Where(i => Under(i.Path, path)).ToList();
    public IReadOnlyList<ConfigIssueDto> WarningsAt(string path) => Warnings.Where(i => Under(i.Path, path)).ToList();
    public bool HasErrorAt(string path) => Errors.Any(i => Under(i.Path, path));

    /// <summary>Errors whose path is NOT under any of the given prefixes (shown in the page-level list so nothing
    /// is ever hidden because no field claimed it).</summary>
    public IReadOnlyList<ConfigIssueDto> Unclaimed(IEnumerable<string> claimedPrefixes)
    {
        var prefixes = claimedPrefixes.ToList();
        return Errors.Where(i => !prefixes.Any(p => Under(i.Path, p))).ToList();
    }

    /// <summary>True when <paramref name="issuePath"/> equals <paramref name="prefix"/> or is a member/element of it.</summary>
    public static bool Under(string? issuePath, string prefix)
    {
        if (string.IsNullOrEmpty(issuePath) || string.IsNullOrEmpty(prefix)) return false;
        if (string.Equals(issuePath, prefix, StringComparison.Ordinal)) return true;
        if (!issuePath.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var next = issuePath[prefix.Length];
        return next is '.' or '[';
    }

    public static string Connection(int i) => "connections[" + I(i) + "]";
    public static string Model(int c, int m) => Connection(c) + ".models[" + I(m) + "]";
    public static string Policy(int i) => "policies[" + I(i) + "]";
    public static string Target(int p, int t) => Policy(p) + ".targets[" + I(t) + "]";
    public static string Budget(int i) => "budgets[" + I(i) + "]";
    public static string Profile(int i) => "profiles[" + I(i) + "]";
    public static string Application(int i) => "applications[" + I(i) + "]";
    public static string Setting(string name) => "settings." + name;

    private static string I(int i) => i.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Actionable guidance for validation codes whose fix is OUTSIDE the console (startup configuration,
/// server-side secrets). Never contains a secret value — only key names.</summary>
public static class IssueHelp
{
    public static string? For(string? code) => code switch
    {
        "endpoint_not_allow_listed" =>
            "The endpoint's host:port is not on the startup egress allow-list. Add it to Providers:ProbeAllowList in the Host's startup configuration and restart Covenant; the console cannot widen where Covenant may connect.",
        "boundary_not_approved" =>
            "A Local connection's endpoint must be listed in Providers:LocalEndpoints (or the legacy Local:Endpoint) at startup; OpenAI and Anthropic connections are always Cloud. Add host:port there and restart.",
        "secret_unavailable" =>
            "The referenced secret is not set on the server. Set it in the Host's environment or vault (for Secrets:openai the environment variable is Secrets__openai) and restart; the console never sees or stores secret values.",
        "invalid_secret_ref" =>
            "Secret references must be Secrets:<name> or one of the legacy keys OpenAI:ApiKey, Anthropic:ApiKey, Local:ApiKey.",
        "cloud_in_local_only" =>
            "Local-only mode cannot execute cloud connections. Switch to Hybrid on the Policies page, or keep this connection disabled.",
        "sensitive_cloud_target" =>
            "PII/PHI never route to cloud destinations. Remove the cloud target from this policy.",
        "unpriced_paid_target_under_hard_budget" =>
            "A paid target needs input and output prices while a hard budget applies — an unknown price is never treated as $0.",
        "client_unbuildable" =>
            "Covenant could not build a provider client for this enabled connection (endpoint, secret or allow-list). Disable it or fix the cause above.",
        "ambiguous_policy_overlap" =>
            "Two enabled policies at the same specificity cover the same classification/profile. Narrow one by profile or disable it — Covenant never picks an arbitrary winner.",
        "default_profile_not_granted" =>
            "An application's default profile must also be granted to it. Grant it, or choose another default.",
        "profile_not_found" =>
            "A grant, default or policy names a workload profile that does not exist. Create the profile, or remove the reference.",
        "search_requires_hybrid" =>
            "Web search is an external call, so it needs Hybrid mode. Local-only means no external calls of any kind — switch the mode on the Policies page, or keep web search off.",
        "search_unpriced" =>
            "Enter the price per Tavily credit from your Tavily plan. An unknown price is never treated as $0, so search stays refused until it is set.",
        "search_egress_not_approved" =>
            "api.tavily.com:443 is not on the startup egress allow-list. Add it to Providers:ProbeAllowList in the Host's startup configuration and restart; the console cannot widen egress.",
        "search_profile_not_public" =>
            "Only a Public-baseline profile may use web search. Turn web search off for this profile, or change its baseline to Public.",
        "missing_global_budget" =>
            "A global budget is required: nothing is unlimited.",
        _ => null,
    };
}

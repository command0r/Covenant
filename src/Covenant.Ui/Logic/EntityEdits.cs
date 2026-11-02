using Covenant.Ui.Models;

namespace Covenant.Ui.Logic;

/// <summary>A private working copy of ONE configuration entity for a focused edit dialog. The applied configuration and
/// any draft are untouched until Save; Cancel simply drops the copy. <see cref="IsDirty"/> compares wire content, so a
/// change that is undone is not dirty.</summary>
public sealed class EntityEdit<T> where T : class
{
    private readonly Func<T, string> _canonical;
    private readonly string _originalJson;

    public EntityEdit(T original, Func<T, T> copy, Func<T, string> canonical, string? originalKey)
    {
        _canonical = canonical;
        Working = copy(original);
        _originalJson = canonical(original);
        OriginalKey = originalKey;
    }

    /// <summary>Configuration version the working copy was taken from (set when the dialog opens). A save is refused when the
    /// active version has moved since — the edited entity is never silently rebased onto newer configuration.</summary>
    public int? BaseVersion { get; set; }

    /// <summary>Id of the entity being edited; null when creating a new one.</summary>
    public string? OriginalKey { get; }
    public bool IsNew => OriginalKey is null;
    public T Working { get; }
    public bool IsDirty => IsNew || !string.Equals(_originalJson, _canonical(Working), StringComparison.Ordinal);
}

/// <summary>Copy, compare and apply single entities (policies, applications, workload profiles, budgets) to a
/// configuration. Each Apply* changes exactly one entity — or returns why it cannot, leaving the configuration untouched.</summary>
public static class EntityEdits
{
    // ---- copies through the one source-generated codec (same as the wire) ----

    public static PolicyDto Copy(PolicyDto p) => ConfigCopy.Deep(new CovenantConfigDto { Policies = [p] }).Policies[0];
    public static ApplicationDto Copy(ApplicationDto a) => ConfigCopy.Deep(new CovenantConfigDto { Applications = [a] }).Applications[0];
    public static WorkloadProfileDto Copy(WorkloadProfileDto w) => ConfigCopy.Deep(new CovenantConfigDto { Profiles = [w] }).Profiles[0];
    public static BudgetConfigDto Copy(BudgetConfigDto b) => ConfigCopy.Deep(new CovenantConfigDto { Budgets = [b] }).Budgets[0];

    public static string Canonical(PolicyDto p) => ConfigCopy.Canonical(p);
    public static string Canonical(ApplicationDto a) => ConfigCopy.Canonical(new CovenantConfigDto { Applications = [a] });
    public static string Canonical(WorkloadProfileDto w) => ConfigCopy.Canonical(new CovenantConfigDto { Profiles = [w] });
    public static string Canonical(BudgetConfigDto b) => ConfigCopy.Canonical(b);

    public static EntityEdit<PolicyDto> Edit(PolicyDto p) => new(p, Copy, Canonical, p.Id);
    public static EntityEdit<ApplicationDto> Edit(ApplicationDto a) => new(a, Copy, Canonical, a.Id);
    public static EntityEdit<WorkloadProfileDto> Edit(WorkloadProfileDto w) => new(w, Copy, Canonical, w.Id);
    public static EntityEdit<BudgetConfigDto> Edit(BudgetConfigDto b) => new(b, Copy, Canonical, b.Id);

    public static EntityEdit<PolicyDto> New(PolicyDto p) => new(p, Copy, Canonical, null);
    public static EntityEdit<ApplicationDto> New(ApplicationDto a) => new(a, Copy, Canonical, null);
    public static EntityEdit<WorkloadProfileDto> New(WorkloadProfileDto w) => new(w, Copy, Canonical, null);
    public static EntityEdit<BudgetConfigDto> New(BudgetConfigDto b) => new(b, Copy, Canonical, null);

    /// <summary>Null when a focused edit opened at <paramref name="openedAt"/> may be saved onto <paramref name="active"/>;
    /// otherwise the conflict message (no auto-rebase: cancel and reopen).</summary>
    public static string? StaleCheck(int? openedAt, int? active)
        => openedAt is { } o && active is { } a && o != a
            ? "The configuration changed since you opened this dialog (opened at v" + o.ToString(System.Globalization.CultureInfo.InvariantCulture)
              + ", now v" + a.ToString(System.Globalization.CultureInfo.InvariantCulture) + "). Nothing was saved. Cancel and reopen to edit the current version."
            : openedAt is null ? "The dialog has no base version — reopen it." : null;

    // ---- settings sections (single objects, not lists) ----

    public static OperationalSettingsDto Copy(OperationalSettingsDto o) => ConfigCopy.Deep(new CovenantConfigDto { Settings = o }).Settings!;
    public static WebSearchSettingsDto Copy(WebSearchSettingsDto w) => ConfigCopy.Deep(new CovenantConfigDto { WebSearch = w }).WebSearch!;
    public static string Canonical(OperationalSettingsDto o) => ConfigCopy.Canonical(new CovenantConfigDto { Settings = o });
    public static string Canonical(WebSearchSettingsDto w) => ConfigCopy.Canonical(new CovenantConfigDto { WebSearch = w });

    public static EntityEdit<OperationalSettingsDto> Edit(OperationalSettingsDto o) => new(o, Copy, Canonical, "settings");
    /// <summary>Web search settings; a configuration without them is edited as a new, disabled section.</summary>
    public static EntityEdit<WebSearchSettingsDto> Edit(WebSearchSettingsDto? w)
        => w is null ? new(new WebSearchSettingsDto { Enabled = false, MaxResults = 3, TimeoutSeconds = 10 }, Copy, Canonical, null)
                     : new(w, Copy, Canonical, "web_search");

    public static string? Apply(CovenantConfigDto cfg, EntityEdit<OperationalSettingsDto> e) { cfg.Settings = e.Working; return null; }
    public static string? Apply(CovenantConfigDto cfg, EntityEdit<WebSearchSettingsDto> e) { cfg.WebSearch = e.Working; return null; }

    // ---- apply ONE entity ----

    public static string? Apply(CovenantConfigDto cfg, EntityEdit<PolicyDto> e) => Upsert(cfg.Policies, e, p => p.Id, "policy");
    public static string? Apply(CovenantConfigDto cfg, EntityEdit<ApplicationDto> e) => Upsert(cfg.Applications, e, a => a.Id, "application");
    public static string? Apply(CovenantConfigDto cfg, EntityEdit<WorkloadProfileDto> e) => Upsert(cfg.Profiles, e, w => w.Id, "workload");
    public static string? Apply(CovenantConfigDto cfg, EntityEdit<BudgetConfigDto> e) => Upsert(cfg.Budgets, e, b => b.Id, "budget");

    private static string? Upsert<T>(List<T> list, EntityEdit<T> e, Func<T, string> id, string noun) where T : class
    {
        var newId = id(e.Working)?.Trim() ?? "";
        if (!ConnectionRules.IsValidId(newId)) return "The " + noun + " id must be 1–64 characters of letters, digits, '.', '_' or '-'.";
        int index = e.OriginalKey is null ? -1 : list.FindIndex(x => string.Equals(id(x), e.OriginalKey, StringComparison.Ordinal));
        if (e.OriginalKey is not null && index < 0)
            return "The " + noun + " '" + e.OriginalKey + "' no longer exists in the active configuration — reload and try again.";
        if (list.Where((x, i) => i != index).Any(x => string.Equals(id(x), newId, StringComparison.Ordinal)))
            return "Another " + noun + " already uses the id '" + newId + "'.";
        var copy = e.Working;
        if (index >= 0) list[index] = copy; else list.Add(copy);
        return null;
    }

    /// <summary>Removes one entity by id (null = removed; else why not).</summary>
    public static string? Remove<T>(List<T> list, string id, Func<T, string> key, string noun)
    {
        int n = list.RemoveAll(x => string.Equals(key(x), id, StringComparison.Ordinal));
        return n == 0 ? "The " + noun + " '" + id + "' no longer exists in the active configuration." : null;
    }

    // ---- route (policy) targets as primary + optional fallback ----

    /// <summary>Sets the policy's targets to [primary, fallback?] from "connection/model" keys, keeping each target's paused
    /// flag when it was already present and keeping any further approved fallbacks after them (shown in the dialog).</summary>
    public static string? SetPrimaryFallback(PolicyDto p, string? primaryKey, string? fallbackKey)
    {
        if (string.IsNullOrWhiteSpace(primaryKey)) return "Choose the primary model.";
        if (string.Equals(primaryKey, fallbackKey, StringComparison.Ordinal)) return "The fallback must differ from the primary.";
        PolicyTargetDto Make(string key)
        {
            var existing = p.Targets.FirstOrDefault(t => PolicyRoutes.Key(t) == key);
            var parts = key.Split('/', 2);
            return existing ?? new PolicyTargetDto { ConnectionId = parts[0], ModelId = parts.Length > 1 ? parts[1] : "" };
        }
        var rest = p.Targets.Skip(2).Where(t => PolicyRoutes.Key(t) != primaryKey && PolicyRoutes.Key(t) != fallbackKey).ToList();
        var targets = new List<PolicyTargetDto> { Make(primaryKey) };
        if (!string.IsNullOrWhiteSpace(fallbackKey)) targets.Add(Make(fallbackKey));
        targets.AddRange(rest);
        p.Targets = targets;
        return null;
    }

    public static string? PrimaryKey(PolicyDto p) => p.Targets.Count > 0 ? PolicyRoutes.Key(p.Targets[0]) : null;
    public static string? FallbackKey(PolicyDto p) => p.Targets.Count > 1 ? PolicyRoutes.Key(p.Targets[1]) : null;
}

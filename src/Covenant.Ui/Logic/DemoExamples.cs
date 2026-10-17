using System.Globalization;
using Covenant.Ui.Models;

namespace Covenant.Ui.Logic;

/// <summary>What an example needs from the configuration to show what it claims. Routing is ALWAYS policy-driven
/// (classification + workload profile); an example never relies on keyword routing or model "quality".</summary>
public enum ExampleNeed
{
    /// <summary>A Public profile allowed to use web search, and search ready on the server.</summary>
    PublicWebResearch,
    /// <summary>A Public profile whose policy primary is an Anthropic connection.</summary>
    PublicAnthropic,
    /// <summary>An Internal profile whose policy primary is the coder model.</summary>
    InternalCoder,
    /// <summary>An Internal profile whose policy primary is the stronger local research model.</summary>
    InternalResearch,
    /// <summary>A Public profile with an eligible cloud target, used with an explicit cloud model pin and a synthetic SSN.</summary>
    SensitiveCloudPinRefusal,
    /// <summary>The exact cache enabled (TTL &gt; 0) and a Public route.</summary>
    CacheRepeat,
    /// <summary>A hard budget applying to the application that cannot cover one more request.</summary>
    BudgetRefusal,
}

/// <summary>A named, meaningful demo example. It only FILLS the composer; Run stays explicit. <see cref="Expected"/> is
/// what the scenario is designed to show; the card also shows the Host's dry-run route for the current configuration,
/// and the run's own evidence is the result.</summary>
public sealed record DemoExample(
    string Id,
    string Title,
    string Teaches,
    string Expected,
    string Observe,
    ExampleNeed Need,
    string Prompt,
    WebSourcesMode WebSources,
    string Workflow,
    string UseCase,
    bool FreshNonce,
    int? MaxTokens = null,
    /// <summary>Words that name the intended workload/team ("research", "writing", "coding"): when several profiles
    /// fit, a profile or application whose id/name contains one is preferred. Never used for routing.</summary>
    string[]? Prefer = null);

/// <summary>A missing prerequisite: exactly what is missing and where to fix it. Never silently downgraded.</summary>
public sealed record ExamplePrerequisite(string Text, string? Href, string? HrefLabel);

/// <summary>An example resolved against the CURRENT applied configuration. When <see cref="Ready"/>, the application and
/// profile it will use are named; otherwise <see cref="Missing"/> says why (the Apply button is not offered).</summary>
public sealed record ExampleResolution(
    DemoExample Example,
    bool Ready,
    string? ApplicationId,
    string? ProfileId,
    string? ModelPin,
    string? ConfiguredRoute,
    string? Payer,
    IReadOnlyList<ExamplePrerequisite> Missing,
    /// <summary>Output bound actually enforced = min(request, application cap, model cap), and whether it is below what
    /// the example asks for (the answer may then be cut — shown, never hidden).</summary>
    string? OutputNote = null);

/// <summary>The policy (and its first eligible target) that would decide a (classification, profile) pair, mirroring
/// PolicyEngineV2.Match/Ineligibility. Used to FIND a fitting profile for an example — the Host's dry run is the preview.</summary>
public sealed record ResolvedRoute(PolicyDto? Policy, PolicyTargetDto? Primary, ConnectionDto? PrimaryConnection, IReadOnlyList<PolicyTargetDto> Eligible, string? Refusal);

public static class DemoExamples
{
    /// <summary>Installed model ids on the demo machine (docs/CONSOLE-DEMO-CLARITY-TASKS.md). Examples look for policies
    /// routing to these; nothing is assumed to exist — a missing route is reported with a link.</summary>
    public const string CoderModel = "qwen2.5-coder:7b";
    public const string ResearchModel = "qwen3.8:27b-mlx";

    /// <summary>Synthetic SSN-shaped marker. Area number 000 is never assigned, so it is explicitly invalid and identifies
    /// no one; it still matches the demo detector's US SSN pattern (\d{3}-\d{2}-\d{4}).</summary>
    public const string SyntheticSsn = "000-12-3456";

    public static readonly IReadOnlyList<DemoExample> All =
    [
        new("public-research", "Public research with web sources",
            "Fresh public facts come from one governed Tavily search, charged as a separate tool call.",
            "Search runs once (Required) against an official source, then the Public route answers only from what was retrieved.",
            "Web sources lists what was retrieved — retrieved, not independently verified; check the answer against the cited page. The search charge is shown separately from inference.",
            ExampleNeed.PublicWebResearch,
            "site:learn.microsoft.com What is the newest .NET version listed in Microsoft's official .NET release notes, and its release date? Answer in one sentence citing the page; if the retrieved pages do not say, reply \"not stated in the sources\".",
            WebSourcesMode.Required, "research", "public-research", false, Prefer: ["research"]),
        new("public-writing", "Public writing through Anthropic",
            "Public content may use an approved cloud model; the team pays the configured price.",
            "Routed by the Public writing profile's policy to the approved Anthropic model.",
            "Route shows the Anthropic model and policy; cost basis is reported provider usage × configured price.",
            ExampleNeed.PublicAnthropic,
            "Rewrite this public announcement as three short newsletter bullets: \"Covenant now records who requested each AI call, which policy routed it, and what it cost.\"",
            WebSourcesMode.Off, "writing", "public-newsletter", false, Prefer: ["writing"]),
        new("internal-coding", "Internal coding on the local coder model",
            "Internal work never leaves the perimeter; a coding workload profile routes to the coder model.",
            "Routed by the coding profile's policy to " + CoderModel + " (local, $0 configured).",
            "Route shows the local coder model; no cloud attempt; cost is $0 configured, infrastructure excluded.",
            ExampleNeed.InternalCoder,
            "Write a C# extension method Chunk<T>(this IEnumerable<T> source, int size) that yields arrays of at most size items, with XML doc comments and argument validation.",
            WebSourcesMode.Off, "coding", "internal-code-assist", false, Prefer: ["coding", "code"]),
        new("internal-research", "Internal research on the stronger local model",
            "A different workload profile selects a stronger local model — routing by declared workload, not by guessing from the prompt.",
            "Routed by the research profile's policy to " + ResearchModel + " (local).",
            "Route shows the stronger local model; the provider stage duration is the real measured time. A reasoning model spends part of the output bound thinking — if it is cut, the result says Truncated.",
            ExampleNeed.InternalResearch,
            "For an internal ledger service, compare optimistic and pessimistic concurrency control: when to use each, and two failure modes of each. Keep it under 200 words.",
            WebSourcesMode.Off, "research", "internal-analysis", false, MaxTokens: 2048, Prefer: ["research"]),
        new("sensitive-refusal", "Sensitive data never goes to the cloud",
            "Detection escalates a Public request containing an SSN pattern to PII; pinning a cloud model is then refused.",
            "Refused at policy: PII is never routed to a cloud destination. Nothing is sent to the provider.",
            "Outcome Refused at Policy with the reason; evidence still names the application, team and profile; no charge. Resolution: remove the marker, or let policy choose (PII → local).",
            ExampleNeed.SensitiveCloudPinRefusal,
            "Summarize this public support ticket in one sentence: \"Printer on floor 3 jammed again. Reporter SSN " + SyntheticSsn + " (synthetic test marker).\"",
            WebSourcesMode.Off, "writing", "sensitive-check", false, Prefer: ["writing"]),
        new("cache-repeat", "Repeat a request: exact cache hit",
            "An identical request within the cache TTL is answered from the exact cache at $0, still governed and audited.",
            "First run: cache miss, provider call. Second identical run: cache hit, no provider call.",
            "Run twice. The second trace shows Cache hit and skipped downstream stages; cost $0 — no provider call.",
            ExampleNeed.CacheRepeat,
            "In one sentence, what does an exact-match response cache do?",
            WebSourcesMode.Off, "demo", "cache-repeat", false),
        new("budget-refusal", "Budget refusal before any spend",
            "A request that cannot fit the remaining budget is refused before the provider call; nothing is charged.",
            "Refused at Budget: the reservation exceeds what remains in an applicable hard budget.",
            "Outcome Refused at Budget naming the budget; no provider attempt; reservations released.",
            ExampleNeed.BudgetRefusal,
            "Write a short paragraph about governed AI spending.",
            WebSourcesMode.Off, "demo", "budget-check", true, Prefer: ["budget"]),
    ];

    public static DemoExample? Find(string? id)
        => string.IsNullOrWhiteSpace(id) ? null : All.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));

    // ---------------------------------------------------------------- route mirror

    /// <summary>Mirror of PolicyEngineV2.Match + Ineligibility (UI copy; the Host decides at run time).</summary>
    public static ResolvedRoute Route(CovenantConfigDto cfg, string classification, string? profileId)
    {
        var matching = cfg.Policies.Where(p => p.Enabled
                && p.Classifications.Contains(classification, StringComparer.OrdinalIgnoreCase)
                && (p.Profiles.Count == 0 || (profileId is not null && p.Profiles.Contains(profileId, StringComparer.Ordinal))))
            .ToList();
        var specific = matching.Where(p => p.Profiles.Count > 0).ToList();
        var tier = specific.Count > 0 ? specific : matching;
        if (tier.Count == 0) return new(null, null, null, [], "no policy covers " + classification + " for this profile");
        if (tier.Count > 1) return new(null, null, null, [], "ambiguous: policies " + string.Join(", ", tier.Select(p => p.Id)) + " match at the same specificity");
        var policy = tier[0];
        if (policy.Paused) return new(policy, null, null, [], "policy " + policy.Id + " is paused");
        var eligible = policy.Targets.Where(t => Ineligible(cfg, t, classification) is null).ToList();
        if (eligible.Count == 0) return new(policy, null, null, [], "no eligible destination under policy " + policy.Id);
        var conn = FindConnection(cfg, eligible[0].ConnectionId);
        return new(policy, eligible[0], conn, eligible, null);
    }

    public static string? Ineligible(CovenantConfigDto cfg, PolicyTargetDto t, string classification)
    {
        if (t.Paused) return "route paused";
        var conn = FindConnection(cfg, t.ConnectionId);
        if (conn is null) return "unknown connection";
        if (!conn.Enabled) return "connection disabled";
        var model = conn.Models.FirstOrDefault(m => string.Equals(m.ModelId, t.ModelId, StringComparison.Ordinal));
        if (model is null) return "model not on connection";
        if (ComposerRules.BoundaryLabel(conn.Boundary) == "Cloud")
        {
            if (PolicyRoutes.IsLocalOnly(cfg.Mode)) return "cloud excluded in local-only mode";
            if (PolicyText.IsSensitive(classification)) return "sensitive data never routed to cloud";
            if (model.Price is null && cfg.Budgets.Any(b => string.Equals(b.Enforcement, "hard", StringComparison.Ordinal) || b.Enforcement is null))
                return "unpriced cloud model under a hard budget";
        }
        return null;
    }

    public static ConnectionDto? FindConnection(CovenantConfigDto cfg, string? id)
        => cfg.Connections.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal));

    public static string RouteText(ResolvedRoute r)
    {
        if (r.Refusal is { } why) return "Refused: " + why;
        var main = r.Primary!.ConnectionId + "/" + r.Primary.ModelId;
        var fb = r.Eligible.Skip(1).Select(t => t.ConnectionId + "/" + t.ModelId).ToList();
        return main + (fb.Count > 0 ? " (approved fallback: " + string.Join(", ", fb) + ")" : "") + " · policy " + r.Policy!.Id;
    }

    // ---------------------------------------------------------------- resolution

    /// <summary>Resolves one example against the applied configuration. Picks the application and profile that satisfy
    /// the example's need (preferring <paramref name="preferredApplication"/>); otherwise lists exactly what is missing.
    /// Never edits configuration and never substitutes a different route than the example claims.</summary>
    public static ExampleResolution Resolve(DemoExample ex, CovenantConfigDto? cfg, IReadOnlyList<ApplicationSummaryDto>? apps,
        IReadOnlyList<BudgetStateDto>? budgets, WebSearchStatusDto? search, string? preferredApplication = null)
    {
        var missing = new List<ExamplePrerequisite>();
        if (cfg is null || apps is null)
            return new(ex, false, null, null, null, null, null, [new("The configuration and applications are not loaded yet.", null, null)]);
        var selectable = apps.Where(a => ComposerRules.WhyNotSelectable(a) is null)
            .OrderBy(a => string.Equals(a.Id, preferredApplication, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(a => a.Id, StringComparer.Ordinal).ToList();
        if (selectable.Count == 0)
            return new(ex, false, null, null, null, null, null,
                [new("No application is enabled for console testing.", "policies#applications-list", "Applications")]);

        string baseline = ex.Need is ExampleNeed.InternalCoder or ExampleNeed.InternalResearch ? "Internal" : "Public";
        var candidates = new List<(ApplicationSummaryDto App, WorkloadProfileDto Profile, ResolvedRoute Route, string Pin, int Score)>();
        foreach (var app in selectable)
        {
            // An application whose applicable hard budget is exhausted would refuse ANY charged request (a priced route or
            // a web-search tool charge): only the budget-refusal example may pick it, so the others never fail by accident.
            bool exhausted = ExhaustedBudget(app, budgets) is not null;
            foreach (var pid in ComposerRules.GrantedProfiles(app))
            {
                var profile = cfg.Profiles.FirstOrDefault(p => p.Id == pid);
                if (profile is null || !string.Equals(profile.Baseline, baseline, StringComparison.Ordinal)) continue;
                var route = Route(cfg, baseline, pid);
                if (Fits(ex, cfg, app, profile, route, budgets, search) is not { } pin) continue;
                if (exhausted && ex.Need != ExampleNeed.BudgetRefusal && (PrimaryPriced(route) || ex.WebSources != WebSourcesMode.Off)) continue;
                candidates.Add((app, profile, route, pin, Score(ex, app, profile, preferredApplication)));
            }
        }
        if (candidates.Count > 0)
        {
            var best = candidates.OrderByDescending(c => c.Score).ThenBy(c => c.App.Id, StringComparer.Ordinal)
                .ThenBy(c => c.Profile.Id, StringComparer.Ordinal).First();
            var routeText = ex.Need == ExampleNeed.SensitiveCloudPinRefusal
                ? RouteText(Route(cfg, "Pii", best.Profile.Id)) + " — the explicit cloud pin is refused"
                : RouteText(best.Route);
            return new(ex, true, best.App.Id, best.Profile.Id, best.Pin.Length == 0 ? null : best.Pin, routeText,
                ComposerRules.PayerText(best.App), [], OutputNote(ex, cfg, best.App.Id, best.Route));
        }
        missing.AddRange(WhatIsMissing(ex, cfg, selectable, budgets, search, baseline));
        return new(ex, false, null, null, null, null, null, missing);
    }

    /// <summary>Preference among fitting candidates: a profile/application named for the example's workload, then the
    /// application currently selected. Display ranking only — the policy engine still routes.</summary>
    private static int Score(DemoExample ex, ApplicationSummaryDto app, WorkloadProfileDto profile, string? preferredApplication)
    {
        int score = 0;
        foreach (var word in ex.Prefer ?? Array.Empty<string>())
        {
            if (Contains(profile.Id, word) || Contains(profile.Name, word)) score += 4;
            if (Contains(app.Id, word) || Contains(app.DisplayName, word) || Contains(app.Team, word)) score += 2;
        }
        if (string.Equals(app.Id, preferredApplication, StringComparison.Ordinal)) score += 1;
        return score;
    }

    private static bool Contains(string? text, string word) => text is not null && text.Contains(word, StringComparison.OrdinalIgnoreCase);

    /// <summary>"Output bound 2048 requested · application cap 1024 → 1024 enforced: the answer may be cut." Null when the
    /// example sets no bound. Effective = min(request, application cap, model cap) — the Host's rule.</summary>
    public static string? OutputNote(DemoExample ex, CovenantConfigDto cfg, string appId, ResolvedRoute route)
    {
        if (ex.MaxTokens is not { } want) return null;
        int appCap = cfg.Applications.FirstOrDefault(a => a.Id == appId)?.MaxOutputTokens ?? 1024;
        int? modelCap = route.Primary is { } p ? route.PrimaryConnection?.Models.FirstOrDefault(m => m.ModelId == p.ModelId)?.MaxOutputTokens : null;
        int effective = Math.Min(want, Math.Min(appCap, modelCap ?? int.MaxValue));
        var text = "Output bound: " + want.ToString(CultureInfo.InvariantCulture) + " requested · application cap " + appCap.ToString(CultureInfo.InvariantCulture)
                   + (modelCap is { } mc ? " · model cap " + mc.ToString(CultureInfo.InvariantCulture) : "")
                   + " → " + effective.ToString(CultureInfo.InvariantCulture) + " enforced";
        return effective < want
            ? text + ". The answer may be cut short (the result will say Truncated); raise the application's max output tokens under Policies → Applications to allow more."
            : text + ".";
    }

    /// <summary>Non-null when (app, profile, route) satisfies the example: "" or the model pin to use.</summary>
    private static string? Fits(DemoExample ex, CovenantConfigDto cfg, ApplicationSummaryDto app, WorkloadProfileDto profile,
        ResolvedRoute route, IReadOnlyList<BudgetStateDto>? budgets, WebSearchStatusDto? search)
    {
        switch (ex.Need)
        {
            case ExampleNeed.PublicWebResearch:
                if (route.Primary is null || !profile.WebSearchAllowed) return null;
                return search is { Ready: true } ? "" : null;
            case ExampleNeed.PublicAnthropic:
                return route.PrimaryConnection is { Kind: "Anthropic" } ? "" : null;
            case ExampleNeed.InternalCoder:
                return route.Primary is { } c && c.ModelId == CoderModel && IsLocal(route) ? "" : null;
            case ExampleNeed.InternalResearch:
                return route.Primary is { } r && r.ModelId == ResearchModel && IsLocal(route) ? "" : null;
            case ExampleNeed.SensitiveCloudPinRefusal:
                var cloud = route.Eligible.FirstOrDefault(t => ComposerRules.BoundaryLabel(FindConnection(cfg, t.ConnectionId)?.Boundary) == "Cloud");
                return cloud is null ? null : cloud.ConnectionId + "/" + cloud.ModelId;
            case ExampleNeed.CacheRepeat:
                return route.Primary is not null && (cfg.Settings?.CacheTtlSeconds ?? 0) > 0 ? "" : null;
            case ExampleNeed.BudgetRefusal:
                // A $0-priced (local) route reserves nothing, so only a PRICED primary can show a budget refusal.
                return PrimaryPriced(route) && ExhaustedBudget(app, budgets) is not null ? "" : null;
        }
        return null;
    }

    private static bool PrimaryPriced(ResolvedRoute r)
        => r.Primary is { } p && r.PrimaryConnection?.Models.FirstOrDefault(m => m.ModelId == p.ModelId)?.Price is { } price
           && ((price.InputPer1M ?? 0m) > 0m || (price.OutputPer1M ?? 0m) > 0m);

    private static bool IsLocal(ResolvedRoute r) => ComposerRules.BoundaryLabel(r.PrimaryConnection?.Boundary) == "Local";

    /// <summary>A hard budget that applies to the application (global, its team, itself) with essentially nothing left.</summary>
    public static BudgetStateDto? ExhaustedBudget(ApplicationSummaryDto app, IReadOnlyList<BudgetStateDto>? budgets)
        => budgets?.FirstOrDefault(b => (b.Scope == "global" || b.Scope == "team:" + app.Team || b.Scope == "application:" + app.Id)
                                        && !string.Equals(b.Enforcement, "warn-only", StringComparison.Ordinal)
                                        && b.RemainingUsd <= 0.0001m);

    private static IEnumerable<ExamplePrerequisite> WhatIsMissing(DemoExample ex, CovenantConfigDto cfg, IReadOnlyList<ApplicationSummaryDto> apps,
        IReadOnlyList<BudgetStateDto>? budgets, WebSearchStatusDto? search, string baseline)
    {
        bool anyBaselineProfile = apps.Any(a => ComposerRules.GrantedProfiles(a)
            .Any(g => cfg.Profiles.Any(p => p.Id == g && string.Equals(p.Baseline, baseline, StringComparison.Ordinal))));
        if (!anyBaselineProfile)
            yield return new("No console-testable application is granted a " + baseline + "-baseline workload profile.", "policies#applications", "Grant a profile");
        switch (ex.Need)
        {
            case ExampleNeed.PublicWebResearch:
                if (search is not { Ready: true })
                    yield return new("Web search is not ready on the server" + (search is null ? " (status not loaded)." : ": " + (search.Setup.FirstOrDefault() ?? "see Settings → Web search.")),
                        "settings#ws-h", "Web search settings");
                if (!cfg.Profiles.Any(p => p.WebSearchAllowed))
                    yield return new("No Public profile is allowed to use web search.", "policies#applications", "Allow web search on a profile");
                break;
            case ExampleNeed.PublicAnthropic:
                if (!cfg.Connections.Any(c => c.Kind == "Anthropic" && c.Enabled))
                    yield return new("No enabled Anthropic connection. Add one (server key Anthropic:ApiKey, model and its price) under Connections.", "connections", "Connections");
                else
                    yield return new("No Public workload profile's policy routes to the Anthropic model first. Add a profile-scoped Public policy with the Anthropic target (e.g. a 'public-writing' profile).", "policies", "Policies");
                break;
            case ExampleNeed.InternalCoder:
            case ExampleNeed.InternalResearch:
                var model = ex.Need == ExampleNeed.InternalCoder ? CoderModel : ResearchModel;
                if (!cfg.Connections.Any(c => c.Enabled && c.Models.Any(m => m.ModelId == model)))
                    yield return new("Local model " + model + " is not registered on an enabled local connection. Add it from the connection's model catalog under Connections.", "connections", "Connections");
                yield return new("No Internal workload profile's policy routes to " + model + " first. Add a profile-scoped Internal policy (e.g. profile '" + (ex.Need == ExampleNeed.InternalCoder ? "internal-coding" : "internal-research") + "' → " + model + ").", "policies", "Policies");
                break;
            case ExampleNeed.SensitiveCloudPinRefusal:
                yield return new("No Public profile has an eligible cloud target to pin. This example needs a Public cloud route (Hybrid mode).", "policies", "Policies");
                break;
            case ExampleNeed.CacheRepeat:
                if ((cfg.Settings?.CacheTtlSeconds ?? 0) <= 0)
                    yield return new("The exact cache is disabled (TTL 0), so a repeat would NOT be a cache hit. Set a cache TTL under Settings → Operational.", "settings#op-h", "Cache settings");
                break;
            case ExampleNeed.BudgetRefusal:
                yield return new("No hard budget that applies to a console-testable application on a PRICED Public route is exhausted" + (budgets is null ? " (budgets not loaded)" : "")
                                 + ". To show a refusal, add an application-scoped budget with a tiny cap (e.g. $0.0001) under Budgets for an application whose Public route is a priced model — a $0 local route reserves nothing.", "budgets", "Budgets");
                break;
        }
    }

    // ---------------------------------------------------------------- apply

    /// <summary>Fills the composer from a READY resolution: application, profile, prompt, web sources, labels, routing
    /// mode (a cloud pin only for the refusal example), output bound. Never runs, never changes configuration.</summary>
    public static bool ApplyTo(ExampleResolution r, PlaygroundForm form)
    {
        if (!r.Ready || r.ApplicationId is null) return false;
        var ex = r.Example;
        form.Scenario = null;
        form.ExampleId = ex.Id;
        form.ApplicationId = r.ApplicationId;
        form.ProfileId = r.ProfileId;
        form.Prompt = ex.Prompt;
        form.WebSources = ex.WebSources;
        form.Location = "";
        form.Workflow = ex.Workflow;
        form.UseCase = ex.UseCase;
        form.FreshNonce = ex.FreshNonce;
        if (ex.MaxTokens is { } mt) form.MaxTokens = mt;
        if (r.ModelPin is { } pin)
        {
            form.Mode = RouteMode.Catalog;
            form.CatalogModel = pin;
        }
        else
        {
            form.Mode = RouteMode.Policy;
            form.CatalogModel = null;
        }
        return true;
    }

    /// <summary>True while the form still holds exactly what the example filled in (labels the run with the example).</summary>
    public static bool StillMatches(DemoExample ex, PlaygroundForm form)
        => string.Equals(form.ExampleId, ex.Id, StringComparison.Ordinal)
           && string.Equals(form.Prompt, ex.Prompt, StringComparison.Ordinal)
           && form.WebSources == ex.WebSources
           && form.FreshNonce == ex.FreshNonce;

    public static string Money(decimal v) => v.ToString("0.####", CultureInfo.InvariantCulture);
}

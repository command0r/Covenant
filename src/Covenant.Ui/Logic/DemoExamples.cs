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
    /// <summary>A Public workload whose PII route is local: the same synthetic sensitive prompt, policy chooses (served locally).</summary>
    SensitiveLocal,
    /// <summary>The exact cache enabled (TTL &gt; 0) and a Public route.</summary>
    CacheRepeat,
    /// <summary>A hard budget applying to the application that cannot cover one more request.</summary>
    BudgetRefusal,
    /// <summary>A Public profile allowed to use web search whose policy primary is LOCAL (retrieval is external, inference local).</summary>
    PublicLocalResearch,
    /// <summary>A Public profile whose policy primary is local and that also approves an external target (explicit escalation).</summary>
    PublicCodingLocalFirst,
    /// <summary>A PII-baseline profile routed by an ADR-0016 sanitized-payload policy with an external target.</summary>
    SanitizedRecord,
    /// <summary>A declared Internal WRITING workload whose policy primary is local.</summary>
    InternalWriting,
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
    string? OutputNote = null,
    /// <summary>The example fits the workloads granted to the application it was resolved for (strict mode). The composer
    /// lists only relevant examples for the selected application; an irrelevant one is never offered.</summary>
    bool Relevant = true,
    /// <summary>Title derived from the CONFIGURED route (e.g. "Compare local coding with Claude Opus 5.5"); null = the example's own.</summary>
    string? DisplayTitle = null,
    /// <summary>What will happen, step by step, before Run (e.g. local first, then an explicit, paid comparison).</summary>
    string? Steps = null)
{
    public string Title => DisplayTitle ?? Example.Title;
}

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

    /// <summary>Synthetic structured record (covenant.record.v1): SSN area 000 is never assigned, example.test is reserved,
    /// 555-01xx numbers are fictional. Mirrors the Host's StructuredRecordSchema.SyntheticExample.</summary>
    public const string SyntheticRecord =
        "{\"schema\":\"covenant.record.v1\",\"task\":\"triage_case\",\"fields\":{\"email\":\"jordan.sample@example.test\","
        + "\"phone\":\"555-010-0199\",\"us_ssn\":\"" + SyntheticSsn + "\",\"service\":\"subscription\",\"issue\":\"billing_question\","
        + "\"channel\":\"portal\",\"days_open\":2,\"amount_usd\":129.99}}";

    /// <summary>Targeted official-source research question (both research examples): one page, one fact. The answer must come
    /// from the retrieved sources only; retrieval is not verification, so the operator checks it against the cited page.</summary>
    public const string ResearchPrompt =
        "site:dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core .NET 10 support end date. "
        + "Answer in one sentence using only the retrieved sources and cite the page; if they do not state it, reply \"not stated in the sources\".";

    /// <summary>One synthetic sensitive prompt for BOTH sensitive examples (local by policy, blocked when pinned external).</summary>
    public const string SensitivePrompt =
        "Summarize this public support ticket in one sentence: \"Printer on floor 3 jammed again. Reporter SSN " + SyntheticSsn + " (synthetic test marker).\"";

    public static readonly IReadOnlyList<DemoExample> All =
    [
        new("public-research", "Public research with web sources",
            "Fresh public facts come from one governed Tavily search, charged as a separate tool call.",
            "Search runs once (Required), bounded to the official .NET support-policy page; the Public route answers only from what was retrieved.",
            "Web sources lists what was retrieved — retrieved, not independently verified; check the answer against the cited page. The search charge is shown separately from inference.",
            ExampleNeed.PublicWebResearch,
            ResearchPrompt,
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
            // Code only with one-line XML docs, and an explicit 2048 bound for THIS example only (the composer default stays
            // 1024): the same prompt then completes on the local coder and, after switching to Public, on a cloud model that
            // writes longer docs. Where the application or model caps lower, OutputNote says so before the run.
            "Write a C# extension method Chunk<T>(this IEnumerable<T> source, int size) that yields arrays of at most size items, with argument validation. Use one-line XML doc comments. Reply with the code only, no explanation.",
            WebSourcesMode.Off, "coding", "internal-code-assist", false, MaxTokens: 2048, Prefer: ["coding", "code"]),
        new("internal-research", "Internal research on the stronger local model",
            "A different workload profile selects a stronger local model — routing by declared workload, not by guessing from the prompt.",
            "Routed by the research profile's policy to " + ResearchModel + " (local).",
            "Route shows the stronger local model; the provider stage duration is the real measured time. A reasoning model spends part of the output bound thinking — if it is cut, the result says Truncated.",
            ExampleNeed.InternalResearch,
            "For an internal ledger service, compare optimistic and pessimistic concurrency control: when to use each, and two failure modes of each. Keep it under 200 words.",
            WebSourcesMode.Off, "research", "internal-analysis", false, MaxTokens: 2048, Prefer: ["research"]),
        new("sensitive-local", "Sensitive data detected: served locally",
            "Detection raises a Public request containing a synthetic SSN to PII; with policy choosing, the PII route keeps it on a local model.",
            "Served locally · sensitive data detected. Nothing leaves the perimeter; the classification is never lowered.",
            "The chips show Sensitive · PII (detected) and a Local destination. Compare with the next example: the SAME prompt with an explicit external pin is blocked.",
            ExampleNeed.SensitiveLocal,
            SensitivePrompt,
            WebSourcesMode.Off, "writing", "sensitive-local", false, Prefer: ["writing"]),
        new("sensitive-refusal", "Sensitive data: explicit external attempt is blocked",
            "The same synthetic sensitive prompt, but the example explicitly pins an external model (shown before you apply it). Policy blocks it before any provider call.",
            "Blocked by policy · sensitive data cannot go external. Nothing is sent; no charge. This is a governance refusal, not a provider outage.",
            "Outcome Blocked at Policy with the reason; evidence still names the application, team and workload. Switching workload resets the pin to \"Let policy choose\".",
            ExampleNeed.SensitiveCloudPinRefusal,
            SensitivePrompt,
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
        new("public-local-research", "Public research: local model + governed web search",
            "Retrieval and inference are separate decisions: one governed Tavily search (external, charged separately) feeds a LOCAL model.",
            "Search runs once (Required); the Public workload's policy answers on the local model from the retrieved sources only.",
            "The run shows an External retrieval chip (Tavily) next to a Local destination, and the search charge separately from inference. Sources are retrieved, not verified.",
            ExampleNeed.PublicLocalResearch,
            ResearchPrompt,
            WebSourcesMode.Required, "research", "public-local-research", false, MaxTokens: 1024, Prefer: ["research"]),
        new("public-coding-escalation", "Compare local coding with an approved external model",
            "Synthetic public code runs on the local coder first. Escalating to the stronger external model is an explicit operator action on the same approved policy — never an automatic quality judgement.",
            "First run: local coder. Then \"Run again on …\" re-runs the same prompt on the approved external target (pinned, recorded separately).",
            "Compare the two runs' destination, duration and charge. Covenant measures those; whether an answer is better is the operator's judgement.",
            ExampleNeed.PublicCodingLocalFirst,
            "Write a Python function slugify(title: str) -> str that lowercases the text, replaces each run of non-alphanumeric characters with one hyphen and trims leading/trailing hyphens. Include three doctest examples. Reply with the code only.",
            WebSourcesMode.Off, "coding", "public-code-compare", false, MaxTokens: 2048, Prefer: ["coding", "code"]),
        new("sanitized-record", "Sensitive record: sanitized external call (opt-in)",
            "A structured record with synthetic identifiers is PII. Only a dedicated, versioned policy lets Covenant build a sanitized payload (placeholders, fixed task text) for an external model; prose and PHI are refused.",
            "Preview shows exactly what would leave — placeholders only; confirm it; the external model receives only that payload. The classification stays PII.",
            "The run shows Sensitive · PII, a Sanitized payload chip and the External destination; the evidence records counts by type, never the values. Edit the record (add a free-text field) to see the refusal.",
            ExampleNeed.SanitizedRecord,
            SyntheticRecord,
            WebSourcesMode.Off, "support", "sanitized-triage", false, Prefer: ["record", "sanit", "support"]),
        new("internal-writing", "Internal writing on a local model",
            "An internal note is Internal work: the declared writing workload routes it to a local model; nothing leaves the perimeter.",
            "Routed by the Internal writing workload's policy to a local model ($0 provider charge configured, infrastructure excluded).",
            "The run shows Internal (workload label) and a Local destination. Switch the same application to its Public writing workload to see a different, approved route.",
            ExampleNeed.InternalWriting,
            "Draft a short internal status note for the team: the quarterly access review is complete, two unused service accounts were disabled, and the next review is in March. Three plain-language bullet points.",
            WebSourcesMode.Off, "writing", "internal-status-note", false, Prefer: ["writing"]),
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
        bool sanitized = PolicyText.IsSanitized(policy);   // ADR-0016: external only for a Covenant-built sanitized payload
        var eligible = policy.Targets.Where(t => Ineligible(cfg, t, classification, sanitized) is null).ToList();
        if (eligible.Count == 0) return new(policy, null, null, [], "no eligible destination under policy " + policy.Id);
        var conn = FindConnection(cfg, eligible[0].ConnectionId);
        return new(policy, eligible[0], conn, eligible, null);
    }

    public static string? Ineligible(CovenantConfigDto cfg, PolicyTargetDto t, string classification, bool sanitizedPolicy = false)
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
            if (PolicyText.IsSensitive(classification) && !(sanitizedPolicy && string.Equals(classification, "Pii", StringComparison.OrdinalIgnoreCase)))
                return "sensitive data never routed to cloud";
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
    /// <summary>Resolves the example for EXACTLY the given calling application (the composer's selection): it never switches
    /// the application or its owner. Irrelevant examples (the application has no granted workload of the right kind) come
    /// back with <see cref="ExampleResolution.Relevant"/> false; relevant but unready ones list what is missing.</summary>
    public static ExampleResolution ResolveFor(DemoExample ex, string applicationId, CovenantConfigDto? cfg, IReadOnlyList<ApplicationSummaryDto>? apps,
        IReadOnlyList<BudgetStateDto>? budgets, WebSearchStatusDto? search)
        => Resolve(ex, cfg, apps, budgets, search, applicationId, strict: true);

    /// <summary>Is the example about something this application actually does — judged from its GRANTED workloads and
    /// their CONFIGURED routes, not just a matching baseline? Runtime readiness (search credentials, cache TTL, budget
    /// left) is NOT part of this: a relevant example that cannot run right now is still listed, with what is missing.
    /// The isolated budget-refusal example is relevant only to an application with its own tiny or exhausted hard budget.</summary>
    public static bool IsRelevant(DemoExample ex, CovenantConfigDto cfg, ApplicationSummaryDto app, IReadOnlyList<BudgetStateDto>? budgets = null)
    {
        string baseline = BaselineFor(ex);
        var workloads = ComposerRules.GrantedProfiles(app)
            .Select(g => cfg.Profiles.FirstOrDefault(p => p.Id == g))
            .Where(p => p is not null && string.Equals(p.Baseline, baseline, StringComparison.Ordinal))
            .Select(p => (Profile: p!, Route: Route(cfg, baseline, p!.Id)))
            .ToList();
        return ex.Need switch
        {
            ExampleNeed.PublicWebResearch => workloads.Any(w => w.Profile.WebSearchAllowed),
            ExampleNeed.PublicAnthropic => workloads.Any(w => IsWritingWorkload(w.Profile) && w.Route.PrimaryConnection is { Kind: "Anthropic" }),
            // Task-specific examples need a workload DECLARED for that task (id or name), not just a matching model: an
            // internal-writing workload that happens to use the coder model is not offered the coding example.
            ExampleNeed.InternalCoder => workloads.Any(w => Declares(w.Profile, Coding) && w.Route.Primary?.ModelId == CoderModel),
            ExampleNeed.InternalResearch => workloads.Any(w => Declares(w.Profile, Research) && w.Route.Primary?.ModelId == ResearchModel),
            ExampleNeed.InternalWriting => workloads.Any(w => Declares(w.Profile, Writing) && IsLocal(w.Route)),
            ExampleNeed.SensitiveCloudPinRefusal => workloads.Any(w => w.Route.Eligible.Any(t => ComposerRules.BoundaryLabel(FindConnection(cfg, t.ConnectionId)?.Boundary) == "Cloud")),
            ExampleNeed.SensitiveLocal => workloads.Any(w => IsLocal(Route(cfg, "Pii", w.Profile.Id))),
            ExampleNeed.CacheRepeat => workloads.Any(w => w.Route.Primary is not null),
            ExampleNeed.BudgetRefusal => workloads.Any(w => w.Route.Primary is not null) && HasIsolatedBudget(cfg, app, budgets),
            ExampleNeed.PublicLocalResearch => workloads.Any(w => w.Profile.WebSearchAllowed && IsLocal(w.Route)),
            ExampleNeed.PublicCodingLocalFirst => workloads.Any(w => Declares(w.Profile, Coding) && IsLocal(w.Route) && HasCloudAlternative(cfg, w.Route)),
            ExampleNeed.SanitizedRecord => workloads.Any(w => w.Route.Policy is { } sp && PolicyText.IsSanitized(sp)),
            _ => false,
        };
    }

    private static readonly string[] Coding = ["cod"];
    private static readonly string[] Research = ["research"];
    private static readonly string[] Writing = ["writ"];

    /// <summary>The workload's id or name declares the task (display matching only — routing stays policy-driven).</summary>
    public static bool Declares(WorkloadProfileDto p, IReadOnlyList<string> words)
        => words.Any(w => p.Id.Contains(w, StringComparison.OrdinalIgnoreCase) || (p.Name ?? "").Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>A declared writing workload: a Public profile without web search whose id or name says writing.</summary>
    public static bool IsWritingWorkload(WorkloadProfileDto p)
        => !p.WebSearchAllowed && string.Equals(p.Baseline, "Public", StringComparison.Ordinal)
           && (p.Id.Contains("writ", StringComparison.OrdinalIgnoreCase) || (p.Name ?? "").Contains("writ", StringComparison.OrdinalIgnoreCase));

    /// <summary>A hard budget scoped to this application or its team (not the global one) that is tiny (≤ $0.01 cap) or
    /// already exhausted — the deliberate set-up for the budget-refusal demonstration.</summary>
    public static bool HasIsolatedBudget(CovenantConfigDto cfg, ApplicationSummaryDto app, IReadOnlyList<BudgetStateDto>? budgets)
    {
        bool Scoped(string scope) => scope == "application:" + app.Id || scope == "team:" + app.Team;
        bool Hard(string? enforcement) => !string.Equals(enforcement, "warn-only", StringComparison.Ordinal);
        return cfg.Budgets.Any(b => Scoped(b.Scope) && Hard(b.Enforcement) && b.CapUsd <= 0.01m)
               || (budgets?.Any(b => Scoped(b.Scope) && Hard(b.Enforcement) && b.RemainingUsd <= 0.0001m) ?? false);
    }

    private static string BaselineFor(DemoExample ex) => ex.Need switch
    {
        ExampleNeed.InternalCoder or ExampleNeed.InternalResearch or ExampleNeed.InternalWriting => "Internal",
        ExampleNeed.SanitizedRecord => "Pii",
        _ => "Public",
    };

    private static bool HasCloudAlternative(CovenantConfigDto cfg, ResolvedRoute r)
        => r.Eligible.Skip(1).Any(t => ComposerRules.BoundaryLabel(FindConnection(cfg, t.ConnectionId)?.Boundary) == "Cloud");

    public static ExampleResolution Resolve(DemoExample ex, CovenantConfigDto? cfg, IReadOnlyList<ApplicationSummaryDto>? apps,
        IReadOnlyList<BudgetStateDto>? budgets, WebSearchStatusDto? search, string? preferredApplication = null, bool strict = false)
    {
        var missing = new List<ExamplePrerequisite>();
        if (cfg is null || apps is null)
            return new(ex, false, null, null, null, null, null, [new("The configuration and applications are not loaded yet.", null, null)]);
        var selectable = apps.Where(a => ComposerRules.WhyNotSelectable(a) is null)
            .Where(a => !strict || string.Equals(a.Id, preferredApplication, StringComparison.Ordinal))
            .OrderBy(a => string.Equals(a.Id, preferredApplication, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(a => a.Id, StringComparer.Ordinal).ToList();
        if (selectable.Count == 0)
            return new(ex, false, null, null, null, null, null,
                [new(strict ? "The selected application cannot run console requests." : "No application is enabled for console testing.", "policies#applications-list", "Applications")],
                Relevant: !strict);
        bool relevant = !strict || IsRelevant(ex, cfg, selectable[0], budgets);

        string baseline = BaselineFor(ex);
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
                if (exhausted && ex.Need != ExampleNeed.BudgetRefusal && (PrimaryPriced(route) || ex.WebSources != WebSourcesMode.Off))
                {
                    if (strict) missing.Add(new("This application's budget " + ExhaustedBudget(app, budgets)!.Id + " is exhausted — this example would be refused at Budget.", "budgets", "Budgets"));
                    continue;
                }
                candidates.Add((app, profile, route, pin, Score(ex, app, profile, preferredApplication)));
            }
        }
        if (candidates.Count > 0)
        {
            var best = candidates.OrderByDescending(c => c.Score).ThenBy(c => c.App.Id, StringComparer.Ordinal)
                .ThenBy(c => c.Profile.Id, StringComparer.Ordinal).First();
            var routeText = ex.Need == ExampleNeed.SensitiveCloudPinRefusal
                ? RouteText(Route(cfg, "Pii", best.Profile.Id)) + " — the explicit cloud pin is refused"
                : ex.Need == ExampleNeed.SensitiveLocal
                    ? RouteText(Route(cfg, "Pii", best.Profile.Id)) + " (after detection raises it to PII)"
                : RouteText(best.Route);
            var (title, steps) = Describe(ex, cfg, best.Route);
            return new(ex, true, best.App.Id, best.Profile.Id, best.Pin.Length == 0 ? null : best.Pin, routeText,
                ComposerRules.PayerText(best.App), [], OutputNote(ex, cfg, best.App.Id, best.Route), DisplayTitle: title, Steps: steps);
        }
        if (missing.Count == 0) missing.AddRange(WhatIsMissing(ex, cfg, selectable, budgets, search, baseline));
        return new(ex, false, null, null, null, null, null, missing, Relevant: relevant);
    }

    /// <summary>Route-derived title and steps (labels from the APPLIED configuration; nothing is claimed available that is not
    /// an eligible target of the resolved route). Only the local-first comparison example has them today.</summary>
    private static (string? Title, string? Steps) Describe(DemoExample ex, CovenantConfigDto cfg, ResolvedRoute route)
    {
        if (ex.Need != ExampleNeed.PublicCodingLocalFirst || route.Primary is not { } local) return (null, null);
        var external = route.Eligible.Skip(1).FirstOrDefault(t => ComposerRules.BoundaryLabel(FindConnection(cfg, t.ConnectionId)?.Boundary) == "Cloud");
        if (external is null) return (null, null);
        var localLabel = ModelLabels.Friendly(cfg, local.ConnectionId, local.ModelId);
        var extLabel = ModelLabels.Friendly(cfg, external.ConnectionId, external.ModelId);
        return ("Compare local coding with " + extLabel,
            "Step 1 — Run: the request goes to the local " + localLabel + ". Step 2 — only if you choose, after the result: "
            + "\"Compare with " + extLabel + "\" sends the same request to that approved external model as a second, paid request, "
            + "recorded separately. Covenant shows where each ran, how long and what it cost; which answer is better is your judgement.");
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
                // The example claims WRITING routed by a writing workload: a research or demo profile that happens to use
                // the same cloud model does not qualify.
                return IsWritingWorkload(profile) && route.PrimaryConnection is { Kind: "Anthropic" } ? "" : null;
            case ExampleNeed.InternalCoder:
                return Declares(profile, Coding) && route.Primary is { } c && c.ModelId == CoderModel && IsLocal(route) ? "" : null;
            case ExampleNeed.InternalResearch:
                return Declares(profile, Research) && route.Primary is { } r && r.ModelId == ResearchModel && IsLocal(route) ? "" : null;
            case ExampleNeed.InternalWriting:
                return Declares(profile, Writing) && IsLocal(route) ? "" : null;
            case ExampleNeed.SensitiveCloudPinRefusal:
                var cloud = route.Eligible.FirstOrDefault(t => ComposerRules.BoundaryLabel(FindConnection(cfg, t.ConnectionId)?.Boundary) == "Cloud");
                return cloud is null ? null : cloud.ConnectionId + "/" + cloud.ModelId;
            case ExampleNeed.SensitiveLocal:
                return IsLocal(Route(cfg, "Pii", profile.Id)) ? "" : null;
            case ExampleNeed.CacheRepeat:
                return route.Primary is not null && (cfg.Settings?.CacheTtlSeconds ?? 0) > 0 ? "" : null;
            case ExampleNeed.BudgetRefusal:
                // A $0-priced (local) route reserves nothing, so only a PRICED primary can show a budget refusal.
                return PrimaryPriced(route) && ExhaustedBudget(app, budgets) is not null ? "" : null;
            case ExampleNeed.PublicLocalResearch:
                return profile.WebSearchAllowed && IsLocal(route) && search is { Ready: true } ? "" : null;
            case ExampleNeed.PublicCodingLocalFirst:
                return Declares(profile, Coding) && IsLocal(route) && HasCloudAlternative(cfg, route) ? "" : null;
            case ExampleNeed.SanitizedRecord:
                return route.Policy is { } sp && PolicyText.IsSanitized(sp) && route.Eligible.Any(t => ComposerRules.BoundaryLabel(FindConnection(cfg, t.ConnectionId)?.Boundary) == "Cloud") ? "" : null;
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
            case ExampleNeed.SensitiveLocal:
                yield return new("No local route covers PII for this application's Public workloads. Add a PII route to a local model (Policies → Routes); without one, sensitive requests are refused.", "policies", "Policies");
                break;
            case ExampleNeed.CacheRepeat:
                if ((cfg.Settings?.CacheTtlSeconds ?? 0) <= 0)
                    yield return new("The exact cache is disabled (TTL 0), so a repeat would NOT be a cache hit. Set a cache TTL under Settings → Operational.", "settings#op-h", "Cache settings");
                break;
            case ExampleNeed.PublicLocalResearch:
                if (search is not { Ready: true })
                    yield return new("Web search is not ready on the server" + (search is null ? " (status not loaded)." : ": " + (search.Setup.FirstOrDefault() ?? "see Settings → Web search.")),
                        "settings#ws-h", "Web search settings");
                yield return new("No Public workload with web search allowed routes to a LOCAL model first. Add one (e.g. 'public-local-research' → a local model) and allow web search on it; search is granted per workload, never to every team.", "policies", "Policies");
                break;
            case ExampleNeed.PublicCodingLocalFirst:
                yield return new("No Public coding workload routes to a local model first with an approved external alternative. Add a route: primary = local coder, fallback = the stronger external model (Policies → Routes).", "policies", "Policies");
                break;
            case ExampleNeed.InternalWriting:
                yield return new("No Internal writing workload routes to a local model. Add an Internal workload named for writing (e.g. 'internal-writing') with a local route, and grant it.", "policies", "Policies");
                break;
            case ExampleNeed.SanitizedRecord:
                yield return new("No sanitized external route exists. Add a PII-baseline workload (e.g. 'support-records'), grant it to the application, then add a PII-only route for it with an external model and turn on \"Sanitized external route\" (Policies → Routes).", "policies", "Policies");
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
        form.ExampleApplicationId = r.ApplicationId;
        form.ExampleProfileId = r.ProfileId;
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
        form.PinnedByExample = r.ModelPin is not null;
        form.ExampleModelPin = r.ModelPin;
        return true;
    }

    /// <summary>A workload or application switch: a pin the EXAMPLE set (the explicit external attempt) is cleared back to
    /// "Let policy choose" — it described the old selection. A pin the operator chose is kept (the composer warns). Returns
    /// true when something was reset.</summary>
    public static bool ResetExamplePin(PlaygroundForm form)
    {
        if (!form.PinnedByExample) return false;
        form.Mode = RouteMode.Policy;
        form.CatalogModel = null;
        form.PinnedByExample = false;
        return true;
    }

    /// <summary>What "Use example" will change in the CURRENT form, shown before it is pressed. Empty = nothing visible changes
    /// besides the prompt.</summary>
    public static IReadOnlyList<string> ChangesFor(ExampleResolution r, PlaygroundForm form)
    {
        var list = new List<string>();
        if (!r.Ready) return list;
        if (!string.Equals(r.ApplicationId, form.ApplicationId, StringComparison.Ordinal)) list.Add("Sender: " + (r.ApplicationId ?? "—"));
        if (!string.Equals(r.ProfileId, form.ProfileId, StringComparison.Ordinal)) list.Add("Workload: " + (r.ProfileId ?? "application default"));
        var requested = form.RequestedModel;
        if (!string.Equals(requested, r.ModelPin, StringComparison.Ordinal))
            list.Add(r.ModelPin is { } pin ? "Model: explicitly pinned to " + pin + " (an external attempt)" : "Model: let policy choose");
        if (form.WebSources != r.Example.WebSources) list.Add("Web sources: " + WebSourcesText.Label(r.Example.WebSources));
        if (r.Example.MaxTokens is { } mt && form.MaxTokens != mt) list.Add("Max output tokens: " + mt.ToString(CultureInfo.InvariantCulture));
        if (!string.Equals(form.Prompt, r.Example.Prompt, StringComparison.Ordinal)) list.Add(form.Prompt.Length == 0 ? "Prompt: filled" : "Prompt: replaced");
        return list;
    }

    /// <summary>True while the form still holds exactly what the example filled in (labels the run with the example).</summary>
    public static bool StillMatches(DemoExample ex, PlaygroundForm form)
        => string.Equals(form.ExampleId, ex.Id, StringComparison.Ordinal)
           && string.Equals(form.ExampleApplicationId, form.ApplicationId, StringComparison.Ordinal)
           && string.Equals(form.ExampleProfileId, form.ProfileId, StringComparison.Ordinal)
           && string.Equals(form.Prompt, ex.Prompt, StringComparison.Ordinal)
           && form.WebSources == ex.WebSources
           && form.FreshNonce == ex.FreshNonce
           && string.Equals(form.RequestedModel, form.ExampleModelPin, StringComparison.Ordinal)   // routing not edited
           && (ex.MaxTokens is null || form.MaxTokens == ex.MaxTokens);

    public static string Money(decimal v) => v.ToString("0.####", CultureInfo.InvariantCulture);
}

using System.Globalization;
using Covenant.Core;
using Covenant.Governance;

namespace Covenant.Host;

/// <summary>Startup-owned facts (ADR-0011): the egress allow-list and secret availability. Neither is
/// console-editable — a configuration edit can never widen where Covenant connects or invent a secret.</summary>
public static class StartupEgress
{
    /// <summary>Providers:ProbeAllowList ∪ the endpoints the startup keys name (Local/OpenAI/Anthropic, or the
    /// provider default authority when a cloud key is configured without an endpoint). Computed from startup
    /// configuration on every boot, so it is identical for the legacy import, a seed and later versions.</summary>
    public static HashSet<string> BuildAllowList(IConfiguration config)
    {
        var set = ProviderProbe.BuildAllowList(config["Providers:ProbeAllowList"], StartupValidation.Trimmed(config["Local:Endpoint"]));
        void AddEndpoint(string? endpoint)
        {
            if (StartupValidation.Trimmed(endpoint) is { } e && ConfigValidator.EndpointAuthority(e) is { } a) set.Add(a);
        }
        AddEndpoint(config["OpenAI:Endpoint"]);
        AddEndpoint(config["Anthropic:Endpoint"]);
        if (!string.IsNullOrWhiteSpace(config["OpenAI:ApiKey"]) && string.IsNullOrWhiteSpace(config["OpenAI:Endpoint"]))
            set.Add(ConfigValidator.DefaultAuthority(ProviderKind.OpenAi)!);
        if (!string.IsNullOrWhiteSpace(config["Anthropic:ApiKey"]) && string.IsNullOrWhiteSpace(config["Anthropic:Endpoint"]))
            set.Add(ConfigValidator.DefaultAuthority(ProviderKind.Anthropic)!);
        return set;
    }

    /// <summary>Secret reference → configured (non-empty) on the server. Only the permitted reference forms resolve.</summary>
    public static Func<string, bool> SecretAvailability(IConfiguration config)
        => secretRef => ConfigValidator.IsAllowedSecretRef(secretRef) && !string.IsNullOrWhiteSpace(config[secretRef]);

    public static string ModeString(ProviderMode mode) => mode switch
    {
        ProviderMode.LocalOnly => "local-only",
        ProviderMode.Hybrid => "hybrid",
        _ => "legacy-cloud",
    };
}

/// <summary>Legacy startup keys → configuration v1 (origin legacy-import), reproducing the pre-v2 effective
/// routing exactly (docs/migration-v2.md). Connections are named "openai", "anthropic" and "local" so the
/// pre-v2 provider keys ("openai:gpt-4o-mini", "local:&lt;tag&gt;", …) keep resolving.</summary>
public static class LegacyImport
{
    public const string UnconfiguredLocalModel = "local-model-not-configured";

    public static CovenantConfig Build(IConfiguration config, DateTimeOffset now, List<string> errors)
    {
        bool localOnly = StartupValidation.ProviderMode(config) == StartupValidation.LocalOnlyMode;
        string? localEndpoint = StartupValidation.Trimmed(config["Local:Endpoint"]);
        string? localModelRaw = StartupValidation.Trimmed(config["Local:ModelId"]);
        string localModelId = localModelRaw is { Length: > 0 } ? localModelRaw : UnconfiguredLocalModel;
        bool localConfigured = localEndpoint is not null && localModelRaw is { Length: > 0 };

        string publicModelId = config["OpenAI:ModelId"] ?? "gpt-4o-mini";
        string strongModelId = config["OpenAI:StrongModelId"] ?? "gpt-4o";
        string anthropicModelId = config["Anthropic:ModelId"] ?? "claude-haiku-4-5";
        bool anthropicEnabled = !localOnly && !string.IsNullOrEmpty(config["Anthropic:ApiKey"]);

        // Same per-1M prices as the pre-v2 priceMap (which stored them per 1K), then Pricing:<model> overrides.
        var prices = new Dictionary<string, PriceConfig>(StringComparer.Ordinal)
        {
            [publicModelId] = new() { InputPer1M = 0.15m, OutputPer1M = 0.60m, Basis = "provider-list (legacy default)" },
            [strongModelId] = new() { InputPer1M = 2.50m, OutputPer1M = 10.00m, Basis = "provider-list (legacy default)" },
            [localModelId] = new() { InputPer1M = 0m, OutputPer1M = 0m, Basis = "zero-marginal-local" },
        };
        if (anthropicEnabled) prices[anthropicModelId] = new() { InputPer1M = 1m, OutputPer1M = 5m, Basis = "provider-list (legacy default)" };
        foreach (var child in config.GetSection("Pricing").GetChildren())
        {
            if (decimal.TryParse(child["InPer1M"], NumberStyles.Number, CultureInfo.InvariantCulture, out var inPer1M)
                && decimal.TryParse(child["OutPer1M"], NumberStyles.Number, CultureInfo.InvariantCulture, out var outPer1M))
                prices[child.Key] = new() { InputPer1M = inPer1M, OutputPer1M = outPer1M, Basis = "configured (Pricing:" + child.Key + ")" };
            else
                errors.Add($"Pricing:{child.Key} — requires numeric InPer1M and OutPer1M");
        }
        ModelConfig Model(string id) => new() { ModelId = id, Price = prices.GetValueOrDefault(id) };

        var connections = new List<ConnectionConfig>();
        if (!localOnly)
        {
            var models = new List<ModelConfig> { Model(publicModelId) };
            if (strongModelId != publicModelId) models.Add(Model(strongModelId));
            connections.Add(new ConnectionConfig
            {
                Id = "openai", DisplayName = "OpenAI (imported)", Kind = ProviderKind.OpenAi, Boundary = Boundary.Cloud,
                Endpoint = StartupValidation.Trimmed(config["OpenAI:Endpoint"]), Enabled = true, SecretRef = "OpenAI:ApiKey",
                Models = models,
            });
            if (anthropicEnabled)
                connections.Add(new ConnectionConfig
                {
                    Id = "anthropic", DisplayName = "Anthropic (imported)", Kind = ProviderKind.Anthropic, Boundary = Boundary.Cloud,
                    Endpoint = StartupValidation.Trimmed(config["Anthropic:Endpoint"]), Enabled = true, SecretRef = "Anthropic:ApiKey",
                    Models = [Model(anthropicModelId)],
                });
        }
        if (localConfigured)
            connections.Add(new ConnectionConfig
            {
                Id = "local", DisplayName = "Local (imported)", Kind = ProviderKind.OpenAiCompatible, Boundary = Boundary.Local,
                Endpoint = localEndpoint, Enabled = true,
                SecretRef = string.IsNullOrWhiteSpace(config["Local:ApiKey"]) ? null : "Local:ApiKey",
                Models = [Model(localModelId)],
            });

        // Pre-v2 routes: Public/Internal ordered cheapest → (Anthropic mid-list) → strongest with the length
        // heuristic; PII/PHI → local (the unconfigured placeholder fails closed at the provider stage, as before).
        // Local-only: every classification → the one approved local model. Single attempt (no pre-v2 fallback).
        var single = new RetrySettings { MaxAttempts = 1 };
        var localTarget = new PolicyTarget { ConnectionId = "local", ModelId = localModelId };
        var policies = new List<PolicyConfigV2>();
        if (localOnly)
        {
            policies.Add(new PolicyConfigV2
            {
                Id = "legacy-local-only", Name = "Local-only (imported)", Enabled = true,
                Classifications = [DataClassification.Public, DataClassification.Internal, DataClassification.Pii, DataClassification.Phi],
                Targets = [localTarget], Strategy = RoutingStrategy.Fixed, Retry = single,
                Explanation = "Imported from Providers:Mode=local-only: every classification is served by the approved local model.",
            });
        }
        else
        {
            var general = new List<PolicyTarget> { new() { ConnectionId = "openai", ModelId = publicModelId } };
            if (anthropicEnabled) general.Add(new() { ConnectionId = "anthropic", ModelId = anthropicModelId });
            general.Add(new() { ConnectionId = "openai", ModelId = strongModelId });
            policies.Add(new PolicyConfigV2
            {
                Id = "legacy-general", Name = "Public & Internal (imported)", Enabled = true,
                Classifications = [DataClassification.Public, DataClassification.Internal],
                Targets = general, Strategy = RoutingStrategy.LengthHeuristic,
                ThresholdTokens = OptionalNonNegativeInt(config, "Routing:ComplexityTokenThreshold", 400, errors),
                Retry = single,
                Explanation = "Imported legacy cloud routing: length-based heuristic between the economical and the strong model.",
            });
            policies.Add(new PolicyConfigV2
            {
                Id = "legacy-sensitive", Name = "PII & PHI (imported)", Enabled = true,
                Classifications = [DataClassification.Pii, DataClassification.Phi],
                Targets = [localTarget], Strategy = RoutingStrategy.Fixed, Retry = single,
                Explanation = localConfigured
                    ? "Imported: sensitive data is served only by the local model."
                    : "Imported: no local model is configured, so sensitive data is refused (fail-closed).",
            });
        }

        var profiles = new List<WorkloadProfileConfig>
        {
            new() { Id = PolicyEngineV2.LegacyInternalProfile, Name = "Legacy internal", Baseline = DataClassification.Internal,
                    Description = "Default for pre-v2 application keys: baseline Internal." },
        };

        var applications = new List<ApplicationConfig>();
        foreach (var child in config.GetSection("Auth:Keys").GetChildren())
        {
            var appId = child["Application"] is { Length: > 0 } a ? a : child["Principal"];
            var team = child["Team"];
            // Keys whose application id is not a valid configuration id stay unbound (legacy behaviour: team from
            // the key, legacy-internal profile) — never a refusal to start over a cosmetic identifier.
            if (!ConfigValidator.IsValidId(appId) || string.IsNullOrEmpty(team) || applications.Any(x => x.Id == appId)) continue;
            applications.Add(new ApplicationConfig
            {
                Id = appId!, Team = team, Enabled = true, ConsoleTesting = false,
                GrantedProfiles = [PolicyEngineV2.LegacyInternalProfile], DefaultProfile = PolicyEngineV2.LegacyInternalProfile,
            });
        }

        var budgets = new List<BudgetConfigV2>();
        if (TryCap(config, errors) is { } globalCap)
            budgets.Add(new BudgetConfigV2 { Id = "global", Scope = "global", CapUsd = globalCap, Period = BudgetPeriod.None });
        int n = 0;
        foreach (var child in config.GetSection("Budget:TeamCapsUsd").GetChildren())
        {
            if (child.Value is { } capRaw && decimal.TryParse(capRaw, NumberStyles.Number, CultureInfo.InvariantCulture, out var cap))
                budgets.Add(new BudgetConfigV2 { Id = $"team-{n++}", Scope = "team:" + child.Key, CapUsd = cap, Period = BudgetPeriod.None });
            else
                errors.Add($"Budget:TeamCapsUsd:{child.Key} — not a number: '{child.Value}'");
        }

        // v2 bounds evidence previews to 500 chars (OperationalSettings); a larger legacy value is clamped (less
        // content retained, never more) and the clamp is recorded in the version's change summary.
        int preview = OptionalNonNegativeInt(config, "Audit:PromptPreviewChars", 0, errors);
        string summary = "Imported from legacy startup configuration";
        if (preview > 500)
        {
            summary += $" (Audit:PromptPreviewChars {preview} clamped to 500)";
            preview = 500;
        }

        return new CovenantConfig
        {
            Version = 1,
            Origin = "legacy-import",
            CreatedUtc = now,
            ChangeSummary = summary,
            Mode = localOnly ? ProviderMode.LocalOnly : ProviderMode.LegacyCloud,
            Connections = connections,
            Applications = applications,
            Profiles = profiles,
            Policies = policies,
            Budgets = budgets,
            Settings = new OperationalSettings
            {
                CacheTtlSeconds = OptionalNonNegativeInt(config, "Cache:TtlSeconds", 0, errors),
                CacheMaxEntries = Math.Max(1, OptionalNonNegativeInt(config, "Cache:MaxEntries", 1_000, errors)),
                PromptPreviewChars = preview,
                RateLimitGlobalPerMinute = OptionalNonNegativeInt(config, "RateLimit:GlobalPerMinute", 0, errors),
                RateLimitPerTeamPerMinute = OptionalNonNegativeInt(config, "RateLimit:PerTeamPerMinute", 0, errors),
            },
            // Pre-v2 savings baseline: every allowed request priced on the strong model (none in local-only).
            SavingsBaseline = localOnly ? null : new ModelRef("openai", strongModelId),
        };
    }

    internal static decimal? TryCap(IConfiguration config, List<string> errors)
    {
        var raw = config["Budget:GlobalCapUsd"];
        if (!string.IsNullOrWhiteSpace(raw) && decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var cap))
            return cap;
        errors.Add("Budget:GlobalCapUsd — appliance-wide spend ceiling, a number like 5.00");
        return null;
    }

    /// <summary>Absent = fallback; present-but-invalid or negative = misconfiguration (refuse to start).</summary>
    internal static int OptionalNonNegativeInt(IConfiguration config, string key, int fallback, List<string> errors)
    {
        var raw = config[key];
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= 0) return v;
        errors.Add($"{key} — must be a non-negative integer, got '{raw}'");
        return fallback;
    }
}

/// <summary>Isolated demonstration profiles (spec §3, Config:Seed). Restrictive defaults: Public → approved
/// economical cloud model with an explicit local fallback (hybrid only); Internal, PII and PHI → the local
/// model, never a cloud fallback. Public is reachable only through the granted "public-demo" profile.</summary>
public static class DemoSeeds
{
    public const string LocalOnly = "demo-local-only";
    public const string Hybrid = "demo-hybrid";

    public static bool IsKnown(string? seed) => seed is LocalOnly or Hybrid;

    public static CovenantConfig Build(string seed, IConfiguration config, DateTimeOffset now, List<string> errors)
    {
        bool hybrid = seed == Hybrid;
        string? endpoint = StartupValidation.Trimmed(config["Local:Endpoint"]);
        string? localModel = StartupValidation.Trimmed(config["Local:ModelId"]);
        if (endpoint is null) errors.Add($"Local:Endpoint      — required by Config:Seed={seed} (the approved Ollama endpoint, e.g. http://localhost:11434/v1)");
        if (localModel is not { Length: > 0 }) errors.Add($"Local:ModelId       — required by Config:Seed={seed} (the exact installed model tag)");
        localModel ??= "local-model";
        string cloudModel = config["OpenAI:ModelId"] ?? "gpt-4o-mini";

        var connections = new List<ConnectionConfig>
        {
            new()
            {
                Id = "ollama", DisplayName = "Ollama (local)", Kind = ProviderKind.Ollama, Boundary = Boundary.Local,
                Endpoint = endpoint, Enabled = true,
                SecretRef = string.IsNullOrWhiteSpace(config["Local:ApiKey"]) ? null : "Local:ApiKey",
                Models = [new ModelConfig { ModelId = localModel, Price = new PriceConfig { InputPer1M = 0m, OutputPer1M = 0m, Basis = "zero-marginal-local" } }],
            },
        };
        var local = new PolicyTarget { ConnectionId = "ollama", ModelId = localModel };
        var publicTargets = new List<PolicyTarget>();
        if (hybrid)
        {
            connections.Add(new ConnectionConfig
            {
                Id = "openai", DisplayName = "OpenAI", Kind = ProviderKind.OpenAi, Boundary = Boundary.Cloud,
                Endpoint = StartupValidation.Trimmed(config["OpenAI:Endpoint"]), Enabled = true, SecretRef = "OpenAI:ApiKey",
                Models = [new ModelConfig { ModelId = cloudModel, Price = new PriceConfig { InputPer1M = 0.15m, OutputPer1M = 0.60m, Basis = "provider-list" } }],
            });
            publicTargets.Add(new PolicyTarget { ConnectionId = "openai", ModelId = cloudModel });
        }
        publicTargets.Add(local);

        var applications = new List<ApplicationConfig>
        {
            new()
            {
                Id = "demo-app", Team = "platform", Enabled = true, ConsoleTesting = true,
                GrantedProfiles = ["public-demo", "internal-default"], DefaultProfile = "internal-default",
            },
        };
        foreach (var child in config.GetSection("Auth:Keys").GetChildren())
        {
            var appId = child["Application"] is { Length: > 0 } a ? a : child["Principal"];
            var team = child["Team"];
            if (!ConfigValidator.IsValidId(appId) || string.IsNullOrEmpty(team) || applications.Any(x => x.Id == appId)) continue;
            applications.Add(new ApplicationConfig
            {
                Id = appId!, Team = team, Enabled = true, ConsoleTesting = false,
                GrantedProfiles = ["internal-default"], DefaultProfile = "internal-default",
            });
        }

        var cap = LegacyImport.TryCap(config, errors) ?? 0m;
        return new CovenantConfig
        {
            Version = 1,
            Origin = "seed:" + seed,
            CreatedUtc = now,
            ChangeSummary = $"Demo seed {seed}",
            Mode = hybrid ? ProviderMode.Hybrid : ProviderMode.LocalOnly,
            Connections = connections,
            Applications = applications,
            Profiles =
            [
                new() { Id = "public-demo", Name = "Public demo (synthetic public content)", Baseline = DataClassification.Public,
                        Description = "Deliberately public, synthetic content only." },
                new() { Id = "internal-default", Name = "Internal (default)", Baseline = DataClassification.Internal },
            ],
            Policies =
            [
                new()
                {
                    Id = "public", Name = hybrid ? "Public → economical cloud, local fallback" : "Public → local",
                    Enabled = true, Classifications = [DataClassification.Public], Targets = publicTargets,
                    Strategy = RoutingStrategy.Fixed,
                    Explanation = hybrid
                        ? "Public demo content uses the approved economical cloud model; the approved local model is the explicit fallback."
                        : "Local-only mode: Public content is served by the approved local model.",
                },
                new()
                {
                    Id = "internal", Name = "Internal → local", Enabled = true, Classifications = [DataClassification.Internal],
                    Targets = [local], Strategy = RoutingStrategy.Fixed, Explanation = "Internal data stays on the approved local model.",
                },
                new()
                {
                    Id = "sensitive", Name = "PII & PHI → local only", Enabled = true,
                    Classifications = [DataClassification.Pii, DataClassification.Phi], Targets = [local], Strategy = RoutingStrategy.Fixed,
                    Explanation = "Sensitive data is served only by the approved local model; never a cloud fallback.",
                },
            ],
            Budgets = [new BudgetConfigV2 { Id = "global", Scope = "global", CapUsd = cap, Period = BudgetPeriod.Monthly }],
            Settings = new OperationalSettings(),
            SavingsBaseline = hybrid ? new ModelRef("openai", cloudModel) : null,
        };
    }
}

/// <summary>Builds config.activated audit events (v3 EventType). They live in the same hash chain as requests
/// and are excluded from request counts, spend and FinOps.</summary>
public static class ConfigAuditEvents
{
    public const string SharedAdminOperator = "shared-admin-token";

    public static AuditEntry Activated(CovenantConfig config, string operatorRef, DateTimeOffset now, bool recovered = false)
    {
        var summary = config.ChangeSummary is { Length: > 0 } s ? ": " + Governance.Evidence.Bounded(s) : "";
        var reason = $"config.activated v{config.Version}"
            + (config.BaseVersion is { } b ? $" (base v{b})" : "")
            + (config.RolledBackFrom is { } r ? $" rollback of v{r}" : "")
            + $" origin {config.Origin}{summary}"
            + (recovered ? " [recovered at boot: no activation event was found for the active version]" : "");
        return new AuditEntry(
            Id: $"cfg_{Guid.NewGuid():n}",
            TimestampUtc: now,
            Principal: operatorRef,
            Tags: new AttributionTags("covenant", "configuration", "activation"),
            Classification: DataClassification.Internal,
            Effect: PolicyEffect.Allow,
            Reason: reason,
            ServedByModel: null,
            Usage: Usage.Empty,
            SchemaVersion: 2,
            CostBasis: "no_call",
            EventType: AuditEntryKinds.ConfigActivated,
            ConfigVersion: config.Version,
            EntryPoint: "admin",
            OperatorRef: operatorRef);
    }
}

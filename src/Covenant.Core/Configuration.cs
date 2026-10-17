using System.Text.Json.Serialization;

namespace Covenant.Core;

// Runtime configuration model (ADR-0011..0014). Persisted as immutable JSON snapshots by the Host's
// configuration repository. Contains NO secret values — only secret references. Pure data: validation,
// compilation and persistence live outside Core so the dependency direction stays Core ← Governance ← Host.

public enum ProviderMode { LocalOnly, Hybrid, LegacyCloud }

public enum ProviderKind { Ollama, OpenAiCompatible, OpenAi, Anthropic }

/// <summary>"local" connections are in-perimeter (Ollama/vLLM on an approved host); "cloud" leaves it.</summary>
public enum Boundary { Local, Cloud }

public enum RoutingStrategy { Fixed, LengthHeuristic }

public enum BudgetPeriod { None, Daily, Monthly }

public enum BudgetEnforcement { Hard, WarnOnly }

public sealed record CovenantConfig
{
    public required int Version { get; init; }
    /// <summary>Version this one was derived from (null for v1). Rollback sets RolledBackFrom.</summary>
    public int? BaseVersion { get; init; }
    public int? RolledBackFrom { get; init; }
    /// <summary>legacy-import | seed:demo-local-only | seed:demo-hybrid | console | rollback</summary>
    public required string Origin { get; init; }
    public required DateTimeOffset CreatedUtc { get; init; }
    public string? ChangeSummary { get; init; }

    public required ProviderMode Mode { get; init; }
    public required IReadOnlyList<ConnectionConfig> Connections { get; init; }
    public required IReadOnlyList<ApplicationConfig> Applications { get; init; }
    public required IReadOnlyList<WorkloadProfileConfig> Profiles { get; init; }
    public required IReadOnlyList<PolicyConfigV2> Policies { get; init; }
    public required IReadOnlyList<BudgetConfigV2> Budgets { get; init; }
    public required OperationalSettings Settings { get; init; }
    /// <summary>Optional counterfactual for "Estimated provider charge avoided"; hidden when null.</summary>
    public ModelRef? SavingsBaseline { get; init; }

    /// <summary>Governed web search (ADR-0015). Null (every pre-0015 version) = disabled.</summary>
    public WebSearchSettings? WebSearch { get; init; }
}

/// <summary>Global web-search settings (ADR-0015), versioned with the rest of the configuration. The endpoint is a fixed
/// constant (api.tavily.com) and must be on the startup egress allow-list; the key is the server-side secret
/// <c>Tavily:ApiKey</c>, never stored here. Only Hybrid mode and Public-baseline profiles with
/// <see cref="WorkloadProfileConfig.WebSearchAllowed"/> may search.</summary>
public sealed record WebSearchSettings
{
    public bool Enabled { get; init; }
    /// <summary>1–3 results per search.</summary>
    public int MaxResults { get; init; } = 3;
    /// <summary>Per-call deadline, 1–30 s. No retries.</summary>
    public int TimeoutSeconds { get; init; } = 10;
    /// <summary>USD per Tavily credit (a basic search = 1 credit). Null = unknown ⇒ search is not allowed (never $0).</summary>
    public decimal? PricePerCreditUsd { get; init; }
    /// <summary>Source of the price (e.g. "Tavily pay-as-you-go list price").</summary>
    public string? PriceBasis { get; init; }
    public DateOnly? PriceEffectiveDate { get; init; }
}

/// <summary>Model identity is connection-scoped: bare model names are not globally unique.</summary>
public sealed record ModelRef(string ConnectionId, string ModelId)
{
    public override string ToString() => $"{ConnectionId}/{ModelId}";
}

public sealed record ConnectionConfig
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required ProviderKind Kind { get; init; }
    public required Boundary Boundary { get; init; }
    /// <summary>Absolute http(s) base URL; null = provider default (cloud SDK endpoint).</summary>
    public string? Endpoint { get; init; }
    public required bool Enabled { get; init; }
    /// <summary>IConfiguration key holding the credential (Secrets:&lt;name&gt; or a legacy key); null = none needed.</summary>
    public string? SecretRef { get; init; }
    public required IReadOnlyList<ModelConfig> Models { get; init; }
    /// <summary>Provider call timeout per attempt.</summary>
    public int TimeoutSeconds { get; init; } = 120;
}

public sealed record ModelConfig
{
    /// <summary>Exact provider model id / Ollama tag.</summary>
    public required string ModelId { get; init; }
    public string? DisplayName { get; init; }
    public int? ContextTokens { get; init; }
    public int? MaxOutputTokens { get; init; }
    /// <summary>Null = unknown price (never treated as $0).</summary>
    public PriceConfig? Price { get; init; }
}

public sealed record PriceConfig
{
    // Explicit wire names: the snake_case naming policy would otherwise split the digit ("input_per1_m").
    // In-box System.Text.Json attribute only — Core still references no package.
    [JsonPropertyName("input_per_1m")] public required decimal InputPer1M { get; init; }
    [JsonPropertyName("output_per_1m")] public required decimal OutputPer1M { get; init; }
    /// <summary>"provider-list" | "contract" | "zero-marginal-local" | free text source note.</summary>
    public required string Basis { get; init; }
    public DateOnly? EffectiveDate { get; init; }
}

public sealed record ApplicationConfig
{
    public required string Id { get; init; }
    public required string Team { get; init; }
    public required bool Enabled { get; init; }
    /// <summary>May the console run requests as this application (ADR-0012).</summary>
    public required bool ConsoleTesting { get; init; }
    public required IReadOnlyList<string> GrantedProfiles { get; init; }
    public string? DefaultProfile { get; init; }
    /// <summary>Enforced output bound used for reservation and sent to the provider (ADR-0014).</summary>
    public int MaxOutputTokens { get; init; } = 1024;
}

public sealed record WorkloadProfileConfig
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>Trusted baseline: detection may escalate above it, never below.</summary>
    public required DataClassification Baseline { get; init; }
    public string? Description { get; init; }
    /// <summary>May requests under this profile use governed web search (ADR-0015)? Default false. Only a Public-baseline
    /// profile may carry it (validation error otherwise).</summary>
    public bool WebSearchAllowed { get; init; }
}

public sealed record PolicyConfigV2
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required bool Enabled { get; init; }
    public bool Paused { get; init; }
    public required IReadOnlyList<DataClassification> Classifications { get; init; }
    /// <summary>Empty = any profile; non-empty = more specific (wins precedence).</summary>
    public IReadOnlyList<string> Profiles { get; init; } = [];
    public required IReadOnlyList<PolicyTarget> Targets { get; init; }
    public RoutingStrategy Strategy { get; init; } = RoutingStrategy.Fixed;
    /// <summary>LengthHeuristic only: chars/4 estimate above this takes the LAST target.</summary>
    public int ThresholdTokens { get; init; } = 400;
    public RetrySettings Retry { get; init; } = new();
    // No sensitive-cloud escape hatch (ADR-0013): a policy covering PII/PHI that lists a cloud target is a
    // validation error, and the engine never makes a cloud target eligible for PII/PHI.
    public string? Explanation { get; init; }
}

public sealed record PolicyTarget
{
    public required string ConnectionId { get; init; }
    public required string ModelId { get; init; }
    public bool Paused { get; init; }
}

public sealed record RetrySettings
{
    public int MaxAttempts { get; init; } = 2;
    public int DeadlineMs { get; init; } = 120_000;
    /// <summary>Subset of: connect, timeout, 429, 5xx.</summary>
    public IReadOnlyList<string> On { get; init; } = ["connect", "timeout", "429", "5xx"];
}

public sealed record BudgetConfigV2
{
    public required string Id { get; init; }
    /// <summary>global | team:&lt;id&gt; | application:&lt;id&gt;</summary>
    public required string Scope { get; init; }
    public required decimal CapUsd { get; init; }
    public BudgetPeriod Period { get; init; } = BudgetPeriod.Monthly;
    public IReadOnlyList<decimal> WarnAt { get; init; } = [0.80m, 0.95m];
    public BudgetEnforcement Enforcement { get; init; } = BudgetEnforcement.Hard;
}

public sealed record OperationalSettings
{
    public int CacheTtlSeconds { get; init; }
    public int CacheMaxEntries { get; init; } = 1_000;
    /// <summary>0 = no prompt preview in evidence (default). Max 500.</summary>
    public int PromptPreviewChars { get; init; }
    public int TraceRetentionMinutes { get; init; } = 10;
    public int RateLimitGlobalPerMinute { get; init; }
    public int RateLimitPerTeamPerMinute { get; init; }
}

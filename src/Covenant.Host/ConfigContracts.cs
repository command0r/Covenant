using System.Text.Json;
using System.Text.Json.Serialization;
using Covenant.Core;
using Covenant.Governance;

namespace Covenant.Host;

// Configuration persistence + admin API payloads (ADR-0011, docs/ui-api-contract.md v2 "Configuration").
// snake_case property names. Enum spellings are a STABLE CONTRACT (docs/ui-api-contract.md v2 "Wire spellings"):
//   mode        ProviderMode       "local-only" | "hybrid" | "legacy-cloud"          (kebab-case)
//   strategy    RoutingStrategy    "fixed" | "length-heuristic"                        (kebab-case)
//   period      BudgetPeriod       "none" | "daily" | "monthly"                        (kebab-case)
//   enforcement BudgetEnforcement  "hard" | "warn-only"                                (kebab-case)
//   kind        ProviderKind       "Ollama" | "OpenAiCompatible" | "OpenAi" | "Anthropic"   (C# names)
//   boundary    Boundary           "Local" | "Cloud"                                   (C# names)
//   classifications / baseline  DataClassification  "Public" | "Internal" | "Pii" | "Phi"  (C# names)
//   price       input_per_1m / output_per_1m (explicit [JsonPropertyName] on PriceConfig)
// Always WRITTEN as those names. On READ a JSON number is accepted so that an out-of-range value (e.g. "mode": 999)
// reaches the validator and is reported as invalid_enum with its field path (200 valid=false / 422) instead of an
// opaque 400. Bodies are (de)serialized explicitly with ConfigJsonContext.Default.* — never the web defaults.

/// <summary>active.json: which immutable version file is live, and its SHA-256 (crash recovery, ADR-0011).</summary>
public sealed class ActivePointer
{
    public required int Version { get; init; }
    public required string Sha256 { get; init; }
}

public sealed class SecretStatus
{
    public required bool Available { get; init; }
}

public sealed class ConfigGetResponse
{
    public required int Version { get; init; }
    public required string Etag { get; init; }
    public required CovenantConfig Config { get; init; }
    /// <summary>Every secret reference the configuration names → available on the server? Never the value.</summary>
    public required Dictionary<string, SecretStatus> Secrets { get; init; }
    /// <summary>Startup egress allow-list ("host:port"); not console-editable.</summary>
    public required List<string> AllowList { get; init; }
}

public sealed class ConfigValidateRequest
{
    public int? BaseVersion { get; init; }
    public CovenantConfig? Config { get; init; }
}

public sealed class ConfigIssueView
{
    public required string Path { get; init; }
    public required string Code { get; init; }
    public required string Message { get; init; }

    public static List<ConfigIssueView> From(IEnumerable<ConfigIssue> issues)
        => issues.Select(i => new ConfigIssueView { Path = i.Path, Code = i.Code, Message = i.Message }).ToList();
}

public sealed class ImpactRowView
{
    public required string Classification { get; init; }
    public required string Profile { get; init; }
    public required string Before { get; init; }
    public required string After { get; init; }
    public required bool Changed { get; init; }
}

public sealed class ImpactView
{
    public required List<ImpactRowView> Classifications { get; init; }
    public required List<string> ApplicationsAffected { get; init; }
    public required List<string> UnavailableDestinations { get; init; }
    public required List<string> PricingGaps { get; init; }
    public required List<string> NewlyRefused { get; init; }

    public static ImpactView From(ConfigImpact i) => new()
    {
        Classifications = i.Classifications.Select(r => new ImpactRowView
        { Classification = r.Classification, Profile = r.Profile, Before = r.Before, After = r.After, Changed = r.Changed }).ToList(),
        ApplicationsAffected = i.ApplicationsAffected.ToList(),
        UnavailableDestinations = i.UnavailableDestinations.ToList(),
        PricingGaps = i.PricingGaps.ToList(),
        NewlyRefused = i.NewlyRefused.ToList(),
    };
}

public sealed class ConfigValidateResponse
{
    public required bool Valid { get; init; }
    public required int ActiveVersion { get; init; }
    public required List<ConfigIssueView> Errors { get; init; }
    public required List<ConfigIssueView> Warnings { get; init; }
    public ImpactView? Impact { get; init; }
}

public sealed class ConfigPutRequest
{
    public CovenantConfig? Config { get; init; }
    public string? ChangeSummary { get; init; }
}

public sealed class ConfigPutResponse
{
    public required int Version { get; init; }
    public required string Etag { get; init; }
}

public sealed class ConfigErrorResponse
{
    public required string Error { get; init; }
    public string? Reason { get; init; }
    public int? ActiveVersion { get; init; }
    public List<ConfigIssueView>? Errors { get; init; }
    public List<ConfigIssueView>? Warnings { get; init; }
}

public sealed class ConfigVersionSummary
{
    public required int Version { get; init; }
    public required DateTimeOffset CreatedUtc { get; init; }
    public required string Origin { get; init; }
    public int? BaseVersion { get; init; }
    public int? RolledBackFrom { get; init; }
    public string? ChangeSummary { get; init; }
    public required bool Active { get; init; }
}

public sealed class ConfigRollbackRequest
{
    public int TargetVersion { get; init; }
    public int ExpectedActiveVersion { get; init; }
}

public sealed class ApplicationView
{
    public required string Id { get; init; }
    public required string Team { get; init; }
    public required bool Enabled { get; init; }
    public required bool ConsoleTesting { get; init; }
    public required List<string> GrantedProfiles { get; init; }
    public string? DefaultProfile { get; init; }
}

public sealed class SimulateRequest
{
    public string? ApplicationId { get; init; }
    public string? ProfileId { get; init; }
    public string? Prompt { get; init; }
    public string? Model { get; init; }
    /// <summary>Optional candidate to simulate against instead of the active configuration (never applied).</summary>
    public CovenantConfig? Config { get; init; }
}

public sealed class SimulatedTarget
{
    public required string Target { get; init; }
    public required string Boundary { get; init; }
    public string? ExcludedReason { get; init; }
}

public sealed class SimulateResponse
{
    /// <summary>Always true: no provider call, no audit record, no spend or reservation.</summary>
    public required bool DryRun { get; init; }
    public required int ConfigVersion { get; init; }
    public string? ApplicationId { get; init; }
    public string? Profile { get; init; }
    public required string Classification { get; init; }
    public required string Baseline { get; init; }
    public string? DetectedSignal { get; init; }
    public string? PolicyId { get; init; }
    public required List<SimulatedTarget> Eligible { get; init; }
    /// <summary>"route" | "refuse".</summary>
    public required string Decision { get; init; }
    public string? Primary { get; init; }
    public required List<string> Fallbacks { get; init; }
    public required string Reason { get; init; }
    public string? Explanation { get; init; }
}

// ---- multi-connection catalog (extends CatalogReport compatibly; serialized by CovenantJsonContext, hence the
// explicit [JsonPropertyName]s like the rest of that context) ----

public sealed class CatalogConnection
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("display_name")] public required string DisplayName { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("boundary")] public required string Boundary { get; init; }
    [JsonPropertyName("enabled")] public required bool Enabled { get; init; }
    /// <summary>scheme://host:port/path, or null = provider default endpoint — never credentials.</summary>
    [JsonPropertyName("endpoint")] public string? Endpoint { get; init; }
    [JsonPropertyName("endpoint_allow_listed")] public required bool EndpointAllowListed { get; init; }
    [JsonPropertyName("secret_ref")] public string? SecretRef { get; init; }
    [JsonPropertyName("secret_available")] public bool? SecretAvailable { get; init; }
    [JsonPropertyName("models")] public required List<CatalogConnectionModel> Models { get; init; }
}

public sealed class CatalogConnectionModel
{
    /// <summary>"connectionId:modelId" — the provider-client key.</summary>
    [JsonPropertyName("key")] public required string Key { get; init; }
    [JsonPropertyName("model_id")] public required string ModelId { get; init; }
    [JsonPropertyName("display_name")] public string? DisplayName { get; init; }
    [JsonPropertyName("registered")] public required bool Registered { get; init; }
    [JsonPropertyName("price_basis")] public required string PriceBasis { get; init; }
    [JsonPropertyName("in_per_1m")] public decimal? InPer1M { get; init; }
    [JsonPropertyName("out_per_1m")] public decimal? OutPer1M { get; init; }
    [JsonPropertyName("health")] public required HealthView Health { get; init; }
}

/// <summary>JsonStringEnumConverter&lt;T&gt; with kebab-case-lower names: the ADR vocabulary ("local-only", "hybrid",
/// "legacy-cloud", "fixed", "length-heuristic", "none|daily|monthly", "hard|warn-only"). Integers are accepted on
/// read only so the validator can report an undefined value with its path; defined values are always written as names.</summary>
public sealed class KebabEnumConverter<TEnum> : JsonStringEnumConverter<TEnum> where TEnum : struct, Enum
{
    public KebabEnumConverter() : base(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: true) { }
}

// Wire vocabulary: mode/strategy/period/enforcement in ADR kebab-case; provider kind, boundary and classification
// keep their C# names ("OpenAi", "Local", "Pii") exactly as the audit record and the catalog already show them.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = false,
    Converters =
    [
        typeof(KebabEnumConverter<ProviderMode>),
        typeof(JsonStringEnumConverter<ProviderKind>),
        typeof(JsonStringEnumConverter<Boundary>),
        typeof(KebabEnumConverter<RoutingStrategy>),
        typeof(KebabEnumConverter<BudgetPeriod>),
        typeof(KebabEnumConverter<BudgetEnforcement>),
        typeof(JsonStringEnumConverter<DataClassification>),
    ])]
[JsonSerializable(typeof(CovenantConfig))]
[JsonSerializable(typeof(ActivePointer))]
[JsonSerializable(typeof(ActivationRecord))]
[JsonSerializable(typeof(ConfigGetResponse))]
[JsonSerializable(typeof(ConfigValidateRequest))]
[JsonSerializable(typeof(ConfigValidateResponse))]
[JsonSerializable(typeof(ConfigPutRequest))]
[JsonSerializable(typeof(ConfigPutResponse))]
[JsonSerializable(typeof(ConfigErrorResponse))]
[JsonSerializable(typeof(List<ConfigVersionSummary>))]
[JsonSerializable(typeof(ConfigRollbackRequest))]
[JsonSerializable(typeof(List<ApplicationView>))]
[JsonSerializable(typeof(SimulateRequest))]
[JsonSerializable(typeof(SimulateResponse))]
public partial class ConfigJsonContext : JsonSerializerContext;

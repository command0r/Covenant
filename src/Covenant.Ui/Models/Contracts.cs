using System.Text.Json.Serialization;

namespace Covenant.Ui.Models;

// Wire shapes the console reads, mirrored one-to-one from the Host DTOs (StatusContracts.cs, Evidence.cs,
// OpenAiContracts.cs, ConsoleBackend.cs, AuditSegments.cs, Covenant.Core AuditEntry) and docs/ui-api-contract.md.
// ONE shape per endpoint: no alternative field names, no shape sniffing. The only intentional variance is the
// documented audit-record evolution (legacy lines lack the v2/v3 fields → nullable here → "Not recorded").
// tests/Covenant.Tests/ContractRoundTripTests.cs serializes the Host types and reads them with UiJsonContext.

public sealed class StatusReport
{
    [JsonPropertyName("generated_utc")] public DateTimeOffset GeneratedUtc { get; set; }
    [JsonPropertyName("started_utc")] public DateTimeOffset StartedUtc { get; set; }
    [JsonPropertyName("kill_switch")] public KillSwitchState KillSwitch { get; set; } = new();
    [JsonPropertyName("budget")] public BudgetStatus Budget { get; set; } = new();
    [JsonPropertyName("routes")] public List<RouteView> Routes { get; set; } = [];
    [JsonPropertyName("finops")] public FinOpsSummary FinOps { get; set; } = new();
    [JsonPropertyName("chain_valid")] public bool ChainValid { get; set; }
    [JsonPropertyName("audit_entries")] public int AuditEntries { get; set; }
    [JsonPropertyName("auth")] public AuthStatus Auth { get; set; } = new();
    [JsonPropertyName("otel_enabled")] public bool OtelEnabled { get; set; }
    [JsonPropertyName("routing_threshold_tokens")] public long RoutingThresholdTokens { get; set; }

    [JsonPropertyName("environment")] public string? Environment { get; set; }
    [JsonPropertyName("mode")] public string? Mode { get; set; }
    [JsonPropertyName("audit_path")] public string? AuditPath { get; set; }
    [JsonPropertyName("cache_ttl_seconds")] public int? CacheTtlSeconds { get; set; }
    [JsonPropertyName("prompt_preview_chars")] public int? PromptPreviewChars { get; set; }
    [JsonPropertyName("anchor")] public AnchorStatus? Anchor { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    // v2: the active configuration version every new request binds to.
    [JsonPropertyName("config_version")] public int? ConfigVersion { get; set; }
    /// <summary>Review round 2 (B11): CURRENT runtime health of every configured connection + model.</summary>
    [JsonPropertyName("connection_health")] public List<ConnectionHealthDto>? ConnectionHealth { get; set; }
}

/// <summary>/admin/status <c>connection_health[]</c> row (Host ConnectionHealthView).</summary>
public sealed class ConnectionHealthDto
{
    /// <summary>"connection_id:model_id".</summary>
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("connection_id")] public string ConnectionId { get; set; } = "";
    [JsonPropertyName("model_id")] public string ModelId { get; set; } = "";
    /// <summary>Local | Cloud.</summary>
    [JsonPropertyName("boundary")] public string Boundary { get; set; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("health")] public ModelHealth Health { get; set; } = new();
}

public sealed class AnchorStatus
{
    [JsonPropertyName("configured")] public bool Configured { get; set; }
    [JsonPropertyName("every")] public int? Every { get; set; }
    [JsonPropertyName("anchors_written")] public long? AnchorsWritten { get; set; }
    [JsonPropertyName("last_anchor_utc")] public DateTimeOffset? LastAnchorUtc { get; set; }
}

public sealed class AuthStatus
{
    [JsonPropertyName("allow_anonymous")] public bool AllowAnonymous { get; set; }
    [JsonPropertyName("key_count")] public int KeyCount { get; set; }
}

public sealed class BudgetStatus
{
    [JsonPropertyName("global_cap_usd")] public decimal GlobalCapUsd { get; set; }
    [JsonPropertyName("global_spend_usd")] public decimal GlobalSpendUsd { get; set; }
    [JsonPropertyName("teams")] public List<TeamBudgetStatus> Teams { get; set; } = [];
}

public sealed class TeamBudgetStatus
{
    [JsonPropertyName("team")] public string Team { get; set; } = "";
    [JsonPropertyName("cap_usd")] public decimal? CapUsd { get; set; }
    [JsonPropertyName("spend_usd")] public decimal SpendUsd { get; set; }
}

public sealed class RouteView
{
    [JsonPropertyName("classification")] public string Classification { get; set; } = "";
    [JsonPropertyName("adapter")] public string Adapter { get; set; } = "";
    [JsonPropertyName("model")] public string Model { get; set; } = "";
}

public sealed class FinOpsSummary
{
    [JsonPropertyName("requests")] public int Requests { get; set; }
    [JsonPropertyName("allowed")] public int Allowed { get; set; }
    [JsonPropertyName("denied")] public int Denied { get; set; }
    [JsonPropertyName("total_cost_usd")] public decimal TotalCostUsd { get; set; }
    [JsonPropertyName("local_requests")] public int LocalRequests { get; set; }
    [JsonPropertyName("local_tokens")] public long LocalTokens { get; set; }
    [JsonPropertyName("estimated_savings_usd")] public decimal EstimatedSavingsUsd { get; set; }
    [JsonPropertyName("savings_local_usd")] public decimal SavingsLocalUsd { get; set; }
    [JsonPropertyName("savings_router_usd")] public decimal SavingsRouterUsd { get; set; }
    [JsonPropertyName("savings_cache_usd")] public decimal SavingsCacheUsd { get; set; }
    [JsonPropertyName("cache_hits")] public int CacheHits { get; set; }
    [JsonPropertyName("unknown_cost_requests")] public int UnknownCostRequests { get; set; }
    /// <summary>Requests whose total includes an estimate (estimated_from_text / estimated_failed_call / mixed).</summary>
    [JsonPropertyName("estimated_cost_requests")] public int EstimatedCostRequests { get; set; }
    [JsonPropertyName("cost_by_team_usd")] public Dictionary<string, decimal> CostByTeamUsd { get; set; } = new();
    [JsonPropertyName("denials_by_reason")] public Dictionary<string, int> DenialsByReason { get; set; } = new();
    [JsonPropertyName("activity")] public List<ActivityBucket> Activity { get; set; } = [];
    [JsonPropertyName("requests_by_model")] public Dictionary<string, int> RequestsByModel { get; set; } = new();
    [JsonPropertyName("recent")] public List<RecentRequest> Recent { get; set; } = [];
    /// <summary>Present only when the active configuration names a priced savings baseline; null otherwise.</summary>
    [JsonPropertyName("savings")] public SavingsSummaryDto? Savings { get; set; }
}

public sealed class SavingsSummaryDto
{
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    /// <summary>"connection/model" of the configured baseline.</summary>
    [JsonPropertyName("baseline")] public string Baseline { get; set; } = "";
    [JsonPropertyName("route_avoided_usd")] public decimal RouteAvoidedUsd { get; set; }
    [JsonPropertyName("cache_avoided_usd")] public decimal CacheAvoidedUsd { get; set; }
    [JsonPropertyName("total_usd")] public decimal TotalUsd { get; set; }
    [JsonPropertyName("excluded_records")] public int ExcludedRecords { get; set; }
}

public sealed class ActivityBucket
{
    [JsonPropertyName("t")] public DateTimeOffset T { get; set; }
    [JsonPropertyName("requests")] public int Requests { get; set; }
    [JsonPropertyName("denied")] public int Denied { get; set; }
    [JsonPropertyName("cost_usd")] public decimal CostUsd { get; set; }
    [JsonPropertyName("avg_ms")] public double AvgMs { get; set; }
}

public sealed class RecentRequest
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("t")] public DateTimeOffset T { get; set; }
    [JsonPropertyName("principal")] public string Principal { get; set; } = "";
    [JsonPropertyName("team")] public string Team { get; set; } = "";
    [JsonPropertyName("workflow")] public string Workflow { get; set; } = "";
    [JsonPropertyName("use_case")] public string UseCase { get; set; } = "";
    [JsonPropertyName("classification")] public string Classification { get; set; } = "";
    [JsonPropertyName("effect")] public string Effect { get; set; } = "";
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("tokens")] public long Tokens { get; set; }
    [JsonPropertyName("tokens_in")] public long TokensIn { get; set; }
    [JsonPropertyName("tokens_out")] public long TokensOut { get; set; }
    [JsonPropertyName("cost_usd")] public decimal CostUsd { get; set; }
    [JsonPropertyName("ms")] public double Ms { get; set; }
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
    [JsonPropertyName("prompt_chars")] public int PromptChars { get; set; }
    [JsonPropertyName("prompt_sha256")] public string? PromptSha256 { get; set; }
    [JsonPropertyName("signal")] public string? Signal { get; set; }
    [JsonPropertyName("prompt_preview")] public string? PromptPreview { get; set; }
    [JsonPropertyName("cache")] public bool CacheHit { get; set; }
    [JsonPropertyName("cost_basis")] public string? CostBasis { get; set; }
}

public sealed class KillSwitchRequest
{
    [JsonPropertyName("engaged")] public bool Engaged { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}

public sealed class KillSwitchState
{
    [JsonPropertyName("engaged")] public bool Engaged { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}

public sealed class ResetResponse
{
    [JsonPropertyName("archived_to")] public string? ArchivedTo { get; set; }
}

public sealed class ErrorResponse
{
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("request_id")] public string? RequestId { get; set; }
    [JsonPropertyName("scanned")] public int? Scanned { get; set; }
}

public sealed class EvidenceReport
{
    [JsonPropertyName("generated_utc")] public DateTimeOffset GeneratedUtc { get; set; }
    [JsonPropertyName("audit_log_path")] public string AuditLogPath { get; set; } = "";
    [JsonPropertyName("chain_valid")] public bool ChainValid { get; set; }
    [JsonPropertyName("chain_failure")] public string? ChainFailure { get; set; }
    [JsonPropertyName("first_invalid_line")] public int? FirstInvalidLine { get; set; }
    [JsonPropertyName("entries")] public int Entries { get; set; }
    /// <summary>Control-plane events in the chain (config.activated, …) — not requests.</summary>
    [JsonPropertyName("events")] public int Events { get; set; }
    [JsonPropertyName("allowed")] public int Allowed { get; set; }
    [JsonPropertyName("denied")] public int Denied { get; set; }
    [JsonPropertyName("first_entry_utc")] public DateTimeOffset? FirstEntryUtc { get; set; }
    [JsonPropertyName("last_entry_utc")] public DateTimeOffset? LastEntryUtc { get; set; }
    [JsonPropertyName("total_cost_usd")] public decimal TotalCostUsd { get; set; }
    [JsonPropertyName("cost_by_team_usd")] public Dictionary<string, decimal> CostByTeamUsd { get; set; } = new();
    [JsonPropertyName("requests_by_classification")] public Dictionary<string, int> RequestsByClassification { get; set; } = new();

    [JsonPropertyName("anchor_configured")] public bool? AnchorConfigured { get; set; }
    [JsonPropertyName("anchor_count")] public long? AnchorCount { get; set; }
    [JsonPropertyName("last_anchor_utc")] public DateTimeOffset? LastAnchorUtc { get; set; }
}

// ---- Admin lookups (Task 02): audit records are serialized with their C# names (AuditJsonContext). ----

/// <summary>GET /admin/requests page (Host RequestsPage, AuditJsonContext).</summary>
public sealed class RequestsResponse
{
    [JsonPropertyName("segment")] public SegmentInfo Segment { get; set; } = new();
    [JsonPropertyName("items")] public List<AuditEntryView> Items { get; set; } = [];
    /// <summary>Opaque cursor of the next (older) page of the SAME segment; null on the last page.</summary>
    [JsonPropertyName("next_cursor")] public string? NextCursor { get; set; }
}

/// <summary>Segment descriptor of a requests page (Host SegmentInfo). <see cref="Id"/>, <see cref="Label"/> and
/// <see cref="IsCurrent"/> come from the server on every page — the console never infers the current segment.</summary>
public sealed class SegmentInfo
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("label")] public string? Label { get; set; }
    [JsonPropertyName("is_current")] public bool IsCurrent { get; set; }
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("events")] public int Events { get; set; }
    [JsonPropertyName("entries")] public int Entries { get; set; }
    [JsonPropertyName("chain_valid")] public bool ChainValid { get; set; }
    [JsonPropertyName("first_utc")] public DateTimeOffset? FirstUtc { get; set; }
    [JsonPropertyName("last_utc")] public DateTimeOffset? LastUtc { get; set; }
}

public sealed class AuditEntryView
{
    [JsonPropertyName("Id")] public string Id { get; set; } = "";
    [JsonPropertyName("TimestampUtc")] public DateTimeOffset TimestampUtc { get; set; }
    [JsonPropertyName("Principal")] public string Principal { get; set; } = "";
    [JsonPropertyName("Tags")] public AttributionTagsView Tags { get; set; } = new();
    [JsonPropertyName("Classification")] public string Classification { get; set; } = "";
    [JsonPropertyName("Effect")] public string Effect { get; set; } = "";
    [JsonPropertyName("Reason")] public string Reason { get; set; } = "";
    [JsonPropertyName("ServedByModel")] public string? ServedByModel { get; set; }
    [JsonPropertyName("Usage")] public UsageView Usage { get; set; } = new();
    [JsonPropertyName("DurationMs")] public double DurationMs { get; set; }
    [JsonPropertyName("PromptChars")] public int PromptChars { get; set; }
    [JsonPropertyName("PromptSha256")] public string? PromptSha256 { get; set; }
    [JsonPropertyName("Signal")] public string? Signal { get; set; }
    [JsonPropertyName("PromptPreview")] public string? PromptPreview { get; set; }
    [JsonPropertyName("CacheHit")] public bool CacheHit { get; set; }

    // Audit record v2 (nullable; absent on legacy records → "Not recorded").
    [JsonPropertyName("SchemaVersion")] public int? SchemaVersion { get; set; }
    [JsonPropertyName("CorrelationId")] public string? CorrelationId { get; set; }
    [JsonPropertyName("ClassificationEvaluated")] public bool? ClassificationEvaluated { get; set; }
    [JsonPropertyName("DenialKind")] public string? DenialKind { get; set; }
    [JsonPropertyName("StoppedAtStage")] public string? StoppedAtStage { get; set; }
    [JsonPropertyName("SelectedModel")] public string? SelectedModel { get; set; }
    [JsonPropertyName("AttemptedModel")] public string? AttemptedModel { get; set; }
    [JsonPropertyName("Stream")] public bool? Stream { get; set; }
    [JsonPropertyName("UsageReported")] public bool? UsageReported { get; set; }
    [JsonPropertyName("CostBasis")] public string? CostBasis { get; set; }

    // Audit record v3 (ADR-0012/0013/0014; additive, nullable — absent on older records → "Not recorded").
    [JsonPropertyName("EventType")] public string? EventType { get; set; }
    [JsonPropertyName("ConfigVersion")] public int? ConfigVersion { get; set; }
    [JsonPropertyName("EntryPoint")] public string? EntryPoint { get; set; }
    [JsonPropertyName("OperatorRef")] public string? OperatorRef { get; set; }
    [JsonPropertyName("ApplicationId")] public string? ApplicationId { get; set; }
    [JsonPropertyName("ProfileId")] public string? ProfileId { get; set; }
    [JsonPropertyName("BaselineClassification")] public string? BaselineClassification { get; set; }
    [JsonPropertyName("Attempts")] public List<AuditAttemptView>? Attempts { get; set; }
    /// <summary>Compact persisted trace (Core AuditTraceStep): terminal states only, one provider step per attempt.</summary>
    [JsonPropertyName("Trace")] public List<AuditTraceStepView>? Trace { get; set; }
    [JsonPropertyName("ReservedUsd")] public decimal? ReservedUsd { get; set; }
    [JsonPropertyName("SettledUsd")] public decimal? SettledUsd { get; set; }
    /// <summary>max_tokens as the caller asked (null = none asked, or a record older than this field).</summary>
    [JsonPropertyName("RequestedMaxOutputTokens")] public int? RequestedMaxOutputTokens { get; set; }
    /// <summary>The output bound Covenant enforced (sent to the provider and used for the reservation).</summary>
    [JsonPropertyName("EffectiveMaxOutputTokens")] public int? EffectiveMaxOutputTokens { get; set; }
    /// <summary>Characters already emitted to the client before a stream failed (non-served records only).</summary>
    [JsonPropertyName("PartialOutputChars")] public int? PartialOutputChars { get; set; }
    /// <summary>Connection of the attempt that served (a fallback when one served; on a cache hit the connection that
    /// produced the cached entry).</summary>
    [JsonPropertyName("ServedConnectionId")] public string? ServedConnectionId { get; set; }
    /// <summary>ADR-0015: bounded web-search metadata (never the query, location or snippets).</summary>
    [JsonPropertyName("WebSearch")] public AuditWebSearchView? WebSearch { get; set; }
    /// <summary>ADR-0015: non-inference tool calls with their own charges, separate from provider Attempts.</summary>
    [JsonPropertyName("ToolCalls")] public List<AuditToolCallView>? ToolCalls { get; set; }
}

/// <summary>Core AuditWebSearch.</summary>
public sealed class AuditWebSearchView
{
    [JsonPropertyName("Mode")] public string Mode { get; set; } = "";
    [JsonPropertyName("Triggered")] public bool Triggered { get; set; }
    [JsonPropertyName("Cue")] public string? Cue { get; set; }
    [JsonPropertyName("Outcome")] public string Outcome { get; set; } = "";
    [JsonPropertyName("ReasonCode")] public string? ReasonCode { get; set; }
    [JsonPropertyName("SourceCount")] public int? SourceCount { get; set; }
    [JsonPropertyName("SourceHosts")] public List<string>? SourceHosts { get; set; }
    [JsonPropertyName("DurationMs")] public double? DurationMs { get; set; }
}

/// <summary>Core AuditToolCall. Outcome ∈ succeeded | failed | cancelled; CostBasis ∈ reported | estimated_failed_call |
/// estimated_unreported | no_call.</summary>
public sealed class AuditToolCallView
{
    [JsonPropertyName("ToolCallId")] public string ToolCallId { get; set; } = "";
    [JsonPropertyName("Tool")] public string Tool { get; set; } = "";
    [JsonPropertyName("Provider")] public string Provider { get; set; } = "";
    [JsonPropertyName("Outcome")] public string Outcome { get; set; } = "";
    [JsonPropertyName("ErrorClass")] public string? ErrorClass { get; set; }
    [JsonPropertyName("DurationMs")] public double DurationMs { get; set; }
    [JsonPropertyName("CostUsd")] public decimal? CostUsd { get; set; }
    [JsonPropertyName("CostBasis")] public string? CostBasis { get; set; }
    [JsonPropertyName("ReservedUsd")] public decimal? ReservedUsd { get; set; }
    [JsonPropertyName("Credits")] public int? Credits { get; set; }
}

/// <summary>One step of the compact persisted trace (Core AuditTraceStep).</summary>
public sealed class AuditTraceStepView
{
    [JsonPropertyName("Stage")] public string Stage { get; set; } = "";
    [JsonPropertyName("State")] public string State { get; set; } = "";
    [JsonPropertyName("DurationMs")] public double? DurationMs { get; set; }
    [JsonPropertyName("AttemptId")] public string? AttemptId { get; set; }
    [JsonPropertyName("ReasonCode")] public string? ReasonCode { get; set; }
}

/// <summary>One provider attempt recorded on an audit v3 record (Core AuditAttempt). Outcome ∈ served | failed | cancelled;
/// ErrorClass ∈ connect | timeout | 429 | 5xx | non_retryable | client_cancelled | client_disconnected | incomplete_stream.</summary>
public sealed class AuditAttemptView
{
    [JsonPropertyName("AttemptId")] public string AttemptId { get; set; } = "";
    [JsonPropertyName("ConnectionId")] public string ConnectionId { get; set; } = "";
    [JsonPropertyName("ModelId")] public string ModelId { get; set; } = "";
    [JsonPropertyName("Outcome")] public string Outcome { get; set; } = "";
    [JsonPropertyName("ErrorClass")] public string? ErrorClass { get; set; }
    [JsonPropertyName("DurationMs")] public double DurationMs { get; set; }
    [JsonPropertyName("CostUsd")] public decimal? CostUsd { get; set; }
    [JsonPropertyName("CostBasis")] public string? CostBasis { get; set; }
    // T04/T06 additive: the attempt's reservation and whether the reported charge exceeded it (settled in full).
    [JsonPropertyName("ReservedUsd")] public decimal? ReservedUsd { get; set; }
    [JsonPropertyName("Overrun")] public bool? Overrun { get; set; }
    /// <summary>The output bound enforced for THIS attempt (a fallback model may have a lower model bound).</summary>
    [JsonPropertyName("EffectiveMaxOutputTokens")] public int? EffectiveMaxOutputTokens { get; set; }
    /// <summary>True when the provider finished with "length" (output cut at the bound).</summary>
    [JsonPropertyName("Truncated")] public bool? Truncated { get; set; }
}

public sealed class AttributionTagsView
{
    [JsonPropertyName("Team")] public string Team { get; set; } = "";
    [JsonPropertyName("Workflow")] public string Workflow { get; set; } = "";
    [JsonPropertyName("UseCase")] public string UseCase { get; set; } = "";
}

public sealed class UsageView
{
    [JsonPropertyName("InputTokens")] public long InputTokens { get; set; }
    [JsonPropertyName("OutputTokens")] public long OutputTokens { get; set; }
    [JsonPropertyName("CostUsd")] public decimal CostUsd { get; set; }
    [JsonPropertyName("TotalTokens")] public long TotalTokens { get; set; }
}

// ---- Catalog & providers (Task 06). ----

public sealed class CatalogResponse
{
    [JsonPropertyName("mode")] public string? Mode { get; set; }
    [JsonPropertyName("models")] public List<CatalogModel> Models { get; set; } = [];
    [JsonPropertyName("local")] public LocalRouteInfo? Local { get; set; }
    [JsonPropertyName("probe_allow_list")] public List<string>? ProbeAllowList { get; set; }
    // v2 (ADR-0011): connection-scoped view of the active configuration.
    [JsonPropertyName("config_version")] public int? ConfigVersion { get; set; }
    [JsonPropertyName("connections")] public List<CatalogConnectionDto>? Connections { get; set; }
    [JsonPropertyName("egress_allow_list")] public List<string>? EgressAllowList { get; set; }
}

/// <summary>Catalog view of one configured connection (Host CatalogConnection): registration and observed health,
/// never credentials. Model health is observed from real governed calls (keyed connection:model).</summary>
public sealed class CatalogConnectionDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("boundary")] public string? Boundary { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("endpoint")] public string? Endpoint { get; set; }
    [JsonPropertyName("endpoint_allow_listed")] public bool EndpointAllowListed { get; set; }
    [JsonPropertyName("secret_ref")] public string? SecretRef { get; set; }
    [JsonPropertyName("secret_available")] public bool? SecretAvailable { get; set; }
    [JsonPropertyName("models")] public List<CatalogConnectionModelDto> Models { get; set; } = [];
}

public sealed class CatalogConnectionModelDto
{
    [JsonPropertyName("key")] public string? Key { get; set; }
    [JsonPropertyName("model_id")] public string ModelId { get; set; } = "";
    [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
    [JsonPropertyName("registered")] public bool Registered { get; set; }
    [JsonPropertyName("price_basis")] public string? PriceBasis { get; set; }
    [JsonPropertyName("in_per_1m")] public decimal? InPer1M { get; set; }
    [JsonPropertyName("out_per_1m")] public decimal? OutPer1M { get; set; }
    [JsonPropertyName("health")] public ModelHealth? Health { get; set; }
}

public sealed class CatalogModel
{
    [JsonPropertyName("adapter")] public string Adapter { get; set; } = "";
    [JsonPropertyName("model_id")] public string ModelId { get; set; } = "";
    [JsonPropertyName("classifications")] public List<string> Classifications { get; set; } = [];
    [JsonPropertyName("registered")] public bool Registered { get; set; }
    [JsonPropertyName("price_basis")] public string? PriceBasis { get; set; }
    [JsonPropertyName("in_per_1m")] public decimal? InPer1M { get; set; }
    [JsonPropertyName("out_per_1m")] public decimal? OutPer1M { get; set; }
    [JsonPropertyName("endpoint_host")] public string? EndpointHost { get; set; }
    [JsonPropertyName("health")] public ModelHealth? Health { get; set; }
    [JsonPropertyName("connection_id")] public string? ConnectionId { get; set; }
    [JsonPropertyName("boundary")] public string? Boundary { get; set; }
    [JsonPropertyName("key")] public string? Key { get; set; }
}

/// <summary>Host HealthView: state ∈ unknown | ok | failed.</summary>
public sealed class ModelHealth
{
    [JsonPropertyName("state")] public string State { get; set; } = "unknown";
    [JsonPropertyName("last_success_utc")] public DateTimeOffset? LastSuccessUtc { get; set; }
    [JsonPropertyName("last_failure_utc")] public DateTimeOffset? LastFailureUtc { get; set; }
    [JsonPropertyName("last_error")] public string? LastError { get; set; }
}

public sealed class LocalRouteInfo
{
    [JsonPropertyName("configured")] public bool Configured { get; set; }
    [JsonPropertyName("endpoint")] public string? Endpoint { get; set; }
    [JsonPropertyName("model_id")] public string? ModelId { get; set; }
    [JsonPropertyName("registered")] public bool Registered { get; set; }
    [JsonPropertyName("endpoint_health")] public ModelHealth EndpointHealth { get; set; } = new();
    /// <summary>Review round 2 (B11): the local-boundary connection this view describes.</summary>
    [JsonPropertyName("connection_id")] public string? ConnectionId { get; set; }
    /// <summary>CURRENT runtime health of exactly <see cref="ConnectionId"/>:<see cref="ModelId"/> (real governed attempts).</summary>
    [JsonPropertyName("health")] public ModelHealth? Health { get; set; }
}

public sealed class ProbeRequest
{
    [JsonPropertyName("endpoint")] public string Endpoint { get; set; } = "";
}

public sealed class ProbeResponse
{
    [JsonPropertyName("allowed")] public bool Allowed { get; set; }
    [JsonPropertyName("reachable")] public bool Reachable { get; set; }
    [JsonPropertyName("http_status")] public int? HttpStatus { get; set; }
    [JsonPropertyName("models")] public List<string> Models { get; set; } = [];
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("duration_ms")] public double? DurationMs { get; set; }
    [JsonPropertyName("endpoint")] public string? Endpoint { get; set; }
    [JsonPropertyName("checked_utc")] public DateTimeOffset? CheckedUtc { get; set; }
}

// ---- Inference wire (OpenAI chat shape). Console inference posts PlaygroundRequestDto (ConfigContracts.cs) to
// /admin/playground/requests with the admin token; the response contract is exactly /v1/chat/completions. ----

public sealed class ChatMessageDto
{
    [JsonPropertyName("role")] public string Role { get; set; } = "user";
    [JsonPropertyName("content")] public string Content { get; set; } = "";
}

public sealed class ChatResponseDto
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("choices")] public List<ChatChoiceDto> Choices { get; set; } = [];
    [JsonPropertyName("usage")] public ChatUsageDto? Usage { get; set; }
}

public sealed class ChatChoiceDto
{
    [JsonPropertyName("index")] public int Index { get; set; }
    /// <summary>Buffered responses carry a message; streamed chunks carry a delta.</summary>
    [JsonPropertyName("message")] public ChatResponseMessageDto? Message { get; set; }
    [JsonPropertyName("delta")] public ChatDeltaDto? Delta { get; set; }
    [JsonPropertyName("finish_reason")] public string? FinishReason { get; set; }
}

public sealed class ChatResponseMessageDto
{
    [JsonPropertyName("role")] public string? Role { get; set; }
    /// <summary>A string in the classic protocol; an array of content parts in the newer one.</summary>
    [JsonPropertyName("content")] public System.Text.Json.JsonElement? Content { get; set; }
}

public sealed class ChatDeltaDto
{
    [JsonPropertyName("role")] public string? Role { get; set; }
    [JsonPropertyName("content")] public string? Content { get; set; }
}

public sealed class ChatUsageDto
{
    [JsonPropertyName("prompt_tokens")] public long PromptTokens { get; set; }
    [JsonPropertyName("completion_tokens")] public long CompletionTokens { get; set; }
    [JsonPropertyName("total_tokens")] public long TotalTokens { get; set; }
}

/// <summary>One SSE `data:` frame: either a chunk (id/choices/usage) or the mid-stream error line
/// ({"error":"upstream_error","reason":…,"request_id":…}).</summary>
public sealed class StreamFrameDto
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("choices")] public List<ChatChoiceDto>? Choices { get; set; }
    [JsonPropertyName("usage")] public ChatUsageDto? Usage { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("request_id")] public string? RequestId { get; set; }
}

/// <summary>Source-generated serializer context: Blazor publish trims reflection metadata, so every DTO the
/// console reads or writes must be listed here (src/CLAUDE.md: no reflection-based serialization).</summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StatusReport))]
[JsonSerializable(typeof(EvidenceReport))]
[JsonSerializable(typeof(KillSwitchRequest))]
[JsonSerializable(typeof(KillSwitchState))]
[JsonSerializable(typeof(ResetResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(RequestsResponse))]
[JsonSerializable(typeof(AuditEntryView))]
[JsonSerializable(typeof(CatalogResponse))]
[JsonSerializable(typeof(ProbeRequest))]
[JsonSerializable(typeof(ProbeResponse))]
[JsonSerializable(typeof(ChatResponseDto))]
[JsonSerializable(typeof(StreamFrameDto))]
// v2 (ConfigContracts.cs)
[JsonSerializable(typeof(ConfigEnvelopeDto))]
[JsonSerializable(typeof(CovenantConfigDto))]
[JsonSerializable(typeof(ConfigValidateRequestDto))]
[JsonSerializable(typeof(ConfigValidationResultDto))]
[JsonSerializable(typeof(ConfigPutRequestDto))]
[JsonSerializable(typeof(ConfigPutResponseDto))]
[JsonSerializable(typeof(List<ConfigVersionDto>))]
[JsonSerializable(typeof(ConfigRollbackRequestDto))]
[JsonSerializable(typeof(List<ApplicationSummaryDto>))]
[JsonSerializable(typeof(PlaygroundRequestDto))]
[JsonSerializable(typeof(TraceEventDto))]
[JsonSerializable(typeof(TraceSnapshotDto))]
[JsonSerializable(typeof(List<BudgetStateDto>))]
[JsonSerializable(typeof(SimulateRequestDto))]
[JsonSerializable(typeof(SimulateResponseDto))]
// T08 editors and evidence
[JsonSerializable(typeof(ConfigErrorResponseDto))]
[JsonSerializable(typeof(List<AuditSegmentDto>))]
// Review round 2: typed budget drill-down, request export, early console request id.
[JsonSerializable(typeof(BudgetRequestsDto))]
[JsonSerializable(typeof(RequestExportDto))]
[JsonSerializable(typeof(PlaygroundSubmissionDto))]
[JsonSerializable(typeof(WebSearchStatusDto))]
[JsonSerializable(typeof(RequestSearchDto))]
public partial class UiJsonContext : JsonSerializerContext;

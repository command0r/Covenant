using System.Text.Json.Serialization;

namespace Covenant.Ui.Models;

// Wire shapes the console reads, mirrored from docs/ui-api-contract.md and the Host's StatusContracts.cs /
// Evidence.cs / OpenAiContracts.cs. Deliberately lenient: nothing is `required`, fields added by later tasks are
// nullable, unknown members are ignored — the console must render an older or newer Host truthfully
// ("Not recorded") rather than fail to deserialize.

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

    // Task 07 additions (nullable until the Host ships them).
    [JsonPropertyName("environment")] public string? Environment { get; set; }
    [JsonPropertyName("mode")] public string? Mode { get; set; }
    [JsonPropertyName("audit_path")] public string? AuditPath { get; set; }
    [JsonPropertyName("cache_ttl_seconds")] public int? CacheTtlSeconds { get; set; }
    [JsonPropertyName("prompt_preview_chars")] public int? PromptPreviewChars { get; set; }
    [JsonPropertyName("anchor")] public AnchorStatus? Anchor { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
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
    [JsonPropertyName("cost_by_team_usd")] public Dictionary<string, decimal> CostByTeamUsd { get; set; } = new();
    [JsonPropertyName("denials_by_reason")] public Dictionary<string, int> DenialsByReason { get; set; } = new();
    [JsonPropertyName("activity")] public List<ActivityBucket> Activity { get; set; } = [];
    [JsonPropertyName("requests_by_model")] public Dictionary<string, int> RequestsByModel { get; set; } = new();
    [JsonPropertyName("recent")] public List<RecentRequest> Recent { get; set; } = [];
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
    [JsonPropertyName("allowed")] public int Allowed { get; set; }
    [JsonPropertyName("denied")] public int Denied { get; set; }
    [JsonPropertyName("first_entry_utc")] public DateTimeOffset? FirstEntryUtc { get; set; }
    [JsonPropertyName("last_entry_utc")] public DateTimeOffset? LastEntryUtc { get; set; }
    [JsonPropertyName("total_cost_usd")] public decimal TotalCostUsd { get; set; }
    [JsonPropertyName("cost_by_team_usd")] public Dictionary<string, decimal> CostByTeamUsd { get; set; } = new();
    [JsonPropertyName("requests_by_classification")] public Dictionary<string, int> RequestsByClassification { get; set; } = new();

    // Task 07 additions.
    [JsonPropertyName("anchor_configured")] public bool? AnchorConfigured { get; set; }
    [JsonPropertyName("anchor_count")] public long? AnchorCount { get; set; }
    [JsonPropertyName("last_anchor_utc")] public DateTimeOffset? LastAnchorUtc { get; set; }
}

// ---- Admin lookups (Task 02): audit records are serialized with their C# names (AuditJsonContext). ----

public sealed class RequestsResponse
{
    [JsonPropertyName("segment")] public SegmentInfo Segment { get; set; } = new();
    [JsonPropertyName("items")] public List<AuditEntryView> Items { get; set; } = [];
}

public sealed class SegmentInfo
{
    [JsonPropertyName("path")] public string? Path { get; set; }
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
}

public sealed class ModelHealth
{
    [JsonPropertyName("state")] public string? State { get; set; }
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
    [JsonPropertyName("endpoint_health")] public ModelHealth? EndpointHealth { get; set; }
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

// ---- Governed inference (/v1/chat/completions, OpenAI wire) — sent with an APPLICATION key, never the admin token. ----

public sealed class ChatRequestDto
{
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("messages")] public List<ChatMessageDto> Messages { get; set; } = [];
    [JsonPropertyName("stream")] public bool Stream { get; set; }
    [JsonPropertyName("max_tokens")] public int? MaxTokens { get; set; }
}

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
[JsonSerializable(typeof(ChatRequestDto))]
[JsonSerializable(typeof(ChatResponseDto))]
[JsonSerializable(typeof(StreamFrameDto))]
public partial class UiJsonContext : JsonSerializerContext;

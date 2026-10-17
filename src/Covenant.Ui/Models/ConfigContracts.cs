using System.Text.Json.Serialization;

namespace Covenant.Ui.Models;

// v2 wire shapes (docs/ui-api-contract.md "v2 additions", ADR-0011..0014), mirrored from
// src/Covenant.Core/Configuration.cs as console DTOs. Plain C#: linked into tests/Covenant.Tests, so no Blazor types.
//
// Naming: every type ends in "Dto" so nothing collides with Covenant.Core's ApplicationConfig/ConnectionConfig/…
// when both namespaces are imported (the tests do). Enums travel as strings in EXACTLY the contract spellings
// (docs/ui-api-contract.md "Wire spellings": "local-only", "length-heuristic", "warn-only", "Public", "Local", …);
// the console compares against those spellings only — there is no alternative-spelling fallback. One shape per
// endpoint; tests/Covenant.Tests/ContractRoundTripTests.cs pins every one against the Host serializer.

// ---- Configuration (ADR-0011) ----

public sealed class ConfigEnvelopeDto
{
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("etag")] public string? Etag { get; set; }
    [JsonPropertyName("config")] public CovenantConfigDto? Config { get; set; }
    [JsonPropertyName("secrets")] public Dictionary<string, SecretAvailabilityDto>? Secrets { get; set; }
    [JsonPropertyName("allow_list")] public List<string>? AllowList { get; set; }
}

public sealed class SecretAvailabilityDto
{
    [JsonPropertyName("available")] public bool Available { get; set; }
}

public sealed class CovenantConfigDto
{
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("base_version")] public int? BaseVersion { get; set; }
    [JsonPropertyName("rolled_back_from")] public int? RolledBackFrom { get; set; }
    [JsonPropertyName("origin")] public string? Origin { get; set; }
    [JsonPropertyName("created_utc")] public DateTimeOffset? CreatedUtc { get; set; }
    [JsonPropertyName("change_summary")] public string? ChangeSummary { get; set; }

    /// <summary>local-only | hybrid | legacy-cloud (ProviderMode).</summary>
    [JsonPropertyName("mode")] public string? Mode { get; set; }
    [JsonPropertyName("connections")] public List<ConnectionDto> Connections { get; set; } = [];
    [JsonPropertyName("applications")] public List<ApplicationDto> Applications { get; set; } = [];
    [JsonPropertyName("profiles")] public List<WorkloadProfileDto> Profiles { get; set; } = [];
    [JsonPropertyName("policies")] public List<PolicyDto> Policies { get; set; } = [];
    [JsonPropertyName("budgets")] public List<BudgetConfigDto> Budgets { get; set; } = [];
    [JsonPropertyName("settings")] public OperationalSettingsDto? Settings { get; set; }
    [JsonPropertyName("savings_baseline")] public ModelRefDto? SavingsBaseline { get; set; }
    /// <summary>ADR-0015. Null = web search disabled (the default). Must round-trip: a console save never drops it.</summary>
    [JsonPropertyName("web_search")] public WebSearchSettingsDto? WebSearch { get; set; }
}

/// <summary>Governed web search settings (ADR-0015). The API key is NOT here — it is a server-side secret
/// (Tavily:ApiKey), reported only as configured/missing by GET /admin/web-search.</summary>
public sealed class WebSearchSettingsDto
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    /// <summary>1–3 (validated by the Host).</summary>
    [JsonPropertyName("max_results")] public int MaxResults { get; set; } = 3;
    /// <summary>1–30 seconds.</summary>
    [JsonPropertyName("timeout_seconds")] public int TimeoutSeconds { get; set; } = 10;
    /// <summary>Null = unknown price (never $0) — search is refused until it is set.</summary>
    [JsonPropertyName("price_per_credit_usd")] public decimal? PricePerCreditUsd { get; set; }
    [JsonPropertyName("price_basis")] public string? PriceBasis { get; set; }
    /// <summary>yyyy-MM-dd</summary>
    [JsonPropertyName("price_effective_date")] public string? PriceEffectiveDate { get; set; }
}

public sealed class ModelRefDto
{
    [JsonPropertyName("connection_id")] public string ConnectionId { get; set; } = "";
    [JsonPropertyName("model_id")] public string ModelId { get; set; } = "";
}

public sealed class ConnectionDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
    /// <summary>Ollama | OpenAiCompatible | OpenAi | Anthropic (ProviderKind).</summary>
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    /// <summary>Local | Cloud (Boundary).</summary>
    [JsonPropertyName("boundary")] public string? Boundary { get; set; }
    [JsonPropertyName("endpoint")] public string? Endpoint { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("secret_ref")] public string? SecretRef { get; set; }
    [JsonPropertyName("models")] public List<ModelDto> Models { get; set; } = [];
    [JsonPropertyName("timeout_seconds")] public int? TimeoutSeconds { get; set; }
}

public sealed class ModelDto
{
    [JsonPropertyName("model_id")] public string ModelId { get; set; } = "";
    [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
    [JsonPropertyName("context_tokens")] public int? ContextTokens { get; set; }
    [JsonPropertyName("max_output_tokens")] public int? MaxOutputTokens { get; set; }
    /// <summary>Null = unknown price (never $0).</summary>
    [JsonPropertyName("price")] public PriceDto? Price { get; set; }
}

public sealed class PriceDto
{
    [JsonPropertyName("input_per_1m")] public decimal? InputPer1M { get; set; }
    [JsonPropertyName("output_per_1m")] public decimal? OutputPer1M { get; set; }
    [JsonPropertyName("basis")] public string? Basis { get; set; }
    [JsonPropertyName("effective_date")] public string? EffectiveDate { get; set; }
}

public sealed class ApplicationDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("team")] public string Team { get; set; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("console_testing")] public bool ConsoleTesting { get; set; }
    [JsonPropertyName("granted_profiles")] public List<string> GrantedProfiles { get; set; } = [];
    [JsonPropertyName("default_profile")] public string? DefaultProfile { get; set; }
    [JsonPropertyName("max_output_tokens")] public int? MaxOutputTokens { get; set; }
}

public sealed class WorkloadProfileDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string? Name { get; set; }
    /// <summary>Trusted baseline classification: Public | Internal | Pii | Phi.</summary>
    [JsonPropertyName("baseline")] public string? Baseline { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    /// <summary>ADR-0015: this profile may use web search. Only a Public-baseline profile may carry it.</summary>
    [JsonPropertyName("web_search_allowed")] public bool WebSearchAllowed { get; set; }
}

public sealed class PolicyDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("paused")] public bool Paused { get; set; }
    [JsonPropertyName("classifications")] public List<string> Classifications { get; set; } = [];
    [JsonPropertyName("profiles")] public List<string> Profiles { get; set; } = [];
    [JsonPropertyName("targets")] public List<PolicyTargetDto> Targets { get; set; } = [];
    /// <summary>fixed | length-heuristic (RoutingStrategy).</summary>
    [JsonPropertyName("strategy")] public string? Strategy { get; set; }
    [JsonPropertyName("threshold_tokens")] public int? ThresholdTokens { get; set; }
    [JsonPropertyName("retry")] public RetrySettingsDto? Retry { get; set; }
    // No "allow sensitive cloud" switch: PII/PHI never route to cloud destinations (the Host enforces it).
    [JsonPropertyName("explanation")] public string? Explanation { get; set; }
}

public sealed class PolicyTargetDto
{
    [JsonPropertyName("connection_id")] public string ConnectionId { get; set; } = "";
    [JsonPropertyName("model_id")] public string ModelId { get; set; } = "";
    [JsonPropertyName("paused")] public bool Paused { get; set; }
}

public sealed class RetrySettingsDto
{
    [JsonPropertyName("max_attempts")] public int? MaxAttempts { get; set; }
    [JsonPropertyName("deadline_ms")] public int? DeadlineMs { get; set; }
    [JsonPropertyName("on")] public List<string>? On { get; set; }
}

public sealed class BudgetConfigDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    /// <summary>global | team:&lt;id&gt; | application:&lt;id&gt;</summary>
    [JsonPropertyName("scope")] public string Scope { get; set; } = "";
    [JsonPropertyName("cap_usd")] public decimal CapUsd { get; set; }
    [JsonPropertyName("period")] public string? Period { get; set; }
    [JsonPropertyName("warn_at")] public List<decimal>? WarnAt { get; set; }
    [JsonPropertyName("enforcement")] public string? Enforcement { get; set; }
}

public sealed class OperationalSettingsDto
{
    [JsonPropertyName("cache_ttl_seconds")] public int? CacheTtlSeconds { get; set; }
    [JsonPropertyName("cache_max_entries")] public int? CacheMaxEntries { get; set; }
    [JsonPropertyName("prompt_preview_chars")] public int? PromptPreviewChars { get; set; }
    [JsonPropertyName("trace_retention_minutes")] public int? TraceRetentionMinutes { get; set; }
    [JsonPropertyName("rate_limit_global_per_minute")] public int? RateLimitGlobalPerMinute { get; set; }
    [JsonPropertyName("rate_limit_per_team_per_minute")] public int? RateLimitPerTeamPerMinute { get; set; }
}

// Validate / apply / history / rollback — read by the T08 editors; listed here so the shared context has them.

public sealed class ConfigValidateRequestDto
{
    [JsonPropertyName("base_version")] public int BaseVersion { get; set; }
    [JsonPropertyName("config")] public CovenantConfigDto? Config { get; set; }
}

public sealed class ConfigIssueDto
{
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}

public sealed class ConfigImpactRowDto
{
    [JsonPropertyName("classification")] public string? Classification { get; set; }
    [JsonPropertyName("profile")] public string? Profile { get; set; }
    [JsonPropertyName("before")] public string? Before { get; set; }
    [JsonPropertyName("after")] public string? After { get; set; }
    [JsonPropertyName("changed")] public bool Changed { get; set; }
}

public sealed class ConfigImpactDto
{
    [JsonPropertyName("classifications")] public List<ConfigImpactRowDto> Classifications { get; set; } = [];
    [JsonPropertyName("applications_affected")] public List<string> ApplicationsAffected { get; set; } = [];
    [JsonPropertyName("unavailable_destinations")] public List<string> UnavailableDestinations { get; set; } = [];
    [JsonPropertyName("pricing_gaps")] public List<string> PricingGaps { get; set; } = [];
    [JsonPropertyName("newly_refused")] public List<string> NewlyRefused { get; set; } = [];
}

public sealed class ConfigValidationResultDto
{
    [JsonPropertyName("valid")] public bool Valid { get; set; }
    [JsonPropertyName("active_version")] public int? ActiveVersion { get; set; }
    [JsonPropertyName("errors")] public List<ConfigIssueDto> Errors { get; set; } = [];
    [JsonPropertyName("warnings")] public List<ConfigIssueDto> Warnings { get; set; } = [];
    [JsonPropertyName("impact")] public ConfigImpactDto? Impact { get; set; }
}

public sealed class ConfigPutRequestDto
{
    [JsonPropertyName("config")] public CovenantConfigDto? Config { get; set; }
    [JsonPropertyName("change_summary")] public string? ChangeSummary { get; set; }
}

public sealed class ConfigPutResponseDto
{
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("etag")] public string? Etag { get; set; }
}

/// <summary>Error body of /admin/config* (400/404/412/422/428): <c>active_version</c> on 412/422/428, field issues on 422.</summary>
public sealed class ConfigErrorResponseDto
{
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("active_version")] public int? ActiveVersion { get; set; }
    [JsonPropertyName("errors")] public List<ConfigIssueDto>? Errors { get; set; }
    [JsonPropertyName("warnings")] public List<ConfigIssueDto>? Warnings { get; set; }
}

public sealed class ConfigVersionDto
{
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("created_utc")] public DateTimeOffset? CreatedUtc { get; set; }
    [JsonPropertyName("origin")] public string? Origin { get; set; }
    [JsonPropertyName("base_version")] public int? BaseVersion { get; set; }
    [JsonPropertyName("rolled_back_from")] public int? RolledBackFrom { get; set; }
    [JsonPropertyName("change_summary")] public string? ChangeSummary { get; set; }
    [JsonPropertyName("active")] public bool Active { get; set; }
}

public sealed class ConfigRollbackRequestDto
{
    [JsonPropertyName("target_version")] public int TargetVersion { get; set; }
    [JsonPropertyName("expected_active_version")] public int ExpectedActiveVersion { get; set; }
}

// ---- Console inference (ADR-0012) ----

/// <summary>GET /admin/applications row. Never carries a credential.</summary>
public sealed class ApplicationSummaryDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("team")] public string Team { get; set; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("console_testing")] public bool ConsoleTesting { get; set; }
    [JsonPropertyName("granted_profiles")] public List<string> GrantedProfiles { get; set; } = [];
    [JsonPropertyName("default_profile")] public string? DefaultProfile { get; set; }
}

/// <summary>POST /admin/playground/requests body (admin token; same response contract as /v1/chat/completions).</summary>
public sealed class PlaygroundRequestDto
{
    [JsonPropertyName("application_id")] public string ApplicationId { get; set; } = "";
    [JsonPropertyName("profile_id")] public string? ProfileId { get; set; }
    [JsonPropertyName("messages")] public List<ChatMessageDto> Messages { get; set; } = [];
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("max_tokens")] public int? MaxTokens { get; set; }
    [JsonPropertyName("stream")] public bool Stream { get; set; }
    [JsonPropertyName("workflow")] public string? Workflow { get; set; }
    [JsonPropertyName("use_case")] public string? UseCase { get; set; }
    [JsonPropertyName("client_request_id")] public string? ClientRequestId { get; set; }
    /// <summary>ADR-0015: off | auto | required. Null = off.</summary>
    [JsonPropertyName("web_search")] public string? WebSearch { get; set; }
    /// <summary>ADR-0015: explicit place for weather-type questions (≤100 chars). Never inferred.</summary>
    [JsonPropertyName("location")] public string? Location { get; set; }
}

// ---- Governed web search (ADR-0015) ----

/// <summary>GET /admin/web-search: readiness and setup steps. Never carries the key.</summary>
public sealed class WebSearchStatusDto
{
    [JsonPropertyName("provider")] public string? Provider { get; set; }
    [JsonPropertyName("endpoint")] public string? Endpoint { get; set; }
    [JsonPropertyName("key_configured")] public bool KeyConfigured { get; set; }
    [JsonPropertyName("egress_approved")] public bool EgressApproved { get; set; }
    [JsonPropertyName("mode")] public string? Mode { get; set; }
    [JsonPropertyName("mode_allows_search")] public bool ModeAllowsSearch { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("price_configured")] public bool PriceConfigured { get; set; }
    [JsonPropertyName("price_per_credit_usd")] public decimal? PricePerCreditUsd { get; set; }
    [JsonPropertyName("profiles_allowed")] public List<string> ProfilesAllowed { get; set; } = [];
    [JsonPropertyName("config_version")] public int? ConfigVersion { get; set; }
    [JsonPropertyName("ready")] public bool Ready { get; set; }
    [JsonPropertyName("setup")] public List<string> Setup { get; set; } = [];
}

/// <summary>GET /admin/requests/{id}/search: what was ACTUALLY retrieved (transient, same TTL as traces). Shown
/// separately from any citations the model wrote.</summary>
public sealed class RequestSearchDto
{
    [JsonPropertyName("request_id")] public string RequestId { get; set; } = "";
    [JsonPropertyName("mode")] public string? Mode { get; set; }
    [JsonPropertyName("triggered")] public bool Triggered { get; set; }
    [JsonPropertyName("cue")] public string? Cue { get; set; }
    /// <summary>passed | refused | failed | skipped | cancelled</summary>
    [JsonPropertyName("outcome")] public string? Outcome { get; set; }
    [JsonPropertyName("reason_code")] public string? ReasonCode { get; set; }
    [JsonPropertyName("retrieved_utc")] public DateTimeOffset? RetrievedUtc { get; set; }
    [JsonPropertyName("duration_ms")] public double? DurationMs { get; set; }
    [JsonPropertyName("sources")] public List<RequestSearchSourceDto> Sources { get; set; } = [];
}

public sealed class RequestSearchSourceDto
{
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("retrieved_utc")] public DateTimeOffset? RetrievedUtc { get; set; }
}

// ---- Trace (ADR-0013) ----

public sealed class TraceEventDto
{
    [JsonPropertyName("seq")] public long Seq { get; set; }
    [JsonPropertyName("request_id")] public string? RequestId { get; set; }
    [JsonPropertyName("config_version")] public int? ConfigVersion { get; set; }
    /// <summary>auth | shape | classify | policy | cache | rate_limit | pause | budget | search | provider | attribution</summary>
    [JsonPropertyName("stage")] public string Stage { get; set; } = "";
    [JsonPropertyName("attempt_id")] public string? AttemptId { get; set; }
    /// <summary>pending | running | passed | refused | failed | cancelled | skipped</summary>
    [JsonPropertyName("state")] public string State { get; set; } = "";
    [JsonPropertyName("ts_utc")] public DateTimeOffset? TsUtc { get; set; }
    [JsonPropertyName("duration_ms")] public double? DurationMs { get; set; }
    [JsonPropertyName("reason_code")] public string? ReasonCode { get; set; }
    [JsonPropertyName("target")] public string? Target { get; set; }
    /// <summary>Provider attempt events only: the request's own max_tokens (absent when the caller sent none).</summary>
    [JsonPropertyName("requested_max_output_tokens")] public int? RequestedMaxOutputTokens { get; set; }
    /// <summary>Provider attempt events only: the bound actually sent to the provider for this attempt.</summary>
    [JsonPropertyName("effective_max_output_tokens")] public int? EffectiveMaxOutputTokens { get; set; }
}

public sealed class TraceSnapshotDto
{
    [JsonPropertyName("request_id")] public string? RequestId { get; set; }
    [JsonPropertyName("config_version")] public int? ConfigVersion { get; set; }
    [JsonPropertyName("complete")] public bool Complete { get; set; }
    /// <summary>More than the per-request bound of events was emitted; later ones were dropped by the Host.</summary>
    [JsonPropertyName("truncated")] public bool Truncated { get; set; }
    [JsonPropertyName("events")] public List<TraceEventDto> Events { get; set; } = [];
}

/// <summary>GET /admin/playground/submissions/{client_request_id} (review round 2, B7): the server request id of a
/// console submission, known BEFORE the first response byte. 404 = not registered (yet), or expired.</summary>
public sealed class PlaygroundSubmissionDto
{
    [JsonPropertyName("client_request_id")] public string ClientRequestId { get; set; } = "";
    [JsonPropertyName("request_id")] public string RequestId { get; set; } = "";
    /// <summary>running | completed</summary>
    [JsonPropertyName("state")] public string State { get; set; } = "";
}

// ---- Budgets (ADR-0014) and the policy simulator — read-only shapes ----

public sealed class BudgetStateDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("scope")] public string Scope { get; set; } = "";
    [JsonPropertyName("period")] public string? Period { get; set; }
    [JsonPropertyName("period_id")] public string? PeriodId { get; set; }
    [JsonPropertyName("period_start_utc")] public DateTimeOffset? PeriodStartUtc { get; set; }
    [JsonPropertyName("period_end_utc")] public DateTimeOffset? PeriodEndUtc { get; set; }
    [JsonPropertyName("cap_usd")] public decimal CapUsd { get; set; }
    [JsonPropertyName("spent_usd")] public decimal SpentUsd { get; set; }
    [JsonPropertyName("reserved_usd")] public decimal ReservedUsd { get; set; }
    [JsonPropertyName("remaining_usd")] public decimal RemainingUsd { get; set; }
    [JsonPropertyName("warn_at")] public List<decimal>? WarnAt { get; set; }
    /// <summary>none | warn | critical | exceeded</summary>
    [JsonPropertyName("warning")] public string? Warning { get; set; }
    [JsonPropertyName("enforcement")] public string? Enforcement { get; set; }
}

public sealed class SimulateRequestDto
{
    [JsonPropertyName("application_id")] public string ApplicationId { get; set; } = "";
    [JsonPropertyName("profile_id")] public string? ProfileId { get; set; }
    [JsonPropertyName("prompt")] public string Prompt { get; set; } = "";
    [JsonPropertyName("model")] public string? Model { get; set; }
    /// <summary>Optional unsaved candidate to simulate against (validated by the Host, never applied).</summary>
    [JsonPropertyName("config")] public CovenantConfigDto? Config { get; set; }
}

public sealed class SimulateEligibleDto
{
    [JsonPropertyName("target")] public string? Target { get; set; }
    [JsonPropertyName("boundary")] public string? Boundary { get; set; }
    [JsonPropertyName("excluded_reason")] public string? ExcludedReason { get; set; }
}

public sealed class SimulateResponseDto
{
    /// <summary>Always true from the Host: no provider call, no audit record, no spend or reservation.</summary>
    [JsonPropertyName("dry_run")] public bool DryRun { get; set; }
    [JsonPropertyName("config_version")] public int? ConfigVersion { get; set; }
    [JsonPropertyName("application_id")] public string? ApplicationId { get; set; }
    [JsonPropertyName("profile")] public string? Profile { get; set; }
    [JsonPropertyName("classification")] public string? Classification { get; set; }
    [JsonPropertyName("baseline")] public string? Baseline { get; set; }
    [JsonPropertyName("detected_signal")] public string? DetectedSignal { get; set; }
    [JsonPropertyName("policy_id")] public string? PolicyId { get; set; }
    [JsonPropertyName("eligible")] public List<SimulateEligibleDto> Eligible { get; set; } = [];
    /// <summary>route | refuse</summary>
    [JsonPropertyName("decision")] public string? Decision { get; set; }
    [JsonPropertyName("primary")] public string? Primary { get; set; }
    [JsonPropertyName("fallbacks")] public List<string> Fallbacks { get; set; } = [];
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("explanation")] public string? Explanation { get; set; }
}

// ---- Budget drill-down and audit archive (ADR-0014, spec §5) ----

/// <summary>GET /admin/budgets/{id}/requests (Host BudgetRequestsView, CovenantJsonContext).</summary>
public sealed class BudgetRequestsDto
{
    [JsonPropertyName("budget_id")] public string BudgetId { get; set; } = "";
    [JsonPropertyName("scope")] public string Scope { get; set; } = "";
    [JsonPropertyName("period_id")] public string PeriodId { get; set; } = "";
    [JsonPropertyName("items")] public List<BudgetContributionDto> Items { get; set; } = [];
}

/// <summary>One settled contribution to a budget's scope (Host BudgetContributionView), newest first.</summary>
public sealed class BudgetContributionDto
{
    [JsonPropertyName("request_id")] public string RequestId { get; set; } = "";
    [JsonPropertyName("attempt_id")] public string AttemptId { get; set; } = "";
    /// <summary>settle | settle-recovered | legacy-import.</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("amount_usd")] public decimal AmountUsd { get; set; }
    [JsonPropertyName("ts_utc")] public DateTimeOffset TsUtc { get; set; }
    /// <summary>Reported charge exceeded the reservation (settled in full, never truncated).</summary>
    [JsonPropertyName("overrun")] public bool Overrun { get; set; }
}

/// <summary>GET /admin/audit/segments row (Host SegmentView): the current segment plus archived siblings (ids only,
/// never paths). <see cref="Current"/> is always present — the console never infers which segment is current.</summary>
public sealed class AuditSegmentDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("current")] public bool Current { get; set; }
    [JsonPropertyName("entries")] public int Entries { get; set; }
    [JsonPropertyName("events")] public int Events { get; set; }
    [JsonPropertyName("first_utc")] public DateTimeOffset? FirstUtc { get; set; }
    [JsonPropertyName("last_utc")] public DateTimeOffset? LastUtc { get; set; }
    [JsonPropertyName("chain_valid")] public bool ChainValid { get; set; }
    [JsonPropertyName("archived_utc")] public DateTimeOffset? ArchivedUtc { get; set; }
}

/// <summary>GET /admin/requests/{id}/export (Host RequestExport, AuditJsonContext): one request's evidence — never prompt
/// or response content. The console downloads the server's bytes verbatim; this typed view is what it may show.</summary>
public sealed class RequestExportDto
{
    [JsonPropertyName("exported_utc")] public DateTimeOffset ExportedUtc { get; set; }
    [JsonPropertyName("request_id")] public string RequestId { get; set; } = "";
    [JsonPropertyName("segment")] public AuditSegmentDto Segment { get; set; } = new();
    [JsonPropertyName("config_version")] public int? ConfigVersion { get; set; }
    [JsonPropertyName("record")] public AuditEntryView Record { get; set; } = new();
    [JsonPropertyName("attempts")] public List<AuditAttemptView>? Attempts { get; set; }
    [JsonPropertyName("trace")] public List<AuditTraceStepView>? Trace { get; set; }
    [JsonPropertyName("integrity")] public ExportIntegrityDto Integrity { get; set; } = new();
    [JsonPropertyName("content_included")] public bool ContentIncluded { get; set; }
}

public sealed class ExportIntegrityDto
{
    [JsonPropertyName("segment_id")] public string SegmentId { get; set; } = "";
    [JsonPropertyName("chain_valid")] public bool ChainValid { get; set; }
    [JsonPropertyName("chain_failure")] public string? ChainFailure { get; set; }
    /// <summary>1-based line of the record in its segment.</summary>
    [JsonPropertyName("line")] public int? Line { get; set; }
    [JsonPropertyName("previous_hash")] public string? PreviousHash { get; set; }
    [JsonPropertyName("entry_hash")] public string? EntryHash { get; set; }
    [JsonPropertyName("anchor_configured")] public bool AnchorConfigured { get; set; }
    /// <summary>verified | unverified</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = "";
}

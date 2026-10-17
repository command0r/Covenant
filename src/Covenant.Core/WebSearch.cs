namespace Covenant.Core;

// Governed web search (ADR-0015): ports and execution facts. Pure Core types — the Governance search stage decides and
// accounts, the Host implements the Tavily client. No HTTP or JSON here.

/// <summary>One bounded search (no retries, no follow-ups). Implemented by the Host (Tavily) and by test doubles.</summary>
public interface IWebSearchClient
{
    /// <summary>Null when the client can make a call; otherwise a bounded, operator-facing reason that NOTHING will be
    /// sent (e.g. "Tavily:ApiKey is not configured", "api.tavily.com:443 is not on the startup egress allow-list").
    /// Never contains secret values.</summary>
    string? UnavailableReason { get; }

    /// <summary>Must never throw for provider/network failures — every failure is a <see cref="WebSearchResult"/> with a
    /// bounded <see cref="WebSearchOutcome"/>. Caller cancellation surfaces as <see cref="OperationCanceledException"/>.</summary>
    Task<WebSearchResult> SearchAsync(WebSearchQuery query, CancellationToken cancellationToken);
}

public sealed record WebSearchQuery(string Query, int MaxResults, TimeSpan Timeout);

public enum WebSearchOutcome { Ok, Timeout, RateLimited, HttpError, Oversized, InvalidResponse, ConnectFailed, NotConfigured }

/// <summary>Result of one search call. <paramref name="Sent"/> = the request may have reached the provider (so it may be
/// billed) — false only when nothing left Covenant (not configured, connection refused before sending).</summary>
public sealed record WebSearchResult(
    WebSearchOutcome Outcome,
    bool Sent,
    IReadOnlyList<WebSource> Sources,
    int? Credits,
    double DurationMs,
    int? HttpStatus = null)
{
    public static WebSearchResult Failed(WebSearchOutcome outcome, bool sent, double durationMs, int? status = null)
        => new(outcome, sent, Array.Empty<WebSource>(), null, durationMs, status);
}

/// <summary>A validated retrieved source: absolute http(s) URL without userinfo, bounded title/snippet, control
/// characters removed. Snippets are UNTRUSTED DATA (never instructions).</summary>
public sealed record WebSource(string Title, string Url, string Snippet, DateTimeOffset RetrievedUtc);

/// <summary>The search facts of one request (set by the search stage). Sources are kept for the console's transient
/// request store; evidence records only bounded metadata (<see cref="AuditWebSearch"/>).</summary>
public sealed class WebSearchState
{
    public required WebSearchMode Mode { get; init; }
    public bool Triggered { get; set; }
    /// <summary>The freshness cue that triggered Auto (bounded vocabulary), "required" for Required, null when not triggered.</summary>
    public string? Cue { get; set; }
    /// <summary>passed | refused | failed | skipped (+ reason code in <see cref="ReasonCode"/>).</summary>
    public string Outcome { get; set; } = TraceStates.Pending;
    public string? ReasonCode { get; set; }
    public IReadOnlyList<WebSource> Sources { get; set; } = Array.Empty<WebSource>();
    public double? DurationMs { get; set; }
    public DateTimeOffset? RetrievedUtc { get; set; }

    public AuditWebSearch ToAudit() => new(
        Mode.ToString().ToLowerInvariant(), Triggered, Cue, Outcome, ReasonCode,
        Sources.Count == 0 ? null : Sources.Count,
        Sources.Count == 0 ? null : Sources.Select(s => Uri.TryCreate(s.Url, UriKind.Absolute, out var u) ? u.Host : "").Where(h => h.Length > 0).Distinct().Take(3).ToList(),
        DurationMs is { } d ? Math.Round(d, 3) : null);
}

/// <summary>A non-inference tool call charged to the request (ADR-0015): today only "web_search". Separate from provider
/// attempts so tool and inference evidence never mix.</summary>
public sealed class ToolCallRecord
{
    public required string ToolCallId { get; init; }
    public required string Tool { get; init; }
    public required string Provider { get; init; }
    /// <summary>running | succeeded | failed | cancelled | released (refused before sending).</summary>
    public string Outcome { get; set; } = TraceStates.Running;
    public string? ErrorClass { get; set; }
    public double DurationMs { get; set; }
    public decimal? CostUsd { get; set; }
    public string? CostBasis { get; set; }
    public decimal? ReservedUsd { get; set; }
    public int? Credits { get; set; }

    public AuditToolCall ToAudit() => new(ToolCallId, Tool, Provider, Outcome, ErrorClass, Math.Round(DurationMs, 3), CostUsd, CostBasis, ReservedUsd, Credits);
}

/// <summary>Bounded web-search metadata in an audit v3 record. No query, no location, no snippets.</summary>
public sealed record AuditWebSearch(
    string Mode,
    bool Triggered,
    string? Cue,
    string Outcome,
    string? ReasonCode,
    int? SourceCount,
    IReadOnlyList<string>? SourceHosts,
    double? DurationMs);

public sealed record AuditToolCall(
    string ToolCallId,
    string Tool,
    string Provider,
    string Outcome,
    string? ErrorClass,
    double DurationMs,
    decimal? CostUsd,
    string? CostBasis,
    decimal? ReservedUsd,
    int? Credits);

/// <summary>Budget accounting for tool calls on the same ledger and scopes as provider attempts (ADR-0014/0015).
/// Implemented by the budget stage's per-request gate. Reserve before the call, settle exactly once after it was sent,
/// release when it never was.</summary>
public interface IToolCallGate
{
    AttemptAdmission ReserveTool(InferenceContext context, string toolCallId, decimal amountUsd);

    /// <summary><paramref name="costUsd"/> = priced reported usage; null ⇒ the reservation (conservative estimate).</summary>
    AttemptCharge SettleTool(InferenceContext context, string toolCallId, decimal? costUsd);

    void ReleaseTool(InferenceContext context, string toolCallId);
}

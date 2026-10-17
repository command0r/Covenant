using Covenant.Ui.Models;

namespace Covenant.Ui.Logic;

// Pure view-model logic (no Blazor, no JS, no HTTP): compiled into the console AND linked into
// tests/Covenant.Tests so every branch is unit-tested without a browser.

/// <summary>What the operator is told happened. Derived from observed facts only: the transport (HTTP status,
/// stream termination, client cancellation) until the audit record arrives, then the record.</summary>
public enum DisplayOutcome
{
    Served,
    ServedFromCache,
    PolicyDenied,
    Paused,
    AuthenticationFailed,
    RateLimited,
    ProviderUnavailable,
    ProviderFailedMidStream,
    Unsupported,
    RejectedBeforeGovernance,
    Cancelled,
    Unknown,
    /// <summary>409 duplicate_submission: the Host already has a request with this client_request_id and did
    /// NOT run it again (a reload or double-click never replays inference).</summary>
    DuplicateSubmission,
}

/// <summary>Everything the client observed about one request. <see cref="Record"/> is the audit record once it
/// has been found (null before that, or for a request that never reached the pipeline).
/// <see cref="HasPartialText"/>: some response text had arrived before a cancel or failure (it is kept).</summary>
public sealed record OutcomeInput(
    int? HttpStatus = null,
    string? ErrorCode = null,
    bool StreamTerminatedWithError = false,
    bool Cancelled = false,
    AuditEntryView? Record = null,
    bool HasPartialText = false);

/// <param name="Outcome">Display outcome.</param>
/// <param name="Title">Large outcome text.</param>
/// <param name="Explanation">Plain-language reason.</param>
/// <param name="StatusLine">Transport line, e.g. "HTTP 403" or "Transport 200, final outcome: failed".</param>
/// <param name="TransportDiffers">HTTP 200 was delivered but the final outcome is not a success.</param>
/// <param name="PillKind">StatusPill kind: served | refused | failed | paused | unknown.</param>
/// <param name="AwaitingRecord">The stage that decided this is not known until the audit record arrives.</param>
public sealed record OutcomeView(
    DisplayOutcome Outcome,
    string Title,
    string Explanation,
    string StatusLine,
    bool TransportDiffers,
    string PillKind,
    bool AwaitingRecord);

public static class OutcomeModel
{
    public const string CancelledExplanation =
        "Stopped waiting — the request may have completed or been charged; this is not a rollback.";

    public const string PartialTitle = "Cancelled — partial response";

    public const string PartialCancelExplanation =
        "Cancelled — partial response; the provider may have charged for work done. The text received so far is kept; this is not a rollback.";

    private static string CancelTitle(OutcomeInput i) => i.HasPartialText ? PartialTitle : "Cancelled — no response received";

    private static string CancelText(OutcomeInput i) => i.HasPartialText ? PartialCancelExplanation : CancelledExplanation;

    public static OutcomeView FromRecord(AuditEntryView record) => Compute(new OutcomeInput(Record: record));

    public static OutcomeView Compute(OutcomeInput input)
    {
        var statusLine = StatusLineFor(input);
        if (input.Record is { } r)
            return FromEvidence(input, r, statusLine);
        return FromTransport(input, statusLine);
    }

    /// <summary>Short label for filters and table cells.</summary>
    public static string Label(DisplayOutcome o) => o switch
    {
        DisplayOutcome.Served => "Served",
        DisplayOutcome.ServedFromCache => "Served from cache",
        DisplayOutcome.PolicyDenied => "Refused by governance",
        DisplayOutcome.Paused => "Paused",
        DisplayOutcome.AuthenticationFailed => "Authentication failed",
        DisplayOutcome.RateLimited => "Rate limited",
        DisplayOutcome.ProviderUnavailable => "Provider unavailable",
        DisplayOutcome.ProviderFailedMidStream => "Failed mid-stream",
        DisplayOutcome.Unsupported => "Unsupported request",
        DisplayOutcome.RejectedBeforeGovernance => "Rejected before governance",
        DisplayOutcome.Cancelled => "Cancelled / incomplete",
        DisplayOutcome.DuplicateSubmission => "Duplicate submission (not re-run)",
        _ => "Unknown",
    };

    public static string PillFor(DisplayOutcome o) => o switch
    {
        DisplayOutcome.Served or DisplayOutcome.ServedFromCache => "served",
        DisplayOutcome.Paused => "paused",
        DisplayOutcome.ProviderUnavailable or DisplayOutcome.ProviderFailedMidStream => "failed",
        DisplayOutcome.PolicyDenied or DisplayOutcome.AuthenticationFailed or DisplayOutcome.RateLimited
            or DisplayOutcome.Unsupported or DisplayOutcome.RejectedBeforeGovernance => "refused",
        _ => "unknown",
    };

    private static bool IsSuccess(DisplayOutcome o) => o is DisplayOutcome.Served or DisplayOutcome.ServedFromCache;

    private static string StatusLineFor(OutcomeInput input)
    {
        if (input.Cancelled && input.HttpStatus is null) return "No HTTP status — stopped waiting";
        if (input.HttpStatus is null) return "No HTTP response";
        if (input.HttpStatus == 200 && input.StreamTerminatedWithError) return "Transport 200, final outcome: failed";
        return "HTTP " + input.HttpStatus.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static OutcomeView Make(OutcomeInput input, DisplayOutcome o, string title, string explanation,
        string statusLine, bool awaiting = false, string? pill = null)
    {
        bool differs = input.HttpStatus == 200 && !IsSuccess(o) && o != DisplayOutcome.Unknown;
        if (differs && statusLine == "HTTP 200")
            statusLine = "Transport 200, final outcome: " + (o == DisplayOutcome.Cancelled ? "incomplete" : "failed");
        return new OutcomeView(o, title, explanation, statusLine, differs, pill ?? PillFor(o), awaiting);
    }

    // ---- Before the audit record: only what the transport showed. Never guesses the deciding stage. ----
    private static OutcomeView FromTransport(OutcomeInput input, string statusLine)
    {
        if (input.Cancelled)
            return Make(input, DisplayOutcome.Cancelled, CancelTitle(input), CancelText(input), statusLine);

        if (input.StreamTerminatedWithError)
            return Make(input, DisplayOutcome.ProviderFailedMidStream, "Provider failed mid-stream",
                "The stream started (HTTP 200 was already sent) and then the provider failed. Any text shown is partial. " +
                "The audit record carries the final outcome.", statusLine);

        switch (input.HttpStatus)
        {
            case null:
                return Make(input, DisplayOutcome.Unknown, "No response",
                    "The console could not reach Covenant (network error). Nothing is known about this request.", statusLine);
            case 200:
                return Make(input, DisplayOutcome.Served, "Served",
                    "Covenant returned a response. Awaiting the audit record for the model, usage and cost facts.",
                    statusLine, awaiting: true);
            case 401:
                return Make(input, DisplayOutcome.AuthenticationFailed, "Authentication failed",
                    "The credential was not accepted. The request stopped at authentication: classification was not evaluated.",
                    statusLine);
            case 404:
                return Make(input, DisplayOutcome.Unknown, "Console inference not available",
                    "This Host does not expose POST /admin/playground/requests (an older appliance version). Nothing was run.",
                    statusLine, pill: "unknown");
            case 409:
                return Make(input, DisplayOutcome.DuplicateSubmission, "Already submitted — not run again",
                    "Covenant already received a request with this client request id and did not run it a second time. The original request id is shown below.",
                    statusLine, pill: "unknown");
            case 429:
                return Make(input, DisplayOutcome.RateLimited, "Rate limited",
                    "A configured request-rate limit refused this request before any provider call.", statusLine);
            case 400 when string.Equals(input.ErrorCode, "unsupported", StringComparison.Ordinal):
                return Make(input, DisplayOutcome.Unsupported, "Unsupported request",
                    "The request shape is one governance cannot inspect (for example images or tools) and was refused.", statusLine);
            case 400:
                return Make(input, DisplayOutcome.RejectedBeforeGovernance, "Rejected before governance",
                    "Rejected before governance (400); no evidence record. The body could not be read, so the request never reached the pipeline.",
                    statusLine);
            case 403:
                return Make(input, DisplayOutcome.Unknown, "Refused (HTTP 403) — awaiting evidence for the stage",
                    "Covenant refused this request. Which stage refused it (application authorization, policy, pause, budget or provider registration) is known only from the audit record.",
                    statusLine, awaiting: true, pill: "refused");
            case 499:
                return Make(input, DisplayOutcome.Cancelled, CancelTitle(input), CancelText(input), statusLine);
            case 502:
                return Make(input, DisplayOutcome.ProviderUnavailable, "Provider unavailable",
                    "The provider call failed or the provider could not be reached, and no approved fallback served it. Covenant refused fail-closed. The audit record lists each attempt.",
                    statusLine);
            default:
                return Make(input, DisplayOutcome.Unknown, "Unexpected response",
                    "Covenant answered with a status this console does not interpret. See the audit record.", statusLine, awaiting: true);
        }
    }

    // ---- From the audit record: the recorded facts decide. ----
    private static OutcomeView FromEvidence(OutcomeInput input, AuditEntryView r, string statusLine)
    {
        if (string.Equals(r.Effect, "Allow", StringComparison.OrdinalIgnoreCase))
        {
            string tail = input.Cancelled ? " You stopped waiting, but Covenant completed the request." : "";
            if (r.CacheHit)
                return Make(input, DisplayOutcome.ServedFromCache, "Served from cache",
                    "An identical earlier request's response was reused. No provider call was made, so the pause control does not apply to it." + tail,
                    statusLine);
            var fallback = AttemptFacts.FallbackSentence(r);
            return Make(input, DisplayOutcome.Served, fallback is null ? "Served" : "Served by approved fallback",
                "Allowed by policy and answered by the provider." + (fallback is null ? "" : " " + fallback) + tail, statusLine);
        }

        if (!string.Equals(r.Effect, "Deny", StringComparison.OrdinalIgnoreCase))
            return Make(input, DisplayOutcome.Unknown, "Unknown outcome", "The audit record has an unrecognised effect: " + r.Effect, statusLine);

        var reason = string.IsNullOrWhiteSpace(r.Reason) ? "No reason recorded." : r.Reason;
        switch (r.DenialKind)
        {
            case "Unauthenticated":
                return Make(input, DisplayOutcome.AuthenticationFailed, "Authentication failed",
                    "Stopped at authentication: " + reason + ". Classification was not evaluated.", statusLine);
            case "RateLimited":
                return Make(input, DisplayOutcome.RateLimited, "Rate limited", reason, statusLine);
            case "Unsupported":
                return Make(input, DisplayOutcome.Unsupported, "Unsupported request", reason, statusLine);
            case "Cancelled":
                return Make(input, DisplayOutcome.Cancelled, CancelTitle(input),
                    CancelText(input) + " Recorded: " + reason, statusLine);
            case "UpstreamFailure":
                bool midStream = input.StreamTerminatedWithError || input.HttpStatus == 200;
                var attempts = AttemptFacts.CountSentence(r);
                return midStream
                    ? Make(input, DisplayOutcome.ProviderFailedMidStream, "Provider failed mid-stream",
                        "HTTP 200 was already sent when the provider failed: " + reason + ". Any text shown is partial; no other provider continued the answer.", statusLine)
                    : Make(input, DisplayOutcome.ProviderUnavailable, "Provider call failed",
                        reason + ". Covenant refused fail-closed." + (attempts is null ? "" : " " + attempts), statusLine);
        }

        // Governance (or a legacy record without DenialKind): the recorded stage decides — never the reason text.
        switch (r.StoppedAtStage)
        {
            case "pause":
                return Make(input, DisplayOutcome.Paused, "Paused — new provider calls refused",
                    "The operator pause is on, so this request was refused before any provider call. " + reason, statusLine);
            case "provider" when r.AttemptedModel is null:
                return Make(input, DisplayOutcome.ProviderUnavailable, "Refused — no registered adapter",
                    "Policy chose a route but no adapter serves it, so no provider call was made (fail-closed). " + reason,
                    statusLine);
            case "budget":
                return Make(input, DisplayOutcome.PolicyDenied, "Refused — budget exhausted", reason, statusLine);
            case "policy":
                return Make(input, DisplayOutcome.PolicyDenied, "Policy denied", reason, statusLine);
            case null when r.DenialKind is null:
                return Make(input, DisplayOutcome.Unknown, "Refused (legacy record)",
                    "This record predates outcome metadata; the refusing stage was not recorded. Reason: " + reason, statusLine,
                    pill: "refused");
            default:
                return Make(input, DisplayOutcome.PolicyDenied, "Refused by governance",
                    "Stopped at " + PipelineStages.Label(r.StoppedAtStage) + ": " + reason, statusLine);
        }
    }
}

/// <summary>The configured processing order (Program.cs pipeline assembly). This is an ordering, not a measured
/// trace: the console highlights the stage recorded in StoppedAtStage and nothing else.</summary>
public static class PipelineStages
{
    public sealed record Stage(string Key, string Name, string Detail);

    public static readonly IReadOnlyList<Stage> Configured =
    [
        new("auth", "Authentication", "Application key → identity and team"),
        new("shape", "Input validation", "Refuses shapes governance cannot inspect"),
        new("classify", "Classification", "Pattern classifier sets the data class"),
        new("policy", "Policy and route selection", "Classification → permitted routes"),
        new("cache", "Cache", "A hit skips every later stage"),
        new("rate_limit", "Rate limit", "Per-team and appliance request caps"),
        new("pause_budget", "Operator pause and budget", "Pause new provider calls; spend caps"),
        new("provider", "Provider", "The one model call"),
        new("attribution", "Cost attribution", "Usage × configured prices"),
    ];

    /// <summary>Maps a recorded StoppedAtStage value onto the row of <see cref="Configured"/>.</summary>
    public static string? RowKey(string? stoppedAtStage) => stoppedAtStage switch
    {
        null => null,
        "pause" or "budget" => "pause_budget",
        _ => stoppedAtStage,
    };

    public static string Label(string? stage) => stage switch
    {
        null => "an unrecorded stage",
        "auth" => "authentication",
        "shape" => "input validation",
        "classify" => "classification",
        "policy" => "policy",
        "cache" => "cache",
        "rate_limit" => "rate limit",
        "pause" => "the operator pause",
        "budget" => "budget",
        "provider" => "provider",
        "attribution" => "cost attribution",
        "audit" => "audit",
        _ => stage,
    };
}

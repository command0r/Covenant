using Covenant.Ui.Logic;

namespace Covenant.Ui.Services;

/// <summary>Display text for one Playground run, derived only from its own snapshot, transport result and audit
/// record (shared by the outcome line, the details drawer and presentation mode).</summary>
public static class RunText
{
    public static string AwaitingText(PlaygroundRun run) => run.InFlight ? "Awaiting response" : run.Evidence.State switch
    {
        EvidenceState.Pending => "Awaiting audit record…",
        EvidenceState.Unavailable => "Not available — no audit record found yet",
        EvidenceState.Error => "Not available — evidence lookup failed",
        _ => run.RequestId is null ? "Not available — no request id received" : "Not available — no audit record",
    };

    public static string Duration(PlaygroundRun run)
    {
        if (run.Evidence.Record is { } rec) return Fmt.Ms(rec.DurationMs) + " (recorded)";
        if (run.InFlight) return "In progress";
        return run.Result is { } r ? Fmt.Ms(r.ClientMs) + " (client-measured)" : CostText.NotReported;
    }

    public static string Tokens(PlaygroundRun run)
        => run.Evidence.Record is { } rec ? CostText.Tokens(rec) : AwaitingText(run);

    public static string Cost(PlaygroundRun run)
        => run.Evidence.Record is { } rec ? CostText.Cost(rec) : AwaitingText(run);

    /// <summary>Pending → Verified (record found) / Unavailable after the bounded retries. Never "verified" before
    /// the record exists.</summary>
    public static (string Text, string Tone) Evidence(PlaygroundRun run)
    {
        if (run.InFlight) return ("Evidence after completion", "hollow");
        return run.Evidence.State switch
        {
            EvidenceState.Pending => ("Evidence pending", "hollow"),
            EvidenceState.Found => ("Evidence verified — record found", "ok"),
            EvidenceState.Unavailable => ("Evidence unavailable", "warn"),
            EvidenceState.Error => ("Evidence lookup failed", "bad"),
            _ => run.Outcome?.Outcome == DisplayOutcome.RejectedBeforeGovernance
                ? ("No evidence record — rejected before governance", "hollow")
                : run.RequestId is null ? ("No request id — no exact record", "hollow") : ("Evidence not looked up", "hollow"),
        };
    }

    /// <summary>One sentence for the polite live region: changes only when the run's state changes.</summary>
    public static string Announcement(PlaygroundRun? run)
    {
        if (run is null) return "";
        var n = "Request " + run.Snapshot.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (run.InFlight) return n + " running.";
        return run.Outcome is { } o ? n + ": " + o.Title + "." : n + " finished.";
    }
}

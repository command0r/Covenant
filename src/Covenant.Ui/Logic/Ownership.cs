using System.Globalization;
using Covenant.Ui.Models;

namespace Covenant.Ui.Logic;

/// <summary>Who requested, who pays and what was finally charged — from the audit record, in two honest phases:
/// identity and cost owner are ASSIGNED before any provider call (auth/profile/policy), and the charge is FINALIZED
/// after execution (usage priced, budgets settled, record written). A refused request still has its application,
/// team and workload; it just has no charge.</summary>
public static class Ownership
{
    public const string NotRecorded = "Not recorded";

    /// <summary>"demo-app · team platform · workload public-writing · policy public · config v3".</summary>
    public static string Assigned(AuditEntryView r)
    {
        var parts = new List<string>
        {
            "application " + (r.ApplicationId ?? NotRecorded),
            "team " + (string.IsNullOrWhiteSpace(r.Tags.Team) ? NotRecorded : r.Tags.Team),
            "workload " + (r.ProfileId ?? NotRecorded),
        };
        parts.Add(r.PolicyId is { Length: > 0 } p ? "policy " + p : r.Effect == "Deny" && r.StoppedAtStage is "auth" or "shape" or "classify" or "policy" ? "no policy decided (refused at " + r.StoppedAtStage + ")" : "policy " + NotRecorded);
        if (r.ConfigVersion is { } v) parts.Add("config v" + v.ToString(CultureInfo.InvariantCulture));
        return string.Join(" · ", parts);
    }

    /// <summary>The finalized charge with its basis, or why there is none.</summary>
    public static string Finalized(AuditEntryView r)
    {
        bool anyCall = (r.Attempts?.Count ?? 0) > 0 || (r.ToolCalls?.Count ?? 0) > 0;
        if (r.CacheHit) return "$0.00 — answered from the exact cache (no provider call)";
        if (!anyCall && ModelFacts.IsV2(r))
            return "No charge — " + (r.StoppedAtStage is { Length: > 0 } st ? "refused at " + TraceModel.Label(st) + " before any provider call" : "no provider call") + "; recorded with its application, team and workload";
        var cost = CostText.Cost(r);
        var parts = new List<string> { cost };
        if (r.ToolCalls is { Count: > 0 } tools)
            parts.Add("includes " + tools.Count.ToString(CultureInfo.InvariantCulture) + (tools.Count == 1 ? " tool call" : " tool calls") + " (" + string.Join(", ", tools.Select(t => t.Tool)) + ")");
        if (r.ReservedUsd is { } res) parts.Add("reserved " + CostText.Usd(res) + ", settled " + (r.SettledUsd is { } s ? CostText.Usd(s) : NotRecorded));
        return string.Join(" · ", parts);
    }

    /// <summary>Plain words for the recorded cache outcome.</summary>
    public static string Cache(AuditEntryView r) => r.CacheOutcome switch
    {
        "hit" => "Hit — served from the exact cache",
        "miss" => "Miss — looked up, then called the provider",
        "bypass_web_search" => "Bypassed — web-search requests never use the cache",
        "disabled" => "Off — the exact cache is disabled",
        "not_reached" => "Not reached — refused before the cache stage",
        null => r.CacheHit ? "Hit" : NotRecorded,
        _ => r.CacheOutcome,
    };
}

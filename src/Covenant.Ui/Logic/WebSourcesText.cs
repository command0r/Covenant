using System.Globalization;
using Covenant.Ui.Models;

namespace Covenant.Ui.Logic;

/// <summary>Playground "Web sources" control (ADR-0015). Off = never search; Auto = the Host searches only on a
/// freshness cue (weather, latest, today, news…); Required = always search (the request fails if search cannot run).</summary>
public enum WebSourcesMode { Off, Auto, Required }

/// <summary>Pure text and safety rules for governed web search in the console. The Host decides and enforces; this only
/// says what happened, in words, and never renders a retrieved URL it would not itself accept.</summary>
public static class WebSourcesText
{
    public const int MaxLocationChars = 100;
    public const int MaxUrlChars = 500;

    /// <summary>Wire value of <c>web_search</c>; null for Off (the field is omitted, identical to a request without it).</summary>
    public static string? Wire(WebSourcesMode mode) => mode switch
    {
        WebSourcesMode.Auto => "auto",
        WebSourcesMode.Required => "required",
        _ => null,
    };

    public static WebSourcesMode Parse(string? wire) => (wire ?? "").Trim().ToLowerInvariant() switch
    {
        "auto" => WebSourcesMode.Auto,
        "required" => WebSourcesMode.Required,
        _ => WebSourcesMode.Off,
    };

    public static string Label(WebSourcesMode mode) => mode switch
    {
        WebSourcesMode.Auto => "Auto",
        WebSourcesMode.Required => "Required",
        _ => "Off",
    };

    public static string Help(WebSourcesMode mode) => mode switch
    {
        WebSourcesMode.Auto => "Searches for current information; otherwise skips search.",
        WebSourcesMode.Required => "Searches first. Stops if search fails or finds no sources.",
        _ => "Uses model knowledge only; it may be out of date.",
    };

    public const string LocationHelp =
        "City for weather, unless named in the prompt. Never inferred.";

    /// <summary>Form-level problem for the location field, or null.</summary>
    public static string? LocationProblem(string? location)
        => location is { Length: > MaxLocationChars } ? "Location must be at most " + MaxLocationChars.ToString(CultureInfo.InvariantCulture) + " characters." : null;

    /// <summary>A retrieved link the console may render as a hyperlink: absolute http(s), no userinfo, bounded. Anything
    /// else (javascript:, data:, relative, credentials) is shown as plain text — never as a link.</summary>
    public static string? SafeHref(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > MaxUrlChars) return null;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)) return null;
        if (u.Scheme is not ("http" or "https")) return null;
        if (!string.IsNullOrEmpty(u.UserInfo) || string.IsNullOrEmpty(u.Host)) return null;
        return u.AbsoluteUri;
    }

    public static string Host(string? url) => SafeHref(url) is { } h && Uri.TryCreate(h, UriKind.Absolute, out var u) ? u.Host : "";

    /// <summary>Plain words for the Host's search reason codes.</summary>
    public static string Reason(string? code) => code switch
    {
        "search_off" => "Web sources were off.",
        "search_not_triggered" => "Auto: the prompt did not ask for current information, so no search was made.",
        "location_required" => "A weather question needs a location. Add one in the Location field (or name the place in the prompt) and run again. Nothing was sent externally.",
        "search_not_permitted" => "Web search is not permitted for this request (disabled, not Hybrid, profile not allowed, no price or key). Nothing was sent externally.",
        "search_sensitive" => "Refused: only Public requests may be searched, and the query or location must not contain sensitive data. Nothing was sent externally.",
        "search_failed" => "The search failed. No answer was given without sources.",
        "search_timeout" => "The search timed out. No answer was given without sources.",
        "search_rate_limited" => "Tavily rate-limited the search (429). No answer was given without sources.",
        "search_empty" => "The search returned no usable sources. No answer was given without evidence.",
        "budget_exceeded" => "The search would exceed a budget, so it was not made.",
        "client_cancelled" => "Cancelled during the search; the reserved search charge was recorded.",
        null or "" => "",
        _ => "Search outcome: " + code,
    };

    /// <summary>One status line for a run's web sources, from what the Host actually recorded (never from the form).</summary>
    public static string Status(WebSourcesMode requested, RequestSearchDto? search, int? lookupStatus)
    {
        if (requested == WebSourcesMode.Off) return "Web sources: off.";
        if (search is null)
            return lookupStatus switch
            {
                null => "Web sources: checking what was retrieved…",
                404 => "Web sources: no search record (the request stopped before the search stage, or the record expired).",
                _ => "Web sources: search details unavailable (HTTP " + lookupStatus.Value.ToString(CultureInfo.InvariantCulture) + ").",
            };
        return search.Outcome switch
        {
            "passed" => "Searched: " + Count(search.Sources.Count) + " retrieved"
                        + (search.RetrievedUtc is { } t ? " at " + t.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) : "")
                        + (search.Cue is { Length: > 0 } c && c != "required" ? " (cue: " + c + ")" : "") + ".",
            "skipped" => Reason(search.ReasonCode),
            _ => Reason(search.ReasonCode) is { Length: > 0 } r ? r : "Web search " + (search.Outcome ?? "outcome unknown") + ".",
        };
    }

    public static string Count(int n) => n.ToString(CultureInfo.InvariantCulture) + (n == 1 ? " source" : " sources");

    /// <summary>Readiness checklist of GET /admin/web-search, in the order an operator fixes it.</summary>
    public static IReadOnlyList<(string Label, bool Ok)> Checklist(WebSearchStatusDto s) =>
    [
        ("API key Tavily:ApiKey configured on the server", s.KeyConfigured),
        ("api.tavily.com:443 on the startup egress allow-list", s.EgressApproved),
        ("Hybrid mode (Local-only makes no external calls)", s.ModeAllowsSearch),
        ("Price per credit configured", s.PriceConfigured),
        ("Web search enabled in configuration", s.Enabled),
        ("At least one Public profile allowed to search", s.ProfilesAllowed.Count > 0),
    ];
}

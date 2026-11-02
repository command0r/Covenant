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
        "search_not_permitted" => "Web search is not set up for this request; the server's reason is below. Nothing was sent externally.",
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

    /// <summary>Which kind of problem a search reason code is — so the console never presents a setup gap as a provider
    /// failure, or a missing city as a policy refusal.</summary>
    public static SearchProblemKind Kind(string? code) => code switch
    {
        "location_required" => SearchProblemKind.MissingLocation,
        "search_not_permitted" => SearchProblemKind.Setup,
        "search_sensitive" or "budget_exceeded" => SearchProblemKind.PolicyRefusal,
        "search_failed" or "search_timeout" or "search_rate_limited" or "search_empty" => SearchProblemKind.ProviderFailure,
        _ => SearchProblemKind.None,
    };

    public static string KindLabel(SearchProblemKind kind) => kind switch
    {
        SearchProblemKind.Setup => "Setup",
        SearchProblemKind.MissingLocation => "Needs a location",
        SearchProblemKind.PolicyRefusal => "Refused by policy",
        SearchProblemKind.ProviderFailure => "Search provider failed",
        _ => "",
    };

    /// <summary>Can the CURRENT composer selection search, according to the applied configuration and the Host's
    /// readiness report? Returns ONE cause — the most fundamental one — with one fix. Never changes the form: a profile
    /// switch is only offered (<see cref="SearchReadiness.SwitchToProfile"/>), never applied. The Host's runtime check stays
    /// authoritative and fail-closed; this only avoids offering a control that is guaranteed to be refused.</summary>
    public static SearchReadiness Readiness(WebSearchStatusDto? status, string? statusError, CovenantConfigDto? config,
        ApplicationSummaryDto? app, string? profileId)
    {
        if (status is null)
            return SearchReadiness.Unknown(statusError ?? "Web search status has not been loaded yet.");
        if (!status.KeyConfigured)
            return SearchReadiness.Blocked("The server has no Tavily API key.",
                "Set Tavily__ApiKey (or Tavily:ApiKey in the server's secret store) and restart Covenant. The key is never entered in the console.",
                "settings#ws-h", "Web search settings");
        if (!status.EgressApproved)
            return SearchReadiness.Blocked("api.tavily.com:443 is not on the startup egress allow-list.",
                "Add api.tavily.com:443 to Providers:ProbeAllowList and restart Covenant. The console cannot widen egress.",
                "settings#ws-h", "Web search settings");
        if (!status.ModeAllowsSearch)
            return SearchReadiness.Blocked("The configuration is Local-only, which makes no external calls.",
                "Switch the mode to Hybrid on the Policies page to allow approved external calls.", "policies", "Policies → mode");
        if (!status.Enabled)
            return SearchReadiness.Blocked("Web search is disabled in the active configuration.",
                "Enable it under Settings → Web search, then Save & apply.", "settings#ws-h", "Enable web search");
        if (!status.PriceConfigured)
            return SearchReadiness.Blocked("No price per Tavily credit is configured (an unknown price is never $0).",
                "Enter the price from your Tavily plan under Settings → Web search.", "settings#ws-h", "Set the price");
        var profile = config is null || profileId is null ? null : config.Profiles.FirstOrDefault(p => p.Id == profileId);
        bool allowed = profileId is not null && (profile?.WebSearchAllowed ?? status.ProfilesAllowed.Contains(profileId, StringComparer.Ordinal));
        if (!allowed)
        {
            var alternative = app is null ? null
                : ComposerRules.GrantedProfiles(app).FirstOrDefault(g => status.ProfilesAllowed.Contains(g, StringComparer.Ordinal));
            var name = profile?.Name ?? profileId ?? "the selected profile";
            return alternative is not null
                ? SearchReadiness.Blocked("Workload profile '" + name + "' is not allowed to use web search.",
                    "Switch to an allowed Public profile, or allow this one under Policies → Applications & workload profiles.",
                    "policies#applications", "Profile permissions", alternative)
                : SearchReadiness.Blocked("No workload profile granted to this application may use web search.",
                    "Allow web search on a Public-baseline profile and grant it to this application.",
                    "policies#applications", "Profile permissions");
        }
        if (profile is not null && !string.Equals(profile.Baseline, "Public", StringComparison.Ordinal))
            return SearchReadiness.Blocked("Only Public requests may be searched; this profile's baseline is " + profile.Baseline + ".",
                "Use a Public-baseline profile for research with web sources.", "policies#applications", "Profile permissions");
        return SearchReadiness.Ok;
    }

    /// <summary>The readiness to SHOW for the configuration shown. A failed fetch shows no readiness (never an old Ready);
    /// readiness computed for a different configuration version than the one shown is discarded (Mismatch ⇒ refetch).</summary>
    public static (WebSearchStatusDto? Status, string? Error, bool Mismatch) Reconcile(int? shownConfigVersion, WebSearchStatusDto? fetched, string? fetchError)
    {
        if (fetched is null) return (null, fetchError ?? "Web search status is not available.", false);
        if (shownConfigVersion is { } cv && fetched.ConfigVersion is { } rv && rv != cv)
            return (null, "Web search readiness was reported for configuration v" + rv.ToString(CultureInfo.InvariantCulture)
                          + " while v" + cv.ToString(CultureInfo.InvariantCulture) + " is shown — refreshing.", true);
        return (fetched, fetchError, false);
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

public enum SearchProblemKind { None, Setup, MissingLocation, PolicyRefusal, ProviderFailure }

/// <summary>One readiness verdict for the composer. <see cref="Possible"/> false ⇒ Auto/Required are not offered as if
/// they would work; the cause and its single fix are shown instead. <see cref="Known"/> false ⇒ the status could not be
/// read; the controls stay usable and the Host decides.</summary>
public sealed record SearchReadiness(bool Possible, bool Known, string? Cause, string? Fix, string? Href, string? HrefLabel, string? SwitchToProfile)
{
    public static readonly SearchReadiness Ok = new(true, true, null, null, null, null, null);
    public static SearchReadiness Unknown(string why) => new(true, false, why, "The Host still checks every search request.", null, null, null);
    public static SearchReadiness Blocked(string cause, string fix, string href, string hrefLabel, string? switchTo = null)
        => new(false, true, cause, fix, href, hrefLabel, switchTo);
}

using System.Globalization;

namespace Covenant.Ui.Logic;

/// <summary>Header mode badge. <see cref="Tone"/> is a CSS modifier: local | hybrid | legacy | unknown.</summary>
public sealed record ModeBadge(string Label, string Tone, string Description);

/// <summary>Header freshness: "Live" while fresh; "Stale · 12 s" only when stale. <see cref="Title"/> carries the
/// exact last-update time for the tooltip/details. <see cref="Tone"/>: ok | warn | bad.</summary>
public sealed record FreshnessView(string Label, string Tone, string Title, bool Stale);

/// <summary>Pure header logic (mode badge, live/stale label, the one polite announcement).</summary>
public static class ShellStatus
{
    public const string ModeNotReported = "Mode not reported";

    public const string LocalOnly = "local-only";
    public const string Hybrid = "hybrid";
    public const string LegacyCloud = "legacy-cloud";

    /// <summary>Maps the status/catalog/config <c>mode</c> value onto the badge. Exactly the contract spellings
    /// (docs/ui-api-contract.md "Wire spellings"): local-only | hybrid | legacy-cloud. Anything else — including null
    /// — is "Mode not reported", never a guess.</summary>
    public static ModeBadge Mode(string? mode) => mode switch
    {
        LocalOnly => new("Local-only", "local",
            "Only in-perimeter (local) connections can execute; cloud configuration cannot run."),
        Hybrid => new("Hybrid", "hybrid",
            "Local and approved cloud routes; PII/PHI never route to cloud destinations."),
        LegacyCloud => new("Legacy cloud", "legacy",
            "Imported pre-v2 routing (cloud default). Review the effective rules before relying on it."),
        _ => new(ModeNotReported, "unknown", "The Host did not report a provider mode."),
    };

    /// <summary>Freshness label for the header. Never a ticking seconds counter while live.</summary>
    public static FreshnessView Freshness(bool connected, bool stale, DateTimeOffset? lastUpdateUtc, DateTimeOffset nowUtc, string? feedError)
    {
        if (!connected)
            return new("Disconnected", "bad", "The console is not connected to the Host.", false);

        var exact = lastUpdateUtc is { } t
            ? "Last status update " + t.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC"
            : "No status update received yet";
        if (!stale)
            return new("Live", "ok", exact, false);

        var title = feedError is { Length: > 0 } ? exact + " · " + feedError : exact;
        return new("Stale · " + Age(lastUpdateUtc, nowUtc), "warn", title, true);
    }

    /// <summary>"12 s", "3 min", "2 h"; "no update" when nothing was ever received.</summary>
    public static string Age(DateTimeOffset? sinceUtc, DateTimeOffset nowUtc)
    {
        if (sinceUtc is null) return "no update";
        var s = (long)Math.Max(0, (nowUtc - sinceUtc.Value).TotalSeconds);
        if (s < 60) return s.ToString(CultureInfo.InvariantCulture) + " s";
        if (s < 3600) return (s / 60).ToString(CultureInfo.InvariantCulture) + " min";
        return (s / 3600).ToString(CultureInfo.InvariantCulture) + " h";
    }

    /// <summary>The single polite live-region text. It depends only on state changes (connected / paused / stale),
    /// never on elapsed time, so screen readers hear a change once — not every second.</summary>
    public static string Announcement(bool connected, bool paused, bool stale)
    {
        if (!connected) return "Console disconnected.";
        var parts = new List<string> { "Console connected." };
        parts.Add(paused ? "New provider calls paused." : "New provider calls accepted.");
        if (stale) parts.Add("Status is stale.");
        return string.Join(" ", parts);
    }
}

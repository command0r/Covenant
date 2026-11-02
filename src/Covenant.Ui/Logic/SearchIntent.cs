using System.Text.RegularExpressions;

namespace Covenant.Ui.Logic;

/// <summary>Console mirror of the Host's weather/place rules (Covenant.Governance.WebSearchPlanner — kept in step by
/// tests) so a weather question without a place is caught BEFORE sending. The Host still enforces it (400
/// location_required). No location is ever inferred: only the Search location field or a place named in the prompt.</summary>
public static partial class SearchIntent
{
    private static readonly HashSet<string> NotPlaces = new(StringComparer.OrdinalIgnoreCase)
    {
        "today", "tonight", "tomorrow", "yesterday", "now", "the", "this", "that", "these", "my", "our", "your", "a", "an",
        "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday", "weekend", "week", "morning",
        "afternoon", "evening", "celsius", "fahrenheit", "general", "detail", "details", "english", "french",
    };

    public static bool IsWeather(string? text) => !string.IsNullOrWhiteSpace(text) && WeatherCue().IsMatch(text);

    public static string? ExtractPlace(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (Match m in PlacePattern().Matches(text))
        {
            var words = m.Groups["place"].Value.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
                .TakeWhile(w => !NotPlaces.Contains(w.Trim('.', '\'', '-'))).ToList();
            if (words.Count == 0) continue;
            var place = string.Join(' ', words).Trim('.', ',', ' ');
            if (place.Length is > 1 and <= WebSourcesText.MaxLocationChars) return place;
        }
        return null;
    }

    /// <summary>Why Run must wait (null = fine): web sources on, a weather question, and no place anywhere.</summary>
    public static string? MissingLocation(WebSourcesMode mode, string? prompt, string? location)
        => mode != WebSourcesMode.Off && IsWeather(prompt) && string.IsNullOrWhiteSpace(location) && ExtractPlace(prompt) is null
            ? "Weather needs a place: fill Search location, or name the city in the prompt (e.g. \"weather in Toronto\"). Covenant never guesses your location."
            : null;

    /// <summary>What the search location will do for the CURRENT form, in one line (shown under the field).</summary>
    public static string LocationEffect(WebSourcesMode mode, string? prompt, string? location)
    {
        var loc = string.IsNullOrWhiteSpace(location) ? null : location.Trim();
        if (mode == WebSourcesMode.Off)
            return loc is null ? "Used only by web search. Web sources are Off." : "Ignored: web sources are Off, so nothing is searched and the model is not told this location.";
        var named = ExtractPlace(prompt);
        if (loc is not null) return "Sent with the search and given to the model as the place to answer for.";
        if (named is not null) return "Not needed: the prompt names " + named + ".";
        return IsWeather(prompt) ? "Required for weather unless the prompt names the place." : "Optional. Needed for weather unless the prompt names the place.";
    }

    [GeneratedRegex(@"\b(weather|forecast|temperatures?|rain(ing|fall)?|snow(ing|fall)?|humidity|wind\s+chill)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WeatherCue();

    [GeneratedRegex(@"\b(?:in|for|at|near)\s+(?<place>\p{Lu}[\p{L}\p{M}.'-]*(?:[ ,]+\p{Lu}[\p{L}\p{M}.'-]*){0,3})", RegexOptions.CultureInvariant)]
    private static partial Regex PlacePattern();
}

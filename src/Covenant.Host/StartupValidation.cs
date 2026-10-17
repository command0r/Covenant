namespace Covenant.Host;

/// <summary>Provider-mode startup rules (ADR-0010), extracted from Program so they are unit-testable
/// without Environment.Exit. Pure: configuration in, refusal lines out. Program prints them and refuses
/// to start (fail-closed) when any are returned.</summary>
public static class StartupValidation
{
    public const string CloudMode = "cloud";
    public const string LocalOnlyMode = "local-only";

    /// <summary>Normalized Providers:Mode; absent → "cloud".</summary>
    public static string ProviderMode(IConfiguration config)
        => (config["Providers:Mode"] ?? CloudMode).Trim().ToLowerInvariant();

    /// <summary>Non-empty values are trimmed; absent/empty → null (a whitespace-only value stays "" so
    /// it is refused as invalid rather than silently treated as unset).</summary>
    public static string? Trimmed(string? value) => value is { Length: > 0 } v ? v.Trim() : null;

    /// <summary>"cloud" requires OpenAI:ApiKey; "local-only" requires Local:Endpoint + Local:ModelId and
    /// refuses cloud credentials (no silent cloud fallback). Local:Endpoint without Local:ModelId is
    /// refused in every mode — the operator's tag is never guessed.</summary>
    public static List<string> ValidateProviders(IConfiguration config)
    {
        var errors = new List<string>();
        string mode = ProviderMode(config);
        bool localOnly = mode == LocalOnlyMode;
        string? localEndpoint = Trimmed(config["Local:Endpoint"]);
        string? localModel = Trimmed(config["Local:ModelId"]);
        string? openAiKey = config["OpenAI:ApiKey"];

        if (mode is not (CloudMode or LocalOnlyMode))
            errors.Add($"Providers:Mode      — 'cloud' or 'local-only', got '{mode}'");
        if (localEndpoint is not null
            && !(Uri.TryCreate(localEndpoint, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https")))
            errors.Add("Local:Endpoint      — must be an absolute http(s) URL, e.g. http://localhost:11434/v1");
        if (localModel is { Length: 0 })
            errors.Add("Local:ModelId       — must not be blank: the exact installed model tag");
        if (localEndpoint is not null && localModel is null)
            errors.Add("Local:ModelId       — required with Local:Endpoint: the exact installed model tag (no default is guessed)");
        if (localOnly)
        {
            if (localEndpoint is null) errors.Add("Local:Endpoint      — required in local-only mode");
            if (localModel is null) errors.Add("Local:ModelId       — required in local-only mode");
            if (!string.IsNullOrWhiteSpace(openAiKey) || !string.IsNullOrWhiteSpace(config["Anthropic:ApiKey"]))
                errors.Add("Providers:Mode      — local-only must not have OpenAI:ApiKey / Anthropic:ApiKey configured (remove them; no cloud fallback)");
        }
        else if (string.IsNullOrWhiteSpace(openAiKey))
            errors.Add("OpenAI:ApiKey       — provider credential (or set Providers:Mode=local-only)");

        return errors;
    }
}

namespace Covenant.Ui.Logic;

/// <summary>Pure endpoint helpers for Models &amp; connections: validation/normalisation (mirrors the Host's
/// ProviderProbe.Validate and catalog formatting) and the governed-test prompt. Configuration is edited through the
/// versioned draft (ConnectionDrafts / ConfigDraft) — there is no "edit a file and restart" path any more.</summary>
public static class ConnectionLogic
{
    /// <summary>Synthetic prompt for the governed test. It contains a synthetic, explicitly invalid SSN-shaped marker (000-12-3456) so that in cloud
    /// mode the PII rule routes it to the local model (PII/PHI → local); in local-only mode every class routes
    /// there anyway.</summary>
    public const string GovernedTestPromptBase =
        "Synthetic connection test record: SSN 000-12-3456 (synthetic, invalid area number). Reply with the single word OK.";

    /// <summary>Fresh per run (nonce) so a governed test can never be answered from the response cache —
    /// a cached reply would prove nothing about the provider being reachable now.</summary>
    public static string GovernedTestPrompt() => $"{GovernedTestPromptBase} [test {Guid.NewGuid():N}]";

    /// <summary>Null when acceptable for a probe; otherwise the reason (no network involved).</summary>
    public static string? EndpointProblem(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Enter the endpoint as seen from the Covenant host, e.g. http://ollama-host:11434/v1";
        if (raw.Trim().Length > 300) return "The endpoint is longer than 300 characters.";
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var u)) return "Not an absolute URL (include http:// or https://).";
        if (u.Scheme is not ("http" or "https")) return "Only http and https endpoints are allowed.";
        if (!string.IsNullOrEmpty(u.UserInfo)) return "Credentials in the URL are not allowed.";
        if (!string.IsNullOrEmpty(u.Query) || !string.IsNullOrEmpty(u.Fragment)) return "Query strings and fragments are not allowed.";
        return null;
    }

    /// <summary>scheme://host:port/path without a trailing slash — the shape GET /admin/catalog reports for
    /// local.endpoint. Null when not a valid absolute URL.</summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || !Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var u)) return null;
        return $"{u.Scheme}://{u.Host}:{u.Port}{u.AbsolutePath.TrimEnd('/')}".ToLowerInvariant();
    }

    /// <summary>"host:port" — the Providers:ProbeAllowList entry format.</summary>
    public static string? Authority(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || !Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var u)) return null;
        return $"{u.Host}:{u.Port}";
    }

    public static bool SameEndpoint(string? a, string? b)
    {
        var na = Normalize(a);
        var nb = Normalize(b);
        return na is not null && nb is not null && string.Equals(na, nb, StringComparison.Ordinal);
    }
}

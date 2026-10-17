using System.Text;
using Covenant.Ui.Models;

namespace Covenant.Ui.Logic;

/// <summary>Pure helpers for the Ollama connection wizard: endpoint validation/normalisation (mirrors the Host's
/// ProviderProbe.Validate and catalog formatting), the read-back check, and the configuration guidance text.
/// Nothing here applies configuration — runtime apply is not available in this version.</summary>
public static class ConnectionLogic
{
    /// <summary>Synthetic prompt for the governed test. It contains the published sample SSN so that in cloud
    /// mode the PII rule routes it to the local model (PII/PHI → local); in local-only mode every class routes
    /// there anyway.</summary>
    public const string GovernedTestPromptBase =
        "Synthetic connection test record: SSN 078-05-1120 (the published sample number). Reply with the single word OK.";

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

    /// <summary>Step 6 gate: the running instance loaded exactly this endpoint and tag, and registered it.</summary>
    public static (bool Pass, string Message) CheckApplied(CatalogResponse? catalog, string draftEndpoint, string tag)
    {
        if (catalog is null) return (false, "The catalog could not be read, so the applied configuration is unknown.");
        var local = catalog.Local;
        if (local is null || !local.Configured)
            return (false, "The running instance has no local route configured (Local:Endpoint / Local:ModelId not loaded). Apply the configuration and restart Covenant.");
        if (!SameEndpoint(local.Endpoint, draftEndpoint))
            return (false, $"The running endpoint is {local.Endpoint ?? "not reported"}, not {Normalize(draftEndpoint)}. Apply the configuration and restart Covenant.");
        if (!string.Equals(local.ModelId, tag, StringComparison.Ordinal))
            return (false, $"The running model id is {local.ModelId ?? "not reported"}, not {tag}. The tag must match exactly.");
        if (!local.Registered)
            return (false, "The endpoint and tag are loaded but the server reports the local model as NOT registered.");
        return (true, "The running instance loaded this endpoint and tag, and the server reports the local model as registered.");
    }

    public static string RouteReview(string? mode, string tag)
        => string.Equals(mode, "local-only", StringComparison.OrdinalIgnoreCase)
            ? $"Local-only mode: every classification (Public, Internal, PII, PHI) routes to local:{tag}. No cloud provider is registered, and Covenant refuses to start if a cloud key is configured."
            : $"Cloud mode: PII and PHI requests route to local:{tag}. Public and Internal stay on their cloud routes. If the local model fails, protected requests are refused — there is no cloud fallback.";

    /// <summary>Configuration guidance: names and values only, never a secret.</summary>
    public static string ConfigBlock(string draftEndpoint, string tag, bool localOnly)
    {
        var endpoint = draftEndpoint.Trim();
        var sb = new StringBuilder();
        sb.AppendLine("# Environment variables (names and values; no secrets)");
        if (localOnly) sb.AppendLine("Providers__Mode=local-only");
        else sb.AppendLine("# Providers__Mode=local-only   (optional: route everything locally; requires NO cloud key)");
        sb.AppendLine("Local__Endpoint=" + endpoint);
        sb.AppendLine("Local__ModelId=" + tag);
        sb.AppendLine();
        sb.AppendLine("# Equivalent dotnet user-secrets (run in src/Covenant.Host)");
        if (localOnly) sb.AppendLine("dotnet user-secrets set \"Providers:Mode\" \"local-only\"");
        sb.AppendLine("dotnet user-secrets set \"Local:Endpoint\" \"" + endpoint + "\"");
        sb.AppendLine("dotnet user-secrets set \"Local:ModelId\" \"" + tag + "\"");
        sb.AppendLine();
        sb.Append("# Local__ApiKey only if Ollama sits behind an authenticating proxy — set it in your vault, never here.");
        return sb.ToString();
    }
}

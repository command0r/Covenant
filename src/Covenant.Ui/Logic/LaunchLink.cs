namespace Covenant.Ui.Logic;

/// <summary>One-click local launch: <c>/ui/connect#token=…</c>. The admin token rides in the URL FRAGMENT, which
/// browsers never send to the server and which never reaches server logs; the console reads it once, then replaces
/// the URL so it isn't left in the address bar or history. Nothing is pre-filled from the server — serving a
/// default token to every visitor would hand out the admin credential.
/// Console v2 (ADR-0012): the console runs requests as an authorized test application, so it never accepts an
/// application key — an <c>appkey=</c> parameter from an older launcher is ignored.</summary>
public static class LaunchLink
{
    public sealed record Credentials(string AdminToken);

    /// <summary>Null when the URI carries no admin token in its fragment.</summary>
    public static Credentials? Parse(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return null;
        int hash = uri.IndexOf('#');
        if (hash < 0 || hash == uri.Length - 1) return null;
        string? token = null;
        foreach (var part in uri[(hash + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0 || part[..eq] != "token") continue;
            var value = Uri.UnescapeDataString(part[(eq + 1)..]).Trim();
            if (value.Length is 0 or > 512) continue;
            token = value;
        }
        return token is null ? null : new Credentials(token);
    }
}

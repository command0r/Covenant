namespace Covenant.Ui.Logic;

/// <summary>One-click local launch: <c>/ui/connect#token=…&amp;appkey=…</c>. Credentials ride in the URL
/// FRAGMENT, which browsers never send to the server and which never reaches server logs; the console reads it
/// once, then replaces the URL so it isn't left in the address bar or history. Nothing is pre-filled from the
/// server — serving a default token to every visitor would hand out the admin credential.</summary>
public static class LaunchLink
{
    public sealed record Credentials(string? AdminToken, string? ApplicationKey);

    /// <summary>Null when the URI carries no launch fragment.</summary>
    public static Credentials? Parse(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return null;
        int hash = uri.IndexOf('#');
        if (hash < 0 || hash == uri.Length - 1) return null;
        string? token = null, appKey = null;
        foreach (var part in uri[(hash + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var value = Uri.UnescapeDataString(part[(eq + 1)..]).Trim();
            if (value.Length is 0 or > 512) continue;
            switch (part[..eq])
            {
                case "token": token = value; break;
                case "appkey": appKey = value; break;
            }
        }
        return token is null && appKey is null ? null : new Credentials(token, appKey);
    }
}

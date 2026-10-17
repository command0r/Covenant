using System.Security.Cryptography;
using System.Text;
using Microsoft.Net.Http.Headers;

namespace Covenant.Host;

/// <summary>Serves the embedded Blazor WebAssembly console (ADR-0004) under /ui/. The bundle is compiled by
/// src/Covenant.Ui and embedded by the PublishConsole target as manifest resources named console/&lt;path&gt;;
/// nothing here reflects, nothing is read from disk, nothing leaves the perimeter (CSP: self only).
/// The shell is public; every data call the console makes goes to /admin/* and needs the admin token.</summary>
public static class ConsoleAssets
{
    public const string ResourcePrefix = "console/";
    public const string IndexPath = "index.html";

    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; font-src 'self'; connect-src 'self'; frame-ancestors 'none'";

    private const string NoCache = "no-cache";
    private const string Immutable = "public,max-age=31536000,immutable";

    private static readonly Dictionary<string, string> Mime = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".wasm"] = "application/wasm",
        [".woff2"] = "font/woff2",
        [".woff"] = "font/woff",
        [".dat"] = "application/octet-stream",
        [".pdb"] = "application/octet-stream",
        [".dll"] = "application/octet-stream",
        [".blat"] = "application/octet-stream",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".ico"] = "image/x-icon",
        [".txt"] = "text/plain; charset=utf-8",
        [".md"] = "text/markdown; charset=utf-8",
        [".webmanifest"] = "application/manifest+json",
    };

    /// <summary>Files the loader fetches by a stable name; they must revalidate on every load so a new
    /// appliance build is picked up without a hard refresh. Everything else under _framework/ and fonts/ is
    /// content-addressed (fingerprinted) and can be cached for a year.</summary>
    private static readonly HashSet<string> AlwaysRevalidate = new(StringComparer.Ordinal)
    {
        IndexPath,
        "_framework/blazor.boot.json",
        "_framework/blazor.webassembly.js",
        "_framework/dotnet.boot.js",
        "_framework/dotnet.js",
    };

    public sealed record Asset(byte[] Bytes, string ContentType, string ETag, string CacheControl);

    /// <summary>Extension → MIME type. Unknown extensions are served as application/octet-stream so a
    /// browser never sniffs them into something executable (nosniff is set alongside).</summary>
    public static string ContentTypeFor(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Length > 0 && Mime.TryGetValue(ext, out var ct) ? ct : "application/octet-stream";
    }

    /// <summary>Resource names are produced by MSBuild (%(RecursiveDir) uses the build host's separator) and
    /// request paths by browsers: both are normalized to forward slashes with no leading slash, no "." or ".."
    /// segments, so a lookup can never escape the console/ namespace.</summary>
    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>(parts.Length);
        foreach (var p in parts)
        {
            if (p == ".") continue;
            if (p == "..") { if (kept.Count > 0) kept.RemoveAt(kept.Count - 1); continue; }
            kept.Add(p);
        }
        return string.Join('/', kept);
    }

    /// <summary>True when a dotted segment of the file name looks like a build fingerprint (≥ 8 chars of
    /// [a-z0-9], containing a digit), e.g. dotnet.native.8vc7vb9ab4.wasm.</summary>
    public static bool IsFingerprinted(string normalizedPath)
    {
        var file = normalizedPath[(normalizedPath.LastIndexOf('/') + 1)..];
        var segments = file.Split('.');
        for (int i = 1; i < segments.Length - 1; i++)
        {
            var seg = segments[i];
            if (seg.Length >= 8 && seg.All(char.IsAsciiLetterOrDigit) && seg.Any(char.IsAsciiDigit))
                return true;
        }
        return false;
    }

    public static string CacheControlFor(string normalizedPath)
    {
        if (AlwaysRevalidate.Contains(normalizedPath)) return NoCache;
        // Immutable only when the file name carries a content fingerprint (name.<hash>.ext); anything else
        // revalidates via ETag so an appliance upgrade can never be masked by a year-long cached file.
        if (normalizedPath.StartsWith("_framework/", StringComparison.Ordinal) && IsFingerprinted(normalizedPath)) return Immutable;
        if (normalizedPath.StartsWith("fonts/", StringComparison.Ordinal)
            && normalizedPath.EndsWith(".woff2", StringComparison.OrdinalIgnoreCase)) return Immutable;
        return NoCache;
    }

    /// <summary>Loads every console/* manifest resource of <paramref name="assembly"/> into memory. The bundle is a
    /// few MB and read once at startup; an appliance has no reason to stream it from the assembly per request.</summary>
    public static Dictionary<string, Asset> Load(System.Reflection.Assembly assembly)
    {
        var assets = new Dictionary<string, Asset>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal)) continue;
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null) continue;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var bytes = ms.ToArray();
            var path = NormalizePath(name[ResourcePrefix.Length..]);
            var etag = "\"" + Convert.ToHexString(SHA256.HashData(bytes))[..32].ToLowerInvariant() + "\"";
            assets[path] = new Asset(bytes, ContentTypeFor(path), etag, CacheControlFor(path));
        }
        return assets;
    }

    public static bool HasEmbeddedConsole(System.Reflection.Assembly assembly)
    {
        foreach (var name in assembly.GetManifestResourceNames())
            if (name.StartsWith(ResourcePrefix, StringComparison.Ordinal)) return true;
        return false;
    }

    public static void MapConsole(this WebApplication app)
    {
        var assets = Load(typeof(ConsoleAssets).Assembly);
        var hasIndex = assets.ContainsKey(IndexPath);

        // One endpoint only: a separate literal "/ui" route also matches "/ui/" and redirected it to itself.
        // Redirect exactly "/ui" (no trailing slash) so relative asset URLs resolve under <base href="/ui/">.
        app.MapGet("/ui/{**path}", (string? path, HttpContext http) =>
        {
            if (http.Request.Path.Value == "/ui") return Results.Redirect("/ui/", permanent: false);
            var headers = http.Response.Headers;
            headers["Content-Security-Policy"] = ContentSecurityPolicy;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["Referrer-Policy"] = "same-origin";

            if (!hasIndex)
            {
                headers[HeaderNames.CacheControl] = "no-store";
                return Results.Text(
                    "The Covenant console was not built into this binary (built with SkipConsolePublish=true). " +
                    "Rebuild without it to embed src/Covenant.Ui; the admin API under /admin/* is unaffected.",
                    "text/plain; charset=utf-8", Encoding.UTF8, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var normalized = NormalizePath(path);
            // Nested-route reload (e.g. /ui/requests/req_…): anything that is not a known file is the app shell,
            // which routes client-side. Only paths that LOOK like files (have an extension) are allowed to 404,
            // so a missing fingerprinted asset fails loudly instead of returning HTML to the loader.
            if (normalized.Length == 0 || !assets.TryGetValue(normalized, out var asset))
            {
                if (normalized.Length > 0 && Path.HasExtension(normalized))
                    return Results.NotFound();
                asset = assets[IndexPath];
            }

            headers[HeaderNames.CacheControl] = asset.CacheControl;
            headers[HeaderNames.ETag] = asset.ETag;

            var ifNoneMatch = http.Request.Headers[HeaderNames.IfNoneMatch];
            foreach (var candidate in ifNoneMatch)
                if (candidate is not null && candidate.Contains(asset.ETag, StringComparison.Ordinal))
                    return Results.StatusCode(StatusCodes.Status304NotModified);

            return Results.Bytes(asset.Bytes, asset.ContentType);
        });
    }
}

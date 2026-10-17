using System.Net;
using Covenant.Host;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Covenant.Tests;

/// <summary>Pure tests for the console asset helpers: MIME map, path normalization, cache policy. These run
/// whether or not the WASM bundle was embedded (SkipConsolePublish).</summary>
public sealed class ConsoleAssetsUnitTests
{
    [Theory]
    [InlineData("index.html", "text/html; charset=utf-8")]
    [InlineData("_framework/blazor.webassembly.js", "text/javascript; charset=utf-8")]
    [InlineData("_framework/dotnet.native.abc123.wasm", "application/wasm")]
    [InlineData("_framework/blazor.boot.json", "application/json; charset=utf-8")]
    [InlineData("_framework/icudt.dat", "application/octet-stream")]
    [InlineData("_framework/Covenant.Ui.pdb", "application/octet-stream")]
    [InlineData("fonts/ibm-plex-sans-latin-400-normal.woff2", "font/woff2")]
    [InlineData("css/app.css", "text/css; charset=utf-8")]
    [InlineData("js/interop.mjs", "text/javascript; charset=utf-8")]
    [InlineData("favicon.ico", "image/x-icon")]
    [InlineData("logo.svg", "image/svg+xml")]
    [InlineData("logo.png", "image/png")]
    [InlineData("README.md", "text/markdown; charset=utf-8")]
    [InlineData("notes.txt", "text/plain; charset=utf-8")]
    [InlineData("INDEX.HTML", "text/html; charset=utf-8")]
    public void Content_type_map_covers_the_bundle(string path, string expected)
        => Assert.Equal(expected, ConsoleAssets.ContentTypeFor(path));

    [Theory]
    [InlineData("thing.unknownext")]
    [InlineData("noextension")]
    public void Unknown_extensions_are_octet_stream(string path)
        => Assert.Equal("application/octet-stream", ConsoleAssets.ContentTypeFor(path));

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("index.html", "index.html")]
    [InlineData("/index.html", "index.html")]
    [InlineData("_framework\\blazor.boot.json", "_framework/blazor.boot.json")]
    [InlineData("_framework//dotnet.js", "_framework/dotnet.js")]
    [InlineData("./css/app.css", "css/app.css")]
    [InlineData("css/../index.html", "index.html")]
    [InlineData("../../etc/passwd", "etc/passwd")]
    [InlineData("requests/req_abc/", "requests/req_abc")]
    public void Paths_normalize_to_forward_slashes_without_traversal(string? input, string expected)
        => Assert.Equal(expected, ConsoleAssets.NormalizePath(input));

    [Theory]
    [InlineData("index.html", "no-cache")]
    [InlineData("_framework/blazor.boot.json", "no-cache")]
    [InlineData("_framework/blazor.webassembly.js", "no-cache")]
    [InlineData("_framework/dotnet.boot.js", "no-cache")]
    [InlineData("_framework/dotnet.ab12cd34.js", "public,max-age=31536000,immutable")]
    [InlineData("_framework/Covenant.Ui.ab12cd34.wasm", "public,max-age=31536000,immutable")]
    [InlineData("fonts/ibm-plex-mono-latin-400-normal.woff2", "public,max-age=31536000,immutable")]
    [InlineData("css/app.css", "no-cache")]
    [InlineData("_framework/System.Runtime.wasm", "no-cache")]                 // unfingerprinted → revalidate, never pinned for a year
    public void Cache_policy_revalidates_entry_points_and_pins_fingerprinted_files(string path, string expected)
        => Assert.Equal(expected, ConsoleAssets.CacheControlFor(path));

    [Fact]
    public void Csp_never_allows_remote_origins()
    {
        Assert.Contains("default-src 'self'", ConsoleAssets.ContentSecurityPolicy);
        Assert.Contains("'wasm-unsafe-eval'", ConsoleAssets.ContentSecurityPolicy);
        Assert.Contains("frame-ancestors 'none'", ConsoleAssets.ContentSecurityPolicy);
        Assert.DoesNotContain("http", ConsoleAssets.ContentSecurityPolicy);
        Assert.DoesNotContain("*", ConsoleAssets.ContentSecurityPolicy);
    }
}

/// <summary>The console through the real Host. The bundle may or may not be embedded in this test build
/// (SkipConsolePublish); each test asserts the branch the manifest says applies, so the suite is deterministic
/// either way.</summary>
public sealed class ConsoleAssetsTests : IClassFixture<ConsoleAssetsTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        public readonly string AuditPath = Path.Combine(Path.GetTempPath(), $"covenant-console-{Guid.NewGuid():n}.log");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("OpenAI:ApiKey", "unused-stubbed");
            builder.UseSetting("Admin:Token", "test-admin");
            builder.UseSetting("Budget:GlobalCapUsd", "100");
            builder.UseSetting("Auth:Keys:0:Key", "k");
            builder.UseSetting("Auth:Keys:0:Principal", "p");
            builder.UseSetting("Auth:Keys:0:Team", "t");
            CorrelationTestKit.IsolatePaths(builder, AuditPath);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            CorrelationTestKit.DeleteWithSiblings(AuditPath);
        }
    }

    private static readonly bool Embedded = ConsoleAssets.HasEmbeddedConsole(typeof(ConsoleAssets).Assembly);
    private readonly HttpClient _client;

    public ConsoleAssetsTests(Factory factory)
    {
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    [Fact]
    public async Task Ui_root_serves_the_shell_or_an_honest_503()
    {
        var resp = await _client.GetAsync("/ui/");
        if (Embedded)
        {
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("text/html", resp.Content.Headers.ContentType?.MediaType);
            var html = await resp.Content.ReadAsStringAsync();
            Assert.Contains("<base href=\"/ui/\"", html);
            Assert.Contains("_framework/blazor.webassembly.js", html);
            Assert.DoesNotContain("https://", html);
        }
        else
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
            Assert.Contains("SkipConsolePublish", await resp.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Ui_without_trailing_slash_redirects_to_the_shell()
    {
        var resp = await _client.GetAsync("/ui");
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal("/ui/", resp.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Nested_route_reload_returns_the_same_index_html()
    {
        var root = await _client.GetAsync("/ui/");
        var nested = await _client.GetAsync("/ui/requests/abc");
        Assert.Equal(root.StatusCode, nested.StatusCode);
        if (Embedded)
        {
            Assert.Equal("text/html", nested.Content.Headers.ContentType?.MediaType);
            Assert.Equal(await root.Content.ReadAsStringAsync(), await nested.Content.ReadAsStringAsync());
            Assert.Equal(root.Headers.ETag, nested.Headers.ETag);
            Assert.Equal("no-cache", nested.Headers.CacheControl?.ToString());
        }
    }

    [Fact]
    public async Task Boot_manifest_when_present_is_json()
    {
        if (!Embedded) return;
        var resp = await _client.GetAsync("/ui/_framework/blazor.boot.json");
        if (resp.StatusCode == HttpStatusCode.NotFound) return; // .NET 9+ folds the boot config into dotnet.boot.js
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Framework_js_when_present_is_javascript_and_wasm_is_wasm()
    {
        if (!Embedded) return;
        var js = await _client.GetAsync("/ui/_framework/blazor.webassembly.js");
        Assert.Equal(HttpStatusCode.OK, js.StatusCode);
        Assert.Equal("text/javascript", js.Content.Headers.ContentType?.MediaType);

        var font = await _client.GetAsync("/ui/fonts/ibm-plex-sans-latin-400-normal.woff2");
        Assert.Equal(HttpStatusCode.OK, font.StatusCode);
        Assert.Equal("font/woff2", font.Content.Headers.ContentType?.MediaType);
        Assert.Contains("immutable", font.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Missing_file_like_path_is_404_not_the_shell()
    {
        if (!Embedded) return;
        var resp = await _client.GetAsync("/ui/_framework/does-not-exist.unknownext");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Etag_round_trip_yields_304()
    {
        if (!Embedded) return;
        var first = await _client.GetAsync("/ui/");
        var etag = first.Headers.ETag;
        Assert.NotNull(etag);
        var req = new HttpRequestMessage(HttpMethod.Get, "/ui/");
        req.Headers.IfNoneMatch.Add(etag);
        var second = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
    }

    [Fact]
    public async Task Console_responses_carry_csp_and_nosniff()
    {
        var resp = await _client.GetAsync("/ui/");
        Assert.True(resp.Headers.TryGetValues("Content-Security-Policy", out var csp));
        Assert.Equal(ConsoleAssets.ContentSecurityPolicy, Assert.Single(csp));
        Assert.True(resp.Headers.TryGetValues("X-Content-Type-Options", out var nosniff));
        Assert.Equal("nosniff", Assert.Single(nosniff));
    }

    [Fact]
    public async Task Admin_and_inference_surfaces_are_unaffected()
    {
        var status = await _client.GetAsync("/admin/status");
        Assert.Equal(HttpStatusCode.Unauthorized, status.StatusCode); // token guard still first
        Assert.False(status.Headers.Contains("Content-Security-Policy"));

        var withToken = new HttpRequestMessage(HttpMethod.Get, "/admin/status");
        withToken.Headers.Add("X-Covenant-Admin-Token", "test-admin");
        var ok = await _client.SendAsync(withToken);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("application/json", ok.Content.Headers.ContentType?.MediaType);

        var models = await _client.GetAsync("/v1/models");
        Assert.Equal(HttpStatusCode.Unauthorized, models.StatusCode); // no app key → refused, not the shell
        Assert.False(models.Headers.Contains("Content-Security-Policy"));

        var legacy = await _client.GetAsync("/admin/ui");
        Assert.Equal(HttpStatusCode.OK, legacy.StatusCode);
    }
}

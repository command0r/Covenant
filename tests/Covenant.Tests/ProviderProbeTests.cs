using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Covenant.Host;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Covenant.Tests;

/// <summary>Minimal loopback HTTP/1.1 server for probe contract tests (TcpListener, ephemeral port). The
/// responder maps a request line to raw response bytes; null = accept and never answer (timeout).</summary>
internal sealed class LoopbackHttpServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<string> _requests = [];
    private int _connections;

    public Func<string, byte[]?> Respond { get; set; }
    public int Port { get; }
    public int Connections => Volatile.Read(ref _connections);
    public IReadOnlyList<string> Requests { get { lock (_requests) return _requests.ToList(); } }
    public string Authority => $"127.0.0.1:{Port}";
    public string V1 => $"http://127.0.0.1:{Port}/v1";

    public LoopbackHttpServer(Func<string, byte[]?> respond)
    {
        Respond = respond;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync();
    }

    public static byte[] Http(int status, string body, string contentType = "application/json", string extraHeaders = "", bool contentLength = true)
        => Encoding.UTF8.GetBytes(
            $"HTTP/1.1 {status} X\r\nContent-Type: {contentType}\r\n" +
            (contentLength ? $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n" : "") +
            $"Connection: close\r\n{extraHeaders}\r\n{body}");

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                Interlocked.Increment(ref _connections);
                _ = HandleAsync(client);
            }
        }
        catch (Exception) { /* listener stopped */ }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var buffer = new byte[8192];
                var head = new StringBuilder();
                while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    int n = await stream.ReadAsync(buffer, _cts.Token);
                    if (n == 0) return;
                    head.Append(Encoding.ASCII.GetString(buffer, 0, n));
                }
                var raw = head.ToString();
                lock (_requests) _requests.Add(raw);
                var response = Respond(raw.Split("\r\n")[0]);
                if (response is null)
                {
                    await Task.Delay(Timeout.Infinite, _cts.Token);
                    return;
                }
                await stream.WriteAsync(response, _cts.Token);
                await stream.FlushAsync(_cts.Token);
            }
            catch (Exception) { /* client went away / server disposed */ }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}

/// <summary>Task 06: the host-side probe is an allow-listed, bounded, redirect-refusing model-list check —
/// never a general URL fetcher — and observed health supersedes stale success.</summary>
public sealed class ProviderProbeTests
{
    private static HashSet<string> Allow(LoopbackHttpServer s) => new(StringComparer.OrdinalIgnoreCase) { s.Authority };

    [Fact]
    public async Task Reachable_endpoint_returns_discovered_model_ids()
    {
        using var server = new LoopbackHttpServer(_ => LoopbackHttpServer.Http(200, """{"object":"list","data":[{"id":"qwen3:8b"},{"id":"llama3.2:3b"}]}"""));
        var r = await ProviderProbe.RunAsync(server.V1, Allow(server), CancellationToken.None);

        Assert.True(r.Allowed);
        Assert.True(r.Reachable, r.Error);
        Assert.Equal(200, r.HttpStatus);
        Assert.Equal(new[] { "qwen3:8b", "llama3.2:3b" }, r.Models);
        Assert.StartsWith("GET /v1/models ", Assert.Single(server.Requests));
    }

    [Fact]
    public async Task Probe_never_forwards_credentials()
    {
        using var server = new LoopbackHttpServer(_ => LoopbackHttpServer.Http(200, """{"data":[]}"""));
        await ProviderProbe.RunAsync(server.V1, Allow(server), CancellationToken.None);
        var request = Assert.Single(server.Requests);
        Assert.DoesNotContain("Authorization", request, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("X-Covenant", request, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Cookie", request, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Empty_discovery_is_reachable_with_no_models()
    {
        using var server = new LoopbackHttpServer(_ => LoopbackHttpServer.Http(200, """{"data":[]}"""));
        var r = await ProviderProbe.RunAsync(server.V1, Allow(server), CancellationToken.None);
        Assert.True(r.Reachable, r.Error);
        Assert.Empty(r.Models);
        Assert.Null(r.Error);
    }

    [Fact]
    public async Task Refused_port_is_unreachable()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();                                           // nothing listens there now

        var allow = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { $"127.0.0.1:{port}" };
        var r = await ProviderProbe.RunAsync($"http://127.0.0.1:{port}/v1", allow, CancellationToken.None);
        Assert.True(r.Allowed);
        Assert.False(r.Reachable);
        Assert.StartsWith("connection failed", r.Error);
        Assert.Empty(r.Models);
    }

    [Fact]
    public async Task Silent_server_times_out_within_the_deadline()
    {
        using var server = new LoopbackHttpServer(_ => null);  // accepts, never answers
        var started = DateTimeOffset.UtcNow;
        var r = await ProviderProbe.RunAsync(server.V1, Allow(server), TimeSpan.FromMilliseconds(400), CancellationToken.None);

        Assert.True(r.Allowed);
        Assert.False(r.Reachable);
        Assert.Contains("timed out", r.Error);
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(4));
    }

    [Theory]
    [InlineData("this is not json", "text/plain")]
    [InlineData("""{"models":[{"name":"qwen3:8b"}]}""", "application/json")]
    [InlineData("""[{"id":"qwen3:8b"}]""", "application/json")]
    [InlineData("""{"data":"nope"}""", "application/json")]
    public async Task Wrong_shape_or_non_json_is_an_error(string body, string contentType)
    {
        using var server = new LoopbackHttpServer(_ => LoopbackHttpServer.Http(200, body, contentType));
        var r = await ProviderProbe.RunAsync(server.V1, Allow(server), CancellationToken.None);
        Assert.False(r.Reachable);
        Assert.Contains("not an OpenAI-compatible model list", r.Error);
    }

    [Fact]
    public async Task Non_success_status_is_an_error()
    {
        using var server = new LoopbackHttpServer(_ => LoopbackHttpServer.Http(500, """{"error":"boom"}"""));
        var r = await ProviderProbe.RunAsync(server.V1, Allow(server), CancellationToken.None);
        Assert.False(r.Reachable);
        Assert.Equal(500, r.HttpStatus);
    }

    [Fact]
    public async Task Redirect_is_not_followed()
    {
        int port = 0;
        using var server = new LoopbackHttpServer(_ => LoopbackHttpServer.Http(302, "",
            extraHeaders: $"Location: http://127.0.0.1:{port}/elsewhere/models\r\n"));
        port = server.Port;
        var r = await ProviderProbe.RunAsync(server.V1, Allow(server), CancellationToken.None);

        Assert.False(r.Reachable);
        Assert.Equal(302, r.HttpStatus);
        Assert.Contains("redirect", r.Error);
        Assert.Single(server.Requests);                         // the Location was never requested
    }

    [Theory]
    [InlineData(true)]   // Content-Length declares the oversize up front
    [InlineData(false)]  // no length: the streaming cap must catch it
    public async Task Oversized_body_is_refused(bool declareLength)
    {
        var huge = """{"data":[""" + string.Join(",", Enumerable.Repeat("""{"id":"padding-model-name-xxxxxxxxxxxxxxxx"}""", 2000)) + "]}";
        Assert.True(huge.Length > ProviderProbe.MaxResponseBytes);
        using var server = new LoopbackHttpServer(_ => LoopbackHttpServer.Http(200, huge, contentLength: declareLength));
        var r = await ProviderProbe.RunAsync(server.V1, Allow(server), CancellationToken.None);
        Assert.False(r.Reachable);
        Assert.Contains("exceeded", r.Error);
    }

    [Fact]
    public async Task Destination_not_on_allow_list_is_refused_without_connecting()
    {
        using var server = new LoopbackHttpServer(_ => LoopbackHttpServer.Http(200, """{"data":[]}"""));
        var allow = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "127.0.0.1:1" };
        var r = await ProviderProbe.RunAsync(server.V1, allow, CancellationToken.None);

        Assert.False(r.Allowed);
        Assert.False(r.Reachable);
        Assert.Contains("allow-list", r.Error);
        Assert.Equal(0, server.Connections);
    }

    [Theory]
    [InlineData("http://user:pass@127.0.0.1:{port}/v1", "credentials")]
    [InlineData("http://127.0.0.1:{port}/v1?x=1", "query")]
    [InlineData("http://127.0.0.1:{port}/v1#frag", "query")]
    [InlineData("ftp://127.0.0.1:{port}/v1", "http and https")]
    [InlineData("file:///etc/passwd", "http and https")]
    [InlineData("no-scheme-here", "absolute")]
    [InlineData("", "required")]
    public async Task Unsafe_endpoint_shapes_are_refused_without_connecting(string template, string expected)
    {
        using var server = new LoopbackHttpServer(_ => LoopbackHttpServer.Http(200, """{"data":[]}"""));
        var r = await ProviderProbe.RunAsync(template.Replace("{port}", server.Port.ToString()), Allow(server), CancellationToken.None);
        Assert.False(r.Allowed);
        Assert.Contains(expected, r.Error);
        Assert.Equal(0, server.Connections);
    }

    [Fact]
    public void Allow_list_defaults_the_ollama_port_and_includes_the_applied_local_endpoint()
    {
        var set = ProviderProbe.BuildAllowList("ollama, 10.0.0.5:8000", "http://gpu-box:11434/v1");
        Assert.Contains("ollama:11434", set);
        Assert.Contains("10.0.0.5:8000", set);
        Assert.Contains("gpu-box:11434", set);
        Assert.Equal(3, set.Count);
        Assert.Empty(ProviderProbe.BuildAllowList(null, null));
    }

    [Fact]
    public void Health_failure_after_success_is_failed_but_keeps_last_success()
    {
        var health = new ProviderHealth();
        Assert.Equal("unknown", health.Get("local:qwen3:8b").State);

        health.RecordSuccess("local", "qwen3:8b");
        var ok = health.Get("local:qwen3:8b");
        Assert.Equal("ok", ok.State);
        Assert.NotNull(ok.LastSuccessUtc);

        health.RecordFailure("local", "qwen3:8b", "connection refused");
        var failed = health.Get("local:qwen3:8b");
        Assert.Equal("failed", failed.State);                   // stale success no longer shows green
        Assert.Equal(ok.LastSuccessUtc, failed.LastSuccessUtc); // ... but "last worked at" survives
        Assert.Equal("connection refused", failed.LastError);
        Assert.NotNull(failed.LastFailureUtc);

        health.Success("local:qwen3:8b");
        var again = health.Get("local:qwen3:8b");
        Assert.Equal("ok", again.State);
        Assert.Equal(failed.LastFailureUtc, again.LastFailureUtc);
    }

    [Fact]
    public async Task Applied_endpoint_probe_success_then_failure_updates_catalog_health()
    {
        bool healthy = true;
        using var server = new LoopbackHttpServer(_ => Volatile.Read(ref healthy)
            ? LoopbackHttpServer.Http(200, """{"data":[{"id":"qwen3:8b"}]}""")
            : LoopbackHttpServer.Http(503, """{"error":"down"}"""));
        var audit = Path.Combine(Path.GetTempPath(), $"covenant-probe-{Guid.NewGuid():n}.log");
        try
        {
            using var baseFactory = new WebApplicationFactory<Program>();
            using var factory = baseFactory.WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Production");
                b.UseSetting("OpenAI:ApiKey", "unused");
                b.UseSetting("Admin:Token", CorrelationTestKit.AdminToken);
                b.UseSetting("Budget:GlobalCapUsd", "10");
                b.UseSetting("Auth:AllowAnonymous", "true");
                CorrelationTestKit.IsolatePaths(b, audit);
                b.UseSetting("Local:Endpoint", server.V1);
                b.UseSetting("Local:ModelId", "qwen3:8b");
            });
            using var client = factory.CreateClient();

            async Task<JsonElement> ProbeAsync()
            {
                using var req = CorrelationTestKit.Admin(HttpMethod.Post, "/admin/providers/probe");
                req.Content = new StringContent($"{{\"endpoint\":\"{server.V1}\"}}", Encoding.UTF8, "application/json");
                using var resp = await client.SendAsync(req);
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
                return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.Clone();
            }

            async Task<JsonElement> EndpointHealthAsync()
            {
                using var resp = await client.SendAsync(CorrelationTestKit.Admin(HttpMethod.Get, "/admin/catalog"));
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                return doc.RootElement.GetProperty("local").GetProperty("endpoint_health").Clone();
            }

            Assert.Equal("unknown", (await EndpointHealthAsync()).GetProperty("state").GetString());

            var first = await ProbeAsync();
            Assert.True(first.GetProperty("reachable").GetBoolean());
            var afterOk = await EndpointHealthAsync();
            Assert.Equal("ok", afterOk.GetProperty("state").GetString());
            var lastSuccess = afterOk.GetProperty("last_success_utc").GetString();
            Assert.False(string.IsNullOrEmpty(lastSuccess));

            Volatile.Write(ref healthy, false);
            var second = await ProbeAsync();
            Assert.False(second.GetProperty("reachable").GetBoolean());
            var afterFail = await EndpointHealthAsync();
            Assert.Equal("failed", afterFail.GetProperty("state").GetString());
            Assert.Equal(lastSuccess, afterFail.GetProperty("last_success_utc").GetString());

            // The admin token never reaches the probed endpoint.
            Assert.All(server.Requests, r => Assert.DoesNotContain(CorrelationTestKit.AdminToken, r));

            // A probe is admin-only.
            using var anon = await client.PostAsync("/admin/providers/probe",
                new StringContent($"{{\"endpoint\":\"{server.V1}\"}}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);
        }
        finally
        {
            CorrelationTestKit.DeleteWithSiblings(audit);
        }
    }
}

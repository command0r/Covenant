using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace Covenant.Tests;

/// <summary>End-to-end HTTP contract through the real Host: denial kinds must map to their status codes (429 rate-limited, 502 upstream, 401 unauthenticated) in buffered AND streamed mode.</summary>
public sealed class HttpMappingTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly string _auditPath = Path.Combine(Path.GetTempPath(), $"covenant-http-test-{Guid.NewGuid():n}.log");
    private readonly HttpClient _client;
    private readonly WebApplicationFactory<Program> _factory;

    public HttpMappingTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");                       // never load the developer's user-secrets
            b.UseSetting("OpenAI:ApiKey", "test-key");
            b.UseSetting("OpenAI:Endpoint", "http://127.0.0.1:9"); // unroutable → instant governed 502
            b.UseSetting("Admin:Token", "test-admin");
            b.UseSetting("Budget:GlobalCapUsd", "100");
            b.UseSetting("Auth:Keys:0:Key", "test-api-key");
            b.UseSetting("Auth:Keys:0:Principal", "itest");
            b.UseSetting("Auth:Keys:0:Team", "itest-team");
            b.UseSetting("RateLimit:PerTeamPerMinute", "1");
            b.UseSetting("Audit:Path", _auditPath);
        });
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-api-key");
    }

    public void Dispose()
    {
        _factory.Dispose();
        if (File.Exists(_auditPath)) File.Delete(_auditPath);
    }

    private static StringContent Chat(string content, bool stream = false) =>
        new($"{{\"stream\":{(stream ? "true" : "false")},\"messages\":[{{\"role\":\"user\",\"content\":\"{content}\"}}]}}",
            Encoding.UTF8, "application/json");

    [Fact]
    public async Task Upstream_failure_is_502_then_rate_limit_is_429_in_buffered_and_streamed_mode()
    {
        // Request 1: admitted by the rate stage (cap 1), dies at the unroutable provider → governed 502.
        var first = await _client.PostAsync("/v1/chat/completions", Chat("hello"));
        Assert.Equal(HttpStatusCode.BadGateway, first.StatusCode);
        Assert.Contains("upstream_error", await first.Content.ReadAsStringAsync());

        // Request 2: over the per-team cap → 429, buffered.
        var second = await _client.PostAsync("/v1/chat/completions", Chat("hello again"));
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Contains("rate_limited", await second.Content.ReadAsStringAsync());

        // Request 3: still over the cap, streamed — pre-flight denial must be plain 429 JSON, no SSE.
        var third = await _client.PostAsync("/v1/chat/completions", Chat("stream me", stream: true));
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.NotEqual("text/event-stream", third.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Missing_api_key_is_401()
    {
        using var bare = _factory.CreateClient();                 // no Authorization header
        var resp = await bare.PostAsync("/v1/chat/completions", Chat("hi"));

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("unauthenticated", await resp.Content.ReadAsStringAsync());
    }
}

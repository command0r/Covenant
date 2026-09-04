using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Covenant.Tests;

/// <summary>The Anthropic dialect through the real Host: x-api-key auth, Anthropic error taxonomy on every denial path, and the cross-protocol case — Anthropic wire in, OpenAI-adapter route out.</summary>
public sealed class AnthropicIngressTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly string _auditPath = Path.Combine(Path.GetTempPath(), $"covenant-anthropic-test-{Guid.NewGuid():n}.log");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public AnthropicIngressTests(WebApplicationFactory<Program> factory)
    {
        // Anthropic:ApiKey deliberately NOT configured — the provider stays unwired, the DIALECT works.
        _factory = factory.WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.UseSetting("OpenAI:ApiKey", "test-key");
            b.UseSetting("OpenAI:Endpoint", "http://127.0.0.1:9");   // unroutable → instant governed 502
            b.UseSetting("Admin:Token", "test-admin");
            b.UseSetting("Budget:GlobalCapUsd", "100");
            b.UseSetting("Auth:Keys:0:Key", "test-api-key");
            b.UseSetting("Auth:Keys:0:Principal", "itest");
            b.UseSetting("Auth:Keys:0:Team", "itest-team");
            b.UseSetting("Audit:Path", _auditPath);
        });
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Add("x-api-key", "test-api-key");   // Anthropic-style auth header
    }

    public void Dispose()
    {
        _factory.Dispose();
        if (File.Exists(_auditPath)) File.Delete(_auditPath);
    }

    private static StringContent Messages(string content, string? model = null, bool stream = false) =>
        new($"{{\"model\":{(model is null ? "null" : $"\"{model}\"")},\"max_tokens\":100,\"stream\":{(stream ? "true" : "false")},\"messages\":[{{\"role\":\"user\",\"content\":\"{content}\"}}]}}",
            Encoding.UTF8, "application/json");

    [Fact]
    public async Task Missing_key_is_401_in_anthropic_error_shape()
    {
        using var bare = _factory.CreateClient();                        // no x-api-key
        var resp = await bare.PostAsync("/v1/messages", Messages("hi"));
        var json = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("authentication_error", json);
        Assert.Contains("\"type\":\"error\"", json);
    }

    [Fact]
    public async Task Unpermitted_claude_model_is_403_permission_error()
    {
        // Anthropic provider unconfigured → claude models are not in the permitted set: fail-closed.
        var resp = await _client.PostAsync("/v1/messages", Messages("hi", model: "claude-haiku-4-5"));
        var json = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Contains("permission_error", json);
        Assert.Contains("not permitted", json);
    }

    [Fact]
    public async Task Cross_protocol_anthropic_wire_reaches_the_openai_route_and_maps_upstream_failure()
    {
        // The point of one canonical model: Anthropic dialect in, OpenAI-adapter route out.
        var resp = await _client.PostAsync("/v1/messages", Messages("hi", model: "gpt-4o-mini"));
        var json = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);        // unroutable upstream, governed
        Assert.Contains("api_error", json);
    }

    [Fact]
    public async Task Pii_over_the_anthropic_dialect_fails_closed_with_permission_error()
    {
        var resp = await _client.PostAsync("/v1/messages", Messages("my SSN is 123-45-6789"));
        var json = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Contains("permission_error", json);
    }

    [Fact]
    public async Task Streamed_preflight_denial_is_plain_json_not_sse()
    {
        var resp = await _client.PostAsync("/v1/messages", Messages("my SSN is 123-45-6789", stream: true));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.NotEqual("text/event-stream", resp.Content.Headers.ContentType?.MediaType);
        Assert.Contains("permission_error", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Bearer_auth_is_accepted_as_a_fallback_on_the_anthropic_dialect()
    {
        using var bearer = _factory.CreateClient();
        bearer.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-api-key");  // no x-api-key

        var resp = await bearer.PostAsync("/v1/messages", Messages("hi", model: "gpt-4o-mini"));

        // Authenticated (not 401): reaches the unroutable provider → governed 502, not authentication_error.
        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        Assert.Contains("api_error", await resp.Content.ReadAsStringAsync());
    }
}

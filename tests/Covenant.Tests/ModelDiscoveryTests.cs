using System.Net;
using System.Text;
using System.Text.Json;
using Covenant.Host;
using Xunit;

namespace Covenant.Tests;

/// <summary>ADR-0017: cloud model catalog discovery — fixed URLs, allow-listed egress, allowed secret references only,
/// bounded pagination/bytes, no redirects, safe error codes, the key never echoed. Fake transport only (no live provider).</summary>
public sealed class ModelDiscoveryTests
{
    private const string Key = "sk-test-SECRET-value-123456";

    private sealed class FakeHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request, Requests.Count));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static readonly HashSet<string> Egress = new(StringComparer.OrdinalIgnoreCase) { "api.anthropic.com:443", "api.openai.com:443" };

    private static Func<string, string?> Secrets(string? value = Key) => _ => value;

    private static Task<DiscoverModelsResponse> Run(FakeHandler h, string kind, string secretRef = "Anthropic:ApiKey",
        Func<string, string?>? secrets = null, IReadOnlySet<string>? egress = null)
    {
        var d = new ModelCatalogDiscovery(h);
        return d.DiscoverAsync(new DiscoverModelsRequest { Kind = kind, SecretRef = secretRef }, secrets ?? Secrets(), egress ?? Egress, default);
    }

    private static string Serialized(DiscoverModelsResponse r) => JsonSerializer.Serialize(r, ModelDiscoveryJsonContext.Default.DiscoverModelsResponse);

    [Fact]
    public async Task Anthropic_pages_are_followed_with_after_id_on_the_fixed_url_and_the_key_never_echoed()
    {
        var h = new FakeHandler((req, n) => n == 1
            ? Json("""{"data":[{"type":"model","id":"model-b","display_name":"Model B","created_at":"2026-09-21T00:00:00Z"}],"has_more":true,"last_id":"model-b"}""")
            : Json("""{"data":[{"type":"model","id":"model-a","display_name":"Model A"}],"has_more":false,"last_id":"model-a"}"""));
        var r = await Run(h, "Anthropic");

        Assert.True(r.Ok, r.Message);
        Assert.Equal(new[] { "model-b", "model-a" }, r.Models.Select(m => m.Id).ToArray());
        Assert.Equal(2, h.Requests.Count);
        Assert.All(h.Requests, q =>
        {
            Assert.Equal("api.anthropic.com", q.RequestUri!.Host);
            Assert.Equal("/v1/models", q.RequestUri.AbsolutePath);
            Assert.Equal(HttpMethod.Get, q.Method);
            Assert.Equal(Key, q.Headers.GetValues("x-api-key").Single());
            Assert.True(q.Headers.Contains("anthropic-version"));
        });
        Assert.Contains("after_id=model-b", h.Requests[1].RequestUri!.Query, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, Serialized(r), StringComparison.Ordinal);
        Assert.Contains("not approval", r.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pagination_is_bounded()
    {
        int n = 0;
        var h = new FakeHandler((_, _) => Json("{\"data\":[{\"id\":\"m" + (++n) + "\"}],\"has_more\":true,\"last_id\":\"m" + n + "\"}"));
        var r = await Run(h, "Anthropic");
        Assert.True(r.Ok);
        Assert.Equal(ModelCatalogDiscovery.MaxPages, h.Requests.Count);
        Assert.True(r.Truncated);
    }

    [Fact]
    public async Task OpenAI_uses_bearer_on_its_fixed_url_and_drops_unsafe_ids()
    {
        var h = new FakeHandler((_, _) => Json("""{"object":"list","data":[{"id":"gpt-x","created":1700000000,"owned_by":"system"},{"id":"bad\nid"},{"id":""}]}"""));
        var r = await Run(h, "OpenAi", "OpenAI:ApiKey");
        Assert.True(r.Ok, r.Message);
        Assert.Equal(new[] { "gpt-x" }, r.Models.Select(m => m.Id).ToArray());
        var q = Assert.Single(h.Requests);
        Assert.Equal(new Uri(ModelCatalogDiscovery.OpenAiUrl), q.RequestUri);
        Assert.Equal("Bearer", q.Headers.Authorization!.Scheme);
    }

    [Theory]
    [InlineData("Ollama", "Anthropic:ApiKey", "unsupported_kind")]
    [InlineData("3", "Anthropic:ApiKey", "unsupported_kind")]          // numeric enum value is not a kind
    [InlineData("OpenAiCompatible", "Anthropic:ApiKey", "unsupported_kind")]
    [InlineData("Anthropic", "Admin:Token", "secret_not_allowed")]    // never an arbitrary configuration key
    [InlineData("Anthropic", "", "secret_not_allowed")]
    public async Task Disallowed_requests_never_reach_the_network(string kind, string secretRef, string code)
    {
        var h = new FakeHandler((_, _) => throw new Xunit.Sdk.XunitException("network must not be used"));
        var r = await Run(h, kind, secretRef);
        Assert.False(r.Ok);
        Assert.Equal(code, r.Code);
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task Missing_secret_or_unapproved_egress_never_reach_the_network()
    {
        var h = new FakeHandler((_, _) => throw new Xunit.Sdk.XunitException("network must not be used"));
        Assert.Equal("secret_missing", (await Run(h, "Anthropic", secrets: Secrets(null))).Code);
        Assert.Equal("egress_not_approved", (await Run(h, "Anthropic", egress: new HashSet<string> { "api.openai.com:443" })).Code);
        Assert.Empty(h.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "unauthorized")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate_limited")]
    [InlineData(HttpStatusCode.InternalServerError, "provider_error")]
    [InlineData(HttpStatusCode.Found, "provider_error")]                 // redirects are never followed
    public async Task Provider_errors_map_to_bounded_codes_without_provider_text(HttpStatusCode status, string code)
    {
        var h = new FakeHandler((_, _) =>
        {
            var resp = Json("{\"error\":{\"message\":\"provider-detail-xyz " + Key + "\"}}", status);
            if (status == HttpStatusCode.Found) resp.Headers.Location = new Uri("http://169.254.169.254/latest");
            return resp;
        });
        var r = await Run(h, "Anthropic");
        Assert.False(r.Ok);
        Assert.Equal(code, r.Code);
        Assert.Single(h.Requests);
        var json = Serialized(r);
        Assert.DoesNotContain("provider-detail-xyz", json, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Oversized_or_malformed_answers_are_invalid_responses()
    {
        var big = new FakeHandler((_, _) => Json("{\"data\":[" + new string(' ', ModelCatalogDiscovery.MaxResponseBytes) + "]}"));
        Assert.Equal("invalid_response", (await Run(big, "Anthropic")).Code);
        var bad = new FakeHandler((_, _) => Json("not json"));
        Assert.Equal("invalid_response", (await Run(bad, "OpenAi", "OpenAI:ApiKey")).Code);
        var shape = new FakeHandler((_, _) => Json("{\"models\":[]}"));
        Assert.Equal("invalid_response", (await Run(shape, "OpenAi", "OpenAI:ApiKey")).Code);
    }

    [Fact]
    public void Discovery_urls_are_fixed_constants()
    {
        Assert.Equal("https://api.anthropic.com/v1/models", ModelCatalogDiscovery.AnthropicUrl);
        Assert.Equal("https://api.openai.com/v1/models", ModelCatalogDiscovery.OpenAiUrl);
        Assert.Null(ModelCatalogDiscovery.UrlFor(Covenant.Core.ProviderKind.Ollama));
        Assert.Null(ModelCatalogDiscovery.UrlFor(Covenant.Core.ProviderKind.OpenAiCompatible));
    }
}

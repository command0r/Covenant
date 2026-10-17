using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Covenant.Adapters;
using Covenant.Host;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MEAI = Microsoft.Extensions.AI;

namespace Covenant.Tests;

/// <summary>ADR-0010: Providers:Mode=local-only starts with no cloud credential, routes every
/// classification to the one approved local model, and never reaches a cloud client.</summary>
public sealed class LocalOnlyModeTests : IClassFixture<LocalOnlyModeTests.Factory>
{
    private const string Tag = "qwen3:8b";

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public readonly string AuditPath = Path.Combine(Path.GetTempPath(), $"covenant-localonly-{Guid.NewGuid():n}.log");
        internal readonly ScriptedChatClient LocalClient = new();
        internal readonly ScriptedChatClient CloudTripwire = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            // Neutralize any developer-shell cloud keys (env vars): local-only refuses them by design.
            builder.UseSetting("OpenAI:ApiKey", "");
            builder.UseSetting("Anthropic:ApiKey", "");
            builder.UseSetting("Providers:Mode", "local-only");
            builder.UseSetting("Local:Endpoint", "http://127.0.0.1:11434/v1");
            builder.UseSetting("Local:ModelId", Tag);
            builder.UseSetting("Admin:Token", CorrelationTestKit.AdminToken);
            builder.UseSetting("Budget:GlobalCapUsd", "10");
            builder.UseSetting("Auth:Keys:0:Key", "k");
            builder.UseSetting("Auth:Keys:0:Principal", "p");
            builder.UseSetting("Auth:Keys:0:Team", "t");
            builder.UseSetting("Audit:Path", AuditPath);
            // Stub the local endpoint in-process; register cloud keys too, as tripwires that must stay at 0 calls.
            builder.ConfigureTestServices(services =>
                services.AddSingleton<IChatClientRegistry>(new ChatClientRegistry(
                    new Dictionary<string, MEAI.IChatClient>
                    {
                        [$"local:{Tag}"] = LocalClient,
                        ["openai:gpt-4o-mini"] = CloudTripwire,
                        ["openai:gpt-4o"] = CloudTripwire,
                        ["openai"] = CloudTripwire,
                    })));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            CorrelationTestKit.DeleteWithSiblings(AuditPath);
        }
    }

    private readonly Factory _factory;
    private readonly HttpClient _client;

    public LocalOnlyModeTests(Factory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "k");
    }

    [Fact]
    public async Task Starts_without_a_cloud_key_and_reports_local_only_mode()
    {
        using var resp = await _client.SendAsync(CorrelationTestKit.Admin(HttpMethod.Get, "/admin/status"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("local-only", doc.RootElement.GetProperty("mode").GetString());
        foreach (var route in doc.RootElement.GetProperty("routes").EnumerateArray())
            Assert.Equal("local", route.GetProperty("adapter").GetString());
    }

    [Theory]
    [InlineData("a normal internal question")]
    [InlineData("my ssn is 123-45-6789")]   // PII: local, never cloud
    [InlineData("patient id 42, diagnosis pending")]   // PHI
    public async Task Every_classification_is_served_locally_and_never_by_cloud(string prompt)
    {
        int cloudBefore = _factory.CloudTripwire.Calls;
        var resp = await _client.PostAsync("/v1/chat/completions", CorrelationTestKit.Chat(prompt));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var id = CorrelationTestKit.HeaderId(resp);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(Tag, doc.RootElement.GetProperty("model").GetString());

        var e = await CorrelationTestKit.LookupAsync(_client, id);
        Assert.Equal(Tag, e.SelectedModel);
        Assert.Equal(Tag, e.AttemptedModel);
        Assert.Equal(Tag, e.ServedByModel);
        Assert.Equal("zero_priced", e.CostBasis);
        Assert.Equal(cloudBefore, _factory.CloudTripwire.Calls);
    }

    [Fact]
    public async Task Requesting_a_cloud_model_is_denied_not_rerouted()
    {
        int cloudBefore = _factory.CloudTripwire.Calls;
        var resp = await _client.PostAsync("/v1/chat/completions", CorrelationTestKit.Chat("hello", model: "gpt-4o-mini"));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        var e = await CorrelationTestKit.LookupAsync(_client, CorrelationTestKit.HeaderId(resp));
        Assert.Equal("policy", e.StoppedAtStage);
        Assert.Equal(cloudBefore, _factory.CloudTripwire.Calls);
    }

    [Fact]
    public async Task Catalog_shows_local_only_registered_local_and_no_cloud_models()
    {
        using var resp = await _client.SendAsync(CorrelationTestKit.Admin(HttpMethod.Get, "/admin/catalog"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        Assert.Equal("local-only", root.GetProperty("mode").GetString());
        var local = root.GetProperty("local");
        Assert.True(local.GetProperty("configured").GetBoolean());
        Assert.True(local.GetProperty("registered").GetBoolean());
        Assert.Equal(Tag, local.GetProperty("model_id").GetString());
        Assert.Equal("http://127.0.0.1:11434/v1", local.GetProperty("endpoint").GetString());

        var models = root.GetProperty("models").EnumerateArray().ToList();
        Assert.NotEmpty(models);
        Assert.All(models, m => Assert.Equal("local", m.GetProperty("adapter").GetString()));
        Assert.DoesNotContain(models, m => m.GetProperty("adapter").GetString() == "openai");
        var model = Assert.Single(models);
        Assert.Equal(Tag, model.GetProperty("model_id").GetString());
        Assert.True(model.GetProperty("registered").GetBoolean());
        Assert.Equal("zero", model.GetProperty("price_basis").GetString());
    }

    [Fact]
    public async Task Model_listing_exposes_only_the_local_tag()
    {
        using var resp = await _client.GetAsync("/v1/models");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var ids = doc.RootElement.GetProperty("data").EnumerateArray().Select(m => m.GetProperty("id").GetString()!).ToList();
        Assert.Equal(new[] { Tag }, ids);
    }
}

/// <summary>The startup refusal rules, tested as a pure function (Program exits the process on them,
/// which must never happen inside the test host).</summary>
public sealed class StartupValidationTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] pairs)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();

    private static List<string> Errors(params (string Key, string? Value)[] pairs)
        => StartupValidation.ValidateProviders(Config(pairs));

    [Fact]
    public void Cloud_mode_requires_the_openai_key()
    {
        Assert.Contains(Errors(), e => e.StartsWith("OpenAI:ApiKey", StringComparison.Ordinal));
        Assert.Empty(Errors(("OpenAI:ApiKey", "sk-test")));
    }

    [Fact]
    public void Local_only_with_endpoint_and_tag_needs_no_cloud_key()
        => Assert.Empty(Errors(("Providers:Mode", "local-only"), ("Local:Endpoint", "http://localhost:11434/v1"), ("Local:ModelId", "qwen3:8b")));

    [Fact]
    public void Mode_is_normalized()
    {
        Assert.Equal("local-only", StartupValidation.ProviderMode(Config(("Providers:Mode", "  Local-Only "))));
        Assert.Equal("cloud", StartupValidation.ProviderMode(Config()));
    }

    [Theory]
    [InlineData(null, "qwen3:8b", "Local:Endpoint")]
    [InlineData("http://localhost:11434/v1", null, "Local:ModelId")]
    public void Local_only_incomplete_configuration_is_refused(string? endpoint, string? model, string expectedKey)
    {
        var errors = Errors(("Providers:Mode", "local-only"), ("Local:Endpoint", endpoint), ("Local:ModelId", model));
        Assert.Contains(errors, e => e.StartsWith(expectedKey, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("OpenAI:ApiKey")]
    [InlineData("Anthropic:ApiKey")]
    public void Local_only_refuses_cloud_credentials(string key)
    {
        var errors = Errors(("Providers:Mode", "local-only"), ("Local:Endpoint", "http://localhost:11434/v1"),
            ("Local:ModelId", "qwen3:8b"), (key, "sk-present"));
        Assert.Contains(errors, e => e.Contains("no cloud fallback", StringComparison.Ordinal));
    }

    [Fact]
    public void Local_endpoint_without_tag_is_refused_in_cloud_mode_too()
    {
        var errors = Errors(("OpenAI:ApiKey", "sk-test"), ("Local:Endpoint", "http://localhost:11434/v1"));
        Assert.Contains(errors, e => e.StartsWith("Local:ModelId", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("ftp://localhost:11434/v1")]
    [InlineData("not a url")]
    [InlineData("   ")]
    public void Local_endpoint_must_be_absolute_http(string endpoint)
    {
        var errors = Errors(("OpenAI:ApiKey", "sk-test"), ("Local:Endpoint", endpoint), ("Local:ModelId", "qwen3:8b"));
        Assert.Contains(errors, e => e.StartsWith("Local:Endpoint", StringComparison.Ordinal));
    }

    [Fact]
    public void Blank_local_tag_is_refused()
        => Assert.Contains(Errors(("Providers:Mode", "local-only"), ("Local:Endpoint", "http://localhost:11434/v1"), ("Local:ModelId", "   ")),
            e => e.StartsWith("Local:ModelId", StringComparison.Ordinal));

    [Fact]
    public void Unknown_mode_is_refused()
        => Assert.Contains(Errors(("Providers:Mode", "hybrid"), ("OpenAI:ApiKey", "sk-test")),
            e => e.StartsWith("Providers:Mode", StringComparison.Ordinal));
}

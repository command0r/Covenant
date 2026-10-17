using System.Net;
using System.Text.Json;
using Covenant.Core;
using Covenant.Host;
using Xunit;

namespace Covenant.Tests;

/// <summary>POST /admin/policies/simulate is a labelled dry run on the REAL classifier and policy engine: it never
/// calls a provider (tripwire clients), never writes audit, never records spend.</summary>
public sealed class SimulatorTests : IDisposable
{
    private readonly HybridSeedFactory _factory = new();
    private readonly HttpClient _client;

    public SimulatorTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private async Task<JsonElement> Simulate(string json)
    {
        var (status, body, _) = await V2Admin.SendAsync(_client, HttpMethod.Post, "/admin/policies/simulate", json);
        if (status != HttpStatusCode.OK) throw new Xunit.Sdk.XunitException($"simulate → {(int)status}: {body}");
        Assert.True(body.GetProperty("dry_run").GetBoolean());
        return body;
    }

    private async Task<int> AuditLinesAsync()
    {
        // The bootstrap config.activated event is the first line; wait for it so the baseline is stable.
        var entries = await V2Admin.AuditAsync(_factory.AuditPath, l => l.Count >= 1);
        return entries.Count;
    }

    [Fact]
    public async Task Dry_run_routes_public_to_cloud_without_calling_any_provider_or_writing_evidence()
    {
        int before = await AuditLinesAsync();
        var body = await Simulate("""{"application_id":"demo-app","profile_id":"public-demo","prompt":"Rewrite this public announcement."}""");

        Assert.Equal("route", body.GetProperty("decision").GetString());
        Assert.Equal("Public", body.GetProperty("classification").GetString());
        Assert.Equal("Public", body.GetProperty("baseline").GetString());
        Assert.Equal("public", body.GetProperty("policy_id").GetString());
        Assert.Equal($"openai/{V2.CloudModel}", body.GetProperty("primary").GetString());
        Assert.Equal($"ollama/{V2.LocalTag}", body.GetProperty("fallbacks")[0].GetString());
        Assert.Equal(1, body.GetProperty("config_version").GetInt32());

        Assert.Equal(0, _factory.Cloud.Calls + _factory.Local.Calls);       // tripwires untouched
        var (_, status, _) = await V2Admin.SendAsync(_client, HttpMethod.Get, "/admin/status");
        Assert.Equal(0m, status.GetProperty("budget").GetProperty("global_spend_usd").GetDecimal());
        Assert.Equal(0, status.GetProperty("finops").GetProperty("requests").GetInt32());

        // FIFO proof (single-reader sink): once a later real request's record is on disk, anything the simulation
        // had enqueued would be too. Exactly one new line ⇒ the simulation wrote nothing.
        using var app = _factory.CreateClient();
        app.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "demo-key");
        using var real = await app.PostAsync("/v1/chat/completions", CorrelationTestKit.Chat("after the dry run"));
        var id = CorrelationTestKit.HeaderId(real);
        await CorrelationTestKit.LookupAsync(_client, id);
        Assert.Equal(before + 1, AuditChainVerifier.VerifyAndRead(_factory.AuditPath).Entries.Count);
    }

    [Fact]
    public async Task Dry_run_shows_detector_escalation_and_cloud_exclusion()
    {
        var body = await Simulate("""{"application_id":"demo-app","profile_id":"public-demo","prompt":"Public notice, SSN 123-45-6789"}""");
        Assert.Equal("Pii", body.GetProperty("classification").GetString());
        Assert.Equal("Public", body.GetProperty("baseline").GetString());
        Assert.Equal("US SSN pattern", body.GetProperty("detected_signal").GetString());
        Assert.Equal($"ollama/{V2.LocalTag}", body.GetProperty("primary").GetString());

        var refused = await Simulate($$"""{"application_id":"demo-app","profile_id":"public-demo","prompt":"Public notice, SSN 123-45-6789","model":"{{V2.CloudModel}}"}""");
        Assert.Equal("refuse", refused.GetProperty("decision").GetString());
        Assert.Equal(0, _factory.Cloud.Calls + _factory.Local.Calls);
    }

    [Fact]
    public async Task Dry_run_refuses_ungranted_profiles_and_unknown_applications()
    {
        var ungranted = await Simulate("""{"application_id":"demo-app","profile_id":"phi-anything","prompt":"x"}""");
        Assert.Equal("refuse", ungranted.GetProperty("decision").GetString());
        Assert.Contains("not granted", ungranted.GetProperty("reason").GetString(), StringComparison.Ordinal);

        var unknown = await Simulate("""{"application_id":"ghost","prompt":"x"}""");
        Assert.Equal("refuse", unknown.GetProperty("decision").GetString());
    }

    [Fact]
    public async Task Dry_run_against_a_candidate_uses_it_without_applying_it()
    {
        var (_, cfg, _) = await V2Admin.GetConfigAsync(_client);
        var paused = cfg with { Policies = cfg.Policies.Select(p => p.Id == "public" ? p with { Paused = true } : p).ToList() };
        var json = JsonSerializer.Serialize(new SimulateRequest
        {
            ApplicationId = "demo-app", ProfileId = "public-demo", Prompt = "Rewrite this public announcement.", Config = paused,
        }, ConfigJsonContext.Default.SimulateRequest);

        var body = await Simulate(json);
        Assert.Equal("refuse", body.GetProperty("decision").GetString());
        Assert.Contains("paused", body.GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.Equal(1, (await V2Admin.GetConfigAsync(_client)).Version);   // never applied
        Assert.Equal(0, _factory.Cloud.Calls + _factory.Local.Calls);
    }

    [Fact]
    public async Task Simulator_is_admin_only()
    {
        using var anon = _factory.CreateClient();
        var resp = await anon.PostAsync("/admin/policies/simulate",
            new StringContent("""{"application_id":"demo-app","prompt":"x"}""", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}

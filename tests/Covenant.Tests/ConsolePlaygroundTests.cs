using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Covenant.Core;
using Covenant.Host;
using Xunit;

namespace Covenant.Tests;

internal static class Playground
{
    public static string Body(string applicationId, string prompt, string? profile = null, bool stream = false,
        string? clientRequestId = null, int? maxTokens = null, string? model = null)
    {
        var sb = new StringBuilder();
        sb.Append("{\"application_id\":").Append(JsonSerializer.Serialize(applicationId, CovenantJsonContext.Default.String));
        if (profile is not null) sb.Append(",\"profile_id\":").Append(JsonSerializer.Serialize(profile, CovenantJsonContext.Default.String));
        sb.Append(",\"messages\":[{\"role\":\"user\",\"content\":").Append(JsonSerializer.Serialize(prompt, CovenantJsonContext.Default.String)).Append("}]");
        sb.Append(",\"stream\":").Append(stream ? "true" : "false");
        // Review round 2 (B7): client_request_id is required by the console endpoint — generated when the test names none.
        clientRequestId ??= "t-" + Guid.NewGuid().ToString("n");
        sb.Append(",\"client_request_id\":\"").Append(clientRequestId).Append('"');
        if (maxTokens is not null) sb.Append(",\"max_tokens\":").Append(maxTokens.Value);
        if (model is not null) sb.Append(",\"model\":").Append(JsonSerializer.Serialize(model, CovenantJsonContext.Default.String));
        return sb.Append('}').ToString();
    }

    public static async Task<HttpResponseMessage> PostAsync(HttpClient client, string json, bool admin = true)
    {
        using var req = admin
            ? CorrelationTestKit.Admin(HttpMethod.Post, "/admin/playground/requests")
            : new HttpRequestMessage(HttpMethod.Post, "/admin/playground/requests");
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await client.SendAsync(req);
    }

    /// <summary>Adds a disabled application and an application that is not console-testable (validated apply).</summary>
    public static async Task<int> AddTestApplicationsAsync(HttpClient admin)
    {
        var (version, cfg, _) = await V2Admin.GetConfigAsync(admin);
        var apps = cfg.Applications.ToList();
        apps.Add(new ApplicationConfig { Id = "off-app", Team = "data", Enabled = false, ConsoleTesting = true, GrantedProfiles = ["public-demo"], DefaultProfile = "public-demo" });
        apps.Add(new ApplicationConfig { Id = "batch-app", Team = "data", Enabled = true, ConsoleTesting = false, GrantedProfiles = ["internal-default"], DefaultProfile = "internal-default" });
        return await V2Admin.ApplyAsync(admin, cfg with { Applications = apps }, version, "test applications");
    }
}

/// <summary>ADR-0012 console inference: POST /admin/playground/requests runs the SAME governed pipeline as /v1 with an
/// application resolved server-side; 400 before governance (no audit), governed 403 with audit, 409 on a repeated
/// client_request_id, no application credential anywhere in the browser-facing payloads.</summary>
public sealed class ConsolePlaygroundTests : IDisposable
{
    private readonly HybridSeedFactory _factory = new();
    private readonly HttpClient _client;

    public ConsolePlaygroundTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Console_request_runs_the_governed_pipeline_as_the_application_with_honest_operator_attribution()
    {
        using var resp = await Playground.PostAsync(_client, Playground.Body("demo-app", "Summarize this public announcement.", profile: "public-demo"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var id = CorrelationTestKit.HeaderId(resp);
        var text = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text);
        Assert.Equal(id, doc.RootElement.GetProperty("id").GetString());               // same response contract as /v1
        Assert.Equal(V2.CloudModel, doc.RootElement.GetProperty("model").GetString());
        Assert.Equal("cloud reply", doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());

        var e = await CorrelationTestKit.LookupAsync(_client, id);
        Assert.Equal("console", e.EntryPoint);
        Assert.Equal("shared-admin-token", e.OperatorRef);
        Assert.Equal("demo-app", e.ApplicationId);
        Assert.Equal("platform", e.Tags.Team);
        Assert.Equal("public-demo", e.ProfileId);
        Assert.Equal(1, e.ConfigVersion);
        Assert.NotNull(e.Trace);
        Assert.Single(e.Attempts!);
        Assert.True(e.ReservedUsd > 0m);
        Assert.Equal(e.Usage.CostUsd, e.SettledUsd);

        // Credentials never cross to the browser: no application key, admin token or provider secret in the payload.
        Assert.DoesNotContain("demo-key", text, StringComparison.Ordinal);
        Assert.DoesNotContain(CorrelationTestKit.AdminToken, text, StringComparison.Ordinal);
        Assert.DoesNotContain(HybridSeedFactory.Secret, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Streaming_console_request_speaks_the_same_SSE_contract()
    {
        using var resp = await Playground.PostAsync(_client, Playground.Body("demo-app", "an internal question", stream: true));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);
        var id = CorrelationTestKit.HeaderId(resp);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("local reply", body, StringComparison.Ordinal);
        Assert.Contains($"\"id\":\"{id}\"", body, StringComparison.Ordinal);
        Assert.EndsWith("data: [DONE]\n\n", body, StringComparison.Ordinal);
        var e = await CorrelationTestKit.LookupAsync(_client, id);
        Assert.Equal("console", e.EntryPoint);
        Assert.True(e.Stream);
    }

    [Theory]
    [InlineData("off-app", null, "disabled")]
    [InlineData("batch-app", null, "not enabled for console testing")]
    [InlineData("demo-app", "phi-workload", "not granted")]
    [InlineData("ghost-app", null, "is not configured")]
    public async Task Refused_identities_are_governed_403_with_an_audit_record_and_no_provider_call(string app, string? profile, string reason)
    {
        await Playground.AddTestApplicationsAsync(_client);
        int calls = _factory.Cloud.Calls + _factory.Local.Calls;
        using var resp = await Playground.PostAsync(_client, Playground.Body(app, "hello", profile: profile));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        var id = CorrelationTestKit.HeaderId(resp);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("denied", doc.RootElement.GetProperty("error").GetString());
        Assert.Equal(id, doc.RootElement.GetProperty("request_id").GetString());

        var e = await CorrelationTestKit.LookupAsync(_client, id);
        Assert.Equal("auth", e.StoppedAtStage);
        Assert.Equal("Governance", e.DenialKind);
        Assert.Equal("console", e.EntryPoint);
        Assert.Contains(reason, e.Reason, StringComparison.Ordinal);
        Assert.Equal(calls, _factory.Cloud.Calls + _factory.Local.Calls);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("""{"profile_id":"public-demo","messages":[{"role":"user","content":"x"}]}""")]
    [InlineData("""{"application_id":"demo-app","messages":[]}""")]
    [InlineData("""{"application_id":"demo-app","messages":[{"role":"wizard","content":"x"}]}""")]
    [InlineData("""{"application_id":"demo-app","messages":[{"role":"user","content":null}]}""")]
    [InlineData("""{"application_id":"demo-app","messages":[{"role":"user","content":"x"}],"max_tokens":0}""")]
    [InlineData("""{"application_id":"demo-app","messages":[{"role":"user","content":"x"}],"client_request_id":"bad id with spaces"}""")]
    [InlineData("""{"application_id":"demo-app","messages":[{"role":"user","content":"x"}]}""")]                       // B7: client_request_id required
    [InlineData("""{"application_id":"demo-app","messages":[{"role":"user","content":"x"}],"client_request_id":"a.b:c"}""")]   // B7: [A-Za-z0-9_-] only
    [InlineData("""{"application_id":"demo-app","messages":[{"role":"user","content":"x"}],"client_request_id":"0123456789012345678901234567890123456789012345678901234567890123x"}""")]   // 65 chars
    public async Task Invalid_body_is_400_before_governance_with_no_audit_record(string json)
    {
        await V2Admin.AuditAsync(_factory.AuditPath, l => l.Count >= 1);   // bootstrap event is on disk
        using var bad = await Playground.PostAsync(_client, json);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var badId = CorrelationTestKit.HeaderId(bad);
        using var doc = JsonDocument.Parse(await bad.Content.ReadAsStringAsync());
        Assert.Equal("invalid_request", doc.RootElement.GetProperty("error").GetString());

        // FIFO proof (single-reader sink): a later request's record exists ⇒ nothing was ever enqueued for the 400.
        using var ok = await Playground.PostAsync(_client, Playground.Body("demo-app", "after the bad one"));
        await CorrelationTestKit.LookupAsync(_client, CorrelationTestKit.HeaderId(ok));
        using var lookup = await _client.SendAsync(CorrelationTestKit.Admin(HttpMethod.Get, $"/admin/requests/{badId}"));
        Assert.Equal(HttpStatusCode.NotFound, lookup.StatusCode);
        Assert.Equal(1, _factory.Local.Calls);
    }

    [Fact]
    public async Task Repeated_client_request_id_is_409_with_the_original_request_id_and_never_runs_again()
    {
        using var first = await Playground.PostAsync(_client, Playground.Body("demo-app", "run once", clientRequestId: "run-7f3a"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var originalId = CorrelationTestKit.HeaderId(first);
        int calls = _factory.Local.Calls;

        using var again = await Playground.PostAsync(_client, Playground.Body("demo-app", "run once", clientRequestId: "run-7f3a"));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        using var doc = JsonDocument.Parse(await again.Content.ReadAsStringAsync());
        Assert.Equal("duplicate_submission", doc.RootElement.GetProperty("error").GetString());
        Assert.Equal(originalId, doc.RootElement.GetProperty("request_id").GetString());
        Assert.Equal(calls, _factory.Local.Calls);                               // a reload never replays inference
    }

    [Fact]
    public async Task Admin_token_is_required_and_v1_is_unchanged()
    {
        using var noToken = await Playground.PostAsync(_client, Playground.Body("demo-app", "hi"), admin: false);
        Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);

        // /v1 still requires an application key: the admin token is not one.
        using var v1 = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = CorrelationTestKit.Chat("hi") };
        v1.Headers.Add("X-Covenant-Admin-Token", CorrelationTestKit.AdminToken);
        using var v1Resp = await _client.SendAsync(v1);
        Assert.Equal(HttpStatusCode.Unauthorized, v1Resp.StatusCode);

        using var app = _factory.CreateClient();
        app.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "demo-key");
        using var keyed = await app.PostAsync("/v1/chat/completions", CorrelationTestKit.Chat("hi from v1"));
        Assert.Equal(HttpStatusCode.OK, keyed.StatusCode);
        var e = await CorrelationTestKit.LookupAsync(_client, CorrelationTestKit.HeaderId(keyed));
        Assert.Equal("v1", e.EntryPoint);
        Assert.Null(e.OperatorRef);
    }

    [Fact]
    public void Submissions_window_expires_and_is_bounded()
    {
        var clock = new SettableClock(DateTimeOffset.Parse("2026-10-17T10:00:00Z"));
        var s = new ConsoleSubmissions(clock, TimeSpan.FromMinutes(10), maxEntries: 4);
        Assert.True(s.TryClaim("a", "req_1", out _));
        Assert.False(s.TryClaim("a", "req_2", out var original));
        Assert.Equal("req_1", original);
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.True(s.TryClaim("a", "req_3", out _));                       // outside the 10-minute window
        for (int i = 0; i < 10; i++) Assert.True(s.TryClaim($"k{i}", $"req_k{i}", out _));
    }
}

using System.Net;
using System.Text.Json;
using Covenant.Core;
using Covenant.Host;
using Xunit;

namespace Covenant.Tests;

/// <summary>/admin/config* through the real Host (ADR-0011, docs/ui-api-contract.md v2): ETag, validate never
/// mutates, PUT preconditions (428/412/422/200), audit event on activation, rollback as a new version, and secret
/// values never in any payload. Each test gets its own Host (isolated config directory + audit file).</summary>
public sealed class ConfigApiTests : IDisposable
{
    private readonly HybridSeedFactory _factory = new();
    private readonly HttpClient _client;

    public ConfigApiTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private static CovenantConfig WithCacheTtl(CovenantConfig c, int ttl) => c with { Settings = c.Settings with { CacheTtlSeconds = ttl } };

    [Fact]
    public async Task Get_returns_version_etag_redacted_secrets_and_allow_list()
    {
        var (status, body, resp) = await V2Admin.SendAsync(_client, HttpMethod.Get, "/admin/config");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, body.GetProperty("version").GetInt32());
        Assert.Equal("\"v1\"", body.GetProperty("etag").GetString());
        Assert.Equal("\"v1\"", resp.Headers.ETag?.ToString());
        Assert.True(body.GetProperty("secrets").GetProperty("OpenAI:ApiKey").GetProperty("available").GetBoolean());
        Assert.Contains(body.GetProperty("allow_list").EnumerateArray(), e => e.GetString() == "127.0.0.1:11434");
        Assert.Equal("seed:demo-hybrid", body.GetProperty("config").GetProperty("origin").GetString());
        Assert.Equal("hybrid", body.GetProperty("config").GetProperty("mode").GetString());
    }

    [Fact]
    public async Task Admin_config_requires_the_admin_token_not_an_application_key()
    {
        using var anon = _factory.CreateClient();
        anon.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "demo-key");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/admin/config")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/admin/applications")).StatusCode);
    }

    [Fact]
    public async Task Validate_reports_errors_and_impact_and_never_mutates()
    {
        var (_, cfg, _) = await V2Admin.GetConfigAsync(_client);
        var broken = cfg with { Connections = [.. cfg.Connections, cfg.Connections[0]] };   // duplicate id
        var json = JsonSerializer.Serialize(new ConfigValidateRequest { BaseVersion = 1, Config = broken }, ConfigJsonContext.Default.ConfigValidateRequest);

        var (status, body, _) = await V2Admin.SendAsync(_client, HttpMethod.Post, "/admin/config/validate", json);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(body.GetProperty("valid").GetBoolean());
        Assert.Contains(body.GetProperty("errors").EnumerateArray(), e => e.GetProperty("code").GetString() == "duplicate_id");
        Assert.True(body.GetProperty("impact").GetProperty("classifications").GetArrayLength() > 0);

        var (version, _, _) = await V2Admin.GetConfigAsync(_client);
        Assert.Equal(1, version);                                   // nothing activated
        Assert.False(File.Exists(Path.Combine(_factory.ConfigDir, "versions", "v000002.json")));
    }

    [Fact]
    public async Task Validate_impact_names_newly_refused_and_unavailable_destinations()
    {
        var (_, cfg, _) = await V2Admin.GetConfigAsync(_client);
        var disabledLocal = cfg with
        {
            Connections = cfg.Connections.Select(c => c.Id == "ollama" ? c with { Enabled = false } : c).ToList(),
        };
        var json = JsonSerializer.Serialize(new ConfigValidateRequest { BaseVersion = 1, Config = disabledLocal }, ConfigJsonContext.Default.ConfigValidateRequest);
        var (_, body, _) = await V2Admin.SendAsync(_client, HttpMethod.Post, "/admin/config/validate", json);

        var impact = body.GetProperty("impact");
        Assert.Contains(impact.GetProperty("newly_refused").EnumerateArray(), e => e.GetString()!.StartsWith("Internal", StringComparison.Ordinal));
        Assert.Contains(impact.GetProperty("unavailable_destinations").EnumerateArray(), e => e.GetString()!.Contains("connection disabled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Put_without_if_match_is_428_and_changes_nothing()
    {
        var (_, cfg, _) = await V2Admin.GetConfigAsync(_client);
        var (status, body, _) = await V2Admin.SendAsync(_client, HttpMethod.Put, "/admin/config", V2Admin.PutBody(WithCacheTtl(cfg, 30), "no precondition"));
        Assert.Equal((HttpStatusCode)428, status);
        Assert.Equal("precondition_required", body.GetProperty("error").GetString());
        Assert.Equal(1, (await V2Admin.GetConfigAsync(_client)).Version);
    }

    [Fact]
    public async Task Stale_if_match_is_412()
    {
        var (_, cfg, _) = await V2Admin.GetConfigAsync(_client);
        Assert.Equal(2, await V2Admin.ApplyAsync(_client, WithCacheTtl(cfg, 30), 1));
        var (status, body, _) = await V2Admin.SendAsync(_client, HttpMethod.Put, "/admin/config",
            V2Admin.PutBody(WithCacheTtl(cfg, 60), "stale browser tab"), "\"v1\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, status);
        Assert.Equal(2, body.GetProperty("active_version").GetInt32());
        var (version, active, _) = await V2Admin.GetConfigAsync(_client);
        Assert.Equal(2, version);
        Assert.Equal(30, active.Settings.CacheTtlSeconds);
    }

    [Fact]
    public async Task Invalid_put_is_422_and_the_active_version_keeps_serving()
    {
        var (_, cfg, _) = await V2Admin.GetConfigAsync(_client);
        // An enabled cloud connection is not allowed in local-only mode.
        var invalid = cfg with { Mode = ProviderMode.LocalOnly };
        var (status, body, _) = await V2Admin.SendAsync(_client, HttpMethod.Put, "/admin/config", V2Admin.PutBody(invalid, "bad"), "\"v1\"");
        Assert.Equal((HttpStatusCode)422, status);
        Assert.Contains(body.GetProperty("errors").EnumerateArray(), e => e.GetProperty("code").GetString() == "cloud_in_local_only");

        var (version, active, _) = await V2Admin.GetConfigAsync(_client);
        Assert.Equal(1, version);
        Assert.Equal(ProviderMode.Hybrid, active.Mode);

        // The active version still serves requests.
        using var app = _factory.CreateClient();
        app.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "demo-key");
        var resp = await app.PostAsync("/v1/chat/completions", CorrelationTestKit.Chat("internal question"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Successful_put_activates_a_new_version_and_appends_a_config_activated_event()
    {
        var (_, cfg, _) = await V2Admin.GetConfigAsync(_client);
        var (status, body, resp) = await V2Admin.SendAsync(_client, HttpMethod.Put, "/admin/config",
            V2Admin.PutBody(WithCacheTtl(cfg, 45), "enable cache for the demo"), "\"v1\"");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2, body.GetProperty("version").GetInt32());
        Assert.Equal("\"v2\"", body.GetProperty("etag").GetString());
        Assert.Equal("\"v2\"", resp.Headers.ETag?.ToString());

        var (version, active, _) = await V2Admin.GetConfigAsync(_client);
        Assert.Equal(2, version);
        Assert.Equal(45, active.Settings.CacheTtlSeconds);
        Assert.Equal("console", active.Origin);
        Assert.Equal(1, active.BaseVersion);

        var entries = await V2Admin.AuditAsync(_factory.AuditPath,
            l => l.Any(e => e.EventType == AuditEntryKinds.ConfigActivated && e.ConfigVersion == 2));
        var ev = entries.Single(e => e.EventType == AuditEntryKinds.ConfigActivated && e.ConfigVersion == 2);
        Assert.Equal("shared-admin-token", ev.OperatorRef);
        Assert.Contains("enable cache for the demo", ev.Reason, StringComparison.Ordinal);
        Assert.Contains(entries, e => e.EventType == AuditEntryKinds.ConfigActivated && e.ConfigVersion == 1);   // bootstrap

        // Events are evidence, not requests: excluded from the request list and evidence counts.
        var (_, list, _) = await V2Admin.SendAsync(_client, HttpMethod.Get, "/admin/requests");
        Assert.DoesNotContain(list.GetProperty("items").EnumerateArray(), i => i.TryGetProperty("EventType", out var t) && t.ValueKind == JsonValueKind.String);
        var (_, evidence, _) = await V2Admin.SendAsync(_client, HttpMethod.Get, "/admin/evidence");
        Assert.True(evidence.GetProperty("events").GetInt32() >= 2);
        Assert.Equal(0m, evidence.GetProperty("total_cost_usd").GetDecimal());

        var (_, versions, _) = await V2Admin.SendAsync(_client, HttpMethod.Get, "/admin/config/versions");
        Assert.Equal(2, versions[0].GetProperty("version").GetInt32());
        Assert.True(versions[0].GetProperty("active").GetBoolean());
        Assert.Equal("enable cache for the demo", versions[0].GetProperty("change_summary").GetString());
    }

    [Fact]
    public async Task Rollback_creates_a_new_version_with_the_old_content()
    {
        var (_, cfg, _) = await V2Admin.GetConfigAsync(_client);
        await V2Admin.ApplyAsync(_client, WithCacheTtl(cfg, 45), 1);

        var stale = await V2Admin.SendAsync(_client, HttpMethod.Post, "/admin/config/rollback",
            """{"target_version":1,"expected_active_version":1}""");
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.Status);

        var (status, body, _) = await V2Admin.SendAsync(_client, HttpMethod.Post, "/admin/config/rollback",
            """{"target_version":1,"expected_active_version":2}""");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(3, body.GetProperty("version").GetInt32());

        var (version, active, _) = await V2Admin.GetConfigAsync(_client);
        Assert.Equal(3, version);
        Assert.Equal(0, active.Settings.CacheTtlSeconds);
        Assert.Equal(1, active.RolledBackFrom);
        Assert.Equal("rollback", active.Origin);

        var (_, v2, _) = await V2Admin.SendAsync(_client, HttpMethod.Get, "/admin/config/versions/2");
        Assert.Equal(45, v2.GetProperty("settings").GetProperty("cache_ttl_seconds").GetInt32());   // history kept
    }

    [Fact]
    public async Task Malformed_body_is_400_and_unknown_version_is_404()
    {
        var bad = await V2Admin.SendAsync(_client, HttpMethod.Put, "/admin/config", "{ nope", "\"v1\"");
        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
        var missing = await V2Admin.SendAsync(_client, HttpMethod.Get, "/admin/config/versions/99");
        Assert.Equal(HttpStatusCode.NotFound, missing.Status);
    }

    [Fact]
    public async Task Secret_values_never_appear_in_any_admin_payload()
    {
        var (_, cfg, raw) = await V2Admin.GetConfigAsync(_client);
        Assert.DoesNotContain(HybridSeedFactory.Secret, raw, StringComparison.Ordinal);
        foreach (var path in new[] { "/admin/catalog", "/admin/status", "/admin/applications", "/admin/config/versions", "/admin/config/versions/1" })
        {
            using var resp = await _client.SendAsync(CorrelationTestKit.Admin(HttpMethod.Get, path));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.DoesNotContain(HybridSeedFactory.Secret, await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        var json = JsonSerializer.Serialize(new ConfigValidateRequest { BaseVersion = 1, Config = cfg }, ConfigJsonContext.Default.ConfigValidateRequest);
        var (_, validated, _) = await V2Admin.SendAsync(_client, HttpMethod.Post, "/admin/config/validate", json);
        Assert.DoesNotContain(HybridSeedFactory.Secret, validated.GetRawText(), StringComparison.Ordinal);

        await V2Admin.ApplyAsync(_client, WithCacheTtl(cfg, 5), 1);
        foreach (var file in Directory.GetFiles(_factory.ConfigDir, "*", SearchOption.AllDirectories))
            Assert.DoesNotContain(HybridSeedFactory.Secret, await File.ReadAllTextAsync(file), StringComparison.Ordinal);
        Assert.DoesNotContain(HybridSeedFactory.Secret, CorrelationTestKit.ReadShared(_factory.AuditPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Applications_lists_identities_without_credentials()
    {
        var (status, body, _) = await V2Admin.SendAsync(_client, HttpMethod.Get, "/admin/applications");
        Assert.Equal(HttpStatusCode.OK, status);
        var demo = body.EnumerateArray().Single(a => a.GetProperty("id").GetString() == "demo-app");
        Assert.True(demo.GetProperty("console_testing").GetBoolean());
        Assert.Equal("internal-default", demo.GetProperty("default_profile").GetString());
        Assert.DoesNotContain("demo-key", body.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_and_catalog_report_mode_version_and_connections()
    {
        var (_, status, _) = await V2Admin.SendAsync(_client, HttpMethod.Get, "/admin/status");
        Assert.Equal("hybrid", status.GetProperty("mode").GetString());
        Assert.Equal(1, status.GetProperty("config_version").GetInt32());

        var (_, catalog, _) = await V2Admin.SendAsync(_client, HttpMethod.Get, "/admin/catalog");
        Assert.Equal("hybrid", catalog.GetProperty("mode").GetString());
        var connections = catalog.GetProperty("connections").EnumerateArray().ToList();
        var openai = connections.Single(c => c.GetProperty("id").GetString() == "openai");
        Assert.Equal("Cloud", openai.GetProperty("boundary").GetString());
        Assert.True(openai.GetProperty("secret_available").GetBoolean());
        var ollama = connections.Single(c => c.GetProperty("id").GetString() == "ollama");
        Assert.Equal("Local", ollama.GetProperty("boundary").GetString());
        Assert.True(ollama.GetProperty("models")[0].GetProperty("registered").GetBoolean());
        Assert.Equal($"ollama:{V2.LocalTag}", ollama.GetProperty("models")[0].GetProperty("key").GetString());
    }
}

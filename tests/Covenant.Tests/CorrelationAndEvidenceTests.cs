using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Covenant.Adapters;
using Covenant.Core;
using Covenant.Governance;
using Covenant.Host;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MEAI = Microsoft.Extensions.AI;

namespace Covenant.Tests;

/// <summary>Shared helpers for the ADR-0009 host-level tests (correlation id ⇄ audit record).</summary>
internal static class CorrelationTestKit
{
    public const string AdminToken = "test-admin";
    public const string Header = "X-Covenant-Request-Id";

    public static StringContent Chat(string content, bool stream = false, string? model = null) =>
        new($"{{{(model is null ? "" : $"\"model\":\"{model}\",")}\"stream\":{(stream ? "true" : "false")},\"messages\":[{{\"role\":\"user\",\"content\":\"{content}\"}}]}}",
            Encoding.UTF8, "application/json");

    public static string HeaderId(HttpResponseMessage resp)
    {
        Assert.True(resp.Headers.TryGetValues(Header, out var values), $"missing {Header} on HTTP {(int)resp.StatusCode}");
        var id = Assert.Single(values!);
        Assert.True(RequestCorrelation.IsWellFormed(id), $"malformed correlation id '{id}'");
        Assert.StartsWith("req_", id);
        return id;
    }

    public static HttpRequestMessage Admin(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Add("X-Covenant-Admin-Token", AdminToken);
        return req;
    }

    /// <summary>Polls the exact lookup (the sink is asynchronous) — never sleeps a fixed time.</summary>
    public static async Task<AuditEntry> LookupAsync(HttpClient client, string id)
    {
        for (int i = 0; i < 200; i++)
        {
            using var resp = await client.SendAsync(Admin(HttpMethod.Get, $"/admin/requests/{id}"));
            if (resp.StatusCode == HttpStatusCode.OK)
            {
                var entry = JsonSerializer.Deserialize(await resp.Content.ReadAsStringAsync(), AuditJsonContext.Default.AuditEntry);
                Assert.NotNull(entry);
                return entry!;
            }
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            await Task.Delay(25);
        }
        throw new Xunit.Sdk.XunitException($"audit record {id} never appeared");
    }

    public static string ReadShared(string path)
    {
        if (!File.Exists(path)) return "";
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd();
    }

    /// <summary>Every per-instance durable path derives from ONE unique audit path (no shared /tmp default): the audit
    /// log, the configuration repository (Config:Path) and the budget ledger (Budget:LedgerPath).</summary>
    public static void IsolatePaths(IWebHostBuilder builder, string auditPath)
    {
        builder.UseSetting("Audit:Path", auditPath);
        builder.UseSetting("Config:Path", ConfigDirFor(auditPath));
        builder.UseSetting("Budget:LedgerPath", auditPath + ".ledger.jsonl");
    }

    public static string ConfigDirFor(string auditPath) => auditPath + ".config";

    /// <summary>Deletes the audit file and every sibling derived from it (archives, anchors, ledger, config repository).</summary>
    public static void DeleteWithSiblings(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        foreach (var f in Directory.GetFiles(dir, Path.GetFileName(path) + "*"))
            File.Delete(f);
        foreach (var d in Directory.GetDirectories(dir, Path.GetFileName(path) + "*"))
            try { Directory.Delete(d, recursive: true); } catch (IOException) { }
    }
}

/// <summary>Scripted provider for host tests. Prompt markers select behaviour: "FAIL-UPSTREAM" throws on
/// the buffered call; "FAIL-MIDSTREAM" streams one delta then throws. Everything else is served with usage.</summary>
internal sealed class ScriptedChatClient : MEAI.IChatClient
{
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);

    private static string Prompt(IEnumerable<MEAI.ChatMessage> messages) => string.Join("\n", messages.Select(m => m.Text));

    public Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> messages, MEAI.ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        if (Prompt(messages).Contains("FAIL-UPSTREAM", StringComparison.Ordinal))
            throw new HttpRequestException("simulated upstream 503");
        return Task.FromResult(new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, "scripted reply"))
        { Usage = new MEAI.UsageDetails { InputTokenCount = 7, OutputTokenCount = 3 } });
    }

    public async IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<MEAI.ChatMessage> messages, MEAI.ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        bool fail = Prompt(messages).Contains("FAIL-MIDSTREAM", StringComparison.Ordinal);
        await Task.Yield();
        yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, "partial ");
        if (fail) throw new HttpRequestException("connection reset mid-stream");
        yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, "rest");
        yield return new MEAI.ChatResponseUpdate { Contents = [new MEAI.UsageContent(new MEAI.UsageDetails { InputTokenCount = 7, OutputTokenCount = 3 })] };
        yield return new MEAI.ChatResponseUpdate { FinishReason = MEAI.ChatFinishReason.Stop };   // terminal completion (real providers send one)
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

/// <summary>ADR-0009 / Task 02: one server-generated id per /v1 request, equal across header, body and
/// audit record, for every outcome the Host can produce; exact admin-only lookup.</summary>
public sealed class CorrelationAndEvidenceTests : IClassFixture<CorrelationAndEvidenceTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        public readonly string AuditPath = Path.Combine(Path.GetTempPath(), $"covenant-corr-{Guid.NewGuid():n}.log");
        internal readonly ScriptedChatClient Provider = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("OpenAI:ApiKey", "unused-stubbed");
            builder.UseSetting("Admin:Token", CorrelationTestKit.AdminToken);
            builder.UseSetting("Budget:GlobalCapUsd", "100");
            builder.UseSetting("Auth:Keys:0:Key", "k");
            builder.UseSetting("Auth:Keys:0:Principal", "p");
            builder.UseSetting("Auth:Keys:0:Team", "t");
            CorrelationTestKit.IsolatePaths(builder, AuditPath);
            // No Local:* → PII routes to the unconfigured local placeholder and fails closed.
            builder.ConfigureTestServices(services =>
                services.AddSingleton<IChatClientRegistry>(new ChatClientRegistry(
                    new Dictionary<string, MEAI.IChatClient>
                    {
                        ["openai:gpt-4o-mini"] = Provider,
                        ["openai:gpt-4o"] = Provider,
                    })));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            CorrelationTestKit.DeleteWithSiblings(AuditPath);
        }
    }

    private readonly Factory _factory;
    private readonly HttpClient _client;   // with application key
    private readonly HttpClient _anon;     // no credentials at all

    public CorrelationAndEvidenceTests(Factory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "k");
        _anon = factory.CreateClient();
    }

    private static string BodyString(JsonElement root, string name) => root.GetProperty(name).GetString()!;

    [Fact]
    public async Task Buffered_200_header_equals_body_id_equals_audit_record()
    {
        var resp = await _client.PostAsync("/v1/chat/completions", CorrelationTestKit.Chat("a normal internal question"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var id = CorrelationTestKit.HeaderId(resp);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(id, BodyString(doc.RootElement, "id"));

        var e = await CorrelationTestKit.LookupAsync(_client, id);
        Assert.Equal(id, e.Id);
        Assert.Equal(id, e.CorrelationId);
        Assert.Equal(2, e.SchemaVersion);
        Assert.Equal(PolicyEffect.Allow, e.Effect);
        Assert.Null(e.DenialKind);
        Assert.Null(e.StoppedAtStage);
        Assert.True(e.ClassificationEvaluated);
        Assert.Equal("gpt-4o-mini", e.SelectedModel);
        Assert.Equal("gpt-4o-mini", e.AttemptedModel);
        Assert.Equal("gpt-4o-mini", e.ServedByModel);
        Assert.False(e.Stream);
        Assert.True(e.UsageReported);
        Assert.Equal("reported", e.CostBasis);
    }

    [Fact]
    public async Task Anthropic_message_id_is_the_correlation_id()
    {
        using var aclient = _factory.CreateClient();
        aclient.DefaultRequestHeaders.Add("x-api-key", "k");
        var resp = await aclient.PostAsync("/v1/messages", new StringContent(
            "{\"model\":\"gpt-4o-mini\",\"max_tokens\":20,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}",
            Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var id = CorrelationTestKit.HeaderId(resp);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(id, BodyString(doc.RootElement, "id"));
        Assert.Equal(id, (await CorrelationTestKit.LookupAsync(_client, id)).Id);
    }

    [Fact]
    public async Task Auth_denial_401_is_correlated_and_classification_is_not_evaluated()
    {
        var resp = await _anon.PostAsync("/v1/chat/completions", CorrelationTestKit.Chat("no key here"));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        var id = CorrelationTestKit.HeaderId(resp);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(id, BodyString(doc.RootElement, "request_id"));

        var e = await CorrelationTestKit.LookupAsync(_client, id);
        Assert.Equal(PolicyEffect.Deny, e.Effect);
        Assert.Equal("Unauthenticated", e.DenialKind);
        Assert.Equal("auth", e.StoppedAtStage);
        Assert.False(e.ClassificationEvaluated);          // never ran ...
        Assert.Equal(DataClassification.Phi, e.Classification);  // ... restrictive enforcement default kept
        Assert.Null(e.SelectedModel);
        Assert.Null(e.AttemptedModel);
        Assert.Null(e.UsageReported);
        Assert.Equal("no_call", e.CostBasis);
        Assert.Equal(0m, e.Usage.CostUsd);
    }

    [Fact]
    public async Task Policy_denial_403_for_unapproved_model_stops_at_policy()
    {
        var resp = await _client.PostAsync("/v1/chat/completions", CorrelationTestKit.Chat("hello", model: "not-an-approved-model"));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        var id = CorrelationTestKit.HeaderId(resp);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(id, BodyString(doc.RootElement, "request_id"));

        var e = await CorrelationTestKit.LookupAsync(_client, id);
        Assert.Equal("Governance", e.DenialKind);
        Assert.Equal("policy", e.StoppedAtStage);
        Assert.True(e.ClassificationEvaluated);
        Assert.Equal(DataClassification.Internal, e.Classification);
        Assert.Null(e.AttemptedModel);
        Assert.Equal("no_call", e.CostBasis);
    }

    [Fact]
    public async Task Pause_403_stops_at_pause_without_calling_the_provider()
    {
        using (var engage = CorrelationTestKit.Admin(HttpMethod.Post, "/admin/kill-switch"))
        {
            engage.Content = new StringContent("{\"engaged\":true,\"reason\":\"pause test\"}", Encoding.UTF8, "application/json");
            Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(engage)).StatusCode);
        }
        try
        {
            int callsBefore = _factory.Provider.Calls;
            var resp = await _client.PostAsync("/v1/chat/completions", CorrelationTestKit.Chat("while paused"));
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
            var id = CorrelationTestKit.HeaderId(resp);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.Equal(id, BodyString(doc.RootElement, "request_id"));

            var e = await CorrelationTestKit.LookupAsync(_client, id);
            Assert.Equal("Governance", e.DenialKind);
            Assert.Equal("pause", e.StoppedAtStage);
            Assert.Equal("gpt-4o-mini", e.SelectedModel);   // policy chose it ...
            Assert.Null(e.AttemptedModel);                  // ... but no call was started
            Assert.Equal("no_call", e.CostBasis);
            Assert.Equal(callsBefore, _factory.Provider.Calls);
        }
        finally
        {
            using var release = CorrelationTestKit.Admin(HttpMethod.Post, "/admin/kill-switch");
            release.Content = new StringContent("{\"engaged\":false}", Encoding.UTF8, "application/json");
            await _client.SendAsync(release);
        }
    }

    /// <summary>Review fix 4 (stricter than pre-v2, same intent): with no Local:* the legacy import's PII/PHI route
    /// names the explicitly DISABLED placeholder connection, so the request is refused at POLICY — no model is
    /// selected, none attempted, no provider and no cloud call. (Pre-v2: selected the placeholder, refused at provider.)</summary>
    [Fact]
    public async Task Missing_local_model_403_refused_at_policy_never_selected_or_attempted()
    {
        int callsBefore = _factory.Provider.Calls;
        var resp = await _client.PostAsync("/v1/chat/completions", CorrelationTestKit.Chat("my ssn is 123-45-6789"));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        var id = CorrelationTestKit.HeaderId(resp);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(id, BodyString(doc.RootElement, "request_id"));

        var e = await CorrelationTestKit.LookupAsync(_client, id);
        Assert.Equal(DataClassification.Pii, e.Classification);
        Assert.Equal("Governance", e.DenialKind);
        Assert.Equal("policy", e.StoppedAtStage);
        Assert.Contains("no eligible destination", e.Reason, StringComparison.Ordinal);
        Assert.Contains("connection 'local' is disabled (not configured)", e.Reason, StringComparison.Ordinal);
        Assert.Null(e.SelectedModel);
        Assert.Null(e.AttemptedModel);
        Assert.Null(e.ServedByModel);
        Assert.Equal("no_call", e.CostBasis);
        Assert.Equal(callsBefore, _factory.Provider.Calls);       // no provider (and so no cloud) call
    }

    [Fact]
    public async Task Upstream_failure_502_is_attempted_and_charged_an_estimate()
    {
        var resp = await _client.PostAsync("/v1/chat/completions", CorrelationTestKit.Chat("FAIL-UPSTREAM please"));
        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        var id = CorrelationTestKit.HeaderId(resp);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(id, BodyString(doc.RootElement, "request_id"));

        var e = await CorrelationTestKit.LookupAsync(_client, id);
        Assert.Equal(PolicyEffect.Deny, e.Effect);
        Assert.Equal("UpstreamFailure", e.DenialKind);
        Assert.Equal("provider", e.StoppedAtStage);
        Assert.Equal("gpt-4o-mini", e.AttemptedModel);
        Assert.Null(e.ServedByModel);
        Assert.False(e.UsageReported);
        Assert.Equal("estimated_failed_call", e.CostBasis);
        Assert.True(e.Usage.CostUsd > 0m);
    }

    [Fact]
    public async Task Mid_stream_failure_keeps_http_200_but_records_a_failed_outcome()
    {
        var resp = await _client.PostAsync("/v1/chat/completions", CorrelationTestKit.Chat("FAIL-MIDSTREAM now", stream: true));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);                 // transport already committed
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);
        var id = CorrelationTestKit.HeaderId(resp);

        var body = await resp.Content.ReadAsStringAsync();
        bool sawError = false, sawChunk = false;
        foreach (var line in body.Split('\n'))
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            var payload = line["data: ".Length..].Trim();
            if (payload == "[DONE]") continue;
            using var ev = JsonDocument.Parse(payload);
            if (ev.RootElement.TryGetProperty("error", out var err))
            {
                sawError = true;
                Assert.Equal("upstream_error", err.GetString());
                Assert.Equal(id, BodyString(ev.RootElement, "request_id"));
            }
            else
            {
                sawChunk = true;
                Assert.Equal(id, BodyString(ev.RootElement, "id"));
            }
        }
        Assert.True(sawChunk);
        Assert.True(sawError);

        var e = await CorrelationTestKit.LookupAsync(_client, id);
        Assert.Equal(PolicyEffect.Deny, e.Effect);
        Assert.Equal("UpstreamFailure", e.DenialKind);
        Assert.Equal("gpt-4o-mini", e.AttemptedModel);
        Assert.True(e.Stream);
        Assert.Equal("estimated_failed_call", e.CostBasis);
    }

    [Fact]
    public async Task Malformed_json_is_400_with_header_and_no_audit_record()
    {
        var bad = await _client.PostAsync("/v1/chat/completions",
            new StringContent("{not json", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var badId = CorrelationTestKit.HeaderId(bad);

        // A later request's record proves the (FIFO, single-reader) sink has drained everything before it.
        var good = await _client.PostAsync("/v1/chat/completions", CorrelationTestKit.Chat("after the bad one"));
        var goodId = CorrelationTestKit.HeaderId(good);
        await CorrelationTestKit.LookupAsync(_client, goodId);

        Assert.DoesNotContain(badId, CorrelationTestKit.ReadShared(_factory.AuditPath));
        using var lookup = await _client.SendAsync(CorrelationTestKit.Admin(HttpMethod.Get, $"/admin/requests/{badId}"));
        Assert.Equal(HttpStatusCode.NotFound, lookup.StatusCode);
    }

    [Fact]
    public async Task Twenty_concurrent_identical_prompts_resolve_to_their_own_records()
    {
        var responses = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => _client.PostAsync("/v1/chat/completions", CorrelationTestKit.Chat("identical concurrent prompt"))));
        var ids = responses.Select(r => { Assert.Equal(HttpStatusCode.OK, r.StatusCode); return CorrelationTestKit.HeaderId(r); }).ToList();
        Assert.Equal(20, ids.Distinct(StringComparer.Ordinal).Count());

        var entries = await Task.WhenAll(ids.Select(id => CorrelationTestKit.LookupAsync(_client, id)));
        for (int i = 0; i < ids.Count; i++) Assert.Equal(ids[i], entries[i].Id);
        Assert.Single(entries.Select(e => e.PromptSha256).Distinct());   // same content — joined by id, never by content
    }

    [Fact]
    public async Task Lookup_and_list_are_admin_only()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _anon.GetAsync("/admin/requests/req_0123")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _anon.GetAsync("/admin/requests")).StatusCode);
        // An application key is not an admin token.
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/admin/requests/req_0123")).StatusCode);
    }

    [Theory]
    [InlineData("has.dot")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 65 chars
    public async Task Malformed_lookup_id_is_400(string id)
    {
        using var resp = await _client.SendAsync(CorrelationTestKit.Admin(HttpMethod.Get, $"/admin/requests/{id}"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("invalid_id", BodyString(doc.RootElement, "error"));
    }

    [Fact]
    public async Task Unknown_id_is_404_with_scanned_count()
    {
        using var resp = await _client.SendAsync(CorrelationTestKit.Admin(HttpMethod.Get, $"/admin/requests/req_{Guid.NewGuid():n}"));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("not_found", BodyString(doc.RootElement, "error"));
        Assert.True(doc.RootElement.GetProperty("scanned").GetInt32() >= 0);
    }

    private async Task<RequestsPage> ListAsync(string query)
    {
        using var resp = await _client.SendAsync(CorrelationTestKit.Admin(HttpMethod.Get, "/admin/requests" + query));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return JsonSerializer.Deserialize(await resp.Content.ReadAsStringAsync(), AuditJsonContext.Default.RequestsPage)!;
    }

    [Fact]
    public async Task Request_list_is_newest_first_and_limit_is_clamped()
    {
        var a = CorrelationTestKit.HeaderId(await _client.PostAsync("/v1/chat/completions", CorrelationTestKit.Chat("list A")));
        await CorrelationTestKit.LookupAsync(_client, a);
        var b = CorrelationTestKit.HeaderId(await _client.PostAsync("/v1/chat/completions", CorrelationTestKit.Chat("list B")));
        await CorrelationTestKit.LookupAsync(_client, b);

        var two = await ListAsync("?limit=2");
        Assert.Equal(2, two.Items.Count);
        Assert.Equal(b, two.Items[0].Id);
        Assert.Equal(a, two.Items[1].Id);
        Assert.True(two.Segment.ChainValid);
        Assert.True(two.Segment.Entries >= 2);

        Assert.Single((await ListAsync("?limit=0")).Items);       // clamped up to 1
        Assert.Single((await ListAsync("?limit=-5")).Items);
        var big = await ListAsync("?limit=1000000");               // clamped down to 1000
        Assert.True(big.Items.Count <= 1000);
        Assert.Equal(Math.Min(big.Segment.Entries, 1000), big.Items.Count);
    }

    [Fact]
    public async Task Records_never_contain_prompt_content_without_preview_opt_in()
    {
        var marker = $"zebra-secret-{Guid.NewGuid():n}";
        var resp = await _client.PostAsync("/v1/chat/completions", CorrelationTestKit.Chat($"please keep {marker} private"));
        var id = CorrelationTestKit.HeaderId(resp);
        var e = await CorrelationTestKit.LookupAsync(_client, id);

        Assert.Null(e.PromptPreview);
        Assert.DoesNotContain(marker, JsonSerializer.Serialize(e, AuditJsonContext.Default.AuditEntry));
        Assert.DoesNotContain(marker, CorrelationTestKit.ReadShared(_factory.AuditPath));
        using var list = await _client.SendAsync(CorrelationTestKit.Admin(HttpMethod.Get, "/admin/requests"));
        Assert.DoesNotContain(marker, await list.Content.ReadAsStringAsync());
    }
}

/// <summary>Rate-limited 429 is correlated. Own host: a global cap of 1/min would starve the other suite.</summary>
public sealed class CorrelationRateLimitTests : IClassFixture<CorrelationRateLimitTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        public readonly string AuditPath = Path.Combine(Path.GetTempPath(), $"covenant-corr-rl-{Guid.NewGuid():n}.log");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("OpenAI:ApiKey", "unused-stubbed");
            builder.UseSetting("Admin:Token", CorrelationTestKit.AdminToken);
            builder.UseSetting("Budget:GlobalCapUsd", "100");
            builder.UseSetting("Auth:Keys:0:Key", "k");
            builder.UseSetting("Auth:Keys:0:Principal", "p");
            builder.UseSetting("Auth:Keys:0:Team", "t");
            CorrelationTestKit.IsolatePaths(builder, AuditPath);
            builder.UseSetting("RateLimit:GlobalPerMinute", "1");
            builder.ConfigureTestServices(services =>
                services.AddSingleton<IChatClientRegistry>(new ChatClientRegistry(
                    new Dictionary<string, MEAI.IChatClient> { ["openai"] = new ScriptedChatClient() })));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            CorrelationTestKit.DeleteWithSiblings(AuditPath);
        }
    }

    private readonly HttpClient _client;

    public CorrelationRateLimitTests(Factory factory)
    {
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "k");
    }

    [Fact]
    public async Task Rate_limited_429_is_correlated_and_stops_at_rate_limit()
    {
        // Cap 1/min: at most one minute boundary can fall inside the burst, so a 429 must appear within 3.
        HttpResponseMessage? limited = null;
        for (int i = 0; i < 3 && limited is null; i++)
        {
            var r = await _client.PostAsync("/v1/chat/completions", CorrelationTestKit.Chat($"burst {i}"));
            if (r.StatusCode == HttpStatusCode.TooManyRequests) limited = r;
        }
        Assert.NotNull(limited);
        var id = CorrelationTestKit.HeaderId(limited!);
        using var doc = JsonDocument.Parse(await limited!.Content.ReadAsStringAsync());
        Assert.Equal(id, doc.RootElement.GetProperty("request_id").GetString());

        var e = await CorrelationTestKit.LookupAsync(_client, id);
        Assert.Equal("RateLimited", e.DenialKind);
        Assert.Equal("rate_limit", e.StoppedAtStage);
        Assert.Null(e.AttemptedModel);
        Assert.Equal("no_call", e.CostBasis);
    }
}

/// <summary>Cancellation is evidence (ADR-0009 §3): the record is written even though the request token
/// is cancelled, and a started call is charged an estimate — never a silent $0.</summary>
public sealed class CancellationEvidenceTests
{
    private sealed class HangingChatClient : MEAI.IChatClient
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> messages, MEAI.ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable: the delay only ends by cancellation");
        }

        public IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<MEAI.ChatMessage> messages, MEAI.ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>Provider-side timeout: an OperationCanceledException the CALLER did not request.</summary>
    private sealed class ProviderTimeoutChatClient : MEAI.IChatClient
    {
        public Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> messages, MEAI.ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout");

        public IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<MEAI.ChatMessage> messages, MEAI.ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>Refuses any cancellable token: proves the audit stage enqueues with CancellationToken.None.</summary>
    private sealed class StrictSink : IAuditSink
    {
        public List<AuditEntry> Entries { get; } = [];
        public ValueTask EnqueueAsync(AuditEntry entry, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.CanBeCanceled)
                throw new InvalidOperationException("audit enqueue must not use the (cancellable) request token");
            lock (Entries) Entries.Add(entry);
            return ValueTask.CompletedTask;
        }
    }

    private static InferencePipeline Build(IAuditSink sink, MEAI.IChatClient client, ISpendLedger ledger)
    {
        var prices = new PriceBook(new Dictionary<string, (decimal, decimal)> { ["gpt-4o-mini"] = (0.01m, 0.03m) });
        var policy = new PolicyConfig
        {
            AllowedRoutes = new Dictionary<DataClassification, IReadOnlyList<RouteTarget>>
            {
                [DataClassification.Internal] = [new("openai", "gpt-4o-mini")],
            }
        };
        return new InferencePipeline(
        [
            new AuditStage(sink),
            new AuthStage(new AuthConfig { AllowAnonymous = true, Keys = [] }),
            new ClassifyStage(new RegexDataClassifier()),
            new PolicyStage(new PolicyEngine(policy)),
            new BudgetStage(new KillSwitch(), ledger, new BudgetConfig { GlobalCapUsd = 1_000m }, prices),
            new ProviderCallStage(new ChatClientRegistry(new Dictionary<string, MEAI.IChatClient> { ["openai"] = client })),
            new AttributionStage(prices),
        ]);
    }

    private static InferenceContext Ctx(string content) =>
        new(new InferenceRequest("tester", [new ChatMessage(ChatRole.User, content)], null,
            new AttributionTags("team-c", "wf", "uc"), RequestId: $"req_{Guid.NewGuid():n}"));

    [Fact]
    public async Task Cancelled_request_still_produces_exactly_one_charged_record()
    {
        var sink = new StrictSink();
        var client = new HangingChatClient();
        var ledger = new InMemorySpendLedger();
        var pipeline = Build(sink, client, ledger);
        var prompt = new string('x', 400);                     // 100 estimated input tokens
        var ctx = Ctx(prompt);

        using var cts = new CancellationTokenSource();
        var run = pipeline.ExecuteAsync(ctx, cts.Token);
        await client.Started.Task;                              // the provider call is genuinely in flight
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        var e = Assert.Single(sink.Entries);
        Assert.Equal(ctx.Request.RequestId, e.Id);
        Assert.Equal(PolicyEffect.Deny, e.Effect);
        Assert.Equal("Cancelled", e.DenialKind);
        Assert.Equal("provider", e.StoppedAtStage);
        Assert.Equal("gpt-4o-mini", e.AttemptedModel);
        Assert.Equal("estimated_failed_call", e.CostBasis);
        Assert.Equal(100 / 1000m * 0.01m, e.Usage.CostUsd);    // input estimate, no output streamed
        Assert.True(e.Usage.CostUsd > 0m);
        Assert.Equal(e.Usage.CostUsd, ledger.GlobalSpendUsd);   // and it counts against the budget
    }

    [Fact]
    public async Task Provider_side_timeout_is_an_upstream_failure_not_a_cancellation()
    {
        var sink = new StrictSink();
        var pipeline = Build(sink, new ProviderTimeoutChatClient(), new InMemorySpendLedger());
        var ctx = Ctx("time me out");

        await pipeline.ExecuteAsync(ctx, CancellationToken.None);   // governed refusal, no exception escapes

        Assert.Equal(DenialKind.UpstreamFailure, ctx.DenialKind);
        var e = Assert.Single(sink.Entries);
        Assert.Equal("UpstreamFailure", e.DenialKind);
        Assert.Equal("estimated_failed_call", e.CostBasis);
    }
}

/// <summary>Audit v2 is additive and hash-compatible (ADR-0009 §2): a TRUE legacy (v1) chain — no v2 keys in
/// the stored bytes — keeps verifying (with anchors) after v2 entries are appended; replay stays correct.</summary>
public sealed class LegacyAuditCompatibilityTests : IDisposable
{
    private readonly string _log = Path.Combine(Path.GetTempPath(), $"covenant-legacy-{Guid.NewGuid():n}.log");
    private readonly string _anchors = Path.Combine(Path.GetTempPath(), $"covenant-legacy-{Guid.NewGuid():n}.anchors");

    public void Dispose()
    {
        CorrelationTestKit.DeleteWithSiblings(_log);
        CorrelationTestKit.DeleteWithSiblings(_anchors);
    }

    // Exactly the v1 shape the pre-ADR-0009 sink wrote (no SchemaVersion/CorrelationId/... keys).
    private static readonly string[] LegacyContents =
    [
        """{"Id":"0f8fad5bd9cb469fa16570867728950e","TimestampUtc":"2026-09-01T10:00:00+00:00","Principal":"alice","Tags":{"Team":"legacy-team","Workflow":"wf","UseCase":"uc"},"Classification":"Internal","Effect":"Allow","Reason":"allowed","ServedByModel":"gpt-4o-mini","Usage":{"InputTokens":100,"OutputTokens":50,"CostUsd":0.002,"TotalTokens":150},"DurationMs":12.5,"PromptChars":40,"PromptSha256":"abc","Signal":null,"PromptPreview":null,"CacheHit":false}""",
        """{"Id":"7c9e6679742540de944be07fc1f90ae7","TimestampUtc":"2026-09-01T10:01:00+00:00","Principal":"bob","Tags":{"Team":"legacy-team","Workflow":"wf","UseCase":"uc"},"Classification":"Pii","Effect":"Deny","Reason":"no adapter registered for 'local:llama-3.1-8b-instruct'","ServedByModel":null,"Usage":{"InputTokens":0,"OutputTokens":0,"CostUsd":0,"TotalTokens":0},"DurationMs":1,"PromptChars":20,"PromptSha256":"def","Signal":"US SSN pattern","PromptPreview":null,"CacheHit":false}""",
    ];

    [Fact]
    public async Task Legacy_chain_plus_v2_entries_verifies_replays_and_reads_v2_fields_as_null()
    {
        // Hand-write the legacy chain exactly as the old sink did.
        var previous = AuditChain.GenesisHash;
        var legacyLines = new List<string>();
        foreach (var content in LegacyContents)
        {
            var hash = AuditChain.Hash(previous, content);
            legacyLines.Add($"{previous}{AuditChain.Separator}{hash}{AuditChain.Separator}{content}");
            previous = hash;
        }
        await File.WriteAllTextAsync(_log, string.Join(Environment.NewLine, legacyLines) + Environment.NewLine);

        // Append v2 entries through the real sink, anchoring every entry from here on.
        var sink = new FileAuditSink(_log, _anchors, anchorEvery: 1);
        await sink.StartAsync(CancellationToken.None);
        await sink.EnqueueAsync(new AuditEntry("req_v2a", DateTimeOffset.Parse("2026-10-16T09:00:00Z"), "carol",
            new AttributionTags("v2-team", "wf", "uc"), DataClassification.Internal, PolicyEffect.Deny, "provider 'openai' call failed",
            null, new Usage(0, 0, 0.01m), SchemaVersion: 2, CorrelationId: "req_v2a", ClassificationEvaluated: true,
            DenialKind: "UpstreamFailure", StoppedAtStage: "provider", SelectedModel: "gpt-4o-mini", AttemptedModel: "gpt-4o-mini",
            Stream: false, UsageReported: false, CostBasis: "estimated_failed_call"));
        await sink.EnqueueAsync(new AuditEntry("req_v2b", DateTimeOffset.Parse("2026-10-16T09:01:00Z"), "carol",
            new AttributionTags("v2-team", "wf", "uc"), DataClassification.Internal, PolicyEffect.Allow, "allowed",
            "gpt-4o-mini", new Usage(10, 5, 0.0005m), SchemaVersion: 2, CorrelationId: "req_v2b", ClassificationEvaluated: true,
            SelectedModel: "gpt-4o-mini", AttemptedModel: "gpt-4o-mini", Stream: false, UsageReported: true, CostBasis: "reported"));
        await sink.StopAsync(CancellationToken.None);   // drains

        // Legacy bytes untouched (history is never rewritten).
        var lines = File.ReadAllLines(_log).Where(l => l.Length > 0).ToList();
        Assert.Equal(4, lines.Count);
        Assert.Equal(legacyLines[0], lines[0]);
        Assert.Equal(legacyLines[1], lines[1]);

        var (verification, entries) = AuditChainVerifier.VerifyAndRead(_log, _anchors);
        Assert.True(verification.Valid, verification.Failure);
        Assert.Equal(4, verification.EntryCount);
        Assert.Equal(2, File.ReadAllLines(_anchors).Count(l => l.Length > 0));

        foreach (var legacy in entries.Take(2))
        {
            Assert.Null(legacy.SchemaVersion);
            Assert.Null(legacy.CorrelationId);
            Assert.Null(legacy.ClassificationEvaluated);
            Assert.Null(legacy.DenialKind);
            Assert.Null(legacy.StoppedAtStage);
            Assert.Null(legacy.SelectedModel);
            Assert.Null(legacy.AttemptedModel);
            Assert.Null(legacy.Stream);
            Assert.Null(legacy.UsageReported);
            Assert.Null(legacy.CostBasis);
        }
        Assert.Equal(2, entries[2].SchemaVersion);
        Assert.Equal("estimated_failed_call", entries[2].CostBasis);

        var ledger = new InMemorySpendLedger();
        Covenant.Host.LedgerReplay.Rebuild(entries, ledger);
        Assert.Equal(0.002m + 0.01m + 0.0005m, ledger.GlobalSpendUsd);
        Assert.Equal(0.002m, ledger.TeamSpendUsd("legacy-team"));
        Assert.Equal(0.0105m, ledger.TeamSpendUsd("v2-team"));   // the estimated failed-call charge replays

        var report = EvidenceExport.Build(_log, TimeProvider.System, _anchors);
        Assert.True(report.ChainValid);
        Assert.True(report.AnchorConfigured);
        Assert.Equal(2, report.AnchorCount);
    }
}

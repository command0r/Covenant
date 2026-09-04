using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Covenant.Adapters;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MEAI = Microsoft.Extensions.AI;

namespace Covenant.Tests;

/// <summary>The success paths through the real Host with an in-memory stub provider (registry swapped
/// via DI): served buffered + streamed responses in BOTH the OpenAI and Anthropic dialects, model
/// discovery content, and a served request landing in evidence with attributed cost. These exercise
/// response building, SSE emission, and attribution — the paths an unroutable provider never reaches.</summary>
public sealed class ServedPathTests : IClassFixture<ServedPathTests.StubFactory>
{
    public sealed class StubFactory : WebApplicationFactory<Program>
    {
        public readonly string AuditPath = Path.Combine(Path.GetTempPath(), $"covenant-served-{Guid.NewGuid():n}.log");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("OpenAI:ApiKey", "unused-stubbed");
            builder.UseSetting("Admin:Token", "test-admin");
            builder.UseSetting("Budget:GlobalCapUsd", "100");
            builder.UseSetting("Auth:Keys:0:Key", "k");
            builder.UseSetting("Auth:Keys:0:Principal", "p");
            builder.UseSetting("Auth:Keys:0:Team", "t");
            builder.UseSetting("Audit:Path", AuditPath);

            // Replace the provider registry with in-memory stubs for the policy's OpenAI routes.
            // ConfigureTestServices runs AFTER the app's registrations, so this override wins; the
            // pipeline factory resolves it when first used.
            builder.ConfigureTestServices(services =>
            {
                var stub = new StubChatClient("served governed reply");
                services.AddSingleton<IChatClientRegistry>(new ChatClientRegistry(
                    new Dictionary<string, MEAI.IChatClient>
                    {
                        ["openai:gpt-4o-mini"] = stub,
                        ["openai:gpt-4o"] = stub,
                    }));
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(AuditPath) + "*"))
                File.Delete(f);
        }
    }

    private sealed class StubChatClient(string reply) : MEAI.IChatClient
    {
        public Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> messages, MEAI.ChatOptions? options = null, CancellationToken ct = default)
            => Task.FromResult(new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, reply))
            { Usage = new MEAI.UsageDetails { InputTokenCount = 7, OutputTokenCount = 4 } });

        public async IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<MEAI.ChatMessage> messages, MEAI.ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, reply[..6]);
            yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, reply[6..]);
            yield return new MEAI.ChatResponseUpdate { Contents = [new MEAI.UsageContent(new MEAI.UsageDetails { InputTokenCount = 7, OutputTokenCount = 4 })] };
        }

        public object? GetService(Type t, object? key = null) => null;
        public void Dispose() { }
    }

    private readonly StubFactory _factory;
    private readonly HttpClient _client;

    public ServedPathTests(StubFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "k");
    }

    private static StringContent OpenAi(string content, bool stream = false) =>
        new($"{{\"stream\":{(stream ? "true" : "false")},\"messages\":[{{\"role\":\"user\",\"content\":\"{content}\"}}]}}",
            Encoding.UTF8, "application/json");

    private static StringContent Anthropic(string content, bool stream = false) =>
        new($"{{\"model\":\"gpt-4o-mini\",\"max_tokens\":50,\"stream\":{(stream ? "true" : "false")},\"messages\":[{{\"role\":\"user\",\"content\":\"{content}\"}}]}}",
            Encoding.UTF8, "application/json");

    [Fact]
    public async Task OpenAi_buffered_request_is_served_with_usage()
    {
        var resp = await _client.PostAsync("/v1/chat/completions", OpenAi("a normal internal question"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("gpt-4o-mini", root.GetProperty("model").GetString());       // routed to cheapest
        Assert.Equal("served governed reply",
            root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        Assert.Equal(11, root.GetProperty("usage").GetProperty("total_tokens").GetInt32());
    }

    [Fact]
    public async Task OpenAi_streamed_request_emits_chunks_then_done()
    {
        var resp = await _client.PostAsync("/v1/chat/completions", OpenAi("stream it", stream: true));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);

        var body = await resp.Content.ReadAsStringAsync();
        var text = new StringBuilder();
        bool sawDone = false;
        foreach (var line in body.Split('\n'))
        {
            if (!line.StartsWith("data: ")) continue;
            var payload = line["data: ".Length..].Trim();
            if (payload == "[DONE]") { sawDone = true; continue; }
            using var chunk = JsonDocument.Parse(payload);
            var delta = chunk.RootElement.GetProperty("choices")[0].GetProperty("delta");
            if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                text.Append(c.GetString());
        }
        Assert.True(sawDone);
        Assert.Equal("served governed reply", text.ToString());
    }

    [Fact]
    public async Task Anthropic_buffered_request_is_served_with_messages_shape()
    {
        using var aclient = _factory.CreateClient();
        aclient.DefaultRequestHeaders.Add("x-api-key", "k");

        var resp = await aclient.PostAsync("/v1/messages", Anthropic("hello"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("message", root.GetProperty("type").GetString());
        Assert.Equal("served governed reply", root.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("end_turn", root.GetProperty("stop_reason").GetString());
        Assert.Equal(7, root.GetProperty("usage").GetProperty("input_tokens").GetInt32());
    }

    [Fact]
    public async Task Anthropic_streamed_request_emits_the_messages_event_sequence()
    {
        using var aclient = _factory.CreateClient();
        aclient.DefaultRequestHeaders.Add("x-api-key", "k");

        var resp = await aclient.PostAsync("/v1/messages", Anthropic("stream it", stream: true));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync();
        var events = new List<string>();
        var text = new StringBuilder();
        foreach (var line in body.Split('\n'))
        {
            if (!line.StartsWith("data: ")) continue;
            using var ev = JsonDocument.Parse(line["data: ".Length..].Trim());
            var type = ev.RootElement.GetProperty("type").GetString()!;
            events.Add(type);
            if (type == "content_block_delta")
                text.Append(ev.RootElement.GetProperty("delta").GetProperty("text").GetString());
        }
        Assert.Equal("message_start", events[0]);
        Assert.Equal("message_stop", events[^1]);
        Assert.Equal("served governed reply", text.ToString());
    }

    [Fact]
    public async Task Served_request_lands_in_evidence_with_attributed_cost()
    {
        await _client.PostAsync("/v1/chat/completions", OpenAi("bill me"));

        var req = new HttpRequestMessage(HttpMethod.Get, "/admin/status");
        req.Headers.Add("X-Covenant-Admin-Token", "test-admin");
        var status = await _client.SendAsync(req);
        using var doc = JsonDocument.Parse(await status.Content.ReadAsStringAsync());

        var finops = doc.RootElement.GetProperty("finops");
        Assert.True(finops.GetProperty("allowed").GetInt32() >= 1);
        Assert.True(doc.RootElement.GetProperty("budget").GetProperty("global_spend_usd").GetDecimal() > 0m);
    }
}

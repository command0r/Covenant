using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Covenant.Adapters;
using Covenant.Core;
using Covenant.Governance;
using Covenant.Host;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using MEAI = Microsoft.Extensions.AI;

namespace Covenant.Tests;

/// <summary>Wire-surface robustness: content governance cannot classify (images, files) and features it
/// cannot govern (tools) are REFUSED, never silently stripped — and the refusal is audited. max_tokens
/// is forwarded to the provider rather than accepted-and-dropped.</summary>
public class WireShapeTests
{
    private static JsonElement J(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    // ---------- pure mapping ----------

    [Fact]
    public void OpenAi_plain_text_and_text_parts_are_supported()
    {
        var r = new OpenAiChatRequest { Messages = [
            new OpenAiMessage { Role = "user", Content = J("\"hi\"") },
            new OpenAiMessage { Role = "user", Content = J("""[{"type":"text","text":"a"},{"type":"text","text":"b"}]""") }] };

        Assert.Null(OpenAiWire.Unsupported(r));
        Assert.Equal("ab", OpenAiWire.Text(r.Messages[1]));
    }

    [Theory]
    [InlineData("""{"messages":[{"role":"user","content":[{"type":"image_url","image_url":{"url":"data:x"}}]}]}""", "image_url")]
    [InlineData("""{"messages":[{"role":"user","content":"hi"}],"tools":[{"type":"function","function":{"name":"f"}}]}""", "tools")]
    [InlineData("""{"messages":[{"role":"user","content":"hi"}],"functions":[{"name":"f"}]}""", "functions")]
    [InlineData("""{"messages":[{"role":"assistant","content":null,"tool_calls":[{"id":"c"}]}]}""", "tool_calls")]
    [InlineData("""{"messages":[{"role":"tool","content":"result"}]}""", "role 'tool'")]
    public void OpenAi_ungovernable_shapes_are_named(string json, string expected)
    {
        var r = JsonSerializer.Deserialize(json, CovenantJsonContext.Default.OpenAiChatRequest)!;
        Assert.Contains(expected, OpenAiWire.Unsupported(r));
    }

    [Theory]
    [InlineData("""{"messages":[{"role":"user","content":"hi"}],"tools":null}""")]
    [InlineData("""{"messages":[{"role":"user","content":"hi"}],"tools":[]}""")]        // clients send tools: [] when tool use is off
    [InlineData("""{"messages":[{"role":"user","content":"hi"}],"n":1,"temperature":0.2,"stream_options":{"include_usage":true}}""")]
    public void OpenAi_absent_or_harmless_extras_are_not_refused(string json)
    {
        var r = JsonSerializer.Deserialize(json, CovenantJsonContext.Default.OpenAiChatRequest)!;
        Assert.Null(OpenAiWire.Unsupported(r));
    }

    [Theory]
    [InlineData("""{"messages":[{"role":"user","content":[{"type":1}]}]}""")]                 // type not a string
    [InlineData("""{"messages":[{"role":"user","content":[{"type":"text","text":5}]}]}""")]    // text not a string
    [InlineData("""{"messages":[{"role":"user","content":{"weird":true}}]}""")]                // content an object
    [InlineData("""{"messages":[{"role":"user","content":[7,"x",null]}]}""")]                  // parts not objects
    [InlineData("""{"messages":null}""")]
    [InlineData("""{"messages":[null]}""")]
    [InlineData("""{"messages":[{"role":null,"content":"hi"}],"n":2.0}""")]                     // null role, non-integer n>1
    public void OpenAi_malformed_content_is_refused_never_thrown(string json)
    {
        var r = JsonSerializer.Deserialize(json, CovenantJsonContext.Default.OpenAiChatRequest)!;
        var reason = OpenAiWire.Unsupported(r);                          // must not throw (a throw = un-audited 500)
        if (r.Messages is [{ } first]) _ = OpenAiWire.Text(first);
        Assert.NotNull(reason);
    }

    [Fact]
    public void Denial_reasons_never_echo_client_bytes()
    {
        // The reason lands in the append-only audit chain: only allow-listed labels may appear in it.
        const string secret = "ZZZ-secret-phi-ZZZ";
        var o = JsonSerializer.Deserialize($$"""{"messages":[{"role":"user","content":[{"type":"{{secret}}"}]}]}""", CovenantJsonContext.Default.OpenAiChatRequest)!;
        var a = JsonSerializer.Deserialize($$"""{"max_tokens":5,"messages":[{"role":"user","content":[{"type":"{{secret}}"}]}]}""", CovenantJsonContext.Default.AnthropicMessagesRequest)!;

        Assert.DoesNotContain(secret, OpenAiWire.Unsupported(o));
        Assert.DoesNotContain(secret, AnthropicWire.Unsupported(a));
        Assert.Contains("unknown", OpenAiWire.Unsupported(o));
        Assert.Contains("image_url", OpenAiWire.Unsupported(JsonSerializer.Deserialize(
            """{"messages":[{"role":"user","content":[{"type":"image_url"}]}]}""", CovenantJsonContext.Default.OpenAiChatRequest)!));
    }

    [Theory]
    [InlineData("""{"max_tokens":5,"messages":[{"role":"user","content":"hi"}],"tools":[]}""")]
    [InlineData("""{"max_tokens":5,"messages":[{"role":"user","content":[{"type":"text","text":"hi"}]}],"metadata":{"user_id":"u"}}""")]
    public void Anthropic_absent_or_harmless_extras_are_not_refused(string json)
    {
        var r = JsonSerializer.Deserialize(json, CovenantJsonContext.Default.AnthropicMessagesRequest)!;
        Assert.Null(AnthropicWire.Unsupported(r));
    }

    [Theory]
    [InlineData("""{"max_tokens":5,"messages":[{"role":"user","content":[{"type":2}]}]}""")]
    [InlineData("""{"max_tokens":5,"messages":[{"role":"user","content":{"x":1}}]}""")]
    [InlineData("""{"max_tokens":5,"messages":[{"role":"user","content":[{"type":"text","text":[1]}]}]}""")]
    [InlineData("""{"max_tokens":5,"messages":null}""")]
    [InlineData("""{"max_tokens":5,"messages":[null]}""")]
    public void Anthropic_malformed_content_is_refused_never_thrown(string json)
    {
        var r = JsonSerializer.Deserialize(json, CovenantJsonContext.Default.AnthropicMessagesRequest)!;
        var reason = AnthropicWire.Unsupported(r);
        _ = AnthropicWire.ToCanonical(r);
        Assert.NotNull(reason);
    }

    [Theory]
    [InlineData("""{"max_tokens":5,"messages":[{"role":"user","content":[{"type":"image","source":{}}]}]}""", "image")]
    [InlineData("""{"max_tokens":5,"messages":[{"role":"user","content":[{"type":"document","source":{}}]}]}""", "document")]
    [InlineData("""{"max_tokens":5,"messages":[{"role":"user","content":[{"type":"tool_result","tool_use_id":"x"}]}]}""", "tool_result")]
    [InlineData("""{"max_tokens":5,"messages":[{"role":"user","content":"hi"}],"tools":[{"name":"f"}]}""", "tools")]
    [InlineData("""{"max_tokens":5,"system":[{"type":"image","source":{}}],"messages":[{"role":"user","content":"hi"}]}""", "image")]
    public void Anthropic_ungovernable_shapes_are_named(string json, string expected)
    {
        var r = JsonSerializer.Deserialize(json, CovenantJsonContext.Default.AnthropicMessagesRequest)!;
        Assert.Contains(expected, AnthropicWire.Unsupported(r));
    }

    [Fact]
    public void Anthropic_text_blocks_are_supported()
    {
        var r = JsonSerializer.Deserialize("""{"max_tokens":5,"system":"s","messages":[{"role":"user","content":[{"type":"text","text":"hi"}]}]}""",
            CovenantJsonContext.Default.AnthropicMessagesRequest)!;
        Assert.Null(AnthropicWire.Unsupported(r));
    }

    // ---------- stage guarantees ----------

    private static InferenceContext Ctx(string? unsupported = null, int? maxTokens = null) =>
        new(new InferenceRequest("t", [new ChatMessage(ChatRole.User, "hi")], null, new AttributionTags("a", "b", "c"),
            MaxOutputTokens: maxTokens, UnsupportedFeature: unsupported));

    [Fact]
    public async Task Shape_stage_denies_unsupported_as_400_kind_and_never_continues()
    {
        bool reached = false;
        var ctx = Ctx(unsupported: "content part 'image_url'");

        await new ShapeStage().InvokeAsync(ctx, (_, _) => { reached = true; return Task.CompletedTask; }, default);

        Assert.True(ctx.IsDenied);
        Assert.Equal(DenialKind.Unsupported, ctx.DenialKind);
        Assert.Contains("image_url", ctx.DenialReason);
        Assert.False(reached);                                            // fail-closed: nothing downstream ran
    }

    [Fact]
    public async Task Shape_stage_rejects_non_positive_max_tokens_and_passes_clean_requests()
    {
        var bad = Ctx(maxTokens: 0);
        await new ShapeStage().InvokeAsync(bad, (_, _) => Task.CompletedTask, default);
        Assert.Equal(DenialKind.Unsupported, bad.DenialKind);

        bool reached = false;
        await new ShapeStage().InvokeAsync(Ctx(maxTokens: 64), (_, _) => { reached = true; return Task.CompletedTask; }, default);
        Assert.True(reached);
    }

    private sealed class CapturingClient : MEAI.IChatClient
    {
        public MEAI.ChatOptions? Seen;
        public Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> m, MEAI.ChatOptions? o = null, CancellationToken ct = default)
        { Seen = o; return Task.FromResult(new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, "ok")) { Usage = new MEAI.UsageDetails { InputTokenCount = 1, OutputTokenCount = 1 } }); }
        public async IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<MEAI.ChatMessage> m, MEAI.ChatOptions? o = null, [EnumeratorCancellation] CancellationToken ct = default)
        { Seen = o; await Task.Yield(); yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, "ok"); }
        public object? GetService(Type t, object? k = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task Provider_stage_forwards_max_tokens_to_the_model_call()
    {
        var client = new CapturingClient();
        var stage = new ProviderCallStage(new ChatClientRegistry(new Dictionary<string, MEAI.IChatClient> { ["openai:m"] = client }));
        var ctx = Ctx(maxTokens: 77);
        ctx.Policy = PolicyOutcome.Allow(new RouteTarget("openai", "m"));

        await stage.InvokeAsync(ctx, (_, _) => Task.CompletedTask, default);

        Assert.Equal(77, client.Seen?.MaxOutputTokens);
    }

    // ---------- through the Host: both dialects return 400 in their own error shape, and the refusal is audited ----------

    public sealed class Ingress : IClassFixture<WebApplicationFactory<Program>>, IDisposable
    {
        private readonly string _auditPath = Path.Combine(Path.GetTempPath(), $"covenant-shape-{Guid.NewGuid():n}.log");
        private readonly WebApplicationFactory<Program> _factory;
        private readonly HttpClient _client;

        public Ingress(WebApplicationFactory<Program> factory)
        {
            _factory = factory.WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Production");
                b.UseSetting("OpenAI:ApiKey", "test-key");
                b.UseSetting("OpenAI:Endpoint", "http://127.0.0.1:9");
                b.UseSetting("Admin:Token", "test-admin");
                b.UseSetting("Budget:GlobalCapUsd", "100");
                b.UseSetting("Auth:Keys:0:Key", "k");
                b.UseSetting("Auth:Keys:0:Principal", "shape-tester");
                b.UseSetting("Auth:Keys:0:Team", "t");
                b.UseSetting("Audit:Path", _auditPath);
            });
            _client = _factory.CreateClient();
            _client.DefaultRequestHeaders.Add("x-api-key", "k");
            _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "k");
        }

        public void Dispose() { _factory.Dispose(); if (File.Exists(_auditPath)) File.Delete(_auditPath); }

        private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

        /// <summary>The audit sink drains off the hot path — poll for the entry instead of sleeping.</summary>
        private async Task<string> WaitForAuditAsync(string marker)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(_auditPath) && await File.ReadAllTextAsync(_auditPath) is var s && s.Contains(marker)) return s;
                await Task.Delay(25);
            }
            throw new TimeoutException($"audit entry '{marker}' not written within 5s");
        }

        [Fact]
        public async Task OpenAi_image_content_is_400_unsupported_and_audited()
        {
            var resp = await _client.PostAsync("/v1/chat/completions",
                Body("""{"messages":[{"role":"user","content":[{"type":"text","text":"what is this"},{"type":"image_url","image_url":{"url":"data:image/png;base64,AAAA"}}]}]}"""));
            var json = await resp.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            Assert.Contains("\"error\":\"unsupported\"", json);
            Assert.Contains("image_url", json);

            var log = await WaitForAuditAsync("unsupported request shape");
            Assert.Contains("shape-tester", log);                         // the resolved caller, not "anonymous"
            Assert.DoesNotContain("AAAA", log);                           // the image bytes never reach evidence
        }

        [Fact]
        public async Task Client_supplied_type_label_never_reaches_response_or_evidence()
        {
            const string secret = "QQQ-not-for-evidence-QQQ";
            var resp = await _client.PostAsync("/v1/chat/completions",
                Body($$"""{"messages":[{"role":"user","content":[{"type":"{{secret}}","data":"x"}]}]}"""));
            var json = await resp.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            Assert.DoesNotContain(secret, json);
            var log = await WaitForAuditAsync("unsupported request shape");
            Assert.DoesNotContain(secret, log);
        }

        [Fact]
        public async Task OpenAi_tools_are_400_unsupported()
        {
            var resp = await _client.PostAsync("/v1/chat/completions",
                Body("""{"messages":[{"role":"user","content":"hi"}],"tools":[{"type":"function","function":{"name":"get_weather"}}]}"""));

            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            Assert.Contains("tools", await resp.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task Anthropic_image_block_is_400_invalid_request_error()
        {
            var resp = await _client.PostAsync("/v1/messages",
                Body("""{"model":"gpt-4o-mini","max_tokens":10,"messages":[{"role":"user","content":[{"type":"image","source":{"type":"base64","media_type":"image/png","data":"AAAA"}}]}]}"""));
            var json = await resp.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            Assert.Contains("invalid_request_error", json);
            Assert.Contains("image", json);
        }

        [Fact]
        public async Task Streamed_unsupported_request_is_plain_json_400_not_sse()
        {
            var resp = await _client.PostAsync("/v1/chat/completions",
                Body("""{"stream":true,"messages":[{"role":"user","content":"hi"}],"tool_choice":"auto","tools":[{"type":"function","function":{"name":"f"}}]}"""));

            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            Assert.NotEqual("text/event-stream", resp.Content.Headers.ContentType?.MediaType);
        }
    }
}

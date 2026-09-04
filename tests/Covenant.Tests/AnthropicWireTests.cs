using System.Text.Json;
using Covenant.Core;
using Covenant.Host;
using Xunit;

namespace Covenant.Tests;

/// <summary>Anthropic wire ↔ canonical mapping: string and block-array content normalize identically, system prepends, non-text blocks are ignored, denial kinds map to Anthropic's error taxonomy.</summary>
public class AnthropicWireTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    [Fact]
    public void String_content_maps_to_a_user_message()
    {
        var request = new AnthropicMessagesRequest
        {
            Messages = [new AnthropicWireMessage { Role = "user", Content = Json("\"hello claude\"") }],
        };

        var canonical = AnthropicWire.ToCanonical(request);

        Assert.Single(canonical);
        Assert.Equal(ChatRole.User, canonical[0].Role);
        Assert.Equal("hello claude", canonical[0].Content);
    }

    [Fact]
    public void Block_array_content_concatenates_text_blocks_and_ignores_the_rest()
    {
        var request = new AnthropicMessagesRequest
        {
            Messages =
            [
                new AnthropicWireMessage
                {
                    Role = "user",
                    Content = Json("""
                        [{"type":"text","text":"part one, "},
                         {"type":"image","source":{"type":"base64","data":"zzz"}},
                         {"type":"text","text":"part two"}]
                        """),
                },
            ],
        };

        var canonical = AnthropicWire.ToCanonical(request);

        Assert.Equal("part one, part two", canonical[0].Content);       // image block dropped, text kept
    }

    [Fact]
    public void System_prompt_prepends_as_a_system_message_and_roles_map()
    {
        var request = new AnthropicMessagesRequest
        {
            System = Json("\"you are terse\""),
            Messages =
            [
                new AnthropicWireMessage { Role = "user", Content = Json("\"hi\"") },
                new AnthropicWireMessage { Role = "assistant", Content = Json("\"hello\"") },
            ],
        };

        var canonical = AnthropicWire.ToCanonical(request);

        Assert.Equal(3, canonical.Count);
        Assert.Equal(ChatRole.System, canonical[0].Role);
        Assert.Equal("you are terse", canonical[0].Content);
        Assert.Equal(ChatRole.User, canonical[1].Role);
        Assert.Equal(ChatRole.Assistant, canonical[2].Role);
    }

    [Fact]
    public void Response_builder_produces_the_messages_api_shape()
    {
        var response = new InferenceResponse(
            new ChatMessage(ChatRole.Assistant, "governed answer"),
            new Usage(120, 45, 0.0007m),
            "claude-haiku-4-5");

        var wire = AnthropicWire.BuildResponse("msg_abc", response);

        Assert.Equal("msg_abc", wire.Id);
        Assert.Equal("message", wire.Type);
        Assert.Equal("assistant", wire.Role);
        Assert.Equal("claude-haiku-4-5", wire.Model);
        Assert.Equal("governed answer", Assert.Single(wire.Content).Text);
        Assert.Equal("end_turn", wire.StopReason);
        Assert.Equal(120, wire.Usage.InputTokens);
        Assert.Equal(45, wire.Usage.OutputTokens);
    }

    [Theory]
    [InlineData(DenialKind.Unauthenticated, "authentication_error", 401)]
    [InlineData(DenialKind.RateLimited, "rate_limit_error", 429)]
    [InlineData(DenialKind.UpstreamFailure, "api_error", 502)]
    [InlineData(DenialKind.Governance, "permission_error", 403)]
    public void Denial_kinds_map_to_anthropics_error_taxonomy(DenialKind kind, string errorType, int status)
    {
        var (type, code) = AnthropicWire.MapDenial(kind);

        Assert.Equal(errorType, type);
        Assert.Equal(status, code);
    }

    // The SSE event payloads are hand-built strings — parse each to prove it is well-formed JSON with
    // the right shape, and that interpolated values (which could contain quotes) are escaped.
    private static JsonElement Parse(string s) => JsonDocument.Parse(s).RootElement.Clone();

    [Fact]
    public void Message_start_event_is_well_formed_and_escapes_its_values()
    {
        var e = Parse(AnthropicWire.MessageStartEvent("msg_1", "weird\"model"));

        Assert.Equal("message_start", e.GetProperty("type").GetString());
        Assert.Equal("msg_1", e.GetProperty("message").GetProperty("id").GetString());
        Assert.Equal("weird\"model", e.GetProperty("message").GetProperty("model").GetString()); // survived escaping
        Assert.Equal("assistant", e.GetProperty("message").GetProperty("role").GetString());
    }

    [Fact]
    public void Content_block_delta_escapes_quotes_and_newlines_in_text()
    {
        var e = Parse(AnthropicWire.ContentBlockDeltaEvent("he said \"hi\"\nbye"));

        Assert.Equal("content_block_delta", e.GetProperty("type").GetString());
        Assert.Equal("he said \"hi\"\nbye", e.GetProperty("delta").GetProperty("text").GetString());
    }

    [Fact]
    public void All_static_and_computed_events_parse_as_json()
    {
        foreach (var payload in new[]
        {
            AnthropicWire.ContentBlockStartEvent,
            AnthropicWire.ContentBlockStopEvent,
            AnthropicWire.MessageStopEvent,
            AnthropicWire.MessageDeltaEvent(42),
            AnthropicWire.ErrorEvent("rate_limit_error", "slow down \"friend\""),
        })
        {
            var e = Parse(payload);
            Assert.True(e.TryGetProperty("type", out _));           // every event carries a type
        }

        var delta = Parse(AnthropicWire.MessageDeltaEvent(42));
        Assert.Equal(42, delta.GetProperty("usage").GetProperty("output_tokens").GetInt32());
        var err = Parse(AnthropicWire.ErrorEvent("rate_limit_error", "slow down \"friend\""));
        Assert.Equal("rate_limit_error", err.GetProperty("error").GetProperty("type").GetString());
        Assert.Equal("slow down \"friend\"", err.GetProperty("error").GetProperty("message").GetString());
    }
}

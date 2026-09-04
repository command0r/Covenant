using System.Text.Json;
using System.Text.Json.Serialization;
using Covenant.Core;

namespace Covenant.Host;

// Anthropic Messages API wire shapes (/v1/messages) — the second ingress dialect (ADR-0001 start set).
// Content fields are string-or-block-array in the protocol, so they bind as JsonElement and normalize
// through AnthropicWire. First slice speaks text blocks; tool_use/multimodal are not yet parsed.

public sealed class AnthropicMessagesRequest
{
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("max_tokens")] public int? MaxTokens { get; set; }   // accepted; not yet forwarded
    [JsonPropertyName("stream")] public bool? Stream { get; set; }
    [JsonPropertyName("system")] public JsonElement? System { get; set; }
    [JsonPropertyName("messages")] public List<AnthropicWireMessage> Messages { get; set; } = [];
}

public sealed class AnthropicWireMessage
{
    [JsonPropertyName("role")] public string Role { get; set; } = "user";
    [JsonPropertyName("content")] public JsonElement Content { get; set; }
}

public sealed class AnthropicTextBlock
{
    [JsonPropertyName("type")] public string Type { get; set; } = "text";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
}

public sealed class AnthropicUsage
{
    [JsonPropertyName("input_tokens")] public long InputTokens { get; set; }
    [JsonPropertyName("output_tokens")] public long OutputTokens { get; set; }
}

public sealed class AnthropicMessageResponse
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "message";
    [JsonPropertyName("role")] public string Role { get; set; } = "assistant";
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("content")] public List<AnthropicTextBlock> Content { get; set; } = [];
    [JsonPropertyName("stop_reason")] public string? StopReason { get; set; }
    [JsonPropertyName("stop_sequence")] public string? StopSequence { get; set; }
    [JsonPropertyName("usage")] public AnthropicUsage Usage { get; set; } = new();
}

public sealed class AnthropicErrorResponse
{
    [JsonPropertyName("type")] public string Type { get; set; } = "error";
    [JsonPropertyName("error")] public AnthropicErrorBody Error { get; set; } = new();
}

public sealed class AnthropicErrorBody
{
    [JsonPropertyName("type")] public string Type { get; set; } = "invalid_request_error";
    [JsonPropertyName("message")] public string Message { get; set; } = "";
}

// The [JsonSerializable] registrations for these types live on the single CovenantJsonContext
// declaration in OpenAiContracts.cs — one attributed partial only (source-generator requirement).

/// <summary>Pure Anthropic-wire ↔ canonical mapping — unit-tested without a server.</summary>
public static class AnthropicWire
{
    /// <summary>Normalizes string-or-block-array content to plain text; non-text blocks are ignored.</summary>
    public static string ExtractText(JsonElement content) => content.ValueKind switch
    {
        JsonValueKind.String => content.GetString() ?? "",
        JsonValueKind.Array => string.Concat(
            content.EnumerateArray()
                .Where(b => b.ValueKind == JsonValueKind.Object
                    && b.TryGetProperty("type", out var t) && t.GetString() == "text"
                    && b.TryGetProperty("text", out _))
                .Select(b => b.GetProperty("text").GetString() ?? "")),
        _ => "",
    };

    public static List<ChatMessage> ToCanonical(AnthropicMessagesRequest request)
    {
        var messages = new List<ChatMessage>();
        if (request.System is { } sys && ExtractText(sys) is { Length: > 0 } sysText)
            messages.Add(new ChatMessage(ChatRole.System, sysText));
        foreach (var m in request.Messages)
            messages.Add(new ChatMessage(
                string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? ChatRole.Assistant : ChatRole.User,
                ExtractText(m.Content)));
        return messages;
    }

    public static AnthropicMessageResponse BuildResponse(string id, InferenceResponse response) => new()
    {
        Id = id,
        Model = response.ServedByModel,
        Content = [new AnthropicTextBlock { Text = response.Message.Content }],
        StopReason = "end_turn",
        Usage = new AnthropicUsage
        {
            InputTokens = response.Usage.InputTokens,
            OutputTokens = response.Usage.OutputTokens,
        },
    };

    /// <summary>Denial kinds → Anthropic error taxonomy + HTTP status (matches the API's own table).</summary>
    public static (string ErrorType, int Status) MapDenial(DenialKind kind) => kind switch
    {
        DenialKind.Unauthenticated => ("authentication_error", StatusCodes.Status401Unauthorized),
        DenialKind.RateLimited => ("rate_limit_error", StatusCodes.Status429TooManyRequests),
        DenialKind.UpstreamFailure => ("api_error", StatusCodes.Status502BadGateway),
        _ => ("permission_error", StatusCodes.Status403Forbidden),
    };

    // Messages-API SSE event payloads, hand-built (source-gen contexts don't cover partial stream frames).
    // JsonEncodedText escapes interpolated values, so model ids and delta text can't break the JSON.
    // These are pure so the wire format is unit-tested by parsing, not proven only in a live stream.
    private static string J(string s) => System.Text.Json.JsonEncodedText.Encode(s).ToString();

    public static string MessageStartEvent(string id, string model) =>
        $"{{\"type\":\"message_start\",\"message\":{{\"id\":\"{J(id)}\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"{J(model)}\",\"content\":[],\"stop_reason\":null,\"usage\":{{\"input_tokens\":0,\"output_tokens\":0}}}}}}";

    public const string ContentBlockStartEvent =
        "{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}";

    public static string ContentBlockDeltaEvent(string text) =>
        $"{{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{{\"type\":\"text_delta\",\"text\":\"{J(text)}\"}}}}";

    public const string ContentBlockStopEvent = "{\"type\":\"content_block_stop\",\"index\":0}";

    public static string MessageDeltaEvent(long outputTokens) =>
        $"{{\"type\":\"message_delta\",\"delta\":{{\"stop_reason\":\"end_turn\",\"stop_sequence\":null}},\"usage\":{{\"output_tokens\":{outputTokens}}}}}";

    public const string MessageStopEvent = "{\"type\":\"message_stop\"}";

    public static string ErrorEvent(string errorType, string message) =>
        $"{{\"type\":\"error\",\"error\":{{\"type\":\"{J(errorType)}\",\"message\":\"{J(message)}\"}}}}";
}

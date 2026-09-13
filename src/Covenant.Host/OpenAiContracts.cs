using System.Text.Json;
using System.Text.Json.Serialization;

namespace Covenant.Host;

// Minimal subset of the OpenAI chat-completions wire shape for the first slice.

public sealed class OpenAiChatRequest
{
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("messages")] public List<OpenAiMessage> Messages { get; set; } = [];
    [JsonPropertyName("stream")] public bool? Stream { get; set; }
    [JsonPropertyName("max_tokens")] public int? MaxTokens { get; set; }
    [JsonPropertyName("max_completion_tokens")] public int? MaxCompletionTokens { get; set; }
    /// <summary>Everything else the client sent. Inspected by OpenAiWire.Unsupported so that features
    /// governance cannot see (tools, functions) are refused, never silently dropped.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class OpenAiMessage
{
    [JsonPropertyName("role")] public string Role { get; set; } = "user";
    /// <summary>String in the classic protocol; an array of content parts (text/image_url/…) in the
    /// multimodal one. Bound raw so ingress can refuse anything but text — see OpenAiWire.</summary>
    [JsonPropertyName("content")] public JsonElement? Content { get; set; }
    [JsonPropertyName("tool_calls"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public JsonElement? ToolCalls { get; set; }
}

/// <summary>Pure OpenAI-wire ↔ canonical helpers — unit-tested without a server.</summary>
public static class OpenAiWire
{
    private static readonly string[] UnsupportedTopLevel = ["tools", "tool_choice", "functions", "function_call"];

    /// <summary>Why this request cannot be governed, or null if it is plain text chat.</summary>
    public static string? Unsupported(OpenAiChatRequest r)
    {
        if (r.Extra is { } extra)
            foreach (var key in UnsupportedTopLevel)
                if (extra.TryGetValue(key, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                    return $"'{key}' (tool use is not yet governed)";
        foreach (var m in r.Messages)
        {
            if (m.ToolCalls is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) })
                return "'tool_calls' (tool use is not yet governed)";
            if (string.Equals(m.Role, "tool", StringComparison.OrdinalIgnoreCase))
                return "role 'tool' (tool use is not yet governed)";
            if (m.Content is { ValueKind: JsonValueKind.Array } parts)
                foreach (var p in parts.EnumerateArray())
                {
                    var type = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("type", out var t) ? t.GetString() : null;
                    if (type != "text") return $"content part '{type ?? "?"}' (only text can be classified)";
                }
        }
        return null;
    }

    /// <summary>Text of a message: a string, or the concatenation of text parts (Unsupported already
    /// guaranteed there are no other kinds).</summary>
    public static string Text(OpenAiMessage m) => m.Content switch
    {
        { ValueKind: JsonValueKind.String } s => s.GetString() ?? "",
        { ValueKind: JsonValueKind.Array } parts => string.Concat(parts.EnumerateArray()
            .Select(p => p.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "")),
        _ => "",
    };

    /// <summary>Response content is always a string on this wire.</summary>
    public static JsonElement TextElement(string s) => JsonSerializer.SerializeToElement(s, CovenantJsonContext.Default.String);
}

public sealed class OpenAiChatResponse
{
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("choices")] public List<OpenAiChoice> Choices { get; set; } = [];
    [JsonPropertyName("usage")] public OpenAiUsage Usage { get; set; } = new();
}

public sealed class OpenAiChoice
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("message")] public OpenAiMessage Message { get; set; } = new();
}

public sealed class OpenAiUsage
{
    [JsonPropertyName("prompt_tokens")] public long PromptTokens { get; set; }
    [JsonPropertyName("completion_tokens")] public long CompletionTokens { get; set; }
    [JsonPropertyName("total_tokens")] public long TotalTokens { get; set; }
}

// SSE chunk shape (OpenAI "chat.completion.chunk"). Intermediate chunks carry delta content with
// null finish_reason/usage; the final chunk carries finish_reason "stop" and the usage totals.

public sealed class OpenAiChatChunk
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("object")] public string Object { get; set; } = "chat.completion.chunk";
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("choices")] public List<OpenAiChunkChoice> Choices { get; set; } = [];
    [JsonPropertyName("usage")] public OpenAiUsage? Usage { get; set; }
}

public sealed class OpenAiChunkChoice
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("delta")] public OpenAiDelta Delta { get; set; } = new();
    [JsonPropertyName("finish_reason")] public string? FinishReason { get; set; }
}

public sealed class OpenAiDelta
{
    [JsonPropertyName("role")] public string? Role { get; set; }
    [JsonPropertyName("content")] public string? Content { get; set; }
}

public sealed class ErrorResponse
{
    [JsonPropertyName("error")] public string Error { get; set; } = "";
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
}

// Model listing (GET /v1/models) — real OpenAI-compatible clients call this to populate their model
// picker before chatting. Covenant lists only policy-permitted models: discovery is governed too.

public sealed class OpenAiModelList
{
    [JsonPropertyName("object")] public string Object { get; set; } = "list";
    [JsonPropertyName("data")] public List<OpenAiModel> Data { get; set; } = [];
}

public sealed class OpenAiModel
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("object")] public string Object { get; set; } = "model";
    [JsonPropertyName("owned_by")] public string OwnedBy { get; set; } = "covenant";
}

// Admin surface: kill-switch control (see Program.cs /admin/kill-switch).

public sealed class KillSwitchRequest
{
    [JsonPropertyName("engaged")] public bool Engaged { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}

public sealed class KillSwitchState
{
    [JsonPropertyName("engaged")] public bool Engaged { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}

public sealed class ResetResponse
{
    /// <summary>Where the previous audit log was archived; null if there was nothing to archive.</summary>
    [JsonPropertyName("archived_to")] public string? ArchivedTo { get; set; }
}

[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(OpenAiChatRequest))]
[JsonSerializable(typeof(OpenAiChatResponse))]
[JsonSerializable(typeof(OpenAiChatChunk))]
[JsonSerializable(typeof(OpenAiModelList))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(KillSwitchRequest))]
[JsonSerializable(typeof(KillSwitchState))]
[JsonSerializable(typeof(ResetResponse))]
[JsonSerializable(typeof(EvidenceReport))]
[JsonSerializable(typeof(StatusReport))]
// Anthropic dialect (types defined in AnthropicContracts.cs) — every [JsonSerializable] must live on
// ONE partial declaration of the context; splitting them collides the source generator's hint names.
[JsonSerializable(typeof(AnthropicMessagesRequest))]
[JsonSerializable(typeof(AnthropicMessageResponse))]
[JsonSerializable(typeof(AnthropicErrorResponse))]
public partial class CovenantJsonContext : JsonSerializerContext;

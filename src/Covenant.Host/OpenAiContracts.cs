using System.Text.Json.Serialization;

namespace Covenant.Host;

// Minimal subset of the OpenAI chat-completions wire shape for the first slice.

public sealed class OpenAiChatRequest
{
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("messages")] public List<OpenAiMessage> Messages { get; set; } = [];
    [JsonPropertyName("stream")] public bool? Stream { get; set; }
}

public sealed class OpenAiMessage
{
    [JsonPropertyName("role")] public string Role { get; set; } = "user";
    [JsonPropertyName("content")] public string Content { get; set; } = "";
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
public partial class CovenantJsonContext : JsonSerializerContext;

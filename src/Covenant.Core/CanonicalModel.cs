namespace Covenant.Core;

/// <summary>Role of a message in the canonical model. Provider-specific roles map to these inside adapters only.</summary>
public enum ChatRole { System, User, Assistant, Tool }

/// <summary>Data-sensitivity classification carried through the pipeline. Ordered: higher value = more restrictive.</summary>
public enum DataClassification { Public = 0, Internal = 1, Pii = 2, Phi = 3 }

/// <summary>A single canonical chat message. No multimodal content in the first slice.</summary>
public sealed record ChatMessage(ChatRole Role, string Content);

/// <summary>One canonical streamed fragment of assistant output (ADR-0002).</summary>
public sealed record ChatDelta(string Content);

/// <summary>Cost-attribution tags supplied by the caller (e.g. via virtual-key metadata or request headers).</summary>
public sealed record AttributionTags(string Team, string Workflow, string UseCase)
{
    public static readonly AttributionTags Unattributed = new("unknown", "unknown", "unknown");
}

/// <summary>The canonical inference request. Everything past ingress speaks this shape, never a provider shape.</summary>
public sealed record InferenceRequest(
    string Principal,
    IReadOnlyList<ChatMessage> Messages,
    string? RequestedModel,
    AttributionTags Attribution,
    bool Stream = false);

/// <summary>Token and cost accounting for a single inference. Cost is filled by the attribution stage.</summary>
public sealed record Usage(long InputTokens, long OutputTokens, decimal CostUsd)
{
    public static readonly Usage Empty = new(0, 0, 0m);
    public long TotalTokens => InputTokens + OutputTokens;
}

/// <summary>The canonical inference response.</summary>
public sealed record InferenceResponse(ChatMessage Message, Usage Usage, string ServedByModel);

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

/// <summary>The canonical inference request — everything past ingress speaks this shape, never a provider
/// shape. Credential is the raw API key: consumed by the auth stage only, never in audit, spans, or logs.</summary>
public sealed record InferenceRequest(
    string Principal,
    IReadOnlyList<ChatMessage> Messages,
    string? RequestedModel,
    AttributionTags Attribution,
    bool Stream = false,
    string? Credential = null,
    int? MaxOutputTokens = null,
    string? UnsupportedFeature = null,    // set by ingress when the wire carried something governance cannot see (images, tools) → denied in-pipeline, audited
    string? RequestId = null,             // server-generated correlation id (ADR-0009); never caller-supplied
    string? RequestedProfile = null);     // workload profile asked for (X-Covenant-Profile / console); must be granted (ADR-0012)

/// <summary>An application identity resolved by the Host itself (console inference, ADR-0012) — never
/// taken from a client header or body. Deliberately NOT a member of <see cref="InferenceRequest"/> (the canonical
/// request every ingress builds from wire input): it can only be attached server-side through
/// <see cref="InferenceContext.BindConsoleIdentity"/>. The auth stage still enforces enabled + console testing +
/// profile grant.</summary>
public sealed record PreAuthenticatedIdentity(string ApplicationId, string Team, string Principal);

/// <summary>Who the pipeline decided the caller is: self-declared until the auth stage overwrites it
/// on a valid key. Everything downstream reads THIS, never the raw request headers.</summary>
public sealed record CallerIdentity(string Principal, AttributionTags Tags);

/// <summary>Token and cost accounting for a single inference. Cost is filled by the attribution stage.</summary>
public sealed record Usage(long InputTokens, long OutputTokens, decimal CostUsd)
{
    public static readonly Usage Empty = new(0, 0, 0m);
    public long TotalTokens => InputTokens + OutputTokens;
}

/// <summary>The canonical inference response. UsageReported=false means the provider returned no (or
/// partial) token counts: Usage tokens are then NOT a real zero (ADR-0009).
/// <para>FinishReason (additive): the provider's terminal finish reason in OpenAI vocabulary ("stop", "length",
/// "content_filter", "tool_calls"), null when not reported (legacy compositions, buffered SDK responses without one).
/// "length" ⇒ the answer was truncated at the enforced output bound.</para></summary>
public sealed record InferenceResponse(ChatMessage Message, Usage Usage, string ServedByModel, bool UsageReported = true, string? FinishReason = null);

using System.ClientModel;
using Anthropic.Exceptions;
using Covenant.Adapters;
using Covenant.Core;

namespace Covenant.Host;

/// <summary>SDK-aware provider failure classification (ADR-0013; the Host knows the SDK types, the adapter layer does not).
/// <list type="bullet">
/// <item>OpenAI / OpenAI-compatible / Ollama (OpenAI SDK 2.12, System.ClientModel 1.14): <see cref="ClientResultException.Status"/>
/// (int; 0 when no response was received ⇒ connect).</item>
/// <item>Anthropic SDK 12.8.0 (types verified in the assembly metadata): <see cref="AnthropicApiException.StatusCode"/>
/// (System.Net.HttpStatusCode) on the API exception family (AnthropicRateLimitException, Anthropic5xxException, …);
/// <see cref="AnthropicIOException"/> (network I/O) ⇒ connect.</item>
/// <item>BCL: HttpRequestException (status or connect), SocketException ⇒ connect, TimeoutException / provider-side
/// cancellation ⇒ timeout (see <see cref="BclProviderFailureClassifier"/>).</item>
/// </list>
/// 429 ⇒ "429"; 500/502/503/504 ⇒ "5xx"; any other status ⇒ "non_retryable" (400/401/403/404/422 are never retried).</summary>
public sealed class SdkProviderFailureClassifier : BclProviderFailureClassifier
{
    public static readonly SdkProviderFailureClassifier Shared = new();

    protected override string? ClassifyOne(Exception e) => e switch
    {
        ClientResultException { Status: > 0 } cre => FromStatus(cre.Status),
        ClientResultException { InnerException: not null } => null,   // no response: classify the transport cause
        ClientResultException => TraceReasons.Connect,
        AnthropicApiException api => FromStatus((int)api.StatusCode),
        AnthropicIOException => TraceReasons.Connect,
        _ => base.ClassifyOne(e),
    };
}

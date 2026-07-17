using Covenant.Adapters;
using Covenant.Core;
using Covenant.Governance;
using MEAI = Microsoft.Extensions.AI;

namespace Covenant.Tests;

/// <summary>Minimal pipeline harness for telemetry tests: standard stage order, stub provider,
/// no-op audit sink. Kept separate from PipelineSliceTests so the two files stay uncoupled.</summary>
internal static class TelemetryPipeline
{
    private sealed class NullAuditSink : IAuditSink
    {
        public ValueTask EnqueueAsync(AuditEntry entry, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    private sealed class FixedChatClient : MEAI.IChatClient
    {
        public Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> messages, MEAI.ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, "ok"))
            {
                Usage = new MEAI.UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 }
            });

        public IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<MEAI.ChatMessage> messages, MEAI.ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>Runs one request through a standard pipeline. failClosed=true expects content that
    /// classifies as PII/PHI (no local adapter registered → denial).</summary>
    public static async Task<InferenceContext> RunAsync(string content, string team, bool failClosed)
    {
        var registry = new ChatClientRegistry(new Dictionary<string, MEAI.IChatClient>
        {
            ["openai"] = new FixedChatClient()
            // no "local" → PII/PHI denied, which is what the deny-path tests want
        });
        var policy = new PolicyConfig
        {
            AllowedRoutes = new Dictionary<DataClassification, IReadOnlyList<RouteTarget>>
            {
                [DataClassification.Public]   = [new("openai", "gpt-4o-mini")],
                [DataClassification.Internal] = [new("openai", "gpt-4o-mini")],
                [DataClassification.Pii]      = [new("local", "llama-3.1-8b-instruct")],
                [DataClassification.Phi]      = [new("local", "llama-3.1-8b-instruct")],
            }
        };
        var prices = new PriceBook(new Dictionary<string, (decimal, decimal)> { ["gpt-4o-mini"] = (0.15m, 0.60m) });

        var pipeline = new InferencePipeline(
        [
            new AuditStage(new NullAuditSink()),
            new ClassifyStage(new RegexDataClassifier()),
            new PolicyStage(new PolicyEngine(policy)),
            new BudgetStage(new KillSwitch(), new InMemorySpendLedger(), new BudgetConfig { GlobalCapUsd = 1_000m }),
            new ProviderCallStage(registry),
            new AttributionStage(prices),
        ]);

        var ctx = new InferenceContext(new InferenceRequest(
            "tester", [new ChatMessage(ChatRole.User, content)], RequestedModel: null,
            new AttributionTags(team, "test-workflow", "test-case")));

        await pipeline.ExecuteAsync(ctx, default);
        return ctx;
    }
}

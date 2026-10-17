using System.Net;
using System.Runtime.CompilerServices;
using Covenant.Adapters;
using Covenant.Core;
using Covenant.Governance;
using Covenant.Host;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MEAI = Microsoft.Extensions.AI;

namespace Covenant.Tests;

/// <summary>Settable clock (no sleeps): budget periods, trace retention.</summary>
internal sealed class SettableClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Set(DateTimeOffset now) => _now = now;
    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Stub provider whose behaviour per call is scripted (never a paid provider). Each call dequeues the next
/// behaviour (the last one repeats). Records the ChatOptions it saw.</summary>
internal sealed class StubChatClient(string reply = "stub reply") : MEAI.IChatClient
{
    public enum Mode
    {
        Serve, ServeNoUsage, Throw, Hang, StreamThenThrow, ServeHugeUsage, StreamGated,
        /// <summary>Streams text, then ends WITHOUT a terminal update carrying a finish reason (review round 2, B1).</summary>
        StreamNoFinish,
        /// <summary>Buffered/streamed: served with finish reason "length" (truncated at the output bound).</summary>
        ServeLength,
        /// <summary>Buffered: waits for <see cref="Gate"/> before serving (B7 early request id).</summary>
        Gated,
        /// <summary>Streamed: a legitimately COMPLETED stream with no text at all (terminal update only).</summary>
        StreamEmptyComplete,
    }

    /// <summary>StreamGated: the first chunk is sent, then the stream waits for this gate before finishing.</summary>
    public TaskCompletionSource? Gate { get; set; }

    private readonly Queue<(Mode Mode, Func<Exception>? Error)> _script = new();
    private (Mode Mode, Func<Exception>? Error) _last = (Mode.Serve, null);
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);
    public MEAI.ChatOptions? LastOptions { get; private set; }
    /// <summary>The messages of the most recent call (ADR-0015 grounding assertions).</summary>
    public IReadOnlyList<MEAI.ChatMessage> LastMessages { get; private set; } = [];
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>Invoked when a call starts (before its behaviour), e.g. to trip the pause between attempts.</summary>
    public Action? OnCall { get; set; }

    public StubChatClient Then(Mode mode, Func<Exception>? error = null)
    {
        lock (_script) _script.Enqueue((mode, error));
        return this;
    }

    private (Mode Mode, Func<Exception>? Error) Next()
    {
        lock (_script)
        {
            if (_script.Count > 0) _last = _script.Dequeue();
            return _last;
        }
    }

    public async Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> messages, MEAI.ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        LastOptions = options;
        LastMessages = messages.ToList();
        OnCall?.Invoke();
        Entered.TrySetResult();
        var (mode, error) = Next();
        switch (mode)
        {
            case Mode.Throw:
                throw error?.Invoke() ?? new HttpRequestException("stub failure");
            case Mode.Hang:
                await Task.Delay(Timeout.Infinite, cancellationToken);
                break;
            case Mode.Gated when Gate is { } g:
                await g.Task.WaitAsync(cancellationToken);
                break;
        }
        var usage = mode switch
        {
            Mode.ServeNoUsage => null,
            Mode.ServeHugeUsage => new MEAI.UsageDetails { InputTokenCount = 1_000_000, OutputTokenCount = 1_000_000 },
            _ => new MEAI.UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 },
        };
        return new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, reply))
        {
            Usage = usage,
            FinishReason = mode == Mode.ServeLength ? MEAI.ChatFinishReason.Length : MEAI.ChatFinishReason.Stop,
        };
    }

    public async IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<MEAI.ChatMessage> messages, MEAI.ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        LastOptions = options;
        OnCall?.Invoke();
        Entered.TrySetResult();
        var (mode, error) = Next();
        await Task.Yield();
        if (mode == Mode.Throw) throw error?.Invoke() ?? new HttpRequestException("stub failure");
        if (mode == Mode.Hang) await Task.Delay(Timeout.Infinite, cancellationToken);
        if (mode == Mode.StreamEmptyComplete)
        {
            yield return new MEAI.ChatResponseUpdate
            {
                Contents = [new MEAI.UsageContent(new MEAI.UsageDetails { InputTokenCount = 10, OutputTokenCount = 0 })],
                FinishReason = MEAI.ChatFinishReason.Stop,
            };
            yield break;
        }
        yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, "partial ");
        if (mode == Mode.StreamGated && Gate is { } gate) await gate.Task.WaitAsync(cancellationToken);
        if (mode == Mode.StreamThenThrow) throw error?.Invoke() ?? new HttpRequestException("connection reset mid-stream", null, HttpStatusCode.ServiceUnavailable);
        yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, reply);
        if (mode == Mode.StreamNoFinish) yield break;   // EOF with no terminal completion (e.g. a swallowed SSE error)
        yield return new MEAI.ChatResponseUpdate
        {
            Contents = [new MEAI.UsageContent(new MEAI.UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 })],
            FinishReason = mode == Mode.ServeLength ? MEAI.ChatFinishReason.Length : MEAI.ChatFinishReason.Stop,
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

internal static class Failures
{
    public static Exception Status(HttpStatusCode code) => new HttpRequestException($"stub HTTP {(int)code}", null, code);
    public static Exception Connect() => new HttpRequestException("stub connection refused", new System.Net.Sockets.SocketException(10061));
    public static Exception ProviderTimeout() => new TaskCanceledException("stub provider-side timeout");
}

/// <summary>Pure v2 pipeline (no Host): the Host's stage order with a bound configuration snapshot, the persistent-ledger
/// budget stage over an in-memory store, the SDK-aware classifier and observed health.</summary>
internal sealed class V2Rig
{
    public sealed class ListSink : IAuditSink
    {
        public List<AuditEntry> Entries { get; } = [];
        public ValueTask EnqueueAsync(AuditEntry entry, CancellationToken cancellationToken = default)
        {
            lock (Entries) Entries.Add(entry);
            return ValueTask.CompletedTask;
        }
    }

    public required InferencePipeline Pipeline { get; init; }
    public required ListSink Sink { get; init; }
    public required InMemoryLedgerStore Store { get; init; }
    public required BudgetLedger Ledger { get; init; }
    public required KillSwitch Kill { get; init; }
    public required ProviderHealth Health { get; init; }
    public RuntimeSnapshot Snapshot { get; set; } = null!;

    public static V2Rig Build(CovenantConfig config, IReadOnlyDictionary<string, MEAI.IChatClient> clients, TimeProvider? clock = null,
        ResponseCache? cache = null, BudgetLedger? ledger = null, InMemoryLedgerStore? store = null, IWebSearchClient? search = null, bool withSearch = false)
    {
        store ??= new InMemoryLedgerStore();
        ledger ??= new BudgetLedger(store, clock: clock);
        var sink = new ListSink();
        var kill = new KillSwitch();
        var health = new ProviderHealth();
        var pipeline = new InferencePipeline(
        [
            new AuditStage(sink),
            new AuthStage(new AuthConfig
            {
                AllowAnonymous = false,
                Keys =
                [
                    new ApiKeyRecord("demo-key", "demo-service", "platform", "demo-app"),
                    new ApiKeyRecord("batch-key", "batch-app", "data"),
                ],
            }),
            new ShapeStage(),
            new ClassifyStage(new RegexDataClassifier()),
            new PolicyStage(new PolicyEngine(new PolicyConfig { AllowedRoutes = new Dictionary<DataClassification, IReadOnlyList<RouteTarget>>() })),
            new CacheStage(cache ?? new ResponseCache(clock), new CacheConfig()),
            new RateLimitStage(new RateCounter(), new RateCounter(), new RateLimitConfig()),
            new PauseStage(kill),
            new BudgetStage(kill, ledger),
            .. (withSearch || search is not null ? new IPipelineStage[] { new WebSearchStage(search, new RegexDataClassifier()) } : Array.Empty<IPipelineStage>()),
            new ProviderCallStage(new ChatClientRegistry(clients), health, classifier: SdkProviderFailureClassifier.Shared),
            new AttributionStage(new PriceBook(new Dictionary<string, (decimal, decimal)>())),
        ]);
        return new V2Rig { Pipeline = pipeline, Sink = sink, Store = store, Ledger = ledger, Kill = kill, Health = health, Snapshot = new RuntimeSnapshot(config) };
    }

    public async Task<(InferenceContext Ctx, AuditEntry Entry)> RunAsync(string prompt, string? profile = null, string key = "demo-key",
        bool stream = false, int? maxTokens = null, string? model = null, CancellationToken ct = default, WebSearchRequest? webSearch = null)
    {
        var ctx = new InferenceContext(new InferenceRequest("caller", [new ChatMessage(ChatRole.User, prompt)], model,
            new AttributionTags("self-declared", "wf", "uc"), Stream: stream, Credential: key, MaxOutputTokens: maxTokens,
            RequestId: $"req_{Guid.NewGuid():n}", RequestedProfile: profile, WebSearch: webSearch));
        ctx.Configuration = Snapshot;
        ctx.ConfigVersion = Snapshot.Version;
        var deltas = new System.Text.StringBuilder();
        if (stream) ctx.DeltaSink = (d, _) => { deltas.Append(d.Content); return ValueTask.CompletedTask; };
        int before;
        lock (Sink.Entries) before = Sink.Entries.Count;
        try
        {
            await Pipeline.ExecuteAsync(ctx, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // the caller cancelled: the record is still written (asserted by the caller)
        }
        lock (Sink.Entries)
        {
            if (Sink.Entries.Count != before + 1) throw new Xunit.Sdk.XunitException($"expected exactly one audit record, got {Sink.Entries.Count - before}");
            return (ctx, Sink.Entries[^1]);
        }
    }

    /// <summary>Hybrid demo config with a priced cloud primary and a priced second cloud fallback so fallback charges are
    /// visible; Public → [openai/gpt-4o-mini, openai/gpt-4o, ollama/qwen3:8b] with the given retry settings.</summary>
    public static CovenantConfig FallbackConfig(RetrySettings? retry = null, decimal cap = 100m, Func<CovenantConfig, CovenantConfig>? tweak = null)
        => V2.Hybrid(tweak: c =>
        {
            var x = c with
            {
                Policies = c.Policies.Select(p => p.Id != "public" ? p : p with
                {
                    Targets = [V2.T("openai", V2.CloudModel), V2.T("openai", "gpt-4o"), V2.T("ollama", V2.LocalTag)],
                    Retry = retry ?? new RetrySettings { MaxAttempts = 3, DeadlineMs = 60_000 },
                }).ToList(),
                Budgets = [new BudgetConfigV2 { Id = "global", Scope = "global", CapUsd = cap, Period = BudgetPeriod.Monthly }],
            };
            return tweak is null ? x : tweak(x);
        });

    public const string PublicPrompt = "Rewrite this public product announcement in one sentence.";
}

/// <summary>A Host booted from the demo-hybrid seed with stub providers on a caller-chosen audit path, optionally NOT
/// deleting its files on dispose — so a second instance can boot on the same audit log, configuration and ledger
/// (restart tests). Cloud price from the seed: $0.15 / $0.60 per 1M.</summary>
public sealed class RestartableHybridFactory(string auditPath, bool cleanup = true) : WebApplicationFactory<Program>
{
    public string AuditPath { get; } = auditPath;
    public string LedgerPath => AuditPath + ".ledger.jsonl";
    internal StubChatClient Local { get; } = new("local reply");
    internal StubChatClient Cloud { get; } = new("cloud reply");

    public static string NewAuditPath() => Path.Combine(Path.GetTempPath(), $"covenant-t04-{Guid.NewGuid():n}.log");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.UseSetting("Config:Seed", "demo-hybrid");
        builder.UseSetting($"Pricing:{V2.CloudModel}:InPer1M", "0.15");    // explicit, model-bound (seed never assumes)
        builder.UseSetting($"Pricing:{V2.CloudModel}:OutPer1M", "0.60");
        builder.UseSetting("OpenAI:ApiKey", HybridSeedFactory.Secret);
        builder.UseSetting("Anthropic:ApiKey", "");
        builder.UseSetting("Local:Endpoint", "http://127.0.0.1:11434/v1");
        builder.UseSetting("Local:ModelId", V2.LocalTag);
        builder.UseSetting("Admin:Token", CorrelationTestKit.AdminToken);
        builder.UseSetting("Budget:GlobalCapUsd", "100");
        builder.UseSetting("Auth:Keys:0:Key", "demo-key");
        builder.UseSetting("Auth:Keys:0:Principal", "demo-service");
        builder.UseSetting("Auth:Keys:0:Team", "platform");
        builder.UseSetting("Auth:Keys:0:Application", "demo-app");
        CorrelationTestKit.IsolatePaths(builder, AuditPath);
        builder.ConfigureTestServices(services =>
            services.AddSingleton<IChatClientRegistry>(new ChatClientRegistry(new Dictionary<string, MEAI.IChatClient>
            {
                [$"ollama:{V2.LocalTag}"] = Local,
                [$"openai:{V2.CloudModel}"] = Cloud,
            })));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (cleanup) CorrelationTestKit.DeleteWithSiblings(AuditPath);
    }
}

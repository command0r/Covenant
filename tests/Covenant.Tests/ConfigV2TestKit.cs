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
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MEAI = Microsoft.Extensions.AI;

namespace Covenant.Tests;

/// <summary>Builders for v2 configurations (ADR-0011..0013) used by the T02/T03 tests.</summary>
internal static class V2
{
    public const string LocalTag = "qwen3:8b";
    public const string CloudModel = "gpt-4o-mini";

    public static ConnectionConfig Ollama(bool enabled = true, string id = "ollama", string model = LocalTag) => new()
    {
        Id = id, DisplayName = "Ollama", Kind = ProviderKind.Ollama, Boundary = Boundary.Local,
        Endpoint = "http://127.0.0.1:11434/v1", Enabled = enabled,
        Models = [new ModelConfig { ModelId = model, Price = new PriceConfig { InputPer1M = 0m, OutputPer1M = 0m, Basis = "zero-marginal-local" } }],
    };

    public static ConnectionConfig OpenAi(bool enabled = true, PriceConfig? price = null, bool priced = true) => new()
    {
        Id = "openai", DisplayName = "OpenAI", Kind = ProviderKind.OpenAi, Boundary = Boundary.Cloud,
        Endpoint = null, Enabled = enabled, SecretRef = "OpenAI:ApiKey",
        Models =
        [
            new ModelConfig { ModelId = CloudModel, Price = priced ? price ?? new PriceConfig { InputPer1M = 0.15m, OutputPer1M = 0.60m, Basis = "provider-list" } : null },
            new ModelConfig { ModelId = "gpt-4o", Price = new PriceConfig { InputPer1M = 2.5m, OutputPer1M = 10m, Basis = "provider-list" } },
        ],
    };

    public static PolicyTarget T(string connection, string model, bool paused = false) => new() { ConnectionId = connection, ModelId = model, Paused = paused };

    public static PolicyConfigV2 Policy(string id, DataClassification[] classes, PolicyTarget[] targets,
        string[]? profiles = null, bool paused = false, bool enabled = true, RoutingStrategy strategy = RoutingStrategy.Fixed,
        int threshold = 400) => new()
    {
        Id = id, Name = id, Enabled = enabled, Paused = paused, Classifications = classes, Targets = targets,
        Profiles = profiles ?? [], Strategy = strategy, ThresholdTokens = threshold,
    };

    /// <summary>Hybrid demo defaults (spec §3): Public → cloud with local fallback; Internal/PII/PHI → local.</summary>
    public static CovenantConfig Hybrid(int version = 1, Func<CovenantConfig, CovenantConfig>? tweak = null)
    {
        var c = new CovenantConfig
        {
            Version = version,
            Origin = "test",
            CreatedUtc = DateTimeOffset.Parse("2026-10-16T00:00:00Z"),
            Mode = ProviderMode.Hybrid,
            Connections = [Ollama(), OpenAi()],
            Profiles =
            [
                new WorkloadProfileConfig { Id = "public-demo", Name = "Public demo", Baseline = DataClassification.Public },
                new WorkloadProfileConfig { Id = "internal-default", Name = "Internal", Baseline = DataClassification.Internal },
                new WorkloadProfileConfig { Id = "phi-workload", Name = "Clinical", Baseline = DataClassification.Phi },
            ],
            Applications =
            [
                new ApplicationConfig
                {
                    Id = "demo-app", Team = "platform", Enabled = true, ConsoleTesting = true,
                    GrantedProfiles = ["public-demo", "internal-default"], DefaultProfile = "internal-default",
                },
                new ApplicationConfig
                {
                    Id = "batch-app", Team = "data", Enabled = true, ConsoleTesting = false,
                    GrantedProfiles = ["internal-default"], DefaultProfile = "internal-default",
                },
                new ApplicationConfig
                {
                    Id = "off-app", Team = "data", Enabled = false, ConsoleTesting = true,
                    GrantedProfiles = ["public-demo"], DefaultProfile = "public-demo",
                },
            ],
            Policies =
            [
                Policy("public", [DataClassification.Public], [T("openai", CloudModel), T("ollama", LocalTag)]),
                Policy("internal", [DataClassification.Internal], [T("ollama", LocalTag)]),
                Policy("sensitive", [DataClassification.Pii, DataClassification.Phi], [T("ollama", LocalTag)]),
            ],
            Budgets = [new BudgetConfigV2 { Id = "global", Scope = "global", CapUsd = 100m, Period = BudgetPeriod.Monthly }],
            Settings = new OperationalSettings(),
        };
        return tweak is null ? c : tweak(c);
    }

    public static RuntimeSnapshot Snap(CovenantConfig c) => new(c);

    /// <summary>Startup environment of the tests: egress allows the local Ollama and OpenAI; only the Ollama
    /// authority is a startup-approved LOCAL endpoint (boundary binding, ADR-0011).</summary>
    public static ValidationEnvironment Env(bool secrets = true, params string[] extraEgress)
    {
        var egress = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "127.0.0.1:11434", "api.openai.com:443" };
        foreach (var e in extraEgress) egress.Add(e);
        return new()
        {
            EgressAllowList = egress,
            SecretAvailable = _ => secrets,
            LocalEndpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "127.0.0.1:11434" },
        };
    }

    public static InferenceContext Ctx(string prompt, string? model = null, string? profile = null, string? credential = null,
        PreAuthenticatedIdentity? pre = null)
    {
        var ctx = new InferenceContext(new InferenceRequest("caller", [new ChatMessage(ChatRole.User, prompt)], model,
            new AttributionTags("self-declared", "wf", "uc"), Credential: credential, RequestId: $"req_{Guid.NewGuid():n}",
            RequestedProfile: profile));
        if (pre is not null) ctx.BindConsoleIdentity(pre, "shared-admin-token");   // server-side only (ADR-0012)
        return ctx;
    }
}

/// <summary>Counts calls; optional gate blocks every call until released (snapshot isolation tests).</summary>
internal sealed class GatedChatClient(string reply = "gated reply") : MEAI.IChatClient
{
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);
    public TaskCompletionSource? Gate { get; set; }
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Disposed { get; private set; }

    public async Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> messages, MEAI.ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        Entered.TrySetResult();
        if (Gate is { } gate) await gate.Task.WaitAsync(cancellationToken);
        return new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, reply))
        { Usage = new MEAI.UsageDetails { InputTokenCount = 1000, OutputTokenCount = 1000 } };
    }

    public async IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<MEAI.ChatMessage> messages, MEAI.ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        await Task.Yield();
        yield return new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, reply);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() => Disposed = true;
}

/// <summary>A Host booted from the demo-hybrid seed (Config:Seed) with in-memory stub providers and an
/// application key bound to demo-app. Isolated audit file + configuration directory per instance.</summary>
public sealed class HybridSeedFactory : WebApplicationFactory<Program>
{
    public const string Secret = "sk-test-SECRET-VALUE-never-in-payloads-7f3a";
    public readonly string AuditPath = Path.Combine(Path.GetTempPath(), $"covenant-v2-{Guid.NewGuid():n}.log");
    internal readonly GatedChatClient Local = new("local reply");
    internal readonly GatedChatClient Cloud = new("cloud reply");

    public string ConfigDir => CorrelationTestKit.ConfigDirFor(AuditPath);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.UseSetting("Config:Seed", "demo-hybrid");
        builder.UseSetting("OpenAI:ApiKey", Secret);
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
        CorrelationTestKit.DeleteWithSiblings(AuditPath);   // audit file, ledger and the configuration repository
    }
}

internal static class V2Admin
{
    public static async Task<(HttpStatusCode Status, JsonElement Body, HttpResponseMessage Response)> SendAsync(
        HttpClient client, HttpMethod method, string path, string? json = null, string? ifMatch = null)
    {
        using var req = CorrelationTestKit.Admin(method, path);
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        if (ifMatch is not null) req.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        var resp = await client.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        var body = text.Length > 0 ? JsonDocument.Parse(text).RootElement.Clone() : default;
        return (resp.StatusCode, body, resp);
    }

    public static async Task<(int Version, CovenantConfig Config, string Raw)> GetConfigAsync(HttpClient client)
    {
        using var req = CorrelationTestKit.Admin(HttpMethod.Get, "/admin/config");
        using var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        var cfg = JsonSerializer.Deserialize(doc.RootElement.GetProperty("config").GetRawText(), ConfigJsonContext.Default.CovenantConfig)!;
        return (doc.RootElement.GetProperty("version").GetInt32(), cfg, raw);
    }

    public static string PutBody(CovenantConfig config, string summary)
        => JsonSerializer.Serialize(new ConfigPutRequest { Config = config, ChangeSummary = summary }, ConfigJsonContext.Default.ConfigPutRequest);

    public static async Task<int> ApplyAsync(HttpClient client, CovenantConfig config, int expected, string summary = "test edit")
    {
        var (status, body, _) = await SendAsync(client, HttpMethod.Put, "/admin/config", PutBody(config, summary), $"\"v{expected}\"");
        if (status != HttpStatusCode.OK) throw new Xunit.Sdk.XunitException($"PUT /admin/config → {(int)status}: {body}");
        return body.GetProperty("version").GetInt32();
    }

    public static async Task<List<AuditEntry>> AuditAsync(string auditPath, Func<List<AuditEntry>, bool> until)
    {
        for (int i = 0; i < 200; i++)
        {
            var (_, entries) = AuditChainVerifier.VerifyAndRead(auditPath);
            var list = entries.ToList();
            if (until(list)) return list;
            await Task.Delay(25);
        }
        throw new Xunit.Sdk.XunitException("expected audit entries never appeared");
    }
}

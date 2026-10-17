using System.Text.Json;
using Covenant.Core;
using Covenant.Host;
using Xunit;
// Aliased, not imported: Core/Host and the console both have config shapes. Not "Ui": inside namespace
// Covenant.Tests that simple name would bind to the Covenant.Ui namespace before any compilation-unit alias.
using UiModels = Covenant.Ui.Models;

namespace Covenant.Tests;

// Proof that a configuration the console saves round-trips through the Host (console v2, ADR-0011):
//   Host GET body  (ConfigJsonContext.ConfigGetResponse)
//     → console reads it (UiJsonContext.ConfigEnvelopeDto)
//     → console writes the PUT body (UiJsonContext.ConfigPutRequestDto)
//     → Host reads it (ConfigJsonContext.ConfigPutRequest)
// and the configuration that comes out is the one that went in. The UI DTOs are compiled in by link
// (Covenant.Tests.csproj), so a field the console drops or misspells fails here, not in production.
public sealed class ContractRoundTripTests
{
    private static readonly DateTimeOffset Created = new(2026, 10, 16, 9, 30, 0, TimeSpan.Zero);

    /// <summary>A representative v2 configuration that uses every enum value at least once.</summary>
    private static CovenantConfig Representative(ProviderMode mode) => new()
    {
        Version = 7,
        BaseVersion = 6,
        RolledBackFrom = null,
        Origin = "console",
        CreatedUtc = Created,
        ChangeSummary = "round-trip fixture",
        Mode = mode,
        Connections =
        [
            new ConnectionConfig
            {
                Id = "ollama", DisplayName = "Ollama (in perimeter)", Kind = ProviderKind.Ollama, Boundary = Boundary.Local,
                Endpoint = "http://ollama-host:11434/v1", Enabled = true, SecretRef = null, TimeoutSeconds = 90,
                Models =
                [
                    new ModelConfig
                    {
                        ModelId = "qwen3:8b", DisplayName = "Qwen 3 8B", ContextTokens = 32768, MaxOutputTokens = 4096,
                        Price = new PriceConfig { InputPer1M = 0m, OutputPer1M = 0m, Basis = "zero-marginal-local", EffectiveDate = new DateOnly(2026, 10, 1) },
                    },
                ],
            },
            new ConnectionConfig
            {
                Id = "vllm", DisplayName = "vLLM", Kind = ProviderKind.OpenAiCompatible, Boundary = Boundary.Local,
                Endpoint = "http://vllm-host:8000/v1", Enabled = false, SecretRef = "Secrets:vllm",
                Models = [new ModelConfig { ModelId = "llama-3.1-8b", Price = null }], // unknown price stays unknown
            },
            new ConnectionConfig
            {
                Id = "openai", DisplayName = "OpenAI", Kind = ProviderKind.OpenAi, Boundary = Boundary.Cloud,
                Endpoint = null, Enabled = true, SecretRef = "Secrets:openai", TimeoutSeconds = 30,
                Models =
                [
                    new ModelConfig
                    {
                        ModelId = "gpt-x-mini", DisplayName = "GPT-X mini",
                        Price = new PriceConfig { InputPer1M = 0.15m, OutputPer1M = 0.60m, Basis = "provider-list", EffectiveDate = new DateOnly(2026, 9, 15) },
                    },
                    new ModelConfig
                    {
                        ModelId = "gpt-x",
                        Price = new PriceConfig { InputPer1M = 2.50m, OutputPer1M = 10.00m, Basis = "contract" },
                    },
                ],
            },
            new ConnectionConfig
            {
                Id = "anthropic", DisplayName = "Anthropic", Kind = ProviderKind.Anthropic, Boundary = Boundary.Cloud,
                Enabled = true, SecretRef = "Secrets:anthropic",
                Models =
                [
                    new ModelConfig
                    {
                        ModelId = "claude-x", MaxOutputTokens = 8192,
                        Price = new PriceConfig { InputPer1M = 3.0m, OutputPer1M = 15.0m, Basis = "provider-list" },
                    },
                ],
            },
        ],
        Applications =
        [
            new ApplicationConfig
            {
                Id = "demo-app", Team = "payments", Enabled = true, ConsoleTesting = true,
                GrantedProfiles = ["public-tickets", "internal", "customer-pii", "clinical"], DefaultProfile = "internal", MaxOutputTokens = 512,
            },
            new ApplicationConfig
            {
                Id = "prod-app", Team = "risk", Enabled = false, ConsoleTesting = false, GrantedProfiles = ["internal"],
            },
        ],
        Profiles =
        [
            new WorkloadProfileConfig { Id = "public-tickets", Name = "Public tickets", Baseline = DataClassification.Public, Description = "Synthetic public text" },
            new WorkloadProfileConfig { Id = "internal", Name = "Internal", Baseline = DataClassification.Internal },
            new WorkloadProfileConfig { Id = "customer-pii", Name = "Customer PII", Baseline = DataClassification.Pii },
            new WorkloadProfileConfig { Id = "clinical", Name = "Clinical", Baseline = DataClassification.Phi },
        ],
        Policies =
        [
            new PolicyConfigV2
            {
                Id = "public", Name = "Public: economical cloud, local fallback", Enabled = true,
                Classifications = [DataClassification.Public],
                Profiles = ["public-tickets"],
                // Order is precedence: primary first, then approved fallbacks.
                Targets =
                [
                    new PolicyTarget { ConnectionId = "openai", ModelId = "gpt-x-mini" },
                    new PolicyTarget { ConnectionId = "anthropic", ModelId = "claude-x", Paused = true },
                    new PolicyTarget { ConnectionId = "ollama", ModelId = "qwen3:8b" },
                ],
                Strategy = RoutingStrategy.Fixed,
                Retry = new RetrySettings { MaxAttempts = 3, DeadlineMs = 45_000, On = ["timeout", "5xx"] },
                Explanation = "Public text may use the approved cloud model.",
            },
            new PolicyConfigV2
            {
                Id = "internal", Name = "Internal: length heuristic", Enabled = true, Paused = true,
                Classifications = [DataClassification.Internal],
                Targets =
                [
                    new PolicyTarget { ConnectionId = "ollama", ModelId = "qwen3:8b" },
                    new PolicyTarget { ConnectionId = "openai", ModelId = "gpt-x" },
                ],
                Strategy = RoutingStrategy.LengthHeuristic,
                ThresholdTokens = 750,
            },
            new PolicyConfigV2
            {
                Id = "sensitive", Name = "PII/PHI: local only", Enabled = false,
                Classifications = [DataClassification.Pii, DataClassification.Phi],
                Targets = [new PolicyTarget { ConnectionId = "ollama", ModelId = "qwen3:8b" }, new PolicyTarget { ConnectionId = "vllm", ModelId = "llama-3.1-8b" }],
                Retry = new RetrySettings { MaxAttempts = 1, DeadlineMs = 10_000, On = ["connect"] },
            },
        ],
        Budgets =
        [
            new BudgetConfigV2 { Id = "global", Scope = "global", CapUsd = 250.00m, Period = BudgetPeriod.Monthly, WarnAt = [0.5m, 0.9m], Enforcement = BudgetEnforcement.Hard },
            new BudgetConfigV2 { Id = "team-risk", Scope = "team:risk", CapUsd = 12.5m, Period = BudgetPeriod.Daily, Enforcement = BudgetEnforcement.WarnOnly },
            new BudgetConfigV2 { Id = "app-demo", Scope = "application:demo-app", CapUsd = 1m, Period = BudgetPeriod.None, WarnAt = [], Enforcement = BudgetEnforcement.Hard },
        ],
        Settings = new OperationalSettings
        {
            CacheTtlSeconds = 300, CacheMaxEntries = 2_000, PromptPreviewChars = 80, TraceRetentionMinutes = 15,
            RateLimitGlobalPerMinute = 600, RateLimitPerTeamPerMinute = 60,
        },
        SavingsBaseline = new ModelRef("openai", "gpt-x"),
    };

    private static string HostGetBody(CovenantConfig cfg) => JsonSerializer.Serialize(new ConfigGetResponse
    {
        Version = cfg.Version,
        Etag = "\"v" + cfg.Version + "\"",
        Config = cfg,
        Secrets = new Dictionary<string, SecretStatus>
        {
            ["Secrets:openai"] = new() { Available = true },
            ["Secrets:anthropic"] = new() { Available = false },
            ["Secrets:vllm"] = new() { Available = false },
        },
        AllowList = ["api.openai.com:443", "ollama-host:11434"],
    }, ConfigJsonContext.Default.ConfigGetResponse);

    /// <summary>GET body → console envelope → console PUT body → Host PUT request.</summary>
    private static (string GetBody, UiModels.ConfigEnvelopeDto Envelope, string PutBody, ConfigPutRequest Put) RoundTrip(CovenantConfig cfg)
    {
        var getBody = HostGetBody(cfg);
        var envelope = JsonSerializer.Deserialize(getBody, UiModels.UiJsonContext.Default.ConfigEnvelopeDto);
        Assert.NotNull(envelope);
        Assert.NotNull(envelope!.Config);

        var putBody = JsonSerializer.Serialize(
            new UiModels.ConfigPutRequestDto { Config = envelope.Config, ChangeSummary = "saved by the console" },
            UiModels.UiJsonContext.Default.ConfigPutRequestDto);
        var put = JsonSerializer.Deserialize(putBody, ConfigJsonContext.Default.ConfigPutRequest);
        Assert.NotNull(put);
        Assert.NotNull(put!.Config);
        return (getBody, envelope, putBody, put);
    }

    [Theory]
    [InlineData(ProviderMode.LocalOnly, "local-only")]
    [InlineData(ProviderMode.Hybrid, "hybrid")]
    [InlineData(ProviderMode.LegacyCloud, "legacy-cloud")]
    public void Console_saved_config_round_trips_through_the_host_unchanged(ProviderMode mode, string wireMode)
    {
        var original = Representative(mode);
        var (_, envelope, putBody, put) = RoundTrip(original);

        // The console saw the wire spelling verbatim.
        Assert.Equal(wireMode, envelope.Config!.Mode);
        Assert.Equal(wireMode, Wire(putBody).GetProperty("config").GetProperty("mode").GetString());
        Assert.Equal("saved by the console", put.ChangeSummary);

        // Deep equality: the Host's own serialization of what came back is byte-identical to what went out.
        // (Records with IReadOnlyList members compare lists by reference, so compare canonical JSON instead.)
        var before = JsonSerializer.Serialize(original, ConfigJsonContext.Default.CovenantConfig);
        var after = JsonSerializer.Serialize(put.Config!, ConfigJsonContext.Default.CovenantConfig);
        Assert.Equal(before, after);

        AssertMeaningfulFieldsEqual(original, put.Config!);
    }

    private static void AssertMeaningfulFieldsEqual(CovenantConfig a, CovenantConfig b)
    {
        Assert.Equal(a.Version, b.Version);
        Assert.Equal(a.BaseVersion, b.BaseVersion);
        Assert.Equal(a.Origin, b.Origin);
        Assert.Equal(a.CreatedUtc, b.CreatedUtc);
        Assert.Equal(a.ChangeSummary, b.ChangeSummary);
        Assert.Equal(a.Mode, b.Mode);
        Assert.Equal(a.SavingsBaseline, b.SavingsBaseline);

        Assert.Equal(a.Connections.Select(c => c.Id), b.Connections.Select(c => c.Id));
        foreach (var (x, y) in a.Connections.Zip(b.Connections))
        {
            Assert.Equal(x.Kind, y.Kind);
            Assert.Equal(x.Boundary, y.Boundary);
            Assert.Equal(x.DisplayName, y.DisplayName);
            Assert.Equal(x.Endpoint, y.Endpoint);
            Assert.Equal(x.Enabled, y.Enabled);
            Assert.Equal(x.SecretRef, y.SecretRef);
            Assert.Equal(x.TimeoutSeconds, y.TimeoutSeconds);
            Assert.Equal(x.Models.Select(m => m.ModelId), y.Models.Select(m => m.ModelId));
            foreach (var (mx, my) in x.Models.Zip(y.Models))
            {
                Assert.Equal(mx.DisplayName, my.DisplayName);
                Assert.Equal(mx.ContextTokens, my.ContextTokens);
                Assert.Equal(mx.MaxOutputTokens, my.MaxOutputTokens);
                Assert.Equal(mx.Price, my.Price); // PriceConfig has only scalar members: record equality is deep
            }
        }

        Assert.Equal(a.Applications.Count, b.Applications.Count);
        foreach (var (x, y) in a.Applications.Zip(b.Applications))
        {
            Assert.Equal(x.Id, y.Id);
            Assert.Equal(x.Team, y.Team);
            Assert.Equal(x.Enabled, y.Enabled);
            Assert.Equal(x.ConsoleTesting, y.ConsoleTesting);
            Assert.Equal(x.GrantedProfiles, y.GrantedProfiles);
            Assert.Equal(x.DefaultProfile, y.DefaultProfile);
            Assert.Equal(x.MaxOutputTokens, y.MaxOutputTokens);
        }

        Assert.Equal(a.Profiles, b.Profiles); // scalar-only records: deep

        Assert.Equal(a.Policies.Count, b.Policies.Count);
        foreach (var (x, y) in a.Policies.Zip(b.Policies))
        {
            Assert.Equal(x.Id, y.Id);
            Assert.Equal(x.Name, y.Name);
            Assert.Equal(x.Enabled, y.Enabled);
            Assert.Equal(x.Paused, y.Paused);
            Assert.Equal(x.Classifications, y.Classifications);
            Assert.Equal(x.Profiles, y.Profiles);
            Assert.Equal(x.Targets, y.Targets); // order, connection, model and paused flag
            Assert.Equal(x.Strategy, y.Strategy);
            Assert.Equal(x.ThresholdTokens, y.ThresholdTokens);
            Assert.Equal(x.Retry.MaxAttempts, y.Retry.MaxAttempts);
            Assert.Equal(x.Retry.DeadlineMs, y.Retry.DeadlineMs);
            Assert.Equal(x.Retry.On, y.Retry.On);
            Assert.Equal(x.Explanation, y.Explanation);
        }

        Assert.Equal(a.Budgets.Count, b.Budgets.Count);
        foreach (var (x, y) in a.Budgets.Zip(b.Budgets))
        {
            Assert.Equal(x.Id, y.Id);
            Assert.Equal(x.Scope, y.Scope);
            Assert.Equal(x.CapUsd, y.CapUsd);
            Assert.Equal(x.Period, y.Period);
            Assert.Equal(x.WarnAt, y.WarnAt);
            Assert.Equal(x.Enforcement, y.Enforcement);
        }

        Assert.Equal(a.Settings, b.Settings);
    }

    [Fact]
    public void Host_wire_spellings_are_the_contract_vocabulary_and_the_console_reads_them_verbatim()
    {
        var (getBody, envelope, putBody, _) = RoundTrip(Representative(ProviderMode.Hybrid));

        foreach (var body in new[] { getBody, putBody })
        {
            var cfg = Wire(body).GetProperty("config");
            Assert.Equal("hybrid", cfg.GetProperty("mode").GetString());

            var conns = cfg.GetProperty("connections").EnumerateArray().ToList();
            Assert.Equal(new[] { "Ollama", "OpenAiCompatible", "OpenAi", "Anthropic" }, conns.Select(c => c.GetProperty("kind").GetString()));
            Assert.Equal(new[] { "Local", "Local", "Cloud", "Cloud" }, conns.Select(c => c.GetProperty("boundary").GetString()));

            var price = conns[2].GetProperty("models")[0].GetProperty("price");
            Assert.Equal(0.15m, price.GetProperty("input_per_1m").GetDecimal());
            Assert.Equal(0.60m, price.GetProperty("output_per_1m").GetDecimal());
            Assert.False(price.TryGetProperty("input_per1_m", out _));

            var policies = cfg.GetProperty("policies").EnumerateArray().ToList();
            Assert.Equal(new[] { "fixed", "length-heuristic", "fixed" }, policies.Select(p => p.GetProperty("strategy").GetString()));
            Assert.Equal(new[] { "Public", "Internal", "Pii", "Phi" },
                policies.SelectMany(p => p.GetProperty("classifications").EnumerateArray().Select(c => c.GetString())));
            Assert.Equal(new[] { "openai/gpt-x-mini", "anthropic/claude-x", "ollama/qwen3:8b" },
                policies[0].GetProperty("targets").EnumerateArray().Select(t => t.GetProperty("connection_id").GetString() + "/" + t.GetProperty("model_id").GetString()));

            Assert.Equal(new[] { "Public", "Internal", "Pii", "Phi" },
                cfg.GetProperty("profiles").EnumerateArray().Select(p => p.GetProperty("baseline").GetString()));

            var budgets = cfg.GetProperty("budgets").EnumerateArray().ToList();
            Assert.Equal(new[] { "monthly", "daily", "none" }, budgets.Select(b => b.GetProperty("period").GetString()));
            Assert.Equal(new[] { "hard", "warn-only", "hard" }, budgets.Select(b => b.GetProperty("enforcement").GetString()));
        }

        // The console DTOs hold the same strings (enums travel as strings; nothing re-spelled client-side).
        var dto = envelope.Config!;
        Assert.Equal(new[] { "Ollama", "OpenAiCompatible", "OpenAi", "Anthropic" }, dto.Connections.Select(x => x.Kind));
        Assert.Equal(new[] { "Local", "Local", "Cloud", "Cloud" }, dto.Connections.Select(x => x.Boundary));
        Assert.Equal(new[] { "fixed", "length-heuristic", "fixed" }, dto.Policies.Select(p => p.Strategy));
        Assert.Equal(new[] { "monthly", "daily", "none" }, dto.Budgets.Select(b => b.Period));
        Assert.Equal(new[] { "hard", "warn-only", "hard" }, dto.Budgets.Select(b => b.Enforcement));
        Assert.Equal(new[] { "Public", "Internal", "Pii", "Phi" }, dto.Profiles.Select(p => p.Baseline));
        Assert.Equal(new[] { "Pii", "Phi" }, dto.Policies[2].Classifications);
        Assert.Equal(0.15m, dto.Connections[2].Models[0].Price!.InputPer1M);
        Assert.Equal(0.60m, dto.Connections[2].Models[0].Price!.OutputPer1M);
        Assert.Equal("2026-09-15", dto.Connections[2].Models[0].Price!.EffectiveDate);
        Assert.Null(dto.Connections[1].Models[0].Price); // unknown price is not $0
        Assert.Equal(new[] { false, true, false }, dto.Policies[0].Targets.Select(t => t.Paused));
        Assert.Equal(new[] { false, true, false }, dto.Policies.Select(p => p.Paused));
        Assert.Equal(3, dto.Policies[0].Retry!.MaxAttempts);
        Assert.Equal(45_000, dto.Policies[0].Retry!.DeadlineMs);
        Assert.Equal(new[] { "timeout", "5xx" }, dto.Policies[0].Retry!.On);

        // Envelope metadata survives too.
        Assert.Equal(7, envelope.Version);
        Assert.Equal("\"v7\"", envelope.Etag);
        Assert.True(envelope.Secrets!["Secrets:openai"].Available);
        Assert.False(envelope.Secrets!["Secrets:anthropic"].Available);
        Assert.Equal(new[] { "api.openai.com:443", "ollama-host:11434" }, envelope.AllowList);
    }

    [Fact]
    public void Console_put_body_never_carries_an_allow_sensitive_cloud_switch()
    {
        // PII/PHI never route to cloud destinations: there is no console-side override to send.
        var (_, _, putBody, _) = RoundTrip(Representative(ProviderMode.Hybrid));
        foreach (var p in Wire(putBody).GetProperty("config").GetProperty("policies").EnumerateArray())
            Assert.False(p.TryGetProperty("allow_sensitive_cloud", out _));
    }

    private static JsonElement Wire(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}

using Covenant.Core;
using Covenant.Governance;
using Covenant.Host;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Covenant.Tests;

/// <summary>docs/migration-v2.md: each legacy startup-key shape imports into a v1 configuration that routes
/// EXACTLY as the pre-v2 composition did (the main proof is that every pre-existing Host test still passes on the
/// imported configuration; this suite compares the two engines decision by decision).</summary>
public sealed class LegacyImportTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] pairs)
        => new ConfigurationBuilder().AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value))).Build();

    /// <summary>Verbatim pre-v2 Program.cs policy construction (the reference behaviour).</summary>
    private static (PolicyConfig Policy, long Threshold) LegacyPolicy(IConfiguration cfg)
    {
        bool localOnly = StartupValidation.ProviderMode(cfg) == StartupValidation.LocalOnlyMode;
        string publicModelId = cfg["OpenAI:ModelId"] ?? "gpt-4o-mini";
        string strongModelId = cfg["OpenAI:StrongModelId"] ?? "gpt-4o";
        string localModelId = StartupValidation.Trimmed(cfg["Local:ModelId"]) ?? "local-model-not-configured";
        string anthropicModelId = cfg["Anthropic:ModelId"] ?? "claude-haiku-4-5";
        bool anthropicEnabled = !localOnly && cfg["Anthropic:ApiKey"] is { Length: > 0 };
        List<RouteTarget> general = localOnly ? [new("local", localModelId)] : [new("openai", publicModelId)];
        if (!localOnly && anthropicEnabled) general.Add(new("anthropic", anthropicModelId));
        if (!localOnly) general.Add(new("openai", strongModelId));
        var policy = new PolicyConfig
        {
            AllowedRoutes = new Dictionary<DataClassification, IReadOnlyList<RouteTarget>>
            {
                [DataClassification.Public] = general,
                [DataClassification.Internal] = general,
                [DataClassification.Pii] = [new("local", localModelId)],
                [DataClassification.Phi] = [new("local", localModelId)],
            },
        };
        long threshold = long.TryParse(cfg["Routing:ComplexityTokenThreshold"], out var t) ? t : 400;
        return (policy, threshold);
    }

    public static TheoryData<string> Shapes => new() { "cloud", "cloud+anthropic", "cloud+local", "cloud+custom", "local-only" };

    private static IConfiguration Shape(string shape) => shape switch
    {
        "cloud" => Config(("OpenAI:ApiKey", "sk"), ("Budget:GlobalCapUsd", "5")),
        "cloud+anthropic" => Config(("OpenAI:ApiKey", "sk"), ("Anthropic:ApiKey", "ak"), ("Budget:GlobalCapUsd", "5")),
        "cloud+local" => Config(("OpenAI:ApiKey", "sk"), ("Local:Endpoint", "http://localhost:11434/v1"), ("Local:ModelId", "qwen3:8b"), ("Budget:GlobalCapUsd", "5")),
        "cloud+custom" => Config(("OpenAI:ApiKey", "sk"), ("OpenAI:ModelId", "cheap-1"), ("OpenAI:StrongModelId", "strong-1"),
            ("Routing:ComplexityTokenThreshold", "25"), ("Pricing:cheap-1:InPer1M", "0.1"), ("Pricing:cheap-1:OutPer1M", "0.2"),
            ("Pricing:strong-1:InPer1M", "3"), ("Pricing:strong-1:OutPer1M", "9"), ("Budget:GlobalCapUsd", "5")),
        "local-only" => Config(("Providers:Mode", "local-only"), ("Local:Endpoint", "http://localhost:11434/v1"), ("Local:ModelId", "qwen3:8b"), ("Budget:GlobalCapUsd", "5")),
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    private static CovenantConfig Import(IConfiguration cfg)
    {
        var errors = new List<string>();
        var v1 = LegacyImport.Build(cfg, DateTimeOffset.UtcNow, errors);
        Assert.Empty(errors);
        return v1;
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Imported_configuration_routes_exactly_like_the_pre_v2_policy(string shape)
    {
        var cfg = Shape(shape);
        var (legacyPolicy, threshold) = LegacyPolicy(cfg);
        var legacy = new PolicyEngine(legacyPolicy, new RoutingOptions { ComplexityTokenThreshold = threshold });
        var snapshot = new RuntimeSnapshot(Import(cfg));

        var prompts = new[] { "hi", new string('x', 99), new string('x', 101), new string('x', 1601), new string('x', 1605) };
        var models = new string?[] { null, "gpt-4o-mini", "GPT-4O", "gpt-4o", "claude-haiku-4-5", "qwen3:8b", "cheap-1", "strong-1",
            "local-model-not-configured", "not-a-model" };
        int compared = 0;
        foreach (var cls in Enum.GetValues<DataClassification>())
            foreach (var prompt in prompts)
                foreach (var model in models)
                {
                    var ctx = new InferenceContext(new InferenceRequest("p", [new ChatMessage(ChatRole.User, prompt)], model, AttributionTags.Unattributed))
                    { Classification = cls };
                    var before = legacy.Evaluate(ctx);
                    var after = PolicyEngineV2.Evaluate(snapshot, cls, PolicyEngineV2.LegacyInternalProfile, model, ctx.Request.Messages);
                    var label = $"{shape} {cls} len={prompt.Length} model={model ?? "(none)"}";
                    if (before.Effect != after.Effect) throw new Xunit.Sdk.XunitException($"{label}: effect {before.Effect} → {after.Effect} ({after.Reason})");
                    if (before.Route != after.Route) throw new Xunit.Sdk.XunitException($"{label}: route {before.Route} → {after.Route}");
                    if (before.Effect == PolicyEffect.Deny) Assert.Equal(before.Reason, after.Reason);
                    compared++;
                }
        Assert.Equal(4 * prompts.Length * models.Length, compared);
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Imported_configuration_validates_against_the_startup_environment(string shape)
    {
        var cfg = Shape(shape);
        var v1 = Import(cfg);
        var result = ConfigValidator.Validate(v1, new ValidationEnvironment
        {
            EgressAllowList = StartupEgress.BuildAllowList(cfg),
            SecretAvailable = StartupEgress.SecretAvailability(cfg),
            LegacyCompat = v1.Mode == ProviderMode.LegacyCloud,
        });
        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => $"{e.Path} {e.Code} {e.Message}")));
    }

    [Fact]
    public void Prices_match_the_pre_v2_price_map_per_connection_model()
    {
        var snap = new RuntimeSnapshot(Import(Shape("cloud+anthropic")));
        Assert.True(ConfigPrices.TryGet(snap, "openai", "gpt-4o-mini", out var mini));
        Assert.Equal((0.00015m, 0.0006m), mini);
        Assert.True(ConfigPrices.TryGet(snap, "openai", "gpt-4o", out var strong));
        Assert.Equal((0.0025m, 0.01m), strong);
        Assert.True(ConfigPrices.TryGet(snap, "anthropic", "claude-haiku-4-5", out var haiku));
        Assert.Equal((0.001m, 0.005m), haiku);

        var custom = new RuntimeSnapshot(Import(Shape("cloud+custom")));
        Assert.True(ConfigPrices.TryGet(custom, "openai", "strong-1", out var s1));
        Assert.Equal((3m / 1000m, 9m / 1000m), s1);

        var local = new RuntimeSnapshot(Import(Shape("local-only")));
        Assert.True(ConfigPrices.TryGet(local, "local", "qwen3:8b", out var zero));
        Assert.Equal((0m, 0m), zero);
    }

    [Fact]
    public void Modes_connections_and_the_unconfigured_local_placeholder_are_explicit()
    {
        var cloud = Import(Shape("cloud"));
        Assert.Equal(ProviderMode.LegacyCloud, cloud.Mode);
        Assert.Equal("legacy-import", cloud.Origin);
        Assert.Equal(new[] { "openai" }, cloud.Connections.Select(c => c.Id).ToArray());
        var sensitive = cloud.Policies.Single(p => p.Id == "legacy-sensitive");
        Assert.Equal(new[] { DataClassification.Pii, DataClassification.Phi }, sensitive.Classifications);
        Assert.Equal("local", sensitive.Targets.Single().ConnectionId);
        Assert.Equal(LegacyImport.UnconfiguredLocalModel, sensitive.Targets.Single().ModelId);
        Assert.Equal(new ModelRef("openai", "gpt-4o"), cloud.SavingsBaseline);

        var localOnly = Import(Shape("local-only"));
        Assert.Equal(ProviderMode.LocalOnly, localOnly.Mode);
        var conn = Assert.Single(localOnly.Connections);
        Assert.Equal("local", conn.Id);
        Assert.Equal(Boundary.Local, conn.Boundary);
        Assert.Null(localOnly.SavingsBaseline);
        Assert.Equal("local-only", StartupEgress.ModeString(localOnly.Mode));
        Assert.Equal("legacy-cloud", StartupEgress.ModeString(cloud.Mode));
    }

    [Fact]
    public void Identities_budgets_and_operational_settings_are_imported()
    {
        var cfg = Config(("OpenAI:ApiKey", "sk"), ("Budget:GlobalCapUsd", "7.5"), ("Budget:TeamCapsUsd:payments", "2"),
            ("Auth:Keys:0:Key", "k0"), ("Auth:Keys:0:Principal", "svc-a"), ("Auth:Keys:0:Team", "payments"),
            ("Auth:Keys:1:Key", "k1"), ("Auth:Keys:1:Principal", "svc-b"), ("Auth:Keys:1:Team", "risk"), ("Auth:Keys:1:Application", "risk-app"),
            ("Auth:Keys:2:Key", "k2"), ("Auth:Keys:2:Principal", "alice@example.com"), ("Auth:Keys:2:Team", "ops"),   // not a valid id → unbound
            ("Cache:TtlSeconds", "30"), ("RateLimit:PerTeamPerMinute", "9"), ("Audit:PromptPreviewChars", "40"));
        var v1 = Import(cfg);

        Assert.Equal(new[] { "svc-a", "risk-app" }, v1.Applications.Select(a => a.Id).ToArray());
        Assert.All(v1.Applications, a =>
        {
            Assert.False(a.ConsoleTesting);
            Assert.Equal(PolicyEngineV2.LegacyInternalProfile, a.DefaultProfile);
        });
        var global = v1.Budgets.Single(b => b.Scope == "global");
        Assert.Equal(7.5m, global.CapUsd);
        Assert.Equal(BudgetPeriod.None, global.Period);
        Assert.Equal(2m, v1.Budgets.Single(b => b.Scope == "team:payments").CapUsd);
        Assert.Equal(30, v1.Settings.CacheTtlSeconds);
        Assert.Equal(9, v1.Settings.RateLimitPerTeamPerMinute);
        Assert.Equal(40, v1.Settings.PromptPreviewChars);
    }

    [Fact]
    public void Invalid_legacy_values_are_reported_not_ignored()
    {
        var errors = new List<string>();
        LegacyImport.Build(Config(("OpenAI:ApiKey", "sk"), ("Budget:GlobalCapUsd", "lots"), ("Pricing:gpt-4o:InPer1M", "x"),
            ("Cache:TtlSeconds", "-1")), DateTimeOffset.UtcNow, errors);
        Assert.Contains(errors, e => e.StartsWith("Budget:GlobalCapUsd", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("Pricing:gpt-4o", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("Cache:TtlSeconds", StringComparison.Ordinal));
    }

    [Fact]
    public void Demo_seeds_use_the_restrictive_defaults()
    {
        var cfg = Config(("OpenAI:ApiKey", "sk"), ("Local:Endpoint", "http://localhost:11434/v1"), ("Local:ModelId", "qwen3:8b"), ("Budget:GlobalCapUsd", "3"));
        var errors = new List<string>();
        var hybrid = DemoSeeds.Build(DemoSeeds.Hybrid, cfg, DateTimeOffset.UtcNow, errors);
        var local = DemoSeeds.Build(DemoSeeds.LocalOnly, cfg, DateTimeOffset.UtcNow, errors);
        Assert.Empty(errors);
        var env = new ValidationEnvironment { EgressAllowList = StartupEgress.BuildAllowList(cfg), SecretAvailable = StartupEgress.SecretAvailability(cfg) };
        Assert.True(ConfigValidator.Validate(hybrid, env).IsValid);
        Assert.True(ConfigValidator.Validate(local, env).IsValid);

        var h = new RuntimeSnapshot(hybrid);
        Assert.Equal("openai/gpt-4o-mini (fallback: ollama/qwen3:8b)", PolicyEngineV2.Summary(h, DataClassification.Public, "public-demo"));
        foreach (var cls in new[] { DataClassification.Internal, DataClassification.Pii, DataClassification.Phi })
            Assert.Equal("ollama/qwen3:8b", PolicyEngineV2.Summary(h, cls, "public-demo"));
        Assert.Equal("ollama/qwen3:8b", PolicyEngineV2.Summary(new RuntimeSnapshot(local), DataClassification.Public, "public-demo"));
        Assert.Equal(BudgetPeriod.Monthly, hybrid.Budgets.Single().Period);
        Assert.Equal(3m, hybrid.Budgets.Single().CapUsd);
    }
}

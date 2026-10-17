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
//
// Review round 2 (U1): ONE explicit contract. Every admin DTO the console consumes is pinned Host → UI here: the Host
// object is serialized with the SAME context its endpoint uses (CovenantJsonContext: status, catalog, probe, evidence,
// errors, budgets; ConfigJsonContext: config, validate, versions, rollback, applications, simulate; AuditJsonContext:
// requests page, request, segments, export; TraceJsonContext: trace snapshot/event, playground submission) and read with
// UiJsonContext, then asserted field by field. The console has no alternative-shape fallback parser left.
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

    // ---- T08 editors: every other DTO the console reads or writes on the config/simulate/catalog/archive paths ----

    [Fact]
    public void Validate_request_and_response_round_trip_with_impact_and_field_paths()
    {
        var cfg = Representative(ProviderMode.Hybrid);
        var envelope = RoundTrip(cfg).Envelope;

        // console → Host: POST /admin/config/validate
        var reqBody = JsonSerializer.Serialize(new UiModels.ConfigValidateRequestDto { BaseVersion = 7, Config = envelope.Config },
            UiModels.UiJsonContext.Default.ConfigValidateRequestDto);
        var hostReq = JsonSerializer.Deserialize(reqBody, ConfigJsonContext.Default.ConfigValidateRequest);
        Assert.Equal(7, hostReq!.BaseVersion);
        Assert.Equal(JsonSerializer.Serialize(cfg, ConfigJsonContext.Default.CovenantConfig),
            JsonSerializer.Serialize(hostReq.Config!, ConfigJsonContext.Default.CovenantConfig));

        // Host → console: the validation result
        var respBody = JsonSerializer.Serialize(new ConfigValidateResponse
        {
            Valid = false,
            ActiveVersion = 7,
            Errors = [new ConfigIssueView { Path = "connections[2].secret_ref", Code = "secret_unavailable", Message = "secret not set" }],
            Warnings = [new ConfigIssueView { Path = "base_version", Code = "stale_base_version", Message = "edited from v6" }],
            Impact = new ImpactView
            {
                Classifications = [new ImpactRowView { Classification = "Public", Profile = "public-tickets", Before = "openai/gpt-x-mini", After = "refused: no eligible target", Changed = true }],
                ApplicationsAffected = ["demo-app"],
                UnavailableDestinations = ["openai/gpt-x-mini"],
                PricingGaps = ["vllm/llama-3.1-8b"],
                NewlyRefused = ["Public/public-tickets"],
            },
        }, ConfigJsonContext.Default.ConfigValidateResponse);
        var ui = JsonSerializer.Deserialize(respBody, UiModels.UiJsonContext.Default.ConfigValidationResultDto)!;
        Assert.False(ui.Valid);
        Assert.Equal(7, ui.ActiveVersion);
        Assert.Equal("connections[2].secret_ref", Assert.Single(ui.Errors).Path);
        Assert.Equal("secret_unavailable", ui.Errors[0].Code);
        Assert.Equal("stale_base_version", Assert.Single(ui.Warnings).Code);
        var row = Assert.Single(ui.Impact!.Classifications);
        Assert.True(row.Changed);
        Assert.Equal("refused: no eligible target", row.After);
        Assert.Equal(new[] { "demo-app" }, ui.Impact.ApplicationsAffected);
        Assert.Equal(new[] { "openai/gpt-x-mini" }, ui.Impact.UnavailableDestinations);
        Assert.Equal(new[] { "vllm/llama-3.1-8b" }, ui.Impact.PricingGaps);
        Assert.Equal(new[] { "Public/public-tickets" }, ui.Impact.NewlyRefused);
        Assert.Single(Covenant.Ui.Logic.ImpactText.ChangedRoutes(ui.Impact));
    }

    [Fact]
    public void Host_write_answers_map_onto_console_outcomes()
    {
        string Error(int? active, bool withIssues) => JsonSerializer.Serialize(new ConfigErrorResponse
        {
            Error = withIssues ? "invalid_configuration" : "stale",
            Reason = "x",
            ActiveVersion = active,
            Errors = withIssues ? [new ConfigIssueView { Path = "policies[0].targets[1]", Code = "sensitive_cloud_target", Message = "no" }] : null,
            Warnings = withIssues ? [] : null,
        }, ConfigJsonContext.Default.ConfigErrorResponse);

        var stale = Covenant.Ui.Logic.EditOutcomes.FromResponse(412, Error(8, withIssues: false));
        Assert.Equal(Covenant.Ui.Logic.EditOutcomeKind.Stale, stale.Kind);
        Assert.Equal(8, stale.Version);

        var invalid = Covenant.Ui.Logic.EditOutcomes.FromResponse(422, Error(7, withIssues: true));
        Assert.Equal(Covenant.Ui.Logic.EditOutcomeKind.Invalid, invalid.Kind);
        Assert.Equal("policies[0].targets[1]", Assert.Single(invalid.Errors!).Path);

        var ok = Covenant.Ui.Logic.EditOutcomes.FromResponse(200,
            JsonSerializer.Serialize(new ConfigPutResponse { Version = 9, Etag = "\"v9\"" }, ConfigJsonContext.Default.ConfigPutResponse));
        Assert.True(ok.Succeeded);
        Assert.Equal(9, ok.Version);

        var err = JsonSerializer.Deserialize(Error(8, withIssues: false), UiModels.UiJsonContext.Default.ConfigErrorResponseDto)!;
        Assert.Equal("stale", err.Error);
        Assert.Equal(8, err.ActiveVersion);
    }

    [Fact]
    public void Version_history_and_rollback_request_round_trip()
    {
        var history = JsonSerializer.Serialize(new List<ConfigVersionSummary>
        {
            new() { Version = 8, CreatedUtc = Created, Origin = "rollback", BaseVersion = 7, RolledBackFrom = 3, ChangeSummary = "rollback to v3", Active = true },
            new() { Version = 7, CreatedUtc = Created.AddMinutes(-5), Origin = "console", BaseVersion = 6, Active = false },
        }, ConfigJsonContext.Default.ListConfigVersionSummary);
        var ui = JsonSerializer.Deserialize(history, UiModels.UiJsonContext.Default.ListConfigVersionDto)!;
        Assert.Equal(new[] { 8, 7 }, ui.Select(v => v.Version));
        Assert.True(ui[0].Active);
        Assert.Equal(3, ui[0].RolledBackFrom);
        Assert.Equal("rollback", ui[0].Origin);
        Assert.Equal(Created, ui[0].CreatedUtc);
        Assert.Null(ui[1].RolledBackFrom);

        var body = JsonSerializer.Serialize(new UiModels.ConfigRollbackRequestDto { TargetVersion = 3, ExpectedActiveVersion = 8 },
            UiModels.UiJsonContext.Default.ConfigRollbackRequestDto);
        var host = JsonSerializer.Deserialize(body, ConfigJsonContext.Default.ConfigRollbackRequest)!;
        Assert.Equal(3, host.TargetVersion);
        Assert.Equal(8, host.ExpectedActiveVersion);
    }

    [Fact]
    public void Simulator_request_with_a_draft_candidate_and_dry_run_response_round_trip()
    {
        var cfg = Representative(ProviderMode.Hybrid);
        var envelope = RoundTrip(cfg).Envelope;
        var body = JsonSerializer.Serialize(new UiModels.SimulateRequestDto
        {
            ApplicationId = "demo-app", ProfileId = "public-tickets", Prompt = "Priority: low", Model = "openai/gpt-x-mini", Config = envelope.Config,
        }, UiModels.UiJsonContext.Default.SimulateRequestDto);
        var host = JsonSerializer.Deserialize(body, ConfigJsonContext.Default.SimulateRequest)!;
        Assert.Equal("demo-app", host.ApplicationId);
        Assert.Equal("public-tickets", host.ProfileId);
        Assert.Equal("openai/gpt-x-mini", host.Model);
        Assert.Equal(JsonSerializer.Serialize(cfg, ConfigJsonContext.Default.CovenantConfig),
            JsonSerializer.Serialize(host.Config!, ConfigJsonContext.Default.CovenantConfig));

        // Without a draft the candidate is omitted (the Host simulates the active version).
        var noDraft = JsonSerializer.Serialize(new UiModels.SimulateRequestDto { ApplicationId = "demo-app", Prompt = "x" }, UiModels.UiJsonContext.Default.SimulateRequestDto);
        Assert.False(Wire(noDraft).TryGetProperty("config", out _));
        Assert.Null(JsonSerializer.Deserialize(noDraft, ConfigJsonContext.Default.SimulateRequest)!.Config);

        var resp = JsonSerializer.Serialize(new SimulateResponse
        {
            DryRun = true, ConfigVersion = 7, ApplicationId = "demo-app", Profile = "public-tickets",
            Classification = "Pii", Baseline = "Public", DetectedSignal = "US SSN pattern", PolicyId = "sensitive",
            Eligible =
            [
                new SimulatedTarget { Target = "ollama/qwen3:8b", Boundary = "Local" },
                new SimulatedTarget { Target = "openai/gpt-x-mini", Boundary = "Cloud", ExcludedReason = "cloud destination for PII" },
            ],
            Decision = "route", Primary = "ollama/qwen3:8b", Fallbacks = [], Reason = "allowed", Explanation = "PII → ollama/qwen3:8b",
        }, ConfigJsonContext.Default.SimulateResponse);
        var ui = JsonSerializer.Deserialize(resp, UiModels.UiJsonContext.Default.SimulateResponseDto)!;
        Assert.True(ui.DryRun);
        Assert.Equal(7, ui.ConfigVersion);
        Assert.Equal("Pii", ui.Classification);
        Assert.Equal("Public", ui.Baseline);
        Assert.Equal("US SSN pattern", ui.DetectedSignal);
        Assert.Equal("route", ui.Decision);
        Assert.Equal("ollama/qwen3:8b", ui.Primary);
        Assert.Equal(new[] { "Local", "Cloud" }, ui.Eligible.Select(e => e.Boundary));
        Assert.Null(ui.Eligible[0].ExcludedReason);
        Assert.Equal("cloud destination for PII", ui.Eligible[1].ExcludedReason);
        Assert.Equal("PII → ollama/qwen3:8b", ui.Explanation);
    }

    [Fact]
    public void Catalog_connections_carry_health_registration_and_allow_listing_to_the_console()
    {
        var ok = new HealthView { State = "ok", LastSuccessUtc = Created };
        var body = JsonSerializer.Serialize(new CatalogReport
        {
            Mode = "hybrid",
            Models = [new CatalogModel { Adapter = "ollama", ModelId = "qwen3:8b", Classifications = ["Internal"], Registered = true, PriceBasis = "zero", Health = ok, ConnectionId = "ollama", Boundary = "Local", Key = "ollama:qwen3:8b" }],
            Local = new LocalConfigView
            {
                Configured = true, Endpoint = "http://ollama-host:11434/v1", ModelId = "qwen3:8b", Registered = true,
                EndpointHealth = new HealthView { State = "failed", LastFailureUtc = Created, LastError = "refused" },
                ConnectionId = "ollama", Health = new HealthView { State = "ok", LastSuccessUtc = Created.AddMinutes(1) },
            },
            ProbeAllowList = ["ollama-host:11434"],
            ConfigVersion = 7,
            EgressAllowList = ["api.openai.com:443", "ollama-host:11434"],
            Connections =
            [
                new CatalogConnection
                {
                    Id = "openai", DisplayName = "OpenAI", Kind = "OpenAi", Boundary = "Cloud", Enabled = true, Endpoint = null, EndpointAllowListed = true,
                    SecretRef = "Secrets:openai", SecretAvailable = false,
                    Models = [new CatalogConnectionModel { Key = "openai:gpt-x-mini", ModelId = "gpt-x-mini", Registered = false, PriceBasis = "configured", InPer1M = 0.15m, OutPer1M = 0.60m, Health = new HealthView { State = "unknown" } }],
                },
            ],
        }, CovenantJsonContext.Default.CatalogReport);

        var ui = JsonSerializer.Deserialize(body, UiModels.UiJsonContext.Default.CatalogResponse)!;
        Assert.Equal(7, ui.ConfigVersion);
        Assert.Equal(new[] { "api.openai.com:443", "ollama-host:11434" }, ui.EgressAllowList);
        Assert.Equal("Local", ui.Models[0].Boundary);
        Assert.Equal("ollama", ui.Models[0].ConnectionId);
        Assert.Equal("ollama:qwen3:8b", ui.Models[0].Key);
        Assert.Equal("hybrid", ui.Mode);
        Assert.Equal(new[] { "ollama-host:11434" }, ui.ProbeAllowList);
        var m0 = Assert.Single(ui.Models);
        Assert.Equal("ollama", m0.Adapter);
        Assert.Equal("qwen3:8b", m0.ModelId);
        Assert.Equal(new[] { "Internal" }, m0.Classifications);
        Assert.True(m0.Registered);
        Assert.Equal("zero", m0.PriceBasis);
        Assert.Equal("ok", m0.Health!.State);
        Assert.Equal(Created, m0.Health!.LastSuccessUtc);
        var local = ui.Local!;
        Assert.True(local.Configured);
        Assert.True(local.Registered);
        Assert.Equal("http://ollama-host:11434/v1", local.Endpoint);
        Assert.Equal("qwen3:8b", local.ModelId);
        Assert.Equal("failed", local.EndpointHealth.State);
        Assert.Equal("refused", local.EndpointHealth.LastError);
        Assert.Equal(Created, local.EndpointHealth.LastFailureUtc);
        // Review round 2 (B11): the CURRENT runtime connection and its health.
        Assert.Equal("ollama", local.ConnectionId);
        Assert.Equal("ok", local.Health!.State);
        Assert.Equal(Created.AddMinutes(1), local.Health!.LastSuccessUtc);
        Assert.Equal(Covenant.Ui.Logic.LocalRouteState.Ready, Covenant.Ui.Logic.LocalHealth.Derive(ui, null).State);
        var c = Assert.Single(ui.Connections!);
        Assert.Equal("openai", c.Id);
        Assert.Equal("OpenAI", c.DisplayName);
        Assert.True(c.Enabled);
        Assert.Null(c.Endpoint);
        Assert.Equal("OpenAi", c.Kind);
        Assert.Equal("Cloud", c.Boundary);
        Assert.True(c.EndpointAllowListed);
        Assert.False(c.SecretAvailable);
        Assert.Equal("Secrets:openai", c.SecretRef);
        var m = Assert.Single(c.Models);
        Assert.Equal("openai:gpt-x-mini", m.Key);
        Assert.Equal("gpt-x-mini", m.ModelId);
        Assert.False(m.Registered);
        Assert.Equal("configured", m.PriceBasis);
        Assert.Equal(0.15m, m.InPer1M);
        Assert.Equal(0.60m, m.OutPer1M);
        Assert.Equal("unknown", m.Health!.State);
    }

    // T04/T06 read paths. These pin the backend's budget-ledger and audit-archive views (implemented concurrently with
    // the console; see docs/console-v2-progress.md "T08 UI" — integration pending until those land and pass).

    [Fact]
    public void Budget_state_and_contributions_reach_the_console()
    {
        var states = JsonSerializer.Serialize(new List<BudgetStateView>
        {
            new()
            {
                Id = "global", Scope = "global", Period = "monthly", PeriodId = "2026-10", PeriodStartUtc = Created, PeriodEndUtc = Created.AddDays(16),
                CapUsd = 25m, SpentUsd = 0.000184m, ReservedUsd = 0.0021m, RemainingUsd = 24.997716m, WarnAt = [0.8m, 0.95m], Warning = "none", Enforcement = "hard",
            },
        }, CovenantJsonContext.Default.ListBudgetStateView);
        var ui = Assert.Single(JsonSerializer.Deserialize(states, UiModels.UiJsonContext.Default.ListBudgetStateDto)!);
        Assert.Equal("global", ui.Id);
        Assert.Equal("global", ui.Scope);
        Assert.Equal("monthly", ui.Period);
        Assert.Equal(25m, ui.CapUsd);
        Assert.Equal(24.997716m, ui.RemainingUsd);
        Assert.Equal("none", ui.Warning);
        Assert.Equal("hard", ui.Enforcement);
        Assert.Equal(Created.AddDays(16), ui.PeriodEndUtc);
        Assert.Equal("2026-10", ui.PeriodId);
        Assert.Equal(0.000184m, ui.SpentUsd);
        Assert.Equal(0.0021m, ui.ReservedUsd);
        Assert.Equal(Created, ui.PeriodStartUtc);
        Assert.Equal(new[] { 0.8m, 0.95m }, ui.WarnAt);
        Assert.Equal("$0.000184", Covenant.Ui.Logic.Money.Usd(ui.SpentUsd));

        var contributions = JsonSerializer.Serialize(new BudgetRequestsView
        {
            BudgetId = "global", Scope = "global", PeriodId = "2026-10",
            Items =
            [
                new BudgetContributionView { RequestId = "req_a", AttemptId = "att_2", Kind = "settle", AmountUsd = 0.000184m, TsUtc = Created, Overrun = true },
                new BudgetContributionView { RequestId = "req_b", AttemptId = "att_1", Kind = "settle-recovered", AmountUsd = 0.0021m, TsUtc = Created },
            ],
        }, CovenantJsonContext.Default.BudgetRequestsView);
        var page = JsonSerializer.Deserialize(contributions, UiModels.UiJsonContext.Default.BudgetRequestsDto)!;
        Assert.Equal("global", page.BudgetId);
        Assert.Equal("global", page.Scope);
        Assert.Equal("2026-10", page.PeriodId);
        var rows = page.Items;
        Assert.Equal(new[] { "req_a", "req_b" }, rows.Select(r => r.RequestId));
        Assert.Equal(0.000184m, rows[0].AmountUsd);
        Assert.Equal("att_2", rows[0].AttemptId);
        Assert.Equal("settle", rows[0].Kind);
        Assert.True(rows[0].Overrun);
        Assert.False(rows[1].Overrun);
        Assert.Equal("settle-recovered", rows[1].Kind);
        Assert.Equal(Created, rows[0].TsUtc);
    }

    [Fact]
    public void Audit_segments_and_cursor_pages_reach_the_console()
    {
        var segs = JsonSerializer.Serialize(new List<SegmentView>
        {
            new() { Id = "current", Label = "Current", Current = true, Entries = 12, ChainValid = true, FirstUtc = Created },
            new() { Id = "a-0123456789abcdef", Label = "Archived 2026-10-01 09:00", Current = false, Entries = 40, Events = 2, ChainValid = true, ArchivedUtc = Created.AddDays(-15) },
        }, AuditJsonContext.Default.ListSegmentView);
        var ui = JsonSerializer.Deserialize(segs, UiModels.UiJsonContext.Default.ListAuditSegmentDto)!;
        Assert.True(Covenant.Ui.Logic.SegmentScope.IsCurrent(ui[0]));
        Assert.False(Covenant.Ui.Logic.SegmentScope.IsCurrent(ui[1]));
        Assert.Equal("current", ui[0].Id);
        Assert.Equal("Current", ui[0].Label);
        Assert.Equal(12, ui[0].Entries);
        Assert.Equal(Created, ui[0].FirstUtc);
        Assert.True(ui[0].ChainValid);
        Assert.Equal("a-0123456789abcdef", ui[1].Id);
        Assert.Equal("Archived 2026-10-01 09:00", ui[1].Label);
        Assert.Equal(40, ui[1].Entries);
        Assert.Equal(2, ui[1].Events);
        Assert.Equal(Created.AddDays(-15), ui[1].ArchivedUtc);

        var cursor = AuditSegments.Cursor("a-0123456789abcdef", "0123456789abcdef", 40);
        var page = JsonSerializer.Serialize(new RequestsPage
        {
            Segment = new SegmentInfo
            {
                Path = "/var/covenant/audit.jsonl", Entries = 80, Events = 3, ChainValid = true, FirstUtc = Created, LastUtc = Created.AddHours(1),
                Id = "a-0123456789abcdef", Label = "Archived 2026-10-01 09:00", IsCurrent = false,
            },
            Items = [],
            NextCursor = cursor,
        }, AuditJsonContext.Default.RequestsPage);
        var uiPage = JsonSerializer.Deserialize(page, UiModels.UiJsonContext.Default.RequestsResponse)!;
        // The segment descriptor — never inferred from a row.
        Assert.Equal("a-0123456789abcdef", uiPage.Segment.Id);
        Assert.Equal("Archived 2026-10-01 09:00", uiPage.Segment.Label);
        Assert.False(uiPage.Segment.IsCurrent);
        Assert.Equal("/var/covenant/audit.jsonl", uiPage.Segment.Path);
        Assert.Equal(80, uiPage.Segment.Entries);
        Assert.Equal(3, uiPage.Segment.Events);
        Assert.True(uiPage.Segment.ChainValid);
        Assert.Equal(Created, uiPage.Segment.FirstUtc);
        Assert.Equal(Created.AddHours(1), uiPage.Segment.LastUtc);
        Assert.Equal(cursor, uiPage.NextCursor);   // opaque: passed back verbatim
        Assert.Equal("Archived segment Archived 2026-10-01 09:00 · read-only", Covenant.Ui.Logic.SegmentScope.Label(uiPage.Segment));

        var paging = new Covenant.Ui.Logic.RequestPaging();
        paging.Reset("a-0123456789abcdef");
        Assert.Equal(Covenant.Ui.Logic.PageResult.Accepted, paging.ApplyPage(uiPage, older: false));

        // is_current is ALWAYS on the wire, even when false.
        Assert.False(Wire(page).GetProperty("segment").GetProperty("is_current").GetBoolean());
        var last = JsonSerializer.Serialize(new RequestsPage
        {
            Segment = new SegmentInfo { Path = "/var/covenant/audit.jsonl", Entries = 0, ChainValid = true, Id = "current", Label = "Current segment", IsCurrent = true },
            Items = [],
            NextCursor = null,
        }, AuditJsonContext.Default.RequestsPage);
        var uiLast = JsonSerializer.Deserialize(last, UiModels.UiJsonContext.Default.RequestsResponse)!;
        Assert.True(uiLast.Segment.IsCurrent);
        Assert.Null(uiLast.NextCursor);
    }

    // ---- Review round 2 (U1): the remaining Host → UI contracts, field by field ----

    private static AuditEntry RoundTwoRecord() => new(
        Id: "req_0123456789abcdef0123456789abcdef",
        TimestampUtc: Created,
        Principal: "console:demo-app",
        Tags: new AttributionTags("payments", "console", "playground"),
        Classification: DataClassification.Pii,
        Effect: PolicyEffect.Allow,
        Reason: "allowed",
        ServedByModel: "ollama/qwen3:8b",
        Usage: new Usage(84, 10, 0.0021m),
        DurationMs: 812.5,
        PromptChars: 120,
        PromptSha256: new string('a', 64),
        Signal: "US SSN pattern",
        PromptPreview: null,
        CacheHit: false,
        SchemaVersion: 2,
        CorrelationId: "req_0123456789abcdef0123456789abcdef",
        ClassificationEvaluated: true,
        DenialKind: "None",
        StoppedAtStage: null,
        SelectedModel: "openai/gpt-x-mini",
        AttemptedModel: "ollama/qwen3:8b",
        Stream: true,
        UsageReported: true,
        CostBasis: "mixed",
        EventType: "request",
        ConfigVersion: 13,
        EntryPoint: "console",
        OperatorRef: "shared-admin-token",
        ApplicationId: "demo-app",
        ProfileId: "public-tickets",
        BaselineClassification: DataClassification.Public,
        Attempts:
        [
            new AuditAttempt("att_1", "openai", "gpt-x-mini", "failed", "incomplete_stream", 1500.25, 0.0021m, "estimated_failed_call",
                ReservedUsd: 0.0021m, Overrun: false, EffectiveMaxOutputTokens: 256, Truncated: null),
            new AuditAttempt("att_2", "ollama", "qwen3:8b", "served", null, 640.5, 0m, "zero_priced",
                ReservedUsd: 0m, Overrun: false, EffectiveMaxOutputTokens: 64, Truncated: true),
        ],
        Trace:
        [
            new AuditTraceStep("auth", "passed", 0.4),
            new AuditTraceStep("provider", "failed", 1500.25, "att_1", "incomplete_stream"),
            new AuditTraceStep("provider", "passed", 640.5, "att_2"),
        ],
        ReservedUsd: 0.0021m,
        SettledUsd: 0.0021m,
        RequestedMaxOutputTokens: 256,
        EffectiveMaxOutputTokens: 64,
        PartialOutputChars: null,
        ServedConnectionId: "ollama");

    private static void AssertRecord(AuditEntry h, UiModels.AuditEntryView u)
    {
        Assert.Equal(h.Id, u.Id);
        Assert.Equal(h.TimestampUtc, u.TimestampUtc);
        Assert.Equal(h.Principal, u.Principal);
        Assert.Equal(h.Tags.Team, u.Tags.Team);
        Assert.Equal(h.Tags.Workflow, u.Tags.Workflow);
        Assert.Equal(h.Tags.UseCase, u.Tags.UseCase);
        Assert.Equal("Pii", u.Classification);
        Assert.Equal("Allow", u.Effect);
        Assert.Equal(h.Reason, u.Reason);
        Assert.Equal(h.ServedByModel, u.ServedByModel);
        Assert.Equal(h.Usage.InputTokens, u.Usage.InputTokens);
        Assert.Equal(h.Usage.OutputTokens, u.Usage.OutputTokens);
        Assert.Equal(h.Usage.TotalTokens, u.Usage.TotalTokens);
        Assert.Equal(h.Usage.CostUsd, u.Usage.CostUsd);
        Assert.Equal(h.DurationMs, u.DurationMs);
        Assert.Equal(h.PromptChars, u.PromptChars);
        Assert.Equal(h.PromptSha256, u.PromptSha256);
        Assert.Equal(h.Signal, u.Signal);
        Assert.Null(u.PromptPreview);
        Assert.Equal(h.CacheHit, u.CacheHit);
        Assert.Equal(h.SchemaVersion, u.SchemaVersion);
        Assert.Equal(h.CorrelationId, u.CorrelationId);
        Assert.Equal(h.ClassificationEvaluated, u.ClassificationEvaluated);
        Assert.Equal(h.DenialKind, u.DenialKind);
        Assert.Equal(h.StoppedAtStage, u.StoppedAtStage);
        Assert.Equal(h.SelectedModel, u.SelectedModel);
        Assert.Equal(h.AttemptedModel, u.AttemptedModel);
        Assert.Equal(h.Stream, u.Stream);
        Assert.Equal(h.UsageReported, u.UsageReported);
        Assert.Equal(h.CostBasis, u.CostBasis);
        Assert.Equal(h.EventType, u.EventType);
        Assert.Equal(h.ConfigVersion, u.ConfigVersion);
        Assert.Equal(h.EntryPoint, u.EntryPoint);
        Assert.Equal(h.OperatorRef, u.OperatorRef);
        Assert.Equal(h.ApplicationId, u.ApplicationId);
        Assert.Equal(h.ProfileId, u.ProfileId);
        Assert.Equal("Public", u.BaselineClassification);
        Assert.Equal(h.ReservedUsd, u.ReservedUsd);
        Assert.Equal(h.SettledUsd, u.SettledUsd);
        Assert.Equal(h.RequestedMaxOutputTokens, u.RequestedMaxOutputTokens);
        Assert.Equal(h.EffectiveMaxOutputTokens, u.EffectiveMaxOutputTokens);
        Assert.Equal(h.PartialOutputChars, u.PartialOutputChars);
        Assert.Equal(h.ServedConnectionId, u.ServedConnectionId);

        Assert.Equal(h.Attempts!.Count, u.Attempts!.Count);
        foreach (var (ha, ua) in h.Attempts!.Zip(u.Attempts!))
        {
            Assert.Equal(ha.AttemptId, ua.AttemptId);
            Assert.Equal(ha.ConnectionId, ua.ConnectionId);
            Assert.Equal(ha.ModelId, ua.ModelId);
            Assert.Equal(ha.Outcome, ua.Outcome);
            Assert.Equal(ha.ErrorClass, ua.ErrorClass);
            Assert.Equal(ha.DurationMs, ua.DurationMs);
            Assert.Equal(ha.CostUsd, ua.CostUsd);
            Assert.Equal(ha.CostBasis, ua.CostBasis);
            Assert.Equal(ha.ReservedUsd, ua.ReservedUsd);
            Assert.Equal(ha.Overrun, ua.Overrun);
            Assert.Equal(ha.EffectiveMaxOutputTokens, ua.EffectiveMaxOutputTokens);
            Assert.Equal(ha.Truncated, ua.Truncated);
        }
        Assert.Equal(h.Trace!.Count, u.Trace!.Count);
        foreach (var (ht, ut) in h.Trace!.Zip(u.Trace!))
        {
            Assert.Equal(ht.Stage, ut.Stage);
            Assert.Equal(ht.State, ut.State);
            Assert.Equal(ht.DurationMs, ut.DurationMs);
            Assert.Equal(ht.AttemptId, ut.AttemptId);
            Assert.Equal(ht.ReasonCode, ut.ReasonCode);
        }
    }

    [Fact]
    public void Audit_record_v3_round_two_fields_reach_the_console_from_the_requests_page_and_the_by_id_lookup()
    {
        var record = RoundTwoRecord();
        var page = JsonSerializer.Serialize(new RequestsPage
        {
            Segment = new SegmentInfo { Path = "/var/covenant/audit.jsonl", Entries = 1, ChainValid = true, Id = "current", Label = "Current segment", IsCurrent = true },
            Items = [record],
            NextCursor = null,
        }, AuditJsonContext.Default.RequestsPage);
        var ui = JsonSerializer.Deserialize(page, UiModels.UiJsonContext.Default.RequestsResponse)!;
        AssertRecord(record, Assert.Single(ui.Items));

        var byId = JsonSerializer.Deserialize(JsonSerializer.Serialize(record, AuditJsonContext.Default.AuditEntry), UiModels.UiJsonContext.Default.AuditEntryView)!;
        AssertRecord(record, byId);

        // What the drawer shows from those fields.
        Assert.Equal("256", Covenant.Ui.Logic.RecordFacts.RequestedMaxOutputTokens(byId));
        Assert.Equal("64", Covenant.Ui.Logic.RecordFacts.EffectiveMaxOutputTokens(byId));
        Assert.True(Covenant.Ui.Logic.RecordFacts.Truncated(byId));
        Assert.True(Covenant.Ui.Logic.RecordFacts.IsIncompleteStream(byId));
        Assert.Equal("ollama", Covenant.Ui.Logic.RecordFacts.ServedConnection(byId));
        Assert.Contains("Mixed (includes estimates)", Covenant.Ui.Logic.CostText.Cost(byId), StringComparison.Ordinal);
        var trace = Covenant.Ui.Logic.TraceModel.FromAuditSummary(byId.Trace, byId.Attempts, byId.ConfigVersion)!;
        var provider = trace.Steps.Single(st => st.Stage == "provider");
        Assert.Equal(2, provider.Attempts.Count);
        Assert.Equal("openai/gpt-x-mini", provider.Attempts[0].Target);
        Assert.Equal("incomplete_stream", provider.Attempts[0].ReasonCode);
    }

    [Fact]
    public void Status_report_with_connection_health_reaches_the_console()
    {
        var recent = new RecentRequest
        {
            Id = "req_1", T = Created, Principal = "console:demo-app", Team = "payments", Workflow = "console", UseCase = "playground",
            Classification = "Pii", Effect = "Allow", Model = "ollama/qwen3:8b", Tokens = 94, TokensIn = 84, TokensOut = 10, CostUsd = 0.0021m,
            Ms = 812.5, Reason = "allowed", PromptChars = 120, PromptSha256 = "abc", Signal = "US SSN pattern", PromptPreview = null, CacheHit = false, CostBasis = "mixed",
        };
        var host = new StatusReport
        {
            GeneratedUtc = Created, StartedUtc = Created.AddHours(-1),
            KillSwitch = new KillSwitchState { Engaged = true, Reason = "demo" },
            Budget = new BudgetStatus { GlobalCapUsd = 25m, GlobalSpendUsd = 0.5m, Teams = [new TeamBudgetStatus { Team = "payments", CapUsd = null, SpendUsd = 0.5m }] },
            Routes = [new RouteView { Classification = "Pii", Adapter = "ollama", Model = "qwen3:8b" }],
            FinOps = new FinOpsSummary
            {
                Requests = 3, Allowed = 2, Denied = 1, TotalCostUsd = 0.0021m, LocalRequests = 1, LocalTokens = 94,
                EstimatedSavingsUsd = 0m, SavingsLocalUsd = 0m, SavingsRouterUsd = 0m, SavingsCacheUsd = 0m, CacheHits = 0,
                UnknownCostRequests = 1, EstimatedCostRequests = 1,
                CostByTeamUsd = new() { ["payments"] = 0.0021m }, DenialsByReason = new() { ["paused"] = 1 },
                Activity = [new ActivityBucket { T = Created, Requests = 3, Denied = 1, CostUsd = 0.0021m, AvgMs = 400.5 }],
                RequestsByModel = new() { ["ollama/qwen3:8b"] = 2 },
                Recent = [recent],
                Savings = new SavingsSummary { Baseline = "openai/gpt-x", RouteAvoidedUsd = 0.01m, CacheAvoidedUsd = 0.002m, TotalUsd = 0.012m, ExcludedRecords = 1 },
            },
            ChainValid = true, AuditEntries = 3, Auth = new AuthStatus { AllowAnonymous = false, KeyCount = 2 }, OtelEnabled = false, RoutingThresholdTokens = 400,
            Environment = "local", Mode = "local-only", AuditPath = "/var/covenant/audit.jsonl", CacheTtlSeconds = 300, PromptPreviewChars = 0,
            Anchor = new AnchorStatus { Configured = true, Every = 100, AnchorsWritten = 2, LastAnchorUtc = Created },
            Version = "2.0.0", ConfigVersion = 13,
            ConnectionHealth =
            [
                new ConnectionHealthView
                {
                    Key = "ollama:qwen3:8b", ConnectionId = "ollama", ModelId = "qwen3:8b", Boundary = "Local", Enabled = true,
                    Health = new HealthView { State = "ok", LastSuccessUtc = Created, LastFailureUtc = Created.AddMinutes(-5), LastError = "timeout" },
                },
            ],
        };
        var ui = JsonSerializer.Deserialize(JsonSerializer.Serialize(host, CovenantJsonContext.Default.StatusReport), UiModels.UiJsonContext.Default.StatusReport)!;

        Assert.Equal(Created, ui.GeneratedUtc);
        Assert.Equal(Created.AddHours(-1), ui.StartedUtc);
        Assert.True(ui.KillSwitch.Engaged);
        Assert.Equal("demo", ui.KillSwitch.Reason);
        Assert.Equal(25m, ui.Budget.GlobalCapUsd);
        Assert.Equal(0.5m, ui.Budget.GlobalSpendUsd);
        Assert.Equal("payments", Assert.Single(ui.Budget.Teams).Team);
        Assert.Null(ui.Budget.Teams[0].CapUsd);
        Covenant.Ui.Models.RouteView route = Assert.Single(ui.Routes);
        Assert.Equal("Pii", route.Classification);
        Assert.Equal("ollama", route.Adapter);
        Assert.Equal("qwen3:8b", route.Model);
        Assert.True(ui.ChainValid);
        Assert.Equal(3, ui.AuditEntries);
        Assert.False(ui.Auth.AllowAnonymous);
        Assert.Equal(2, ui.Auth.KeyCount);
        Assert.False(ui.OtelEnabled);
        Assert.Equal(400, ui.RoutingThresholdTokens);
        Assert.Equal("local", ui.Environment);
        Assert.Equal("local-only", ui.Mode);
        Assert.Equal("Local-only", Covenant.Ui.Logic.ShellStatus.Mode(ui.Mode).Label);
        Assert.Equal("/var/covenant/audit.jsonl", ui.AuditPath);
        Assert.Equal(300, ui.CacheTtlSeconds);
        Assert.Equal(0, ui.PromptPreviewChars);
        Assert.True(ui.Anchor!.Configured);
        Assert.Equal(100, ui.Anchor!.Every);
        Assert.Equal(2, ui.Anchor!.AnchorsWritten);
        Assert.Equal(Created, ui.Anchor!.LastAnchorUtc);
        Assert.Equal("2.0.0", ui.Version);
        Assert.Equal(13, ui.ConfigVersion);

        var f = ui.FinOps;
        Assert.Equal(3, f.Requests);
        Assert.Equal(2, f.Allowed);
        Assert.Equal(1, f.Denied);
        Assert.Equal(0.0021m, f.TotalCostUsd);
        Assert.Equal(1, f.LocalRequests);
        Assert.Equal(94, f.LocalTokens);
        Assert.Equal(1, f.UnknownCostRequests);
        Assert.Equal(1, f.EstimatedCostRequests);
        Assert.Equal(0.0021m, f.CostByTeamUsd["payments"]);
        Assert.Equal(1, f.DenialsByReason["paused"]);
        Assert.Equal(2, f.RequestsByModel["ollama/qwen3:8b"]);
        var b = Assert.Single(f.Activity);
        Assert.Equal(Created, b.T);
        Assert.Equal(400.5, b.AvgMs);
        Assert.Equal("Estimated provider charge avoided", f.Savings!.Label);
        Assert.Equal("openai/gpt-x", f.Savings!.Baseline);
        Assert.Equal(0.01m, f.Savings!.RouteAvoidedUsd);
        Assert.Equal(0.002m, f.Savings!.CacheAvoidedUsd);
        Assert.Equal(0.012m, f.Savings!.TotalUsd);
        Assert.Equal(1, f.Savings!.ExcludedRecords);
        var r = Assert.Single(f.Recent);
        Assert.Equal("req_1", r.Id);
        Assert.Equal(Created, r.T);
        Assert.Equal("Pii", r.Classification);
        Assert.Equal("Allow", r.Effect);
        Assert.Equal("ollama/qwen3:8b", r.Model);
        Assert.Equal(94, r.Tokens);
        Assert.Equal(84, r.TokensIn);
        Assert.Equal(10, r.TokensOut);
        Assert.Equal(0.0021m, r.CostUsd);
        Assert.Equal(812.5, r.Ms);
        Assert.Equal(120, r.PromptChars);
        Assert.Equal("US SSN pattern", r.Signal);
        Assert.False(r.CacheHit);
        Assert.Equal("mixed", r.CostBasis);

        // Review round 2 (B11): connection_health — the header's source for the CURRENT runtime connection.
        var h = Assert.Single(ui.ConnectionHealth!);
        Assert.Equal("ollama:qwen3:8b", h.Key);
        Assert.Equal("ollama", h.ConnectionId);
        Assert.Equal("qwen3:8b", h.ModelId);
        Assert.Equal("Local", h.Boundary);
        Assert.True(h.Enabled);
        Assert.Equal("ok", h.Health.State);
        Assert.Equal(Created, h.Health.LastSuccessUtc);
        Assert.Equal(Created.AddMinutes(-5), h.Health.LastFailureUtc);
        Assert.Equal("timeout", h.Health.LastError);

        var catalog = new UiModels.CatalogResponse
        {
            Local = new UiModels.LocalRouteInfo { Configured = true, Registered = true, ConnectionId = "ollama", ModelId = "qwen3:8b" },
        };
        Assert.Equal(Covenant.Ui.Logic.LocalRouteState.Ready, Covenant.Ui.Logic.LocalHealth.Derive(catalog, ui.ConnectionHealth).State);
    }

    [Fact]
    public void Applications_reach_the_console_without_credentials()
    {
        var body = JsonSerializer.Serialize(new List<ApplicationView>
        {
            new() { Id = "demo-app", Team = "payments", Enabled = true, ConsoleTesting = true, GrantedProfiles = ["public-tickets", "internal"], DefaultProfile = "internal" },
            new() { Id = "prod-app", Team = "risk", Enabled = false, ConsoleTesting = false, GrantedProfiles = [], DefaultProfile = null },
        }, ConfigJsonContext.Default.ListApplicationView);
        var ui = JsonSerializer.Deserialize(body, UiModels.UiJsonContext.Default.ListApplicationSummaryDto)!;
        Assert.Equal(2, ui.Count);
        Assert.Equal("demo-app", ui[0].Id);
        Assert.Equal("payments", ui[0].Team);
        Assert.True(ui[0].Enabled);
        Assert.True(ui[0].ConsoleTesting);
        Assert.Equal(new[] { "public-tickets", "internal" }, ui[0].GrantedProfiles);
        Assert.Equal("internal", ui[0].DefaultProfile);
        Assert.False(ui[1].Enabled);
        Assert.False(ui[1].ConsoleTesting);
        Assert.Empty(ui[1].GrantedProfiles);
        Assert.Null(ui[1].DefaultProfile);
        Assert.DoesNotContain("key", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rollback_answer_and_config_errors_reach_the_console()
    {
        // POST /admin/config/rollback answers like a PUT: {version, etag} (ConfigJsonContext.ConfigPutResponse).
        var ok = JsonSerializer.Deserialize(JsonSerializer.Serialize(new ConfigPutResponse { Version = 14, Etag = "\"v14\"" }, ConfigJsonContext.Default.ConfigPutResponse),
            UiModels.UiJsonContext.Default.ConfigPutResponseDto)!;
        Assert.Equal(14, ok.Version);
        Assert.Equal("\"v14\"", ok.Etag);

        var notFound = JsonSerializer.Deserialize(JsonSerializer.Serialize(new ConfigErrorResponse { Error = "not_found", Reason = "no activated configuration version 99", ActiveVersion = 13 },
            ConfigJsonContext.Default.ConfigErrorResponse), UiModels.UiJsonContext.Default.ConfigErrorResponseDto)!;
        Assert.Equal("not_found", notFound.Error);
        Assert.Equal("no activated configuration version 99", notFound.Reason);
        Assert.Equal(13, notFound.ActiveVersion);
        Assert.Null(notFound.Errors);

        var rolledBack = Covenant.Ui.Logic.EditOutcomes.FromResponse(200,
            JsonSerializer.Serialize(new ConfigPutResponse { Version = 14, Etag = "\"v14\"" }, ConfigJsonContext.Default.ConfigPutResponse), rollback: true);
        Assert.True(rolledBack.Succeeded);
        Assert.Equal(14, rolledBack.Version);

        // A historical snapshot (GET /admin/config/versions/{n}) is a plain CovenantConfig.
        var snap = JsonSerializer.Deserialize(JsonSerializer.Serialize(Representative(ProviderMode.LocalOnly), ConfigJsonContext.Default.CovenantConfig),
            UiModels.UiJsonContext.Default.CovenantConfigDto)!;
        Assert.Equal(7, snap.Version);
        Assert.Equal("local-only", snap.Mode);
        Assert.Equal(4, snap.Connections.Count);
        Assert.Equal(90, snap.Connections[0].TimeoutSeconds);
    }

    [Fact]
    public void Common_admin_error_body_carries_the_error_code_the_console_branches_on()
    {
        foreach (var (error, reason) in new[] { ("cursor_expired", "the segment was rotated"), ("invalid_cursor", "malformed"), ("not_found", "no record") })
        {
            var ui = JsonSerializer.Deserialize(JsonSerializer.Serialize(new ErrorResponse { Error = error, Reason = reason, RequestId = "req_1", Scanned = 3 }, CovenantJsonContext.Default.ErrorResponse),
                UiModels.UiJsonContext.Default.ErrorResponse)!;
            Assert.Equal(error, ui.Error);
            Assert.Equal(reason, ui.Reason);
            Assert.Equal("req_1", ui.RequestId);
            Assert.Equal(3, ui.Scanned);
        }
        var p = new Covenant.Ui.Logic.RequestPaging();
        p.Reset(null);
        Assert.Equal(Covenant.Ui.Logic.PageResult.CursorExpired, p.ApplyError(410, "cursor_expired", "410"));
    }

    [Fact]
    public void Request_export_reaches_the_console_without_content()
    {
        var record = RoundTwoRecord() with { PromptPreview = null };
        var host = new RequestExport
        {
            ExportedUtc = Created.AddDays(1),
            RequestId = record.Id,
            Segment = new SegmentView { Id = "a-0123456789abcdef", Label = "Archived 2026-10-01 09:00:00 UTC", Current = false, Entries = 40, Events = 2, FirstUtc = Created, LastUtc = Created.AddHours(2), ChainValid = true, ArchivedUtc = Created.AddDays(1) },
            ConfigVersion = 13,
            Record = record,
            Attempts = record.Attempts,
            Trace = record.Trace,
            Integrity = new ExportIntegrity
            {
                SegmentId = "a-0123456789abcdef", ChainValid = true, ChainFailure = null, Line = 17, PreviousHash = new string('b', 64), EntryHash = new string('c', 64),
                AnchorConfigured = true, Status = "verified",
            },
            ContentIncluded = false,
        };
        var ui = JsonSerializer.Deserialize(JsonSerializer.Serialize(host, AuditJsonContext.Default.RequestExport), UiModels.UiJsonContext.Default.RequestExportDto)!;
        Assert.Equal(Created.AddDays(1), ui.ExportedUtc);
        Assert.Equal(record.Id, ui.RequestId);
        Assert.Equal("a-0123456789abcdef", ui.Segment.Id);
        Assert.Equal("Archived 2026-10-01 09:00:00 UTC", ui.Segment.Label);
        Assert.False(ui.Segment.Current);
        Assert.Equal(40, ui.Segment.Entries);
        Assert.Equal(2, ui.Segment.Events);
        Assert.Equal(Created, ui.Segment.FirstUtc);
        Assert.Equal(Created.AddHours(2), ui.Segment.LastUtc);
        Assert.True(ui.Segment.ChainValid);
        Assert.Equal(Created.AddDays(1), ui.Segment.ArchivedUtc);
        Assert.Equal(13, ui.ConfigVersion);
        AssertRecord(record, ui.Record);
        Assert.Equal(new[] { "att_1", "att_2" }, ui.Attempts!.Select(a => a.AttemptId));
        Assert.Equal(new[] { "auth", "provider", "provider" }, ui.Trace!.Select(t => t.Stage));
        Assert.Equal("a-0123456789abcdef", ui.Integrity.SegmentId);
        Assert.True(ui.Integrity.ChainValid);
        Assert.Null(ui.Integrity.ChainFailure);
        Assert.Equal(17, ui.Integrity.Line);
        Assert.Equal(new string('b', 64), ui.Integrity.PreviousHash);
        Assert.Equal(new string('c', 64), ui.Integrity.EntryHash);
        Assert.True(ui.Integrity.AnchorConfigured);
        Assert.Equal("verified", ui.Integrity.Status);
        Assert.False(ui.ContentIncluded);
    }

    [Fact]
    public void Trace_snapshot_event_and_playground_submission_reach_the_console()
    {
        var ev = new TraceEventView
        {
            Seq = 9, RequestId = "req_1", ConfigVersion = 13, Stage = "provider", AttemptId = "att_1", State = "running", TsUtc = Created,
            DurationMs = null, ReasonCode = null, Target = "ollama/qwen3:8b", RequestedMaxOutputTokens = 256, EffectiveMaxOutputTokens = 64,
        };
        var uiEv = JsonSerializer.Deserialize(JsonSerializer.Serialize(ev, TraceJsonContext.Default.TraceEventView), UiModels.UiJsonContext.Default.TraceEventDto)!;
        Assert.Equal(9, uiEv.Seq);
        Assert.Equal("req_1", uiEv.RequestId);
        Assert.Equal(13, uiEv.ConfigVersion);
        Assert.Equal("provider", uiEv.Stage);
        Assert.Equal("att_1", uiEv.AttemptId);
        Assert.Equal("running", uiEv.State);
        Assert.Equal(Created, uiEv.TsUtc);
        Assert.Null(uiEv.DurationMs);
        Assert.Null(uiEv.ReasonCode);
        Assert.Equal("ollama/qwen3:8b", uiEv.Target);
        Assert.Equal(256, uiEv.RequestedMaxOutputTokens);
        Assert.Equal(64, uiEv.EffectiveMaxOutputTokens);

        var snap = new TraceSnapshotView
        {
            RequestId = "req_1", ConfigVersion = 13, Complete = true, Truncated = true,
            Events = [ev, new TraceEventView { Seq = 10, RequestId = "req_1", ConfigVersion = 13, Stage = "provider", AttemptId = "att_1", State = "passed", TsUtc = Created.AddSeconds(1), DurationMs = 812.25 }],
        };
        var uiSnap = JsonSerializer.Deserialize(JsonSerializer.Serialize(snap, TraceJsonContext.Default.TraceSnapshotView), UiModels.UiJsonContext.Default.TraceSnapshotDto)!;
        Assert.Equal("req_1", uiSnap.RequestId);
        Assert.Equal(13, uiSnap.ConfigVersion);
        Assert.True(uiSnap.Complete);
        Assert.True(uiSnap.Truncated);
        Assert.Equal(new long[] { 9, 10 }, uiSnap.Events.Select(e => e.Seq));
        Assert.Equal(812.25, uiSnap.Events[1].DurationMs);
        var acc = new Covenant.Ui.Logic.TraceAccumulator();
        acc.ApplySnapshot(uiSnap);
        var attempt = Assert.Single(acc.View().Steps.Single(st => st.Stage == "provider").Attempts);
        Assert.Equal(Covenant.Ui.Logic.TraceStepState.Passed, attempt.State);
        Assert.Equal(64, attempt.EffectiveMaxOutputTokens);

        foreach (var state in new[] { "running", "completed" })
        {
            var sub = new PlaygroundSubmissionView { ClientRequestId = "0123456789abcdef0123456789abcdef", RequestId = "req_1", State = state };
            var uiSub = JsonSerializer.Deserialize(JsonSerializer.Serialize(sub, TraceJsonContext.Default.PlaygroundSubmissionView), UiModels.UiJsonContext.Default.PlaygroundSubmissionDto)!;
            Assert.Equal("0123456789abcdef0123456789abcdef", uiSub.ClientRequestId);
            Assert.Equal("req_1", uiSub.RequestId);
            Assert.Equal(state, uiSub.State);
            var poll = new Covenant.Ui.Logic.SubmissionPoll("0123456789abcdef0123456789abcdef");
            Assert.Equal(Covenant.Ui.Logic.SubmissionPollAction.Subscribe, poll.OnPollResult(200, uiSub));
            Assert.Equal("req_1", poll.RequestId);
        }
    }

    [Fact]
    public void Console_inference_body_always_carries_a_client_request_id_the_host_accepts()
    {
        var snapshot = Covenant.Ui.Logic.RequestSnapshot.Freeze(1, new Covenant.Ui.Logic.PlaygroundForm { ApplicationId = "demo-app", Prompt = "Reply OK." }, Created);
        var body = JsonSerializer.Serialize(new UiModels.PlaygroundRequestDto
        {
            ApplicationId = "demo-app", Messages = [new UiModels.ChatMessageDto { Role = "user", Content = "Reply OK." }], MaxTokens = 256, Stream = true,
            ClientRequestId = snapshot.ClientRequestId,
        }, UiModels.UiJsonContext.Default.PlaygroundRequestDto);
        var host = JsonSerializer.Deserialize(body, CovenantJsonContext.Default.PlaygroundRequest)!;
        Assert.Null(PlaygroundApi.Validate(host));   // required since review round 2 (B7)
        Assert.Equal(snapshot.ClientRequestId, host.ClientRequestId);
        Assert.True(PlaygroundApi.IsValidClientRequestId(host.ClientRequestId));
        Assert.Equal(256, host.MaxTokens);
        Assert.True(host.Stream);
    }

    [Fact]
    public void Probe_evidence_and_kill_switch_reach_the_console()
    {
        var probe = JsonSerializer.Deserialize(JsonSerializer.Serialize(new ProbeResult
        {
            Allowed = true, Reachable = true, HttpStatus = 200, Models = ["qwen2.5-coder:7b"], Error = null, DurationMs = 12.5, Endpoint = "http://localhost:11434/v1/", CheckedUtc = Created,
        }, CovenantJsonContext.Default.ProbeResult), UiModels.UiJsonContext.Default.ProbeResponse)!;
        Assert.True(probe.Allowed);
        Assert.True(probe.Reachable);
        Assert.Equal(200, probe.HttpStatus);
        Assert.Equal(new[] { "qwen2.5-coder:7b" }, probe.Models);
        Assert.Equal(12.5, probe.DurationMs);
        Assert.Equal("http://localhost:11434/v1/", probe.Endpoint);
        Assert.Equal(Created, probe.CheckedUtc);

        var ev = JsonSerializer.Deserialize(JsonSerializer.Serialize(new EvidenceReport
        {
            GeneratedUtc = Created, AuditLogPath = "/var/covenant/audit.jsonl", ChainValid = false, ChainFailure = "hash mismatch", FirstInvalidLine = 4,
            Entries = 10, Events = 2, Allowed = 7, Denied = 3, FirstEntryUtc = Created, LastEntryUtc = Created.AddHours(1), TotalCostUsd = 0.5m,
            CostByTeamUsd = new() { ["payments"] = 0.5m }, RequestsByClassification = new() { ["Pii"] = 4 }, AnchorConfigured = true, AnchorCount = 3, LastAnchorUtc = Created,
        }, CovenantJsonContext.Default.EvidenceReport), UiModels.UiJsonContext.Default.EvidenceReport)!;
        Assert.Equal(Created, ev.GeneratedUtc);
        Assert.Equal("/var/covenant/audit.jsonl", ev.AuditLogPath);
        Assert.False(ev.ChainValid);
        Assert.Equal("hash mismatch", ev.ChainFailure);
        Assert.Equal(4, ev.FirstInvalidLine);
        Assert.Equal(10, ev.Entries);
        Assert.Equal(2, ev.Events);
        Assert.Equal(7, ev.Allowed);
        Assert.Equal(3, ev.Denied);
        Assert.Equal(0.5m, ev.TotalCostUsd);
        Assert.Equal(0.5m, ev.CostByTeamUsd["payments"]);
        Assert.Equal(4, ev.RequestsByClassification["Pii"]);
        Assert.True(ev.AnchorConfigured);
        Assert.Equal(3, ev.AnchorCount);
        Assert.Equal(Created, ev.LastAnchorUtc);

        var ks = JsonSerializer.Deserialize(JsonSerializer.Serialize(new KillSwitchState { Engaged = true, Reason = "paused from console" }, CovenantJsonContext.Default.KillSwitchState),
            UiModels.UiJsonContext.Default.KillSwitchState)!;
        Assert.True(ks.Engaged);
        Assert.Equal("paused from console", ks.Reason);
        var reset = JsonSerializer.Deserialize(JsonSerializer.Serialize(new ResetResponse { ArchivedTo = "/var/covenant/audit.jsonl.20261017-120000.archived" }, CovenantJsonContext.Default.ResetResponse),
            UiModels.UiJsonContext.Default.ResetResponse)!;
        Assert.Equal("/var/covenant/audit.jsonl.20261017-120000.archived", reset.ArchivedTo);
    }

    private static JsonElement Wire(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    // ---- Review round 3 (UI): the profile/grant editor builds drafts the Host's validation accepts ----

    private static Covenant.Governance.ValidationEnvironment PermissiveEnvironment() => new()
    {
        EgressAllowList = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ollama-host:11434", "vllm-host:8000", "api.openai.com:443", "api.anthropic.com:443" },
        SecretAvailable = _ => true,
        LocalEndpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ollama-host:11434", "vllm-host:8000" },
    };

    /// <summary>Console draft → PUT body (UiJsonContext) → Host ConfigPutRequest → ConfigValidator.</summary>
    private static (CovenantConfig Host, Covenant.Governance.ConfigValidationResult Result) HostValidates(UiModels.CovenantConfigDto draft)
    {
        var body = JsonSerializer.Serialize(new UiModels.ConfigPutRequestDto { Config = draft, ChangeSummary = "profiles" }, UiModels.UiJsonContext.Default.ConfigPutRequestDto);
        var put = JsonSerializer.Deserialize(body, ConfigJsonContext.Default.ConfigPutRequest);
        Assert.NotNull(put?.Config);
        var config = put!.Config!;
        var result = Covenant.Governance.ConfigValidator.Validate(config, PermissiveEnvironment());
        Assert.True(result.StructurallySound, string.Join("; ", result.Errors.Select(e => e.Path + ": " + e.Code)));
        return (config, result);
    }

    private static bool ProfileOrGrantPath(string path)
        => path.StartsWith("profiles", StringComparison.Ordinal) || path.StartsWith("applications", StringComparison.Ordinal) || path.Contains(".profiles", StringComparison.Ordinal);

    [Fact]
    public void Profile_and_grant_edits_made_by_the_console_pass_host_validation()
    {
        var (_, envelope, _, _) = RoundTrip(Representative(ProviderMode.Hybrid));
        var draft = envelope.Config!;

        Assert.Empty(Covenant.Ui.Logic.ProfileEditing.Add(draft, new Covenant.Ui.Logic.NewProfileForm { Id = "support", Name = "Support tickets", Baseline = "Public", Description = "Synthetic" }));
        var demo = Covenant.Ui.Logic.ProfileEditing.FindApplication(draft, "demo-app")!;
        Assert.Null(Covenant.Ui.Logic.ProfileEditing.SetGrant(demo, "support", true, draft));
        Assert.Null(Covenant.Ui.Logic.ProfileEditing.SetDefault(demo, "support"));
        Assert.Null(Covenant.Ui.Logic.ProfileEditing.SetMaxOutputTokens(demo, 128));
        Assert.Null(Covenant.Ui.Logic.ProfileEditing.SetTeam(demo, "payments-ops"));
        var prod = Covenant.Ui.Logic.ProfileEditing.FindApplication(draft, "prod-app")!;
        Assert.Null(Covenant.Ui.Logic.ProfileEditing.SetGrant(prod, "support", true, draft));
        Assert.Null(Covenant.Ui.Logic.ProfileEditing.SetDefault(prod, "internal"));
        prod.Enabled = true;
        prod.ConsoleTesting = true;

        // "clinical" is only granted (no default, no policy) → removable; "public-tickets" is policy 'public''s only
        // profile → refused client-side (removing it would widen that policy).
        Assert.Null(Covenant.Ui.Logic.ProfileEditing.Remove(draft, "clinical"));
        Assert.NotNull(Covenant.Ui.Logic.ProfileEditing.Remove(draft, "public-tickets"));
        Assert.All(draft.Applications, a => Assert.Empty(Covenant.Ui.Logic.ProfileEditing.ApplicationProblems(a, draft)));

        var (host, result) = HostValidates(draft);
        Assert.DoesNotContain(result.Errors, e => ProfileOrGrantPath(e.Path));
        var support = host.Profiles.Single(p => p.Id == "support");
        Assert.Equal(DataClassification.Public, support.Baseline);
        Assert.Equal("Synthetic", support.Description);
        Assert.DoesNotContain(host.Profiles, p => p.Id == "clinical");
        var hostDemo = host.Applications.Single(a => a.Id == "demo-app");
        Assert.Equal("support", hostDemo.DefaultProfile);
        Assert.Equal(128, hostDemo.MaxOutputTokens);
        Assert.Equal("payments-ops", hostDemo.Team);
        Assert.DoesNotContain("clinical", hostDemo.GrantedProfiles);
        var hostProd = host.Applications.Single(a => a.Id == "prod-app");
        Assert.True(hostProd.Enabled);
        Assert.True(hostProd.ConsoleTesting);
        Assert.Equal(new[] { "internal", "support" }, hostProd.GrantedProfiles);
    }

    [Fact]
    public void Console_and_host_agree_that_a_default_profile_must_be_granted()
    {
        var (_, envelope, _, _) = RoundTrip(Representative(ProviderMode.Hybrid));
        var draft = envelope.Config!;
        var prod = Covenant.Ui.Logic.ProfileEditing.FindApplication(draft, "prod-app")!;
        Assert.NotNull(Covenant.Ui.Logic.ProfileEditing.SetDefault(prod, "customer-pii"));   // the editor refuses
        prod.DefaultProfile = "customer-pii";                                                  // forced, to compare
        Assert.NotEmpty(Covenant.Ui.Logic.ProfileEditing.ApplicationProblems(prod, draft));
        var (_, result) = HostValidates(draft);
        Assert.Contains(result.Errors, e => e.Code == "default_profile_not_granted");
    }
}

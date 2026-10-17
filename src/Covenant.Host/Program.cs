using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Covenant.Adapters;
using Covenant.Core;
using Covenant.Governance;
using Covenant.Host;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
// Canonical types win the name collision with Microsoft.Extensions.AI (which stays imported
// for IChatClient and the AsIChatClient extension). Provider shapes belong in adapters only.
using ChatMessage = Covenant.Core.ChatMessage;
using ChatRole = Covenant.Core.ChatRole;

var builder = WebApplication.CreateBuilder(args);

// --- Startup-only configuration (fail-closed: missing/invalid required values → refuse to start; a refusal is a diagnostic, not a crash).
//     Startup-only per ADR-0011: Admin:Token, secret values, Auth:Keys, Audit:*, Config:Path, Providers:ProbeAllowList. ---
string auditPath = builder.Configuration["Audit:Path"] ?? "covenant-audit.log";
string? adminToken = builder.Configuration["Admin:Token"];

var configErrors = new List<string>();

// Optional numeric settings: absent = fallback (opt-in off); present-but-invalid or negative =
// misconfiguration → refuse to start. Fail-open "unparseable means unlimited" is forbidden here.
int OptionalNonNegativeInt(string key, int fallback)
    => LegacyImport.OptionalNonNegativeInt(builder.Configuration, key, fallback, configErrors);

if (string.IsNullOrWhiteSpace(adminToken))
    configErrors.Add("Admin:Token         — guards /admin/* (kill switch, evidence export)");

// API keys (virtual keys). Anonymous serving is an explicit opt-in, never a default. A key binds to an
// application via Auth:Keys:N:Application (default: its Principal) — ADR-0012.
bool allowAnonymous = string.Equals(builder.Configuration["Auth:AllowAnonymous"], "true", StringComparison.OrdinalIgnoreCase);
var apiKeys = new List<ApiKeyRecord>();
foreach (var child in builder.Configuration.GetSection("Auth:Keys").GetChildren())
{
    if (child["Key"] is { Length: > 0 } key
        && child["Principal"] is { Length: > 0 } keyPrincipal
        && child["Team"] is { Length: > 0 } keyTeam)
        apiKeys.Add(new ApiKeyRecord(key, keyPrincipal, keyTeam, child["Application"] is { Length: > 0 } keyApp ? keyApp : null));
    else
        configErrors.Add($"Auth:Keys:{child.Key} — requires Key, Principal, and Team");
}
if (apiKeys.Count == 0 && !allowAnonymous)
    configErrors.Add("Auth                — no API keys configured and Auth:AllowAnonymous is not 'true' (fail-closed: unauthenticated serving must be an explicit choice)");

// Fail-closed refusal, printed as a diagnostic (never a crash). Called after each config region. Program is the
// composition root: it is the only place that exits; library code returns error lists.
void FailIfConfigErrors()
{
    if (configErrors.Count == 0) return;
    Console.Error.WriteLine("covenant: refusing to start (fail-closed). Missing or invalid configuration:");
    foreach (var e in configErrors) Console.Error.WriteLine($"  {e}");
    Console.Error.WriteLine();
    Console.Error.WriteLine("dev:  dotnet user-secrets set \"OpenAI:ApiKey\" \"<value>\" --project src/Covenant.Host");
    Console.Error.WriteLine("      (see README-SCAFFOLD.md §4 — user-secrets load only in the Development");
    Console.Error.WriteLine("      environment, which Properties/launchSettings.json sets for local runs)");
    Console.Error.WriteLine("prod: env vars (OpenAI__ApiKey, Admin__Token, Budget__GlobalCapUsd) or the customer vault.");
    Environment.Exit(1);
}
FailIfConfigErrors();

// --- Runtime configuration (ADR-0011): versioned repository. Bootstrap precedence: (1) an existing repository
//     wins; (2) empty + Config:Seed ∈ {demo-local-only, demo-hybrid} ⇒ that seed is v1; (3) otherwise the legacy
//     startup keys are imported into v1 (origin legacy-import), reproducing the pre-v2 routing exactly.
//     Default location: "<Audit:Path>.config/" — beside the audit log, and unique per audit log. ---
string configDir = StartupValidation.Trimmed(builder.Configuration["Config:Path"]) is { Length: > 0 } cp ? cp : auditPath + ".config";
var egressAllowList = StartupEgress.BuildAllowList(builder.Configuration);
var secretAvailable = StartupEgress.SecretAvailability(builder.Configuration);
ConfigValidationResult ValidateConfig(CovenantConfig c) => ConfigValidator.Validate(c, new ValidationEnvironment
{
    EgressAllowList = egressAllowList,
    SecretAvailable = secretAvailable,
    // Imported legacy-cloud configurations keep the pre-v2 PII/PHI placeholder (refused at the provider stage).
    LegacyCompat = c.Mode == ProviderMode.LegacyCloud,
});
void AddValidationErrors(CovenantConfig c, ConfigValidationResult v)
{
    foreach (var e in v.Errors) configErrors.Add($"Config v{c.Version} ({c.Origin}) {e.Path} — {e.Code}: {e.Message}");
    foreach (var w in v.Warnings) Console.Error.WriteLine($"covenant: config warning v{c.Version} {w.Path} — {w.Code}: {w.Message}");
}

var configRepo = new ConfigRepository(configDir);
var opened = configRepo.Open();
configErrors.AddRange(opened.Errors);
FailIfConfigErrors();
foreach (var w in opened.Warnings) Console.Error.WriteLine($"covenant: config warning — {w}");

CovenantConfig activeConfig;
bool bootstrapped = false;
if (opened.IsEmpty)
{
    var seed = StartupValidation.Trimmed(builder.Configuration["Config:Seed"]);
    CovenantConfig? v1 = null;
    if (seed is { Length: > 0 })
    {
        if (DemoSeeds.IsKnown(seed)) v1 = DemoSeeds.Build(seed, builder.Configuration, DateTimeOffset.UtcNow, configErrors);
        else configErrors.Add($"Config:Seed         — '{seed}' is not a known seed ({DemoSeeds.LocalOnly}, {DemoSeeds.Hybrid})");
    }
    else
    {
        // Legacy import: the pre-v2 provider-mode rules (ADR-0010) still apply to the keys being imported.
        configErrors.AddRange(StartupValidation.ValidateProviders(builder.Configuration));
        v1 = LegacyImport.Build(builder.Configuration, DateTimeOffset.UtcNow, configErrors);
    }
    FailIfConfigErrors();
    AddValidationErrors(v1!, ValidateConfig(v1!));
    FailIfConfigErrors();
    activeConfig = configRepo.Bootstrap(v1!);
    bootstrapped = true;
}
else
{
    activeConfig = opened.Active!;
    // Fail-closed: an active configuration that no longer validates (e.g. a referenced secret was removed from
    // the environment) refuses to start rather than serving on half-valid rules.
    AddValidationErrors(activeConfig, ValidateConfig(activeConfig));
    FailIfConfigErrors();
    foreach (var legacyKey in new[] { "Providers:Mode", "OpenAI:ModelId", "OpenAI:StrongModelId", "Routing:ComplexityTokenThreshold", "Cache:TtlSeconds", "RateLimit:GlobalPerMinute", "RateLimit:PerTeamPerMinute", "Budget:GlobalCapUsd" })
        if (!string.IsNullOrWhiteSpace(builder.Configuration[legacyKey]))
            Console.Error.WriteLine($"covenant: note — {legacyKey} is a legacy startup key; configuration v{activeConfig.Version} in {configDir} governs routing/budgets (key ignored).");
}

// --- Observability (ADR-0003): opt-in OTel export — no Otel:Endpoint → no SDK; same egress discipline as model endpoints (in-perimeter, never phone-home); spans are diagnostics, the audit chain is the evidence. ---
bool otelEnabled = false;
if (builder.Configuration["Otel:Endpoint"] is { Length: > 0 } otelEndpoint)
{
    otelEnabled = true;
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService(builder.Configuration["Otel:ServiceName"] ?? "covenant"))
        .WithTracing(t => t
            .AddSource(CovenantDiagnostics.SourceName)
            .AddOtlpExporter(o =>
            {
                o.Endpoint = new Uri(otelEndpoint);
                o.Protocol = OtlpExportProtocol.HttpProtobuf;
                if (builder.Configuration["Otel:Headers"] is { Length: > 0 } otelHeaders)
                    o.Headers = otelHeaders; // e.g. "Authorization=Basic <base64 pk:sk>" for Langfuse
            }));
}

// Source-generated JSON for request binding + responses (AOT-safe).
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.TypeInfoResolverChain.Insert(0, CovenantJsonContext.Default));

// --- Provider layer (ADR-0001) + runtime snapshots (ADR-0011): each validated version compiles into an immutable
//     RuntimeSnapshot owning its provider clients ("connectionId:modelId"); a request acquires one at entry. ---
var clientFactory = new SdkProviderClientFactory(builder.Configuration, egressAllowList);
RuntimeSnapshot CompileSnapshot(CovenantConfig c)
{
    var clients = clientFactory.Build(c);
    foreach (var skip in clients.Skipped) Console.Error.WriteLine($"covenant: config v{c.Version} — no provider client for {skip}");
    return new RuntimeSnapshot(c, clients);
}
var runtime = new SnapshotHolder(CompileSnapshot(activeConfig));

// Chain-head anchoring (ADR-0007): both settings together or neither; the anchor path should live
// on an independent storage domain — that placement is the entire security value.
string? anchorPath = builder.Configuration["Audit:AnchorPath"];
int anchorEvery = OptionalNonNegativeInt("Audit:AnchorEvery", 0);
if (string.IsNullOrWhiteSpace(anchorPath)) anchorPath = null;
if ((anchorPath is null) != (anchorEvery == 0))
    configErrors.Add("Audit:AnchorPath / Audit:AnchorEvery — set both to enable anchoring, or neither");
FailIfConfigErrors();

var auditSink = new FileAuditSink(auditPath, anchorPath, anchorEvery);
var killSwitch = new KillSwitch();
var spendLedger = new InMemorySpendLedger();

// Budgets survive restarts: replay the audit log (event store) into the ledger (projection).
// A tampered chain at boot is fail-closed — never serve on top of corrupted evidence.
var (chainAtBoot, priorEntries) = AuditChainVerifier.VerifyAndRead(auditPath, anchorPath);
if (!chainAtBoot.Valid)
{
    Console.Error.WriteLine(
        $"covenant: audit chain INVALID at line {chainAtBoot.FirstInvalidLine} ({chainAtBoot.Failure}).");
    Console.Error.WriteLine(
        "Refusing to start (fail-closed). Archive the log for investigation — do not delete evidence. " +
        "Point Audit:Path at a fresh file to resume serving.");
    Environment.Exit(1);
}
LedgerReplay.Rebuild(priorEntries, spendLedger);   // control-plane events are skipped

// ADR-0011: the active version's activation is evidenced in the chain. A fresh bootstrap records it; a crash
// between pointer rename and audit append (no event for the active version) is recovered here, marked as such.
if (bootstrapped || !priorEntries.Any(e => e.EventType == AuditEntryKinds.ConfigActivated && e.ConfigVersion == activeConfig.Version))
    auditSink.TryWrite(ConfigAuditEvents.Activated(activeConfig, bootstrapped ? "covenant-bootstrap" : "covenant-boot", DateTimeOffset.UtcNow,
        recovered: !bootstrapped));

// --- Pipeline assembly. The provider registry is resolved from DI so tests can substitute a stub
//     provider (a later registration wins); everything else is captured here. Outermost first;
//     audit wraps everything; order per src/CLAUDE.md. Every stage reads the request's bound snapshot
//     (ctx.Configuration); the constructor arguments below are only the fail-closed fallback for a context
//     without one (deny-all policy, zero budget), which the Host never produces. ---
builder.Services.AddSingleton<IRuntimeConfig>(runtime);
builder.Services.AddSingleton<IChatClientRegistry>(new SnapshotChatClientRegistry(runtime));
var providerHealth = new ProviderHealth();
var probeAllowList = ProviderProbe.BuildAllowList(builder.Configuration["Providers:ProbeAllowList"], StartupValidation.Trimmed(builder.Configuration["Local:Endpoint"]));
string environmentName = builder.Configuration["Covenant:Environment"] is { Length: > 0 } envName ? envName : "local";
var responseCache = new ResponseCache();
var rateCounterGlobal = new RateCounter();
var rateCounterTeams = new RateCounter();
var classifier = new RegexDataClassifier();
var denyAllPolicy = new PolicyConfig { AllowedRoutes = new Dictionary<DataClassification, IReadOnlyList<RouteTarget>>() };
var noPrices = new PriceBook(new Dictionary<string, (decimal, decimal)>());
builder.Services.AddSingleton(sp =>
{
    var liveRegistry = sp.GetRequiredService<IChatClientRegistry>();
    return new InferencePipeline(
    [
        new AuditStage(auditSink),                       // outermost: audits allow, deny, and error alike
        new AuthStage(new AuthConfig { AllowAnonymous = allowAnonymous, Keys = apiKeys }), // auth first (+ application/profile)
        new ShapeStage(),                                // refuse shapes governance can't see (images, tools) — audited
        new ClassifyStage(classifier),                   // classify: max(trusted baseline, detected)
        new PolicyStage(new PolicyEngine(denyAllPolicy)),// policy v2 on the bound snapshot, fail-closed
        new CacheStage(responseCache, new CacheConfig()),// cache before budget/provider: a hit skips the model call
        new RateLimitStage(rateCounterGlobal, rateCounterTeams, new RateLimitConfig()), // rate half of budget/rate; cache hits bypass (free)
        new BudgetStage(killSwitch, spendLedger, new BudgetConfig { GlobalCapUsd = 0m }, noPrices), // pause + caps; charges failed calls on unwind
        new ProviderCallStage(liveRegistry, providerHealth,
            preferRequestClients: liveRegistry is SnapshotChatClientRegistry), // route + provider call on the request's snapshot clients
        new AttributionStage(noPrices),                  // attribute cost (connection-scoped price book of the snapshot)
    ]);
});
builder.Services.AddHostedService(_ => auditSink);   // drains the audit channel in the background

// ADR-0006: evidence graph — optional, in-perimeter, never load-bearing. No Neo4j:Uri → not registered.
if (builder.Configuration["Neo4j:Uri"] is { Length: > 0 } neo4jUri)
{
    builder.Services.AddHostedService(_ => new EvidenceGraphProjector(
        auditPath,
        neo4jUri,
        builder.Configuration["Neo4j:User"] ?? "neo4j",
        builder.Configuration["Neo4j:Password"] ?? "",
        anchorPath));
}

var app = builder.Build();

// Correlation (ADR-0009): one server-generated id per /v1 request, created BEFORE body binding so even a
// request rejected by the binder (400, no pipeline, no audit record) carries it. Never caller-supplied.
app.Use(async (http, next) =>
{
    if (http.Request.Path.StartsWithSegments("/v1"))
    {
        var rid = RequestCorrelation.NewId();
        http.Items[RequestCorrelation.ItemKey] = rid;
        http.Response.Headers[RequestCorrelation.Header] = rid;
    }
    await next(http);
});

// Model discovery for OpenAI-compatible clients (Open WebUI, SDKs). Same auth semantics as chat:
// a valid API key, or anonymous only if explicitly allowed. Only policy-permitted models are listed.
// Anthropic-dialect ingress (/v1/messages): same pipeline, same governance — only the wire differs.
// Anthropic clients authenticate with x-api-key (Bearer also accepted); errors use Anthropic's
// taxonomy; streaming speaks the Messages SSE event protocol with lazy header commit (pre-flight
// denials stay plain JSON).
app.MapPost("/v1/messages", async (AnthropicMessagesRequest body, InferencePipeline pipe, HttpContext http, CancellationToken ct) =>
{
    string? credential = http.Request.Headers["x-api-key"].FirstOrDefault();
    if (string.IsNullOrEmpty(credential)
        && http.Request.Headers.Authorization.FirstOrDefault() is { } authHeader
        && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        credential = authHeader["Bearer ".Length..].Trim();

    var principal = http.Request.Headers["X-Covenant-Principal"].FirstOrDefault() ?? "anonymous";
    var tags = new AttributionTags(
        Team: http.Request.Headers["X-Covenant-Team"].FirstOrDefault() ?? "unknown",
        Workflow: http.Request.Headers["X-Covenant-Workflow"].FirstOrDefault() ?? "unknown",
        UseCase: http.Request.Headers["X-Covenant-UseCase"].FirstOrDefault() ?? "unknown");

    var requestId = RequestCorrelation.Get(http);
    var request = new InferenceRequest(principal, AnthropicWire.ToCanonical(body), body.Model, tags,
        Stream: body.Stream == true, Credential: credential,
        MaxOutputTokens: body.MaxTokens, UnsupportedFeature: AnthropicWire.Unsupported(body),
        RequestId: requestId, RequestedProfile: http.Request.Headers[AuthStage.ProfileHeader].FirstOrDefault());
    var ctx = new InferenceContext(request);
    using var lease = SnapshotLease.Bind(runtime, ctx);   // ADR-0011: one configuration version for the whole request
    var msgId = requestId;

    if (request.Stream)
    {
        var resp = http.Response;
        bool sseStarted = false;

        async ValueTask WriteEventAsync(string evt, string json, CancellationToken token)
        {
            await resp.WriteAsync($"event: {evt}\ndata: {json}\n\n", token);
            await resp.Body.FlushAsync(token);
        }

        ctx.DeltaSink = async (delta, token) =>
        {
            if (!sseStarted)
            {
                sseStarted = true;
                resp.StatusCode = StatusCodes.Status200OK;
                resp.ContentType = "text/event-stream";
                resp.Headers.CacheControl = "no-cache";
                await WriteEventAsync("message_start", AnthropicWire.MessageStartEvent(msgId, ctx.Policy?.Route?.ModelId ?? ""), token);
                await WriteEventAsync("content_block_start", AnthropicWire.ContentBlockStartEvent, token);
            }
            await WriteEventAsync("content_block_delta", AnthropicWire.ContentBlockDeltaEvent(delta.Content), token);
        };

        await pipe.ExecuteAsync(ctx, ct);

        if (!sseStarted)
            return AnthropicDenial(ctx);            // denied before the first byte — plain JSON error

        if (ctx.IsDenied)
        {
            var (streamErrType, _) = AnthropicWire.MapDenial(ctx.DenialKind);
            await WriteEventAsync("error", AnthropicWire.ErrorEvent(streamErrType, ctx.DenialReason ?? "stream failed"), ct);
        }
        else if (ctx.Response is { } fin)
        {
            await WriteEventAsync("content_block_stop", AnthropicWire.ContentBlockStopEvent, ct);
            await WriteEventAsync("message_delta", AnthropicWire.MessageDeltaEvent(fin.Usage.OutputTokens), ct);
            await WriteEventAsync("message_stop", AnthropicWire.MessageStopEvent, ct);
        }
        return Results.Empty;
    }

    await pipe.ExecuteAsync(ctx, ct);

    if (ctx.IsDenied || ctx.Response is null)
        return AnthropicDenial(ctx);

    return Results.Json(AnthropicWire.BuildResponse(msgId, ctx.Response), CovenantJsonContext.Default.AnthropicMessageResponse);
});

app.MapGet("/v1/models", (HttpContext http) =>
{
    string? cred = null;
    if (http.Request.Headers.Authorization.FirstOrDefault() is { } auth
        && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        cred = auth["Bearer ".Length..].Trim();

    bool authorized = cred is { Length: > 0 }
        ? apiKeys.Any(k => string.Equals(k.Key, cred, StringComparison.Ordinal))
        : allowAnonymous;
    if (!authorized)
        return Results.Json(
            new ErrorResponse { Error = "unauthenticated", Reason = "valid API key required" },
            CovenantJsonContext.Default.ErrorResponse, statusCode: StatusCodes.Status401Unauthorized);

    var models = runtime.Current.Config.Policies
        .Where(p => p.Enabled && !p.Paused)
        .SelectMany(p => p.Targets.Where(t => !t.Paused))
        .Select(r => r.ModelId)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Select(m => new OpenAiModel { Id = m })
        .ToList();
    return Results.Json(new OpenAiModelList { Data = models }, CovenantJsonContext.Default.OpenAiModelList);
});

app.MapPost("/v1/chat/completions",
    async (OpenAiChatRequest body, InferencePipeline pipe, HttpContext http, CancellationToken ct) =>
{
    // Bearer key for the auth stage. OpenAI SDK clients pointed at Covenant send their configured
    // api-key here automatically — a virtual key drops into existing tooling unchanged.
    string? credential = null;
    if (http.Request.Headers.Authorization.FirstOrDefault() is { } authHeader
        && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        credential = authHeader["Bearer ".Length..].Trim();

    var principal = http.Request.Headers["X-Covenant-Principal"].FirstOrDefault() ?? "anonymous";
    var tags = new AttributionTags(
        Team: http.Request.Headers["X-Covenant-Team"].FirstOrDefault() ?? "unknown",
        Workflow: http.Request.Headers["X-Covenant-Workflow"].FirstOrDefault() ?? "unknown",
        UseCase: http.Request.Headers["X-Covenant-UseCase"].FirstOrDefault() ?? "unknown");

    var request = new InferenceRequest(
        Principal: principal,
        Messages: (body.Messages ?? []).Where(m => m is not null).Select(m => new ChatMessage(ParseRole(m.Role), OpenAiWire.Text(m))).ToList(),
        RequestedModel: body.Model,
        Attribution: tags,
        Stream: body.Stream == true,
        Credential: credential,
        MaxOutputTokens: body.MaxCompletionTokens ?? body.MaxTokens,
        UnsupportedFeature: OpenAiWire.Unsupported(body),
        RequestId: RequestCorrelation.Get(http),
        RequestedProfile: http.Request.Headers[AuthStage.ProfileHeader].FirstOrDefault());

    var ctx = new InferenceContext(request);
    using var lease = SnapshotLease.Bind(runtime, ctx);   // ADR-0011: one configuration version for the whole request

    // --- Streamed mode (ADR-0002): SSE headers commit lazily on the FIRST delta, so a pre-flight denial still returns a plain JSON error below. ---
    if (request.Stream)
    {
        var resp = http.Response;
        var chunkId = request.RequestId!;
        bool sseStarted = false;

        async ValueTask WriteChunkAsync(OpenAiChatChunk chunk, CancellationToken token)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(chunk, CovenantJsonContext.Default.OpenAiChatChunk);
            await resp.WriteAsync($"data: {json}\n\n", token);
            await resp.Body.FlushAsync(token);
        }

        OpenAiChatChunk Chunk(string? role, string? content, string? finish, OpenAiUsage? usage) => new()
        {
            Id = chunkId,
            Model = ctx.Policy?.Route?.ModelId ?? "",
            Choices = [new OpenAiChunkChoice { Index = 0, Delta = new OpenAiDelta { Role = role, Content = content }, FinishReason = finish }],
            Usage = usage,
        };

        ctx.DeltaSink = async (delta, token) =>
        {
            if (!sseStarted)
            {
                sseStarted = true;
                resp.StatusCode = StatusCodes.Status200OK;
                resp.ContentType = "text/event-stream";
                resp.Headers.CacheControl = "no-cache";
                await WriteChunkAsync(Chunk("assistant", null, null, null), token);
            }
            await WriteChunkAsync(Chunk(null, delta.Content, null, null), token);
        };

        await pipe.ExecuteAsync(ctx, ct);

        if (!sseStarted)
        {
            // Denied before the first byte — same JSON error contract as buffered mode.
            return DenialResult(ctx);
        }

        if (ctx.IsDenied)
        {
            // Mid-stream failure after a committed 200: terminate with an error event (audited already).
            var err = System.Text.Json.JsonSerializer.Serialize(
                new ErrorResponse { Error = "upstream_error", Reason = ctx.DenialReason ?? "stream failed", RequestId = request.RequestId },
                CovenantJsonContext.Default.ErrorResponse);
            await resp.WriteAsync($"data: {err}\n\n", ct);
        }
        else if (ctx.Response is { } finalResp)
        {
            await WriteChunkAsync(Chunk(null, null, "stop", new OpenAiUsage
            {
                PromptTokens = finalResp.Usage.InputTokens,
                CompletionTokens = finalResp.Usage.OutputTokens,
                TotalTokens = finalResp.Usage.TotalTokens,
            }), ct);
        }

        await resp.WriteAsync("data: [DONE]\n\n", ct);
        await resp.Body.FlushAsync(ct);
        return Results.Empty;
    }

    await pipe.ExecuteAsync(ctx, ct);

    if (ctx.IsDenied || ctx.Response is null)
        return DenialResult(ctx);

    var r = ctx.Response;
    var dto = new OpenAiChatResponse
    {
        Id = request.RequestId,
        Model = r.ServedByModel,
        Choices = [new OpenAiChoice { Index = 0, Message = new OpenAiMessage { Role = "assistant", Content = OpenAiWire.TextElement(r.Message.Content) } }],
        Usage = new OpenAiUsage
        {
            PromptTokens = r.Usage.InputTokens,
            CompletionTokens = r.Usage.OutputTokens,
            TotalTokens = r.Usage.TotalTokens
        }
    };
    return Results.Json(dto, CovenantJsonContext.Default.OpenAiChatResponse);
});

// No credentials → 401. Governance said no → 403. Upstream broke, refused fail-closed → 502.
static IResult DenialResult(InferenceContext ctx)
{
    var (error, status) = ctx.DenialKind switch
    {
        DenialKind.Unauthenticated => ("unauthenticated", StatusCodes.Status401Unauthorized),
        DenialKind.UpstreamFailure => ("upstream_error", StatusCodes.Status502BadGateway),
        DenialKind.RateLimited => ("rate_limited", StatusCodes.Status429TooManyRequests),
        DenialKind.Unsupported => ("unsupported", StatusCodes.Status400BadRequest),
        DenialKind.Cancelled => ("cancelled", 499),
        _ => ("denied", StatusCodes.Status403Forbidden),
    };
    return Results.Json(
        new ErrorResponse { Error = error, Reason = ctx.DenialReason ?? "no response", RequestId = ctx.Request.RequestId },
        CovenantJsonContext.Default.ErrorResponse, statusCode: status);
}

// Anthropic-dialect denial: same DenialKind semantics, Anthropic's error taxonomy and statuses.
static IResult AnthropicDenial(InferenceContext ctx)
{
    var (errType, status) = AnthropicWire.MapDenial(ctx.DenialKind);
    return Results.Json(new AnthropicErrorResponse
    {
        Error = new AnthropicErrorBody { Type = errType, Message = ctx.DenialReason ?? "no response" }
    }, CovenantJsonContext.Default.AnthropicErrorResponse, statusCode: status);
}

bool Authorized(HttpContext http)
{
    var provided = http.Request.Headers["X-Covenant-Admin-Token"].FirstOrDefault() ?? string.Empty;
    return CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(adminToken!));
}

IResult Unauthorized() => Results.Json(
    new ErrorResponse { Error = "unauthorized", Reason = "invalid admin token" },
    CovenantJsonContext.Default.ErrorResponse, statusCode: StatusCodes.Status401Unauthorized);

// Kill-switch control surface. Token-guarded; state is in-process (matches the in-memory ledger slice).
app.MapPost("/admin/kill-switch", (KillSwitchRequest body, HttpContext http) =>
{
    if (!Authorized(http)) return Unauthorized();

    if (body.Engaged) killSwitch.Trip(body.Reason ?? "engaged by admin");
    else killSwitch.Reset();

    return Results.Json(
        new KillSwitchState { Engaged = killSwitch.IsTripped, Reason = killSwitch.Reason },
        CovenantJsonContext.Default.KillSwitchState);
});

// Compliance-evidence export: verifies the hash chain end-to-end, then summarizes what an auditor
// asks for first. The log file itself stays the raw evidence.
app.MapGet("/admin/evidence", (HttpContext http) =>
{
    if (!Authorized(http)) return Unauthorized();

    var report = EvidenceExport.Build(auditPath, TimeProvider.System, anchorPath);
    return Results.Json(report, CovenantJsonContext.Default.EvidenceReport);
});

// Dashboard data: config + live ledger + audit aggregates. Read-only — the dashboard is a view,
// never a second source of truth.
var startedUtc = DateTimeOffset.UtcNow;

// Model-id keyed price view (per 1K) of a configuration, for FinOps' pre-v2 savings math (first entry wins).
static Dictionary<string, (decimal, decimal)> PricesByModel(CovenantConfig c)
{
    var map = new Dictionary<string, (decimal, decimal)>(StringComparer.Ordinal);
    foreach (var conn in c.Connections)
        foreach (var m in conn.Models)
            if (m.Price is { } p) map.TryAdd(m.ModelId, (p.InputPer1M / 1000m, p.OutputPer1M / 1000m));
    return map;
}

// Status routes per classification in enum order (Public, Internal, Pii, Phi), targets in policy order.
static List<(DataClassification Cls, PolicyTarget Target)> RoutesOf(CovenantConfig c)
{
    var list = new List<(DataClassification, PolicyTarget)>();
    foreach (var cls in Enum.GetValues<DataClassification>())
        foreach (var policyV2 in c.Policies.Where(x => x.Enabled && x.Classifications.Contains(cls)))
            foreach (var t in policyV2.Targets)
                list.Add((cls, t));
    return list;
}

static string? EndpointSummary(string? endpoint)
    => endpoint is not null && Uri.TryCreate(endpoint, UriKind.Absolute, out var u)
        ? $"{u.Scheme}://{u.Host}:{u.Port}{u.AbsolutePath.TrimEnd('/')}" : null;

StatusReport BuildStatus()
{
    var snapshot = runtime.Current;
    var cfg = snapshot.Config;
    var (verification, entries) = AuditChainVerifier.VerifyAndRead(auditPath, anchorPath);
    var anchorInfo = AnchorInfo.Read(anchorPath);
    var teamSpend = spendLedger.SnapshotByTeam();

    var teamCaps = cfg.Budgets
        .Where(b => b.Scope.StartsWith("team:", StringComparison.Ordinal))
        .GroupBy(b => b.Scope["team:".Length..], StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.First().CapUsd, StringComparer.OrdinalIgnoreCase);
    var teams = new List<TeamBudgetStatus>();
    foreach (var (team, cap) in teamCaps)
        teams.Add(new TeamBudgetStatus { Team = team, CapUsd = cap, SpendUsd = teamSpend.GetValueOrDefault(team) });
    foreach (var (team, spend) in teamSpend)
        if (!teamCaps.ContainsKey(team))
            teams.Add(new TeamBudgetStatus { Team = team, CapUsd = null, SpendUsd = spend });

    var routes = RoutesOf(cfg)
        .Select(r => new RouteView { Classification = r.Cls.ToString(), Adapter = r.Target.ConnectionId, Model = r.Target.ModelId })
        .ToList();

    // FinOps savings baseline: the configured counterfactual (legacy import: the strong model; none in local-only,
    // where no savings are claimed). The "local" model for the local split is the first local-boundary model.
    var localModel = cfg.Connections.Where(c => c.Boundary == Boundary.Local).SelectMany(c => c.Models).Select(m => m.ModelId).FirstOrDefault()
        ?? LegacyImport.UnconfiguredLocalModel;
    var baselineModel = cfg.SavingsBaseline?.ModelId ?? localModel;

    return new StatusReport
    {
        GeneratedUtc = DateTimeOffset.UtcNow,
        StartedUtc = startedUtc,
        KillSwitch = new KillSwitchState { Engaged = killSwitch.IsTripped, Reason = killSwitch.Reason },
        Budget = new BudgetStatus
        {
            GlobalCapUsd = cfg.Budgets.FirstOrDefault(b => b.Scope == "global")?.CapUsd ?? 0m,
            GlobalSpendUsd = spendLedger.GlobalSpendUsd,
            Teams = teams,
        },
        Routes = routes,
        FinOps = FinOps.Build(entries, localModel, baselineModel, PricesByModel(cfg)),
        ChainValid = verification.Valid,
        AuditEntries = verification.EntryCount,
        Auth = new AuthStatus { AllowAnonymous = allowAnonymous, KeyCount = apiKeys.Count },
        OtelEnabled = otelEnabled,
        RoutingThresholdTokens = cfg.Policies.FirstOrDefault(p => p.Enabled && p.Strategy == RoutingStrategy.LengthHeuristic)?.ThresholdTokens ?? 400,
        Environment = environmentName,
        Mode = StartupEgress.ModeString(cfg.Mode),
        AuditPath = Path.GetFullPath(auditPath),
        CacheTtlSeconds = cfg.Settings.CacheTtlSeconds,
        PromptPreviewChars = cfg.Settings.PromptPreviewChars,
        Anchor = new AnchorStatus
        {
            Configured = anchorPath is not null,
            Every = anchorPath is not null ? anchorEvery : null,
            AnchorsWritten = anchorInfo.Count,
            LastAnchorUtc = anchorInfo.LastUtc,
        },
        Version = typeof(Program).Assembly.GetName().Version?.ToString(),
        ConfigVersion = cfg.Version,
    };
}

app.MapGet("/admin/status", (HttpContext http) =>
{
    if (!Authorized(http)) return Unauthorized();
    http.Response.Headers.CacheControl = "no-store";
    return Results.Json(BuildStatus(), CovenantJsonContext.Default.StatusReport);
});

// Live stream: pushes a status snapshot every 2s over SSE so the dashboard updates without polling.
// fetch()-based consumption on the page keeps the admin token in a header (EventSource can't).
app.MapGet("/admin/status/stream", async (HttpContext http, CancellationToken ct) =>
{
    if (!Authorized(http)) return Unauthorized();

    var resp = http.Response;
    resp.ContentType = "text/event-stream";
    resp.Headers.CacheControl = "no-store";
    try
    {
        while (!ct.IsCancellationRequested)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(BuildStatus(), CovenantJsonContext.Default.StatusReport);
            await resp.WriteAsync($"data: {json}\n\n", ct);
            await resp.Body.FlushAsync(ct);
            await Task.Delay(2000, ct);
        }
    }
    catch (OperationCanceledException) { /* client disconnected — normal */ }
    return Results.Empty;
});

// Demo/ops reset: ARCHIVES the audit log (rename — evidence is never deleted) and clears the ledger
// together, so ledger and evidence stay consistent; the archived chain remains verifiable.
app.MapPost("/admin/reset", async (HttpContext http) =>
{
    if (!Authorized(http)) return Unauthorized();

    var archived = await auditSink.RotateAsync();
    spendLedger.Reset();
    return Results.Json(
        new ResetResponse { ArchivedTo = archived },
        CovenantJsonContext.Default.ResetResponse);
});

// ---- Console API (docs/ui-api-contract.md) ----

// Recent evidence: newest-first records of the CURRENT audit segment (since the last archive), bounded.
// Not a historical search — the response says which segment it covers.
app.MapGet("/admin/requests", (HttpContext http, int? limit) =>
{
    if (!Authorized(http)) return Unauthorized();
    http.Response.Headers.CacheControl = "no-store";
    var (verification, all) = AuditChainVerifier.VerifyAndRead(auditPath, anchorPath);
    var entries = all.Where(e => e.IsRequest()).ToList();   // v3: control-plane events → GET /admin/config/versions
    int take = Math.Clamp(limit ?? 200, 1, 1000);
    var page = new RequestsPage
    {
        Segment = new SegmentInfo
        {
            Path = Path.GetFullPath(auditPath),
            Entries = entries.Count,
            ChainValid = verification.Valid,
            FirstUtc = entries.Count > 0 ? entries[0].TimestampUtc : null,
            LastUtc = entries.Count > 0 ? entries[^1].TimestampUtc : null,
            Events = all.Count - entries.Count,
        },
        Items = Enumerable.Reverse(entries).Take(take).ToList(),
    };
    return Results.Json(page, AuditJsonContext.Default.RequestsPage);
});

// Exact lookup by correlation id (== audit Id). 404 means "not in the current segment yet" — the sink
// is asynchronous, so a caller polls briefly before concluding "unavailable"; it is never proof that an
// audit write failed.
app.MapGet("/admin/requests/{id}", (HttpContext http, string id) =>
{
    if (!Authorized(http)) return Unauthorized();
    http.Response.Headers.CacheControl = "no-store";
    if (!RequestCorrelation.IsWellFormed(id))
        return Results.Json(new ErrorResponse { Error = "invalid_id", Reason = "request id must be ≤64 chars of [A-Za-z0-9_-]" },
            CovenantJsonContext.Default.ErrorResponse, statusCode: StatusCodes.Status400BadRequest);
    var (lookupVerification, entries) = AuditChainVerifier.VerifyAndRead(auditPath, anchorPath);
    if (!lookupVerification.Valid)
        return Results.Json(new ErrorResponse { Error = "chain_invalid", Reason = $"audit chain verification failed: {lookupVerification.Failure} — evidence cannot be trusted; see /admin/evidence" },
            CovenantJsonContext.Default.ErrorResponse, statusCode: StatusCodes.Status409Conflict);
    for (int i = entries.Count - 1; i >= 0; i--)
        if (entries[i].IsRequest() && string.Equals(entries[i].Id, id, StringComparison.Ordinal))
            return Results.Json(entries[i], AuditJsonContext.Default.AuditEntry);
    return Results.Json(new ErrorResponse { Error = "not_found", Reason = "no record with this id in the current audit segment", Scanned = entries.Count },
        CovenantJsonContext.Default.ErrorResponse, statusCode: StatusCodes.Status404NotFound);
});

// Operational catalog: configured vs registered vs permitted vs observed health (GET /v1/models is
// only the permitted list and is not a health check).
app.MapGet("/admin/catalog", (HttpContext http) =>
{
    if (!Authorized(http)) return Unauthorized();
    // Resolved explicitly (not parameter-inferred): the LIVE registration, incl. a test override.
    var liveRegistry = http.RequestServices.GetRequiredService<IChatClientRegistry>();
    http.Response.Headers.CacheControl = "no-store";
    var snapshot = runtime.Current;
    var cfg = snapshot.Config;
    var byRoute = new Dictionary<(string, string), List<string>>();
    foreach (var (cls, t) in RoutesOf(cfg))
    {
        if (!byRoute.TryGetValue((t.ConnectionId, t.ModelId), out var list)) byRoute[(t.ConnectionId, t.ModelId)] = list = [];
        if (!list.Contains(cls.ToString())) list.Add(cls.ToString());
    }

    (string Basis, decimal? In, decimal? Out) PriceView(ModelConfig? m)
        => m?.Price is not { } p ? ("unpriced", null, null)
         : (p.InputPer1M == 0m && p.OutputPer1M == 0m ? "zero" : "configured", p.InputPer1M, p.OutputPer1M);

    var models = byRoute.Select(kv =>
    {
        var (connectionId, model) = kv.Key;
        var conn = snapshot.FindConnection(connectionId);
        var price = PriceView(snapshot.FindModel(connectionId, model));
        return new CatalogModel
        {
            Adapter = connectionId,
            ModelId = model,
            Classifications = kv.Value,
            // Named by policy is not registered: the connection must exist and a client must resolve.
            Registered = conn is not null && liveRegistry.TryResolve(connectionId, model, out _),
            PriceBasis = price.Basis,
            InPer1M = price.In,
            OutPer1M = price.Out,
            EndpointHost = conn?.Boundary == Boundary.Local ? EndpointSummary(conn.Endpoint) : null,
            Health = providerHealth.Get($"{connectionId}:{model}"),
            ConnectionId = connectionId,
            Boundary = conn?.Boundary.ToString(),
            Key = $"{connectionId}:{model}",
        };
    }).ToList();

    var connections = cfg.Connections.Select(c =>
    {
        var authority = c.Endpoint is null ? ConfigValidator.DefaultAuthority(c.Kind) : ConfigValidator.EndpointAuthority(c.Endpoint);
        return new CatalogConnection
        {
            Id = c.Id,
            DisplayName = c.DisplayName,
            Kind = c.Kind.ToString(),
            Boundary = c.Boundary.ToString(),
            Enabled = c.Enabled,
            Endpoint = EndpointSummary(c.Endpoint),
            EndpointAllowListed = authority is not null && egressAllowList.Contains(authority),
            SecretRef = c.SecretRef,
            SecretAvailable = c.SecretRef is { } r ? secretAvailable(r) : null,
            Models = c.Models.Select(m =>
            {
                var price = PriceView(m);
                return new CatalogConnectionModel
                {
                    Key = $"{c.Id}:{m.ModelId}",
                    ModelId = m.ModelId,
                    DisplayName = m.DisplayName,
                    Registered = liveRegistry.TryResolve(c.Id, m.ModelId, out _),
                    PriceBasis = price.Basis,
                    InPer1M = price.In,
                    OutPer1M = price.Out,
                    Health = providerHealth.Get($"{c.Id}:{m.ModelId}"),
                };
            }).ToList(),
        };
    }).ToList();

    // Pre-v2 "local" view: the connection named "local" (legacy import), else the first local-boundary one.
    var local = snapshot.FindConnection("local") ?? cfg.Connections.FirstOrDefault(c => c.Boundary == Boundary.Local);
    var localAuthority = EndpointSummary(local?.Endpoint);
    var localModelId = local?.Models.FirstOrDefault()?.ModelId;
    var report = new CatalogReport
    {
        Mode = StartupEgress.ModeString(cfg.Mode),
        Models = models,
        Local = new LocalConfigView
        {
            Configured = local is not null && local.Endpoint is not null && localModelId is not null,
            Endpoint = localAuthority,
            ModelId = localModelId,
            Registered = local is not null && localModelId is not null && liveRegistry.TryResolve(local.Id, localModelId, out _),
            EndpointHealth = providerHealth.Get($"endpoint:{localAuthority}"),
        },
        ProbeAllowList = probeAllowList.OrderBy(x => x, StringComparer.Ordinal).ToList(),
        ConfigVersion = cfg.Version,
        Connections = connections,
        EgressAllowList = egressAllowList.OrderBy(x => x, StringComparer.Ordinal).ToList(),
    };
    return Results.Json(report, CovenantJsonContext.Default.CatalogReport);
});

// Host-side probe + discovery of an OpenAI-compatible endpoint (Ollama). Allow-listed destinations
// only; see ProviderProbe. A successful probe of the APPLIED local endpoint updates its endpoint health;
// draft endpoints never change applied state.
app.MapPost("/admin/providers/probe", async (ProbeRequest body, HttpContext http, CancellationToken ct) =>
{
    if (!Authorized(http)) return Unauthorized();
    var result = await ProviderProbe.RunAsync(body.Endpoint, probeAllowList, ct);
    // Only an APPLIED connection endpoint (active configuration) records endpoint health; drafts never do.
    if (result.Allowed && result.Endpoint is not null)
    {
        foreach (var applied in runtime.Current.Config.Connections.Select(c => EndpointSummary(c.Endpoint)).Where(e => e is not null).Distinct())
            if (string.Equals(result.Endpoint.TrimEnd('/'), applied, StringComparison.OrdinalIgnoreCase))
            {
                var key = $"endpoint:{applied}";
                if (result.Reachable) providerHealth.Success(key); else providerHealth.Failure(key, result.Error ?? "unreachable");
            }
    }
    return Results.Json(result, CovenantJsonContext.Default.ProbeResult);
});

// The dashboard: one self-contained embedded page, no CDN, no egress (root CLAUDE.md #4). Data calls
// need the token; no-store because a stale cached page against a newer endpoint dies silently.
app.MapGet("/admin/ui", (HttpContext http) =>
{
    http.Response.Headers.CacheControl = "no-store";
    return Results.Content(AdminUi.Html, "text/html");
});

app.MapConfigAdmin(new ConfigAdminServices
{
    Repository = configRepo,
    Runtime = runtime,
    Validate = ValidateConfig,
    Compile = CompileSnapshot,
    SecretAvailable = secretAvailable,
    EgressAllowList = egressAllowList,
    AuditEvents = auditSink,
    Classifier = classifier,
    Authorized = Authorized,
});

app.MapConsole();
app.Run();

static ChatRole ParseRole(string? role) => (role ?? "user").ToLowerInvariant() switch
{
    "system" => ChatRole.System,
    "assistant" => ChatRole.Assistant,
    "tool" => ChatRole.Tool,
    _ => ChatRole.User
};

public partial class Program; // exposed for the test project

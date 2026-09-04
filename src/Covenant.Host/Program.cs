using System.ClientModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Covenant.Adapters;
using Covenant.Core;
using Covenant.Governance;
using Covenant.Host;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
// Canonical types win the name collision with Microsoft.Extensions.AI (which stays imported
// for IChatClient and the AsIChatClient extension). Provider shapes belong in adapters only.
using ChatMessage = Covenant.Core.ChatMessage;
using ChatRole = Covenant.Core.ChatRole;

var builder = WebApplication.CreateBuilder(args);

// --- Configuration (fail-closed: missing/invalid required values → refuse to start; a refusal is a diagnostic, not a crash) ---
string auditPath = builder.Configuration["Audit:Path"] ?? "covenant-audit.log";

string? openAiKey = builder.Configuration["OpenAI:ApiKey"];
string? adminToken = builder.Configuration["Admin:Token"];
string? globalCapRaw = builder.Configuration["Budget:GlobalCapUsd"];
decimal globalCapUsd = 0m;

var configErrors = new List<string>();
if (string.IsNullOrWhiteSpace(openAiKey))
    configErrors.Add("OpenAI:ApiKey       — provider credential");
if (string.IsNullOrWhiteSpace(adminToken))
    configErrors.Add("Admin:Token         — guards /admin/* (kill switch, evidence export)");
if (string.IsNullOrWhiteSpace(globalCapRaw)
    || !decimal.TryParse(globalCapRaw, NumberStyles.Number, CultureInfo.InvariantCulture, out globalCapUsd))
    configErrors.Add("Budget:GlobalCapUsd — appliance-wide spend ceiling, a number like 5.00");

var teamCapsUsd = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
foreach (var child in builder.Configuration.GetSection("Budget:TeamCapsUsd").GetChildren())
{
    if (child.Value is { } capRaw
        && decimal.TryParse(capRaw, NumberStyles.Number, CultureInfo.InvariantCulture, out var cap))
        teamCapsUsd[child.Key] = cap;
    else
        configErrors.Add($"Budget:TeamCapsUsd:{child.Key} — not a number: '{child.Value}'");
}

// API keys (virtual keys). Anonymous serving is an explicit opt-in, never a default.
bool allowAnonymous = string.Equals(builder.Configuration["Auth:AllowAnonymous"], "true", StringComparison.OrdinalIgnoreCase);
var apiKeys = new List<ApiKeyRecord>();
foreach (var child in builder.Configuration.GetSection("Auth:Keys").GetChildren())
{
    if (child["Key"] is { Length: > 0 } key
        && child["Principal"] is { Length: > 0 } keyPrincipal
        && child["Team"] is { Length: > 0 } keyTeam)
        apiKeys.Add(new ApiKeyRecord(key, keyPrincipal, keyTeam));
    else
        configErrors.Add($"Auth:Keys:{child.Key} — requires Key, Principal, and Team");
}
if (apiKeys.Count == 0 && !allowAnonymous)
    configErrors.Add("Auth                — no API keys configured and Auth:AllowAnonymous is not 'true' (fail-closed: unauthenticated serving must be an explicit choice)");

if (configErrors.Count > 0)
{
    Console.Error.WriteLine("covenant: refusing to start (fail-closed). Missing or invalid configuration:");
    foreach (var e in configErrors) Console.Error.WriteLine($"  {e}");
    Console.Error.WriteLine();
    Console.Error.WriteLine("dev:  dotnet user-secrets set \"OpenAI:ApiKey\" \"<value>\" --project src/Covenant.Host");
    Console.Error.WriteLine("      (see README-SCAFFOLD.md §4 — user-secrets load only in the Development");
    Console.Error.WriteLine("      environment, which Properties/launchSettings.json sets for local runs)");
    Console.Error.WriteLine("prod: env vars (OpenAI__ApiKey, Admin__Token, Budget__GlobalCapUsd) or the customer vault.");
    Environment.Exit(1);
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

// --- Provider layer (ADR-0001). OpenAI:* config can point at any OpenAI-compatible server; clients register per exact "adapter:model" key — a route to an unregistered model fails closed. ---
string publicModelId = builder.Configuration["OpenAI:ModelId"] ?? "gpt-4o-mini";
string strongModelId = builder.Configuration["OpenAI:StrongModelId"] ?? "gpt-4o";

var openAiRoot = builder.Configuration["OpenAI:Endpoint"] is { Length: > 0 } openAiEndpoint
    ? new OpenAIClient(new ApiKeyCredential(openAiKey!), new OpenAIClientOptions { Endpoint = new Uri(openAiEndpoint) })
    : new OpenAIClient(openAiKey!);

var clients = new Dictionary<string, IChatClient>
{
    [$"openai:{publicModelId}"] = openAiRoot.GetChatClient(publicModelId).AsIChatClient(),
};
clients[$"openai:{strongModelId}"] = openAiRoot.GetChatClient(strongModelId).AsIChatClient();

// Optional in-perimeter target (vLLM, Ollama — any OpenAI-compatible endpoint). Not configured →
// "local" stays unregistered and PII/PHI keep failing closed at the provider stage.
string localModelId = builder.Configuration["Local:ModelId"] ?? "llama-3.1-8b-instruct";
if (builder.Configuration["Local:Endpoint"] is { Length: > 0 } localEndpoint)
{
    clients[$"local:{localModelId}"] = new OpenAIClient(
            new ApiKeyCredential(builder.Configuration["Local:ApiKey"] ?? "not-needed"),
            new OpenAIClientOptions { Endpoint = new Uri(localEndpoint) })
        .GetChatClient(localModelId)
        .AsIChatClient();
}

var registry = new ChatClientRegistry(clients);

// --- Policy (first slice): PII/PHI have no public route, so they fail closed until "local" is wired. ---
var policy = new PolicyConfig
{
    AllowedRoutes = new Dictionary<DataClassification, IReadOnlyList<RouteTarget>>
    {
        // Ordered cheapest → strongest: the policy engine complexity-routes within this set.
        [DataClassification.Public]   = [new("openai", publicModelId), new("openai", strongModelId)],
        [DataClassification.Internal] = [new("openai", publicModelId), new("openai", strongModelId)],
        [DataClassification.Pii]      = [new("local", localModelId)],
        [DataClassification.Phi]      = [new("local", localModelId)],
    }
};

// Real per-token prices (published per-1M rates converted to per-1K; earlier placeholders were 1000× off).
// Override per model: Pricing:<model>:InPer1M / OutPer1M (USD per million tokens).
var priceMap = new Dictionary<string, (decimal, decimal)>
{
    [publicModelId] = (0.00015m, 0.0006m),     // gpt-4o-mini: $0.15 / $0.60 per 1M
    [strongModelId] = (0.0025m, 0.01m),        // gpt-4o:      $2.50 / $10.00 per 1M
    [localModelId] = (0m, 0m),                 // in-perimeter compute: no per-token provider cost
};
foreach (var child in builder.Configuration.GetSection("Pricing").GetChildren())
{
    if (decimal.TryParse(child["InPer1M"], NumberStyles.Number, CultureInfo.InvariantCulture, out var inPer1M)
        && decimal.TryParse(child["OutPer1M"], NumberStyles.Number, CultureInfo.InvariantCulture, out var outPer1M))
        priceMap[child.Key] = (inPer1M / 1000m, outPer1M / 1000m);
    else
        configErrors.Add($"Pricing:{child.Key} — requires numeric InPer1M and OutPer1M");
}
var prices = new PriceBook(priceMap);

long complexityThreshold =
    long.TryParse(builder.Configuration["Routing:ComplexityTokenThreshold"], out var thr) ? thr : 400;

// Response cache: opt-in via Cache:TtlSeconds > 0 (holds response content in memory; team-scoped keys).
var cacheConfig = new CacheConfig
{
    TtlSeconds = int.TryParse(builder.Configuration["Cache:TtlSeconds"], out var cttl) ? cttl : 0,
    MaxEntries = int.TryParse(builder.Configuration["Cache:MaxEntries"], out var cmax) ? cmax : 1_000,
};
var responseCache = new ResponseCache();

var auditSink = new FileAuditSink(auditPath);
var killSwitch = new KillSwitch();
var spendLedger = new InMemorySpendLedger();

// Budgets survive restarts: replay the audit log (event store) into the ledger (projection).
// A tampered chain at boot is fail-closed — never serve on top of corrupted evidence.
var (chainAtBoot, priorEntries) = AuditChainVerifier.VerifyAndRead(auditPath);
if (!chainAtBoot.Valid)
{
    Console.Error.WriteLine(
        $"covenant: audit chain INVALID at line {chainAtBoot.FirstInvalidLine} ({chainAtBoot.Failure}).");
    Console.Error.WriteLine(
        "Refusing to start (fail-closed). Archive the log for investigation — do not delete evidence. " +
        "Point Audit:Path at a fresh file to resume serving.");
    Environment.Exit(1);
}
LedgerReplay.Rebuild(priorEntries, spendLedger);
var budget = new BudgetConfig { GlobalCapUsd = globalCapUsd, TeamCapsUsd = teamCapsUsd };

// --- Pipeline assembly. Outermost first; audit wraps everything; order per src/CLAUDE.md. ---
var pipeline = new InferencePipeline(
[
    new AuditStage(auditSink, promptPreviewChars:    // outermost: audits allow, deny, and error alike
        int.TryParse(builder.Configuration["Audit:PromptPreviewChars"], out var ppc) ? ppc : 0),
    new AuthStage(new AuthConfig { AllowAnonymous = allowAnonymous, Keys = apiKeys }), // auth first
    new ClassifyStage(new RegexDataClassifier()),    // classify
    new PolicyStage(new PolicyEngine(policy, new RoutingOptions
    {
        ComplexityTokenThreshold = complexityThreshold,
    })),                                             // policy + complexity routing, fail-closed
    new CacheStage(responseCache, cacheConfig),      // cache before budget/provider: a hit skips the model call
    new BudgetStage(killSwitch, spendLedger, budget),// kill switch + caps, before anything costs money
    new ProviderCallStage(registry),                 // route + provider call
    new AttributionStage(prices),                    // attribute cost
]);

builder.Services.AddSingleton(pipeline);
builder.Services.AddHostedService(_ => auditSink);   // drains the audit channel in the background

// ADR-0006: evidence graph — optional, in-perimeter, never load-bearing. No Neo4j:Uri → not registered.
if (builder.Configuration["Neo4j:Uri"] is { Length: > 0 } neo4jUri)
{
    builder.Services.AddHostedService(_ => new EvidenceGraphProjector(
        auditPath,
        neo4jUri,
        builder.Configuration["Neo4j:User"] ?? "neo4j",
        builder.Configuration["Neo4j:Password"] ?? ""));
}

var app = builder.Build();

// Model discovery for OpenAI-compatible clients (Open WebUI, SDKs). Same auth semantics as chat:
// a valid API key, or anonymous only if explicitly allowed. Only policy-permitted models are listed.
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

    var models = policy.AllowedRoutes.Values
        .SelectMany(routes => routes)
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
        Messages: body.Messages.Select(m => new ChatMessage(ParseRole(m.Role), m.Content)).ToList(),
        RequestedModel: body.Model,
        Attribution: tags,
        Stream: body.Stream == true,
        Credential: credential);

    var ctx = new InferenceContext(request);

    // --- Streamed mode (ADR-0002): SSE headers commit lazily on the FIRST delta, so a pre-flight denial still returns a plain JSON error below. ---
    if (request.Stream)
    {
        var resp = http.Response;
        var chunkId = $"chatcmpl-{Guid.NewGuid():n}";
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
                new ErrorResponse { Error = "upstream_error", Reason = ctx.DenialReason ?? "stream failed" },
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
        Model = r.ServedByModel,
        Choices = [new OpenAiChoice { Index = 0, Message = new OpenAiMessage { Role = "assistant", Content = r.Message.Content } }],
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
        _ => ("denied", StatusCodes.Status403Forbidden),
    };
    return Results.Json(
        new ErrorResponse { Error = error, Reason = ctx.DenialReason ?? "no response" },
        CovenantJsonContext.Default.ErrorResponse, statusCode: status);
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

    var report = EvidenceExport.Build(auditPath, TimeProvider.System);
    return Results.Json(report, CovenantJsonContext.Default.EvidenceReport);
});

// Dashboard data: config + live ledger + audit aggregates. Read-only — the dashboard is a view,
// never a second source of truth.
var startedUtc = DateTimeOffset.UtcNow;

StatusReport BuildStatus()
{
    var (verification, entries) = AuditChainVerifier.VerifyAndRead(auditPath);
    var teamSpend = spendLedger.SnapshotByTeam();

    var teams = new List<TeamBudgetStatus>();
    foreach (var (team, cap) in teamCapsUsd)
        teams.Add(new TeamBudgetStatus { Team = team, CapUsd = cap, SpendUsd = teamSpend.GetValueOrDefault(team) });
    foreach (var (team, spend) in teamSpend)
        if (!teamCapsUsd.ContainsKey(team))
            teams.Add(new TeamBudgetStatus { Team = team, CapUsd = null, SpendUsd = spend });

    var routes = new List<RouteView>();
    foreach (var (cls, targets) in policy.AllowedRoutes)
        foreach (var t in targets)
            routes.Add(new RouteView { Classification = cls.ToString(), Adapter = t.AdapterKey, Model = t.ModelId });

    return new StatusReport
    {
        GeneratedUtc = DateTimeOffset.UtcNow,
        StartedUtc = startedUtc,
        KillSwitch = new KillSwitchState { Engaged = killSwitch.IsTripped, Reason = killSwitch.Reason },
        Budget = new BudgetStatus
        {
            GlobalCapUsd = globalCapUsd,
            GlobalSpendUsd = spendLedger.GlobalSpendUsd,
            Teams = teams,
        },
        Routes = routes,
        FinOps = FinOps.Build(entries, localModelId, strongModelId, priceMap),
        ChainValid = verification.Valid,
        AuditEntries = verification.EntryCount,
        Auth = new AuthStatus { AllowAnonymous = allowAnonymous, KeyCount = apiKeys.Count },
        OtelEnabled = otelEnabled,
        RoutingThresholdTokens = complexityThreshold,
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

// The dashboard: one self-contained embedded page, no CDN, no egress (root CLAUDE.md #4). Data calls
// need the token; no-store because a stale cached page against a newer endpoint dies silently.
app.MapGet("/admin/ui", (HttpContext http) =>
{
    http.Response.Headers.CacheControl = "no-store";
    return Results.Content(AdminUi.Html, "text/html");
});

app.Run();

static ChatRole ParseRole(string role) => role.ToLowerInvariant() switch
{
    "system" => ChatRole.System,
    "assistant" => ChatRole.Assistant,
    "tool" => ChatRole.Tool,
    _ => ChatRole.User
};

public partial class Program; // exposed for the test project

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
// Canonical types win the name collision with Microsoft.Extensions.AI (which stays imported
// for IChatClient and the AsIChatClient extension). Provider shapes belong in adapters only.
using ChatMessage = Covenant.Core.ChatMessage;
using ChatRole = Covenant.Core.ChatRole;

var builder = WebApplication.CreateBuilder(args);

// --- Configuration (fail-closed: missing or invalid required values → refuse to start, with every
//     problem reported at once and no stack trace — a refusal is a diagnostic, not a crash) ---
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

// Source-generated JSON for request binding + responses (AOT-safe).
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.TypeInfoResolverChain.Insert(0, CovenantJsonContext.Default));

// --- Provider layer (ADR-0001: adapters over Microsoft.Extensions.AI) ---
// OpenAI:Endpoint / OpenAI:ModelId are optional overrides: point the "openai" adapter at any
// OpenAI-compatible server (e.g. Ollama) for a fully offline demo. Governance doesn't change.
string publicModelId = builder.Configuration["OpenAI:ModelId"] ?? "gpt-4o-mini";
IChatClient openAi = (builder.Configuration["OpenAI:Endpoint"] is { Length: > 0 } openAiEndpoint
        ? new OpenAIClient(new ApiKeyCredential(openAiKey!), new OpenAIClientOptions { Endpoint = new Uri(openAiEndpoint) })
        : new OpenAIClient(openAiKey!))
    .GetChatClient(publicModelId).AsIChatClient();
var clients = new Dictionary<string, IChatClient> { ["openai"] = openAi };

// Optional in-perimeter target (vLLM, Ollama — any OpenAI-compatible endpoint). Not configured →
// "local" stays unregistered and PII/PHI keep failing closed at the provider stage.
string localModelId = builder.Configuration["Local:ModelId"] ?? "llama-3.1-8b-instruct";
if (builder.Configuration["Local:Endpoint"] is { Length: > 0 } localEndpoint)
{
    clients["local"] = new OpenAIClient(
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
        [DataClassification.Public]   = [new("openai", publicModelId)],
        [DataClassification.Internal] = [new("openai", publicModelId)],
        [DataClassification.Pii]      = [new("local", localModelId)],
        [DataClassification.Phi]      = [new("local", localModelId)],
    }
};

var publicPrice = (InPer1K: 0.15m, OutPer1K: 0.60m);  // illustrative USD per 1K tokens
var prices = new PriceBook(new Dictionary<string, (decimal, decimal)>
{
    [publicModelId] = publicPrice,
    [localModelId] = (0m, 0m),                 // in-perimeter compute: no per-token provider cost
});

var auditSink = new FileAuditSink(auditPath);
var killSwitch = new KillSwitch();
var spendLedger = new InMemorySpendLedger();
var budget = new BudgetConfig { GlobalCapUsd = globalCapUsd, TeamCapsUsd = teamCapsUsd };

// --- Pipeline assembly. Outermost first; audit wraps everything; order per src/CLAUDE.md. ---
var pipeline = new InferencePipeline(
[
    new AuditStage(auditSink),                       // outermost: audits allow, deny, and error alike
    new ClassifyStage(new RegexDataClassifier()),    // classify
    new PolicyStage(new PolicyEngine(policy)),       // policy (routes by classification, fail-closed)
    new BudgetStage(killSwitch, spendLedger, budget),// kill switch + caps, before anything costs money
    new ProviderCallStage(registry),                 // route + provider call
    new AttributionStage(prices),                    // attribute cost
]);

builder.Services.AddSingleton(pipeline);
builder.Services.AddHostedService(_ => auditSink);   // drains the audit channel in the background

var app = builder.Build();

app.MapPost("/v1/chat/completions",
    async (OpenAiChatRequest body, InferencePipeline pipe, HttpContext http, CancellationToken ct) =>
{
    var principal = http.Request.Headers["X-Covenant-Principal"].FirstOrDefault() ?? "anonymous";
    var tags = new AttributionTags(
        Team: http.Request.Headers["X-Covenant-Team"].FirstOrDefault() ?? "unknown",
        Workflow: http.Request.Headers["X-Covenant-Workflow"].FirstOrDefault() ?? "unknown",
        UseCase: http.Request.Headers["X-Covenant-UseCase"].FirstOrDefault() ?? "unknown");

    var request = new InferenceRequest(
        Principal: principal,
        Messages: body.Messages.Select(m => new ChatMessage(ParseRole(m.Role), m.Content)).ToList(),
        RequestedModel: body.Model,
        Attribution: tags);

    var ctx = new InferenceContext(request);
    await pipe.ExecuteAsync(ctx, ct);

    if (ctx.IsDenied || ctx.Response is null)
    {
        // Governance said no → 403. Upstream broke and we refused fail-closed → 502.
        bool upstream = ctx.DenialKind == DenialKind.UpstreamFailure;
        return Results.Json(
            new ErrorResponse
            {
                Error = upstream ? "upstream_error" : "denied",
                Reason = ctx.DenialReason ?? "no response"
            },
            CovenantJsonContext.Default.ErrorResponse,
            statusCode: upstream ? StatusCodes.Status502BadGateway : StatusCodes.Status403Forbidden);
    }

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
app.MapGet("/admin/status", (HttpContext http) =>
{
    if (!Authorized(http)) return Unauthorized();

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

    var report = new StatusReport
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
        FinOps = FinOps.Build(entries, localModelId, publicPrice),
        ChainValid = verification.Valid,
        AuditEntries = verification.EntryCount,
    };
    return Results.Json(report, CovenantJsonContext.Default.StatusReport);
});

// The dashboard itself: one self-contained embedded page, no CDN, no external assets — the
// appliance stays a single artifact with no egress (root CLAUDE.md #4). Data calls need the token.
app.MapGet("/admin/ui", () => Results.Content(AdminUi.Html, "text/html"));

app.Run();

static ChatRole ParseRole(string role) => role.ToLowerInvariant() switch
{
    "system" => ChatRole.System,
    "assistant" => ChatRole.Assistant,
    "tool" => ChatRole.Tool,
    _ => ChatRole.User
};

public partial class Program; // exposed for the test project

using System.Text.Json;
using Covenant.Core;
using Covenant.Governance;

namespace Covenant.Host;

/// <summary>Binds one request to the active configuration snapshot for its whole lifetime (ADR-0011): acquired
/// once at entry, released when the handler finishes (after the stream is fully written).</summary>
public sealed class SnapshotLease : IDisposable
{
    private RuntimeSnapshot? _snapshot;

    private SnapshotLease(RuntimeSnapshot snapshot) => _snapshot = snapshot;

    public static SnapshotLease Bind(IRuntimeConfig runtime, InferenceContext ctx)
    {
        var snapshot = runtime.Acquire();
        ctx.Configuration = snapshot;
        ctx.ConfigVersion = snapshot.Version;
        return new SnapshotLease(snapshot);
    }

    public void Dispose() => Interlocked.Exchange(ref _snapshot, null)?.Release();
}

/// <summary>Everything the configuration admin API needs, composed by Program.</summary>
public sealed class ConfigAdminServices
{
    public required ConfigRepository Repository { get; init; }
    public required IRuntimeConfig Runtime { get; init; }
    public required Func<CovenantConfig, ConfigValidationResult> Validate { get; init; }
    public required Func<CovenantConfig, RuntimeSnapshot> Compile { get; init; }
    public required Func<string, bool> SecretAvailable { get; init; }
    public required IReadOnlySet<string> EgressAllowList { get; init; }
    public required IAuditEventWriter AuditEvents { get; init; }
    public required IDataClassifier Classifier { get; init; }
    public required Func<HttpContext, bool> Authorized { get; init; }
    public TimeProvider Clock { get; init; } = TimeProvider.System;
}

/// <summary>docs/ui-api-contract.md v2: /admin/config*, /admin/applications, /admin/policies/simulate.
/// Bodies are read and written with ConfigJsonContext (snake_case) explicitly. Secret VALUES never appear in any
/// payload — only references and availability.</summary>
public static class ConfigAdminApi
{
    private static IResult Json<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info, int status = StatusCodes.Status200OK)
        => Results.Json(value, info, statusCode: status);

    private static IResult Error(string error, string? reason, int status, int? activeVersion = null,
        ConfigValidationResult? validation = null)
        => Json(new ConfigErrorResponse
        {
            Error = error,
            Reason = reason,
            ActiveVersion = activeVersion,
            Errors = validation is null ? null : ConfigIssueView.From(validation.Errors),
            Warnings = validation is null ? null : ConfigIssueView.From(validation.Warnings),
        }, ConfigJsonContext.Default.ConfigErrorResponse, status);

    private static IResult Unauthorized() => Error("unauthorized", "invalid admin token", StatusCodes.Status401Unauthorized);

    private static async Task<(T? Value, IResult? Failure)> ReadAsync<T>(HttpContext http,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info, CancellationToken ct) where T : class
    {
        try
        {
            var value = await JsonSerializer.DeserializeAsync(http.Request.Body, info, ct);
            if (value is null) return (null, Error("invalid_request", "request body is required", StatusCodes.Status400BadRequest));
            return (value, null);
        }
        catch (JsonException ex)
        {
            // Bounded, no echo of the payload: the path/line is enough to fix a malformed body.
            var where = ex.Path is { Length: > 0 } p ? $" at {Evidence.Bounded(p)}" : "";
            IResult bad = Error("invalid_request", $"malformed or incomplete JSON{where}", StatusCodes.Status400BadRequest);
            return (null, bad);
        }
    }

    public static void MapConfigAdmin(this WebApplication app, ConfigAdminServices s)
    {
        app.MapGet("/admin/config", (HttpContext http) =>
        {
            if (!s.Authorized(http)) return Unauthorized();
            http.Response.Headers.CacheControl = "no-store";
            var cfg = s.Runtime.Current.Config;
            var etag = ConfigRepository.ETag(cfg.Version);
            http.Response.Headers.ETag = etag;
            return Json(new ConfigGetResponse
            {
                Version = cfg.Version,
                Etag = etag,
                Config = cfg,
                Secrets = SecretsOf(cfg, s.SecretAvailable),
                AllowList = s.EgressAllowList.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            }, ConfigJsonContext.Default.ConfigGetResponse);
        });

        // Never mutates: validation + impact against the active version.
        app.MapPost("/admin/config/validate", async Task<IResult> (HttpContext http, CancellationToken ct) =>
        {
            if (!s.Authorized(http)) return Unauthorized();
            var (body, failure) = await ReadAsync(http, ConfigJsonContext.Default.ConfigValidateRequest, ct);
            if (failure is not null) return failure;
            if (body!.Config is not { } candidate)
                return Error("invalid_request", "config is required", StatusCodes.Status400BadRequest);
            var active = s.Runtime.Current;
            var validation = s.Validate(candidate);
            var errors = validation.Errors.ToList();
            var warnings = validation.Warnings.ToList();
            if (body.BaseVersion is { } bv && bv != active.Version)
                warnings.Add(new ConfigIssue("base_version", "stale_base_version",
                    $"candidate was edited from v{bv}; the active version is v{active.Version} — applying with If-Match \"v{bv}\" will be rejected (412)"));
            if (validation.IsValid)
            {
                // Same compile step as apply (provider clients included, no network I/O): a client that cannot be
                // built for an enabled, referenced connection is reported here too (client_unbuildable).
                try
                {
                    s.Compile(candidate).Release();
                }
                catch (ConfigCompileException ex)
                {
                    errors.AddRange(ex.Issues);
                }
            }
            // A structurally broken candidate (null required value / undefined enum) cannot be compiled even for
            // a preview: report the errors with their paths, no impact — never an exception or a 500.
            ImpactView? impact = validation.StructurallySound
                ? ImpactView.From(ImpactAnalyzer.Analyze(active, new RuntimeSnapshot(candidate), s.SecretAvailable))
                : null;
            return Json(new ConfigValidateResponse
            {
                Valid = errors.Count == 0,
                ActiveVersion = active.Version,
                Errors = ConfigIssueView.From(errors),
                Warnings = ConfigIssueView.From(warnings),
                Impact = impact,
            }, ConfigJsonContext.Default.ConfigValidateResponse);
        });

        app.MapPut("/admin/config", async Task<IResult> (HttpContext http, CancellationToken ct) =>
        {
            if (!s.Authorized(http)) return Unauthorized();
            var ifMatch = http.Request.Headers.IfMatch.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(ifMatch))
                return Error("precondition_required", "If-Match: \"v<N>\" (the version you edited) is required", StatusCodes.Status428PreconditionRequired,
                    s.Runtime.Current.Version);
            if (ConfigRepository.ParseETag(ifMatch) is not { } expected)
                return Error("invalid_if_match", "If-Match must be an ETag of the form \"v<N>\"", StatusCodes.Status400BadRequest, s.Runtime.Current.Version);
            var (body, failure) = await ReadAsync(http, ConfigJsonContext.Default.ConfigPutRequest, ct);
            if (failure is not null) return failure;
            if (body!.Config is not { } candidate)
                return Error("invalid_request", "config is required", StatusCodes.Status400BadRequest);
            return await ApplyAsync(http, s, candidate, expected, "console", body.ChangeSummary, null, ct);
        });

        app.MapGet("/admin/config/versions", (HttpContext http, int? limit) =>
        {
            if (!s.Authorized(http)) return Unauthorized();
            http.Response.Headers.CacheControl = "no-store";
            int activeVersion = s.Runtime.Current.Version;
            var list = s.Repository.ListVersions(Math.Clamp(limit ?? 50, 1, 500)).Select(c => new ConfigVersionSummary
            {
                Version = c.Version,
                CreatedUtc = c.CreatedUtc,
                Origin = c.Origin,
                BaseVersion = c.BaseVersion,
                RolledBackFrom = c.RolledBackFrom,
                ChangeSummary = c.ChangeSummary,
                Active = c.Version == activeVersion,
            }).ToList();
            return Json(list, ConfigJsonContext.Default.ListConfigVersionSummary);
        });

        app.MapGet("/admin/config/versions/{n:int}", (HttpContext http, int n) =>
        {
            if (!s.Authorized(http)) return Unauthorized();
            http.Response.Headers.CacheControl = "no-store";
            // Journal-only (ADR-0011): an orphan (written, never activated) is 404 exactly like an unknown number.
            return s.Repository.GetVersion(n) is { } c
                ? Json(c, ConfigJsonContext.Default.CovenantConfig)
                : Error("not_found", $"no activated configuration version {n}", StatusCodes.Status404NotFound);
        });

        // Rollback = validate the historical snapshot as a NEW candidate and activate it as version M+1.
        app.MapPost("/admin/config/rollback", async Task<IResult> (HttpContext http, CancellationToken ct) =>
        {
            if (!s.Authorized(http)) return Unauthorized();
            var (body, failure) = await ReadAsync(http, ConfigJsonContext.Default.ConfigRollbackRequest, ct);
            if (failure is not null) return failure;
            var active = s.Runtime.Current.Version;
            // Only activated versions (journal entries) can be rolled back to; orphans and unknown numbers are 404.
            if (s.Repository.GetVersion(body!.TargetVersion) is not { } target)
                return Error("not_found", $"no activated configuration version {body.TargetVersion}", StatusCodes.Status404NotFound, active);
            return await ApplyAsync(http, s, target, body.ExpectedActiveVersion, "rollback",
                $"rollback to v{body.TargetVersion}", body.TargetVersion, ct);
        });

        app.MapGet("/admin/applications", (HttpContext http) =>
        {
            if (!s.Authorized(http)) return Unauthorized();
            http.Response.Headers.CacheControl = "no-store";
            var list = s.Runtime.Current.Config.Applications.Select(a => new ApplicationView
            {
                Id = a.Id,
                Team = a.Team,
                Enabled = a.Enabled,
                ConsoleTesting = a.ConsoleTesting,
                GrantedProfiles = a.GrantedProfiles.ToList(),
                DefaultProfile = a.DefaultProfile,
                DisplayName = a.DisplayName,
                Description = a.Description,
                Budgets = s.Runtime.Current.Config.Budgets
                    .Where(b => b.Scope == "global" || b.Scope == "team:" + a.Team || b.Scope == "application:" + a.Id)
                    .Select(b => b.Id).ToList(),
            }).ToList();
            return Json(list, ConfigJsonContext.Default.ListApplicationView);
        });

        // Dry run with the REAL classifier and policy engine. Never calls a provider, never writes audit,
        // never reserves or records spend.
        app.MapPost("/admin/policies/simulate", async Task<IResult> (HttpContext http, CancellationToken ct) =>
        {
            if (!s.Authorized(http)) return Unauthorized();
            var (body, failure) = await ReadAsync(http, ConfigJsonContext.Default.SimulateRequest, ct);
            if (failure is not null) return failure;
            if (string.IsNullOrWhiteSpace(body!.ApplicationId))
                return Error("invalid_request", "application_id is required", StatusCodes.Status400BadRequest);

            IRequestConfiguration cfg = s.Runtime.Current;
            if (body.Config is { } candidate)
            {
                var v = s.Validate(candidate);
                if (!v.IsValid)
                    return Error("invalid_configuration", "the supplied candidate does not validate", StatusCodes.Status422UnprocessableEntity,
                        s.Runtime.Current.Version, v);
                cfg = new RuntimeSnapshot(candidate);   // no provider clients: a simulation cannot call anything
            }
            return Json(Simulate(cfg, s.Classifier, body), ConfigJsonContext.Default.SimulateResponse);
        });
    }

    public static SimulateResponse Simulate(IRequestConfiguration cfg, IDataClassifier classifier, SimulateRequest body)
    {
        var appId = body.ApplicationId ?? "";
        var app = cfg.FindApplication(appId);
        SimulateResponse Refuse(string reason, string? profile = null, DataClassification? baseline = null) => new()
        {
            DryRun = true, ConfigVersion = cfg.Version, ApplicationId = appId, Profile = profile,
            Classification = "Not evaluated", Baseline = baseline?.ToString() ?? "Not evaluated",
            Eligible = [], Decision = "refuse", Fallbacks = [], Reason = reason,
        };
        if (app is null) return Refuse($"application '{Evidence.Bounded(appId)}' is not configured");
        if (!app.Enabled) return Refuse($"application '{app.Id}' disabled");

        var resolved = ProfileResolution.Resolve(cfg, app, body.ProfileId);
        if (resolved.Denial is { } denial) return Refuse(denial);

        var messages = new List<ChatMessage> { new(ChatRole.User, body.Prompt ?? "") };
        var request = new InferenceRequest(app.Id, messages, body.Model, new AttributionTags(app.Team, "simulation", "simulation"));
        var detected = classifier.Classify(request);
        var final = ClassificationRules.Combine(resolved.Baseline, detected);
        var outcome = PolicyEngineV2.Evaluate(cfg, final, resolved.ProfileId, body.Model, messages);

        var eligible = new List<SimulatedTarget>();
        var policy = outcome.PolicyId is { } pid ? cfg.Config.Policies.FirstOrDefault(p => p.Id == pid) : null;
        if (policy is not null)
            foreach (var t in policy.Targets)
            {
                var excluded = outcome.Excluded.FirstOrDefault(x => x.Target.AdapterKey == t.ConnectionId && x.Target.ModelId == t.ModelId);
                var boundary = cfg.FindConnection(t.ConnectionId)?.Boundary.ToString() ?? "Unknown";
                eligible.Add(new SimulatedTarget
                {
                    Target = $"{t.ConnectionId}/{t.ModelId}",
                    Boundary = boundary,
                    ExcludedReason = excluded?.Reason
                        ?? (outcome.Effect == PolicyEffect.Allow && !outcome.Candidates.Any(c => c.AdapterKey == t.ConnectionId && c.ModelId == t.ModelId)
                            ? "not selected (model override pins one target)" : null),
                });
            }

        bool route = outcome.Effect == PolicyEffect.Allow && outcome.Route is not null;
        return new SimulateResponse
        {
            DryRun = true,
            ConfigVersion = cfg.Version,
            ApplicationId = app.Id,
            Profile = resolved.ProfileId,
            Classification = final.ToString(),
            Baseline = resolved.Baseline.ToString(),
            DetectedSignal = detected.Signal,
            PolicyId = outcome.PolicyId,
            Eligible = eligible,
            Decision = route ? "route" : "refuse",
            Primary = route ? $"{outcome.Route!.AdapterKey}/{outcome.Route.ModelId}" : null,
            Fallbacks = route ? outcome.Candidates.Skip(1).Select(c => $"{c.AdapterKey}/{c.ModelId}").ToList() : [],
            Reason = outcome.Reason,
            Explanation = outcome.Explanation,
        };
    }

    private static async Task<IResult> ApplyAsync(HttpContext http, ConfigAdminServices s, CovenantConfig candidate, int expected,
        string origin, string? summary, int? rolledBackFrom, CancellationToken ct)
    {
        var result = await s.Repository.ApplyAsync(candidate, expected, origin, summary, rolledBackFrom,
            s.Validate,
            s.Compile,
            (applied, snapshot) =>
            {
                s.Runtime.Swap(snapshot);   // the previous snapshot is disposed when its last request releases it
                s.AuditEvents.TryWrite(ConfigAuditEvents.Activated(applied, ConfigAuditEvents.SharedAdminOperator, s.Clock.GetUtcNow()));
            },
            snapshot => snapshot.Release(),
            ct);

        switch (result.Status)
        {
            case ApplyStatus.Stale:
                return Error("stale", $"the active configuration is v{result.ActiveVersion}; reload and re-apply your edit",
                    StatusCodes.Status412PreconditionFailed, result.ActiveVersion);
            case ApplyStatus.Invalid:
                return Error("invalid_configuration", "validation failed; the active configuration is unchanged",
                    StatusCodes.Status422UnprocessableEntity, result.ActiveVersion, result.Validation);
            default:
                var etag = ConfigRepository.ETag(result.ActiveVersion);
                http.Response.Headers.ETag = etag;
                return Json(new ConfigPutResponse { Version = result.ActiveVersion, Etag = etag }, ConfigJsonContext.Default.ConfigPutResponse);
        }
    }

    public static Dictionary<string, SecretStatus> SecretsOf(CovenantConfig cfg, Func<string, bool> available)
    {
        var map = new Dictionary<string, SecretStatus>(StringComparer.Ordinal);
        foreach (var c in cfg.Connections)
            if (c.SecretRef is { } r && !map.ContainsKey(r))
                map[r] = new SecretStatus { Available = available(r) };
        return map;
    }
}

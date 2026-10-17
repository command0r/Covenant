using System.Security.Cryptography;
using System.Text;
using Covenant.Core;

namespace Covenant.Governance;

/// <summary>One virtual API key: the opaque key value and the identity it resolves to. Keys come from
/// config (dev: user-secrets; prod: the customer's vault) — never from the repo or image.</summary>
/// <param name="ApplicationId">Application this key binds to (Auth:Keys:N:Application; default = Principal, ADR-0012).</param>
public sealed record ApiKeyRecord(string Key, string Principal, string Team, string? ApplicationId = null)
{
    /// <summary>The configuration application id this key binds to (normalized exactly like the legacy import).</summary>
    public string Application => ApplicationIds.FromKey(ApplicationId ?? Principal);
}

/// <summary>Key → application id (ADR-0012). A valid configuration id is used verbatim; anything else (e.g. an
/// e-mail principal) maps deterministically to "&lt;sanitized&gt;-&lt;8 hex of SHA-256&gt;" (≤64 chars), so the legacy
/// import can create one application for EVERY key and the auth stage finds it — no key is left unbound.</summary>
public static class ApplicationIds
{
    public static string FromKey(string raw)
    {
        if (ConfigValidator.IsValidId(raw)) return raw;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw ?? ""))).ToLowerInvariant()[..8];
        var sb = new StringBuilder();
        foreach (var ch in raw ?? "")
            sb.Append(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '-');
        var clean = sb.ToString().Trim('-');
        if (clean.Length == 0) return "app-" + hash;
        return (clean.Length > 55 ? clean[..55] : clean) + "-" + hash;
    }
}

public sealed class AuthConfig
{
    /// <summary>Anonymous access must be an explicit opt-in. Default posture: no key, no service.</summary>
    public required bool AllowAnonymous { get; init; }
    public required IReadOnlyList<ApiKeyRecord> Keys { get; init; }
}

/// <summary>Auth stage (order in src/CLAUDE.md), fail-closed: a valid key overwrites caller identity; an unknown key is denied even when anonymous is allowed; no key is denied unless AllowAnonymous is explicit.
/// The raw credential is consumed here and never reaches audit, spans, or logs.</summary>
public sealed class AuthStage : IPipelineStage
{
    private readonly AuthConfig _config;
    private readonly Dictionary<string, ApiKeyRecord> _byKey;

    public AuthStage(AuthConfig config)
    {
        _config = config;
        _byKey = new Dictionary<string, ApiKeyRecord>(StringComparer.Ordinal);
        foreach (var k in config.Keys) _byKey[k.Key] = k;
    }

    public const string ProfileHeader = "X-Covenant-Profile";

    public async Task InvokeAsync(InferenceContext ctx, PipelineDelegate next, CancellationToken ct)
    {
        var credential = ctx.Request.Credential;

        // Console entry point (ADR-0012): identity was resolved server-side by the Host from an authorized
        // admin session; no application key exists in the browser. Still enforced here: application enabled,
        // console testing allowed, profile granted — governed, audited refusals.
        if (ctx.PreAuthenticated is { } pre)
        {
            if (ctx.Configuration is not { } cfg)
            {
                ctx.Deny("console identity requires a runtime configuration", DenialKind.Unauthenticated);
                return;
            }
            ctx.Identity = new CallerIdentity(pre.Principal, ctx.Request.Attribution with { Team = pre.Team });
            var app = cfg.FindApplication(pre.ApplicationId);
            if (app is null) { ctx.Deny($"application '{Evidence.Bounded(pre.ApplicationId)}' is not configured"); return; }
            ctx.ApplicationId = app.Id;
            ctx.Identity = new CallerIdentity(pre.Principal, ctx.Request.Attribution with { Team = app.Team });
            if (!app.Enabled) { ctx.Deny($"application '{app.Id}' disabled"); return; }
            if (!app.ConsoleTesting) { ctx.Deny($"application '{app.Id}' is not enabled for console testing"); return; }
            if (!ResolveProfile(ctx, cfg, app)) return;
            await next(ctx, ct);
            return;
        }

        if (credential is { Length: > 0 })
        {
            if (_byKey.TryGetValue(credential, out var record))
            {
                ctx.Identity = new CallerIdentity(
                    record.Principal,
                    ctx.Request.Attribution with { Team = record.Team });
                if (ctx.Configuration is { } cfg)
                {
                    // Key → application (ADR-0012). /v1 key authentication itself is unchanged (team from the key);
                    // the application adds enabled state and profile grants. Fail-closed: a key whose application
                    // is missing from the active configuration (removed, renamed, never added) is refused — there is
                    // no implicit legacy fallback once a configuration governs.
                    var app = cfg.FindApplication(record.Application);
                    if (app is null)
                    {
                        ctx.Deny($"application not configured: '{Evidence.Bounded(record.Application)}' is not in configuration v{cfg.Version}");
                        return;
                    }
                    ctx.ApplicationId = app.Id;
                    // Review round 2 (B6): once a configuration governs, the key only RESOLVES the application; the
                    // identity's team is the ACTIVE application's team — exactly like the console entry point — so a
                    // team change applied through configuration governs new charges on both entry points. Historical
                    // charges keep the scopes recorded at reservation time in the ledger.
                    ctx.Identity = new CallerIdentity(record.Principal, ctx.Request.Attribution with { Team = app.Team });
                    if (!app.Enabled) { ctx.Deny($"application '{app.Id}' disabled"); return; }
                    if (!ResolveProfile(ctx, cfg, app)) return;
                }
                await next(ctx, ct);
                return;
            }

            ctx.Deny("unknown API key", DenialKind.Unauthenticated);
            return;
        }

        if (_config.AllowAnonymous)
        {
            // identity stays as the request's self-declared values; no application ⇒ no grants.
            if (ctx.Configuration is { } cfg && !ResolveProfile(ctx, cfg, null)) return;
            await next(ctx, ct);
            return;
        }

        ctx.Deny("authentication required: no API key presented", DenialKind.Unauthenticated);
    }

    private static bool ResolveProfile(InferenceContext ctx, IRequestConfiguration cfg, ApplicationConfig? app)
    {
        var r = ProfileResolution.Resolve(cfg, app, ctx.Request.RequestedProfile);
        if (r.Denial is { } denial) { ctx.Deny(denial); return false; }
        ctx.ProfileId = r.ProfileId;
        ctx.BaselineClassification = r.Baseline;
        return true;
    }
}

/// <summary>Workload-profile resolution (ADR-0012), shared by the auth stage and the policy simulator.
/// Requested profile must be granted to the application; none requested ⇒ the application's default ⇒
/// legacy-internal (baseline Internal). The baseline comes only from server configuration, never the client.</summary>
public static class ProfileResolution
{
    public readonly record struct Result(string? ProfileId, DataClassification Baseline, string? Denial);

    public static Result Resolve(IRequestConfiguration cfg, ApplicationConfig? app, string? requestedProfile)
    {
        var requested = requestedProfile is { Length: > 0 } r && r.Trim().Length > 0 ? r.Trim() : null;
        string profileId;
        if (requested is not null)
        {
            if (app is null || !app.GrantedProfiles.Contains(requested, StringComparer.Ordinal))
                return new Result(null, DataClassification.Phi, app is null
                    ? $"profile '{Evidence.Bounded(requested)}' not granted (no configured application for this caller)"
                    : $"profile '{Evidence.Bounded(requested)}' not granted to application '{app.Id}'");
            profileId = requested;
        }
        else
        {
            profileId = app?.DefaultProfile ?? PolicyEngineV2.LegacyInternalProfile;
        }

        var profile = cfg.FindProfile(profileId);
        if (profile is null && profileId != PolicyEngineV2.LegacyInternalProfile)
            return new Result(null, DataClassification.Phi, $"profile '{Evidence.Bounded(profileId)}' is not configured"); // never guess a baseline
        return new Result(profileId, profile?.Baseline ?? DataClassification.Internal, null);
    }
}

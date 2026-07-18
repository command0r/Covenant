using Covenant.Core;

namespace Covenant.Governance;

/// <summary>One virtual API key: the opaque key value and the identity it resolves to. Keys come from
/// config (dev: user-secrets; prod: the customer's vault) — never from the repo or image.</summary>
public sealed record ApiKeyRecord(string Key, string Principal, string Team);

public sealed class AuthConfig
{
    /// <summary>Anonymous access must be an explicit opt-in. Default posture: no key, no service.</summary>
    public required bool AllowAnonymous { get; init; }
    public required IReadOnlyList<ApiKeyRecord> Keys { get; init; }
}

/// <summary>
/// Pipeline stage: authentication (first stage after audit — canonical order in src/CLAUDE.md).
/// A valid key overwrites the caller identity with the key's principal and team — client-supplied
/// headers become hints that a key can override, never authority. Fail-closed:
///  - a presented-but-unknown key is denied even when anonymous access is allowed (a wrong credential
///    is an authentication failure, not an anonymous request);
///  - no key is denied unless AllowAnonymous was explicitly configured.
/// The raw credential is consumed here and goes nowhere else — not into audit, spans, or logs.
/// </summary>
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

    public async Task InvokeAsync(InferenceContext ctx, PipelineDelegate next, CancellationToken ct)
    {
        var credential = ctx.Request.Credential;

        if (credential is { Length: > 0 })
        {
            if (_byKey.TryGetValue(credential, out var record))
            {
                ctx.Identity = new CallerIdentity(
                    record.Principal,
                    ctx.Request.Attribution with { Team = record.Team });
                await next(ctx, ct);
                return;
            }

            ctx.Deny("unknown API key", DenialKind.Unauthenticated);
            return;
        }

        if (_config.AllowAnonymous)
        {
            await next(ctx, ct); // identity stays as the request's self-declared values
            return;
        }

        ctx.Deny("authentication required: no API key presented", DenialKind.Unauthenticated);
    }
}

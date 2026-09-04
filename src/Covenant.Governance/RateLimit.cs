using Covenant.Core;

namespace Covenant.Governance;

/// <summary>Requests-per-minute caps; 0 = unlimited (opt-in). Global cap bounds the appliance, team caps refine within it.</summary>
public sealed class RateLimitConfig
{
    public int GlobalPerMinute { get; init; }
    public int PerTeamPerMinute { get; init; }
}

/// <summary>Fixed-minute-window counters (deliberate first slice: deterministic, testable, O(1)); a boundary burst can briefly reach 2× the cap.</summary>
public sealed class RateCounter(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);
    private long _windowMinute = -1;

    /// <summary>Counts the request against a key and returns the new total within the current minute window.</summary>
    public int Increment(string key)
    {
        var minute = _clock.GetUtcNow().ToUnixTimeSeconds() / 60;
        lock (_gate)
        {
            if (minute != _windowMinute)
            {
                _counts.Clear();
                _windowMinute = minute;
            }
            return _counts[key] = _counts.GetValueOrDefault(key) + 1;
        }
    }
}

/// <summary>Rate stage — the "rate" half of the canonical budget/rate slot (src/CLAUDE.md): checked before
/// any money is spent; every request arriving here counts, and refusals are audited like every denial.</summary>
public sealed class RateLimitStage(RateCounter counter, RateLimitConfig config) : IPipelineStage
{
    private const string GlobalKey = "global";

    public async Task InvokeAsync(InferenceContext ctx, PipelineDelegate next, CancellationToken ct)
    {
        if (config.GlobalPerMinute > 0 && counter.Increment(GlobalKey) > config.GlobalPerMinute)
        {
            ctx.Deny($"rate limit exceeded: appliance cap {config.GlobalPerMinute}/min", DenialKind.RateLimited);
            return;
        }

        if (config.PerTeamPerMinute > 0)
        {
            var team = ctx.Identity.Tags.Team;
            if (counter.Increment(team) > config.PerTeamPerMinute)
            {
                ctx.Deny($"rate limit exceeded for team '{team}': {config.PerTeamPerMinute}/min", DenialKind.RateLimited);
                return;
            }
        }

        await next(ctx, ct);
    }
}

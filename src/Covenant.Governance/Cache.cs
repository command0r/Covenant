using System.Security.Cryptography;
using System.Text;
using Covenant.Core;

namespace Covenant.Governance;

/// <summary>TtlSeconds = 0 disables caching (default posture — caching holds response content in memory).</summary>
public sealed class CacheConfig
{
    public int TtlSeconds { get; init; }
    public int MaxEntries { get; init; } = 1_000;
}

/// <summary>In-memory, TTL-bound response cache. Never persisted; entries die with the process.</summary>
public sealed class ResponseCache(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, (InferenceResponse Response, DateTimeOffset Expires)> _entries = new();

    public bool TryGet(string key, out InferenceResponse response)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var e) && e.Expires > _clock.GetUtcNow())
            {
                response = e.Response;
                return true;
            }
            _entries.Remove(key);
            response = null!;
            return false;
        }
    }

    public void Store(string key, InferenceResponse response, TimeSpan ttl, int maxEntries)
    {
        lock (_gate)
        {
            if (_entries.Count >= maxEntries && !_entries.ContainsKey(key))
            {
                var now = _clock.GetUtcNow();
                foreach (var expired in _entries.Where(kv => kv.Value.Expires <= now).Select(kv => kv.Key).ToList())
                    _entries.Remove(expired);
                if (_entries.Count >= maxEntries)
                    _entries.Remove(_entries.OrderBy(kv => kv.Value.Expires).First().Key);
            }
            _entries[key] = (response, _clock.GetUtcNow() + ttl);
        }
    }
}

/// <summary>Cache stage (src/CLAUDE.md ordering: cache before budget/provider — a hit skips the model call).
/// Keys are team-scoped so responses never leak across teams; only successful responses are cached; hits cost $0.</summary>
public sealed class CacheStage(ResponseCache cache, CacheConfig config) : IPipelineStage
{
    public async Task InvokeAsync(InferenceContext ctx, PipelineDelegate next, CancellationToken ct)
    {
        if (config.TtlSeconds <= 0 || ctx.Policy?.Route is not { } route)
        {
            await next(ctx, ct);
            return;
        }

        var key = Key(ctx.Identity.Tags.Team, route.ModelId, ctx.Request.Messages);
        if (cache.TryGet(key, out var hit))
        {
            ctx.ServedFromCache = true;
            ctx.Response = hit with { Usage = hit.Usage with { CostUsd = 0m } };
            if (ctx.Request.Stream && ctx.DeltaSink is { } sink)
                await sink(new ChatDelta(hit.Message.Content), ct); // one delta: the cached text
            return; // budget, provider, attribution all skipped — nothing spent, nothing to price
        }

        await next(ctx, ct);

        if (!ctx.IsDenied && ctx.Response is { } fresh)
            cache.Store(key, fresh, TimeSpan.FromSeconds(config.TtlSeconds), config.MaxEntries);
    }

    private static string Key(string team, string modelId, IReadOnlyList<ChatMessage> messages)
    {
        var sb = new StringBuilder(team).Append('\u001f').Append(modelId);
        foreach (var m in messages) sb.Append('\u001f').Append(m.Role).Append(':').Append(m.Content);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}

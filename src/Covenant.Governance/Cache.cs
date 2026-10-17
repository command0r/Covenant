using System.Buffers.Binary;
using System.Globalization;
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

/// <summary>In-memory, TTL-bound response cache. Never persisted; entries die with the process. Each entry keeps the
/// provenance of the attempt that actually produced it (connection + model — possibly an approved fallback).</summary>
public sealed class ResponseCache(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, (InferenceResponse Response, RouteTarget? ServedTarget, DateTimeOffset Expires)> _entries = new();

    public bool TryGet(string key, out InferenceResponse response) => TryGet(key, out response, out _);

    public bool TryGet(string key, out InferenceResponse response, out RouteTarget? servedTarget)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var e) && e.Expires > _clock.GetUtcNow())
            {
                response = e.Response;
                servedTarget = e.ServedTarget;
                return true;
            }
            _entries.Remove(key);
            response = null!;
            servedTarget = null;
            return false;
        }
    }

    public void Store(string key, InferenceResponse response, TimeSpan ttl, int maxEntries)
        => Store(key, response, ttl, maxEntries, null);

    public void Store(string key, InferenceResponse response, TimeSpan ttl, int maxEntries, RouteTarget? servedTarget)
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
            _entries[key] = (response, servedTarget, _clock.GetUtcNow() + ttl);
        }
    }
}

/// <summary>Unambiguous canonical cache-key encoding (review round 2, B5): every field is written into an incremental
/// SHA-256 as a presence byte (0 = null, 1 = value) followed by a 4-byte big-endian length and the UTF-8 bytes. No
/// delimiter can be forged by content: "x\u001fAssistant:y" in one message and ["x","y"] in two messages, or a shifted
/// message boundary, always hash differently. Lists are prefixed with their count.</summary>
public sealed class CacheKeyBuilder : IDisposable
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    public CacheKeyBuilder(string domain) => Add(domain);

    public CacheKeyBuilder Add(string? value)
    {
        Span<byte> header = stackalloc byte[5];
        if (value is null)
        {
            header[0] = 0;
            BinaryPrimitives.WriteInt32BigEndian(header[1..], 0);
            _hash.AppendData(header);
            return this;
        }
        var bytes = Encoding.UTF8.GetBytes(value);
        header[0] = 1;
        BinaryPrimitives.WriteInt32BigEndian(header[1..], bytes.Length);
        _hash.AppendData(header);
        _hash.AppendData(bytes);
        return this;
    }

    public CacheKeyBuilder Add(int? value) => Add(value?.ToString(CultureInfo.InvariantCulture));

    public CacheKeyBuilder Count(int count) => Add(count);

    public CacheKeyBuilder Messages(IReadOnlyList<ChatMessage> messages)
    {
        Count(messages.Count);
        foreach (var m in messages)
        {
            Add(m.Role.ToString());
            Add(m.Content);
        }
        return this;
    }

    public string Finish() => Convert.ToHexString(_hash.GetHashAndReset());

    public void Dispose() => _hash.Dispose();
}

/// <summary>Cache stage (src/CLAUDE.md ordering: cache before budget/provider — a hit skips the model call).
/// Keys are team-scoped so responses never leak across teams; only successful responses are cached; hits cost $0.</summary>
public sealed class CacheStage(ResponseCache cache, CacheConfig config) : IPipelineStage
{
    public async Task InvokeAsync(InferenceContext ctx, PipelineDelegate next, CancellationToken ct)
    {
        // Bound configuration (ADR-0011/0013) owns TTL/size; the key additionally scopes to the config version,
        // connection, application, profile and final classification — no reuse across versions or identities.
        int ttl = ctx.Configuration?.Config.Settings.CacheTtlSeconds ?? config.TtlSeconds;
        int maxEntries = ctx.Configuration?.Config.Settings.CacheMaxEntries ?? config.MaxEntries;
        if (ttl <= 0 || ctx.Policy?.Route is not { } route)
        {
            ctx.CacheOutcome = CacheOutcomes.Disabled;
            if (ttl <= 0) ctx.MarkCurrentStageSkipped(TraceReasons.CacheDisabled);
            await next(ctx, ct);
            return;
        }

        // ADR-0015: a request that asks for web sources and triggers a search is never answered from (nor stored in) the
        // exact cache — "current" answers must come from a fresh, governed search, not a replay.
        if (WebSearchPlanner.Evaluate(ctx.Request).Triggered)
        {
            ctx.MarkCurrentStageSkipped(TraceReasons.WebSearchBypass);
            ctx.CacheOutcome = CacheOutcomes.BypassWebSearch;
            await next(ctx, ct);
            return;
        }

        var key = KeyFor(ctx, route);
        if (cache.TryGet(key, out var hit, out var servedTarget))
        {
            ctx.ServedFromCache = true;
            ctx.CacheOutcome = CacheOutcomes.Hit;
            ctx.CostBasis = "no_call";
            ctx.ServedTarget = servedTarget;   // provenance: the attempt that produced the entry (maybe a fallback)
            ctx.Response = hit with { Usage = hit.Usage with { CostUsd = 0m } };
            if (ctx.Request.Stream && ctx.DeltaSink is { } sink)
                await sink(new ChatDelta(hit.Message.Content), ct); // one delta: the cached text
            return; // budget, provider, attribution all skipped — nothing spent, nothing to price
        }

        ctx.CacheOutcome = CacheOutcomes.Miss;
        ctx.MarkCurrentStagePassed(TraceReasons.CacheMiss);
        await next(ctx, ct);

        if (!ctx.IsDenied && ctx.Response is { } fresh && !ctx.ServedFromCache)
            cache.Store(key, fresh, TimeSpan.FromSeconds(ttl), Math.Max(1, maxEntries), ctx.ServedTarget ?? route);
    }

    /// <summary>v4 key (review round 2). Configured: application, team, profile, FINAL classification, configuration
    /// version, the ORIGINAL request parameters (requested model override, requested max_tokens — stream-independent),
    /// every candidate target in order with its EFFECTIVE output bound, then the messages (count, role, content).
    /// Legacy (no configuration): team, route model, requested max_tokens, messages.</summary>
    public static string KeyFor(InferenceContext ctx, RouteTarget route)
    {
        if (ctx.Configuration is { } cfg)
        {
            IReadOnlyList<RouteTarget> candidates = ctx.Policy!.Candidates.Count > 0 ? ctx.Policy.Candidates : new[] { route };
            using var k = new CacheKeyBuilder("covenant-cache-v4");
            k.Add(cfg.Version)
             .Add(ctx.ApplicationId)
             .Add(ctx.Identity.Tags.Team)
             .Add(ctx.ProfileId)
             .Add(ctx.Classification.ToString())
             .Add(ctx.Request.RequestedModel)
             .Add(ctx.Request.MaxOutputTokens)
             .Count(candidates.Count);
            foreach (var c in candidates)
                k.Add(c.AdapterKey).Add(c.ModelId).Add(RequestBounds.OutputTokenBound(ctx, c));
            k.Messages(ctx.Request.Messages);
            return k.Finish();
        }

        // max_tokens is part of the contract: a 400-token cached answer must not serve a max_tokens=5 ask.
        using var legacy = new CacheKeyBuilder("covenant-cache-legacy-v2");
        legacy.Add(ctx.Identity.Tags.Team).Add(route.ModelId).Add(ctx.Request.MaxOutputTokens).Messages(ctx.Request.Messages);
        return legacy.Finish();
    }
}

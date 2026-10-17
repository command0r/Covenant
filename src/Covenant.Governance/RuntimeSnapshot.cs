using Covenant.Core;

namespace Covenant.Governance;

/// <summary>Compiled, immutable view of one validated <see cref="CovenantConfig"/> version (ADR-0011): lookup
/// tables for applications, profiles, connections and the connection-scoped price book, plus the snapshot-owned
/// runtime resources (provider clients) built by the Host. Reference-counted: the holder owns one reference
/// while the snapshot is current; each request <see cref="TryAcquire"/>s one at entry and <see cref="Release"/>s it
/// in a finally. When a retired snapshot's count reaches zero its resources are disposed — in-flight calls always
/// finish on the clients they started with.</summary>
public sealed class RuntimeSnapshot : IRequestConfiguration
{
    private readonly Dictionary<string, ApplicationConfig> _applications;
    private readonly Dictionary<string, WorkloadProfileConfig> _profiles;
    private readonly Dictionary<string, ConnectionConfig> _connections;
    private readonly Dictionary<(string Connection, string Model), ModelConfig> _models;
    private readonly object? _resources;
    private int _refCount = 1;   // the holder's reference
    private int _disposed;

    public RuntimeSnapshot(CovenantConfig config, object? resources = null)
    {
        Config = config;
        _resources = resources;
        _applications = new Dictionary<string, ApplicationConfig>(StringComparer.Ordinal);
        foreach (var a in config.Applications) _applications.TryAdd(a.Id, a);
        _profiles = new Dictionary<string, WorkloadProfileConfig>(StringComparer.Ordinal);
        foreach (var p in config.Profiles) _profiles.TryAdd(p.Id, p);
        _connections = new Dictionary<string, ConnectionConfig>(StringComparer.Ordinal);
        _models = new Dictionary<(string, string), ModelConfig>();
        foreach (var c in config.Connections)
        {
            if (!_connections.TryAdd(c.Id, c)) continue;
            foreach (var m in c.Models) _models.TryAdd((c.Id, m.ModelId), m);
        }
    }

    public int Version => Config.Version;
    public CovenantConfig Config { get; }
    public ProviderMode Mode => Config.Mode;

    public ApplicationConfig? FindApplication(string applicationId) => _applications.GetValueOrDefault(applicationId);
    public WorkloadProfileConfig? FindProfile(string profileId) => _profiles.GetValueOrDefault(profileId);
    public ConnectionConfig? FindConnection(string connectionId) => _connections.GetValueOrDefault(connectionId);
    public ModelConfig? FindModel(string connectionId, string modelId) => _models.GetValueOrDefault((connectionId, modelId));

    public T? GetResource<T>() where T : class => _resources as T;

    /// <summary>Current references (holder + in-flight requests). Diagnostics/tests only.</summary>
    public int RefCount => Volatile.Read(ref _refCount);

    /// <summary>True once the resources were disposed (retired and fully released).</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) == 1;

    /// <summary>Adds a reference unless the snapshot is already fully released (then the caller re-reads Current).</summary>
    public bool TryAcquire()
    {
        while (true)
        {
            int current = Volatile.Read(ref _refCount);
            if (current <= 0) return false;
            if (Interlocked.CompareExchange(ref _refCount, current + 1, current) == current) return true;
        }
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref _refCount) == 0 && Interlocked.Exchange(ref _disposed, 1) == 0)
            (_resources as IDisposable)?.Dispose();
    }
}

/// <summary>The active-configuration seam the Host and admin endpoints use (ADR-0011): lock-free readers via a
/// volatile reference; a single writer swaps.</summary>
public interface IRuntimeConfig
{
    /// <summary>The active snapshot (no reference taken — read-only use like status rendering).</summary>
    RuntimeSnapshot Current { get; }

    /// <summary>Takes a reference on the active snapshot. The caller MUST <see cref="RuntimeSnapshot.Release"/> it.</summary>
    RuntimeSnapshot Acquire();

    /// <summary>Activates <paramref name="next"/>; the previous snapshot is retired (disposed when its last
    /// in-flight request releases it). Returns the retired snapshot.</summary>
    RuntimeSnapshot Swap(RuntimeSnapshot next);
}

public sealed class SnapshotHolder(RuntimeSnapshot initial) : IRuntimeConfig
{
    private RuntimeSnapshot _current = initial;

    public RuntimeSnapshot Current => Volatile.Read(ref _current);

    public RuntimeSnapshot Acquire()
    {
        while (true)
        {
            var s = Volatile.Read(ref _current);
            if (s.TryAcquire()) return s;
            // Lost a race with Swap + final release of the old snapshot: the new one is visible now.
        }
    }

    public RuntimeSnapshot Swap(RuntimeSnapshot next)
    {
        var previous = Interlocked.Exchange(ref _current, next);
        previous.Release();   // drop the holder's reference; in-flight requests keep theirs
        return previous;
    }
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Covenant.Core;
using Covenant.Governance;

namespace Covenant.Host;

/// <summary>File-system seam for the configuration repository so tests can inject faults (a crash between
/// writes) without touching the real disk semantics. Production uses <see cref="PhysicalConfigFileSystem"/>.</summary>
public interface IConfigFileSystem
{
    bool FileExists(string path);
    byte[] ReadAllBytes(string path);
    /// <summary>Create/truncate, write, flush to disk (fsync).</summary>
    void WriteDurable(string path, byte[] content);
    /// <summary>Atomic rename. overwrite=false must fail when the destination exists.</summary>
    void Move(string source, string destination, bool overwrite);
    void CreateDirectory(string path);
    IEnumerable<string> EnumerateFiles(string directory, string pattern);
}

public sealed class PhysicalConfigFileSystem : IConfigFileSystem
{
    public static readonly PhysicalConfigFileSystem Instance = new();

    public bool FileExists(string path) => File.Exists(path);
    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

    public void WriteDurable(string path, byte[] content)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        fs.Write(content);
        fs.Flush(flushToDisk: true);
    }

    public void Move(string source, string destination, bool overwrite) => File.Move(source, destination, overwrite);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public IEnumerable<string> EnumerateFiles(string directory, string pattern)
        => Directory.Exists(directory) ? Directory.EnumerateFiles(directory, pattern) : Array.Empty<string>();
}

/// <summary>Result of opening the repository at boot. Errors ⇒ refuse to start (fail-closed; the operator
/// restores the pointer — never a silent "latest").</summary>
public sealed class ConfigOpenResult
{
    public CovenantConfig? Active { get; init; }
    public bool IsEmpty { get; init; }
    public List<string> Errors { get; } = [];
    public List<string> Warnings { get; } = [];
}

public enum ApplyStatus { Applied, Stale, Invalid }

public sealed class ApplyResult
{
    public required ApplyStatus Status { get; init; }
    public CovenantConfig? Applied { get; init; }
    public required int ActiveVersion { get; init; }
    public ConfigValidationResult? Validation { get; init; }
}

/// <summary>Single-appliance configuration repository (ADR-0011): immutable snapshots
/// <c>versions/v000001.json …</c> plus <c>active.json</c> = { version, sha256 }. Only non-secret configuration and
/// secret REFERENCES are persisted.
/// Write ordering (apply/rollback), all under one writer lock: check If-Match → stamp + validate → write
/// <c>vM.json.tmp</c>, flush, rename to <c>vM.json</c> (create-new; never overwrites) → write
/// <c>active.json.tmp</c>, flush, rename over <c>active.json</c> → activate (swap the in-memory snapshot, append the
/// <c>config.activated</c> audit event) → release. A crash before the pointer rename leaves a version file that is
/// reported as "written, never activated" and is never reused (M is always above every existing file).</summary>
public sealed class ConfigRepository
{
    public const string PointerFile = "active.json";
    public const string VersionsDir = "versions";

    private readonly IConfigFileSystem _fs;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private volatile CovenantConfig? _active;

    public ConfigRepository(string directory, IConfigFileSystem? fileSystem = null, TimeProvider? clock = null)
    {
        Directory = directory;
        _fs = fileSystem ?? PhysicalConfigFileSystem.Instance;
        _clock = clock ?? TimeProvider.System;
    }

    public string Directory { get; }
    public CovenantConfig? Active => _active;

    private string VersionsPath => Path.Combine(Directory, VersionsDir);
    private string PointerPath => Path.Combine(Directory, PointerFile);
    public string VersionPath(int version) => Path.Combine(VersionsPath, $"v{version.ToString("D6", CultureInfo.InvariantCulture)}.json");

    public static string ETag(int version) => $"\"v{version.ToString(CultureInfo.InvariantCulture)}\"";

    /// <summary>Parses an If-Match value "vN" / "\"vN\"" (W/ prefix tolerated). Null when malformed.</summary>
    public static int? ParseETag(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        if (v.StartsWith("W/", StringComparison.Ordinal)) v = v[2..];
        v = v.Trim('"');
        return v.Length > 1 && v[0] == 'v' && int.TryParse(v.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0
            ? n : null;
    }

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static byte[] Serialize(CovenantConfig config) => JsonSerializer.SerializeToUtf8Bytes(config, ConfigJsonContext.Default.CovenantConfig);

    /// <summary>Version numbers present on disk (activated or not).</summary>
    public List<int> ExistingVersions()
    {
        var list = new List<int>();
        foreach (var f in _fs.EnumerateFiles(VersionsPath, "v*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(f);
            if (name.Length > 1 && int.TryParse(name.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0)
                list.Add(n);
        }
        list.Sort();
        return list;
    }

    /// <summary>Boot: read + verify the pointer. Missing pointer with no versions ⇒ empty (bootstrap). Missing
    /// pointer with versions, unreadable pointer, missing/altered version file ⇒ errors (refuse to start).</summary>
    public ConfigOpenResult Open()
    {
        var versions = ExistingVersions();
        if (!_fs.FileExists(PointerPath))
        {
            if (versions.Count == 0) return new ConfigOpenResult { IsEmpty = true };
            var r = new ConfigOpenResult();
            r.Errors.Add($"Config:Path {Directory} — {PointerFile} is missing but {versions.Count} version file(s) exist; " +
                         "refusing to guess the active version. Restore active.json (version + sha256) from backup.");
            return r;
        }

        var result = new ConfigOpenResult();
        ActivePointer? pointer;
        try
        {
            pointer = JsonSerializer.Deserialize(_fs.ReadAllBytes(PointerPath), ConfigJsonContext.Default.ActivePointer);
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            pointer = null;
        }
        if (pointer is null || pointer.Version < 1 || string.IsNullOrEmpty(pointer.Sha256))
        {
            result.Errors.Add($"Config:Path {Directory} — {PointerFile} is corrupt (not a valid {{version, sha256}} pointer). Restore it from backup.");
            return result;
        }

        var file = VersionPath(pointer.Version);
        if (!_fs.FileExists(file))
        {
            result.Errors.Add($"Config:Path {Directory} — active version v{pointer.Version} file is missing ({file}).");
            return result;
        }
        var bytes = _fs.ReadAllBytes(file);
        if (!string.Equals(Sha256Hex(bytes), pointer.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            result.Errors.Add($"Config:Path {Directory} — active version v{pointer.Version} does not match the pointer's SHA-256 (file altered or torn).");
            return result;
        }
        CovenantConfig? config;
        try
        {
            config = JsonSerializer.Deserialize(bytes, ConfigJsonContext.Default.CovenantConfig);
        }
        catch (JsonException ex)
        {
            result.Errors.Add($"Config:Path {Directory} — active version v{pointer.Version} is not a valid configuration ({ex.Message}).");
            return result;
        }
        if (config is null || config.Version != pointer.Version)
        {
            result.Errors.Add($"Config:Path {Directory} — active version file v{pointer.Version} carries version {config?.Version.ToString(CultureInfo.InvariantCulture) ?? "null"}.");
            return result;
        }

        foreach (var orphan in versions.Where(v => v > pointer.Version))
            result.Warnings.Add($"configuration v{orphan} was written but never activated (interrupted apply); it is ignored and never reused");
        foreach (var tmp in _fs.EnumerateFiles(VersionsPath, "*.tmp").Concat(_fs.EnumerateFiles(Directory, "*.tmp")))
            result.Warnings.Add($"leftover temporary file {Path.GetFileName(tmp)} from an interrupted write is ignored");

        _active = config;
        return new ConfigOpenResult { Active = config }.WithMessages(result);
    }

    /// <summary>Bootstrap v1 into an EMPTY repository (legacy import or seed). Same durable write ordering.</summary>
    public CovenantConfig Bootstrap(CovenantConfig v1)
    {
        _writer.Wait();
        try
        {
            if (_active is not null || _fs.FileExists(PointerPath) || ExistingVersions().Count > 0)
                throw new InvalidOperationException("configuration repository is not empty");
            var stamped = v1 with { Version = 1, BaseVersion = null };
            Persist(stamped);
            _active = stamped;
            return stamped;
        }
        finally
        {
            _writer.Release();
        }
    }

    /// <summary>Validate-and-activate a candidate as a NEW version (apply and rollback). <paramref name="prepare"/>
    /// compiles the runtime snapshot BEFORE anything is persisted (a construction failure leaves the active
    /// version untouched); <paramref name="activate"/> swaps it in and appends the audit event after the pointer
    /// rename. A stale <paramref name="expectedActiveVersion"/> ⇒ Stale; validation errors ⇒ Invalid (nothing written).</summary>
    public async Task<ApplyResult> ApplyAsync<T>(
        CovenantConfig candidate, int expectedActiveVersion, string origin, string? changeSummary, int? rolledBackFrom,
        Func<CovenantConfig, ConfigValidationResult> validate,
        Func<CovenantConfig, T> prepare,
        Action<CovenantConfig, T> activate,
        Action<T>? discard = null,
        CancellationToken cancellationToken = default)
    {
        await _writer.WaitAsync(cancellationToken);
        try
        {
            var active = _active ?? throw new InvalidOperationException("configuration repository has no active version");
            if (expectedActiveVersion != active.Version)
                return new ApplyResult { Status = ApplyStatus.Stale, ActiveVersion = active.Version };

            var existing = ExistingVersions();
            int next = Math.Max(active.Version, existing.Count > 0 ? existing[^1] : 0) + 1;
            var stamped = candidate with
            {
                Version = next,
                BaseVersion = active.Version,
                RolledBackFrom = rolledBackFrom,
                Origin = origin,
                CreatedUtc = _clock.GetUtcNow(),
                ChangeSummary = Bound(changeSummary),
            };

            var validation = validate(stamped);
            if (!validation.IsValid)
                return new ApplyResult { Status = ApplyStatus.Invalid, ActiveVersion = active.Version, Validation = validation };

            var prepared = prepare(stamped);
            try
            {
                Persist(stamped);
            }
            catch
            {
                discard?.Invoke(prepared);
                throw;
            }
            _active = stamped;
            activate(stamped, prepared);
            return new ApplyResult { Status = ApplyStatus.Applied, Applied = stamped, ActiveVersion = stamped.Version, Validation = validation };
        }
        finally
        {
            _writer.Release();
        }
    }

    private void Persist(CovenantConfig stamped)
    {
        _fs.CreateDirectory(VersionsPath);
        var bytes = Serialize(stamped);
        var final = VersionPath(stamped.Version);
        var tmp = final + ".tmp";
        _fs.WriteDurable(tmp, bytes);
        _fs.Move(tmp, final, overwrite: false);          // create-new: an existing version is never overwritten

        var pointer = JsonSerializer.SerializeToUtf8Bytes(
            new ActivePointer { Version = stamped.Version, Sha256 = Sha256Hex(bytes) }, ConfigJsonContext.Default.ActivePointer);
        var pointerTmp = PointerPath + ".tmp";
        _fs.WriteDurable(pointerTmp, pointer);
        _fs.Move(pointerTmp, PointerPath, overwrite: true); // the activation point
    }

    public CovenantConfig? GetVersion(int version)
    {
        var path = VersionPath(version);
        if (version < 1 || !_fs.FileExists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize(_fs.ReadAllBytes(path), ConfigJsonContext.Default.CovenantConfig);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Newest first, bounded. Versions written-but-never-activated (above the active one) are excluded.</summary>
    public List<CovenantConfig> ListVersions(int limit)
    {
        var activeVersion = _active?.Version ?? 0;
        var list = new List<CovenantConfig>();
        foreach (var v in ExistingVersions().Where(v => v <= activeVersion).OrderByDescending(v => v))
        {
            if (list.Count >= limit) break;
            if (GetVersion(v) is { } c) list.Add(c);
        }
        return list;
    }

    private static string? Bound(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary)) return null;
        var clean = new string(summary.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length <= 500 ? clean : clean[..500] + "…";
    }
}

internal static class ConfigOpenResultExtensions
{
    public static ConfigOpenResult WithMessages(this ConfigOpenResult target, ConfigOpenResult source)
    {
        target.Errors.AddRange(source.Errors);
        target.Warnings.AddRange(source.Warnings);
        return target;
    }
}

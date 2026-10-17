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
    /// <summary>Append (create if missing), then flush to disk (fsync). Used for the activation journal.</summary>
    void AppendDurable(string path, byte[] content);
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

    public void AppendDurable(string path, byte[] content)
    {
        using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        fs.Write(content);
        fs.Flush(flushToDisk: true);
    }

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
    /// <summary>Non-fatal repository notes (e.g. the journal append failed after the pointer rename; boot repairs it).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>Single-appliance configuration repository (ADR-0011): immutable snapshots
/// <c>versions/v000001.json …</c>, <c>active.json</c> = { version, sha256 }, and <c>activations.jsonl</c>, the
/// append-only ACTIVATION JOURNAL (one line per activation). The journal — not the presence of a version file —
/// defines history: version files never activated (orphans of an interrupted apply) are excluded from history,
/// <see cref="GetVersion"/> and rollback, and their numbers are never reused. Only non-secret configuration and secret
/// REFERENCES are persisted; change summaries are redacted and bounded (≤ 500 chars).
/// Write ordering (apply/rollback), all under one writer lock: check If-Match → stamp + validate → COMPILE (the
/// caller's prepare step, provider clients included; a failure writes nothing) → write <c>vM.json.tmp</c>, flush,
/// rename to <c>vM.json</c> (create-new) → write <c>active.json.tmp</c>, flush, rename over <c>active.json</c> →
/// append the journal line, flush → activate (swap the in-memory snapshot, enqueue the <c>config.activated</c>
/// audit event) → release. M = max(version files, journal) + 1.</summary>
public sealed class ConfigRepository
{
    public const string PointerFile = "active.json";
    public const string VersionsDir = "versions";
    public const string JournalFile = "activations.jsonl";
    public const int MaxSummaryChars = 500;

    private readonly IConfigFileSystem _fs;
    private readonly TimeProvider _clock;
    private readonly Func<string, string> _redact;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private volatile CovenantConfig? _active;
    private readonly object _journalGate = new();
    private readonly List<ActivationRecord> _journal = [];

    /// <param name="redact">Replaces secret values in free text (change summaries) — the Host passes one that knows
    /// every configured secret value; default = identity (pattern-based redaction still applies).</param>
    public ConfigRepository(string directory, IConfigFileSystem? fileSystem = null, TimeProvider? clock = null,
        Func<string, string>? redact = null)
    {
        Directory = directory;
        _fs = fileSystem ?? PhysicalConfigFileSystem.Instance;
        _clock = clock ?? TimeProvider.System;
        _redact = redact ?? (x => x);
    }

    public string Directory { get; }
    public CovenantConfig? Active => _active;

    private string VersionsPath => Path.Combine(Directory, VersionsDir);
    private string PointerPath => Path.Combine(Directory, PointerFile);
    private string JournalPath => Path.Combine(Directory, JournalFile);
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

    /// <summary>Boot: read + verify the pointer and the activation journal. Missing pointer with no versions ⇒ empty
    /// (bootstrap). Missing pointer with versions, unreadable pointer, missing/altered version file, corrupt journal
    /// line (other than a torn last line) ⇒ errors (refuse to start). Version files not in the journal ⇒ orphan
    /// warnings. Active version missing from the journal (crash between pointer rename and journal append) ⇒ a
    /// <c>recovered: true</c> line is appended.</summary>
    public ConfigOpenResult Open()
    {
        var versions = ExistingVersions();
        var result = new ConfigOpenResult();
        var journal = ReadJournal(result);
        if (result.Errors.Count > 0) return result;

        if (!_fs.FileExists(PointerPath))
        {
            if (versions.Count == 0 && journal.Count == 0) return new ConfigOpenResult { IsEmpty = true };
            result.Errors.Add($"Config:Path {Directory} — {PointerFile} is missing but {versions.Count} version file(s) exist; " +
                              "refusing to guess the active version. Restore active.json (version + sha256) from backup.");
            return result;
        }

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

        if (!journal.Any(j => j.Version == config.Version))
        {
            // Crash between the pointer rename and the journal append: the pointer is the activation point, so the
            // active version WAS activated — record it, marked recovered, before serving.
            var recovered = ActivationRecord.For(config, _clock.GetUtcNow(), recovered: true);
            try
            {
                AppendJournal(recovered);
                result.Warnings.Add($"configuration v{config.Version} was active but missing from {JournalFile}; recorded as recovered");
            }
            catch (IOException ex)
            {
                result.Errors.Add($"Config:Path {Directory} — cannot append the recovered activation of v{config.Version} to {JournalFile} ({ex.Message}).");
                return result;
            }
            journal.Add(recovered);
        }

        var activated = journal.Select(j => j.Version).ToHashSet();
        foreach (var orphan in versions.Where(v => !activated.Contains(v)))
            result.Warnings.Add($"configuration v{orphan} was written but never activated (interrupted apply); it is excluded from history and its number is never reused");
        foreach (var tmp in _fs.EnumerateFiles(VersionsPath, "*.tmp").Concat(_fs.EnumerateFiles(Directory, "*.tmp")))
            result.Warnings.Add($"leftover temporary file {Path.GetFileName(tmp)} from an interrupted write is ignored");

        lock (_journalGate)
        {
            _journal.Clear();
            _journal.AddRange(journal);
        }
        _active = config;
        return new ConfigOpenResult { Active = config }.WithMessages(result);
    }

    /// <summary>Reads the journal. A TORN tail — the final line, with no terminating newline, that does not parse
    /// (an interrupted append) — is quarantined to <c>activations.jsonl.torn-&lt;utc&gt;</c> and the journal is
    /// rewritten atomically with only the complete lines, BEFORE anything else can append. Without that repair the
    /// next append would be concatenated onto the torn bytes and corrupt history. Any other unparsable line (including
    /// a complete, newline-terminated last line) is corruption ⇒ refuse to start (fail-closed).</summary>
    private List<ActivationRecord> ReadJournal(ConfigOpenResult result)
    {
        var list = new List<ActivationRecord>();
        if (!_fs.FileExists(JournalPath)) return list;
        var text = System.Text.Encoding.UTF8.GetString(_fs.ReadAllBytes(JournalPath));
        bool endsWithNewline = text.Length == 0 || text[^1] == '\n';
        var lines = text.Split('\n');
        int last = lines.Length - 1;
        while (last >= 0 && lines[last].Trim().Length == 0) last--;
        string? tornTail = null;
        var goodLines = new System.Text.StringBuilder();
        for (int i = 0; i <= last; i++)
        {
            var raw = lines[i];
            var line = raw.Trim();
            if (line.Length == 0) continue;
            ActivationRecord? rec = null;
            try { rec = JsonSerializer.Deserialize(line, ConfigJsonContext.Default.ActivationRecord); }
            catch (JsonException) { }
            if (rec is { Version: > 0 }) { list.Add(rec); goodLines.Append(line).Append('\n'); continue; }
            if (i == last && !endsWithNewline)
                tornTail = raw;
            else
            {
                result.Errors.Add($"Config:Path {Directory} — {JournalFile} line {i + 1} is corrupt; the activation history cannot be trusted. Restore it from backup.");
                return list;
            }
        }
        if (tornTail is null && !endsWithNewline && last >= 0)
        {
            // Last line parsed but its newline never landed: terminate it so the next append starts a new line.
            tornTail = "";
        }
        if (tornTail is not null)
        {
            try
            {
                if (tornTail.Length > 0)
                {
                    var quarantine = $"{JournalPath}.torn-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}";
                    _fs.WriteDurable(quarantine, System.Text.Encoding.UTF8.GetBytes(tornTail));
                    result.Warnings.Add($"{JournalFile}: the last line was torn (interrupted append); preserved in {Path.GetFileName(quarantine)} and removed from the journal");
                }
                var tmp = JournalPath + ".tmp";
                _fs.WriteDurable(tmp, System.Text.Encoding.UTF8.GetBytes(goodLines.ToString()));
                _fs.Move(tmp, JournalPath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Errors.Add($"Config:Path {Directory} — {JournalFile} has a torn tail that could not be repaired ({ex.Message}); refusing to start.");
            }
        }
        return list;
    }

    private void AppendJournal(ActivationRecord record)
    {
        // Defence in depth: never append onto an unterminated line (Open() repairs torn tails first).
        if (_fs.FileExists(JournalPath) && _fs.ReadAllBytes(JournalPath) is { Length: > 0 } existing && existing[^1] != (byte)'\n')
            throw new IOException($"{JournalFile} does not end with a newline; refusing to append (restart to repair the torn tail)");
        var line = JsonSerializer.SerializeToUtf8Bytes(record, ConfigJsonContext.Default.ActivationRecord);
        var bytes = new byte[line.Length + 1];
        line.CopyTo(bytes, 0);
        bytes[^1] = (byte)'\n';
        _fs.CreateDirectory(Directory);
        _fs.AppendDurable(JournalPath, bytes);
    }

    /// <summary>The activation journal, oldest first (copy).</summary>
    public IReadOnlyList<ActivationRecord> Journal()
    {
        lock (_journalGate) return _journal.ToArray();
    }

    /// <summary>True when version <paramref name="version"/> was activated (is in the journal).</summary>
    public bool IsActivated(int version)
    {
        lock (_journalGate) return _journal.Any(j => j.Version == version);
    }

    private int JournalMax()
    {
        lock (_journalGate) return _journal.Count == 0 ? 0 : _journal.Max(j => j.Version);
    }

    /// <summary>Bootstrap v1 into an EMPTY repository (legacy import or seed). Same durable write ordering. The caller
    /// compiles the snapshot from <see cref="StampV1"/> BEFORE calling this, so a compile failure writes nothing.</summary>
    public CovenantConfig Bootstrap(CovenantConfig v1)
    {
        _writer.Wait();
        try
        {
            if (_active is not null || _fs.FileExists(PointerPath) || ExistingVersions().Count > 0 || JournalMax() > 0)
                throw new InvalidOperationException("configuration repository is not empty");
            var stamped = StampV1(v1);
            Persist(stamped);
            var record = ActivationRecord.For(stamped, _clock.GetUtcNow(), recovered: false);
            AppendJournal(record);   // a failure here propagates: boot refuses to start (and recovers next time)
            lock (_journalGate) _journal.Add(record);
            _active = stamped;
            return stamped;
        }
        finally
        {
            _writer.Release();
        }
    }

    /// <summary>The exact v1 that <see cref="Bootstrap"/> will persist (version 1, no base, redacted bounded summary).</summary>
    public CovenantConfig StampV1(CovenantConfig v1)
        => v1 with { Version = 1, BaseVersion = null, ChangeSummary = Bound(v1.ChangeSummary) };

    /// <summary>Validate-and-activate a candidate as a NEW version (apply and rollback). <paramref name="prepare"/>
    /// compiles the runtime snapshot BEFORE anything is persisted; a <see cref="ConfigCompileException"/> from it is a
    /// validation failure (Invalid, nothing written). <paramref name="activate"/> swaps it in and appends the audit
    /// event after the pointer rename and the journal append. A stale <paramref name="expectedActiveVersion"/> ⇒
    /// Stale; validation errors ⇒ Invalid (nothing written).</summary>
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

            // Monotonic and never reusing an orphan's number: above every version file AND every journal entry.
            var existing = ExistingVersions();
            int next = Math.Max(Math.Max(active.Version, existing.Count > 0 ? existing[^1] : 0), JournalMax()) + 1;
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

            T prepared;
            try
            {
                prepared = prepare(stamped);   // compile (provider clients included) — nothing durable written yet
            }
            catch (ConfigCompileException ex)
            {
                return new ApplyResult
                {
                    Status = ApplyStatus.Invalid,
                    ActiveVersion = active.Version,
                    Validation = new ConfigValidationResult(ex.Issues, validation.Warnings),
                };
            }

            try
            {
                Persist(stamped);
            }
            catch
            {
                discard?.Invoke(prepared);
                throw;
            }

            var notes = new List<string>();
            var record = ActivationRecord.For(stamped, _clock.GetUtcNow(), recovered: false);
            try
            {
                AppendJournal(record);
            }
            catch (IOException ex)
            {
                // The pointer rename (the activation point) already happened: disk says vM is active, so the process
                // honours it. The next boot finds vM missing from the journal and appends it as recovered.
                notes.Add($"activation journal append failed ({ex.GetType().Name}); repaired at next boot");
            }
            lock (_journalGate) _journal.Add(record);
            _active = stamped;
            activate(stamped, prepared);
            return new ApplyResult { Status = ApplyStatus.Applied, Applied = stamped, ActiveVersion = stamped.Version, Validation = validation, Notes = notes };
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

    /// <summary>An ACTIVATED version (in the journal); null for unknown numbers and for orphans.</summary>
    public CovenantConfig? GetVersion(int version)
        => version >= 1 && IsActivated(version) ? ReadVersionFile(version) : null;

    private CovenantConfig? ReadVersionFile(int version)
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

    /// <summary>Activation history, newest first, bounded: journal entries only (orphans never appear).</summary>
    public List<CovenantConfig> ListVersions(int limit)
    {
        var list = new List<CovenantConfig>();
        var seen = new HashSet<int>();
        foreach (var rec in Enumerable.Reverse(Journal()))
        {
            if (list.Count >= limit) break;
            if (!seen.Add(rec.Version)) continue;
            if (ReadVersionFile(rec.Version) is { } c) list.Add(c);
        }
        return list;
    }

    /// <summary>Redacted (secret values and secret-looking tokens replaced) and bounded (≤ 500 chars, no control chars).</summary>
    public string? Bound(string? summary) => BoundSummary(summary is null ? null : _redact(summary));

    public static string? BoundSummary(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary)) return null;
        var clean = SecretRedaction.Patterns(new string(summary.Where(c => !char.IsControl(c)).ToArray())).Trim();
        return clean.Length <= MaxSummaryChars ? clean : clean[..(MaxSummaryChars - 1)] + "…";
    }
}

/// <summary>Free-text redaction for configuration-write history (ADR-0011: never secret values). Value-based: every
/// configured secret value the Host knows (secret refs, legacy provider keys, Auth:Keys values, Admin:Token) is
/// replaced; pattern-based: common credential shapes ("sk-…", "Bearer …", long hex/base64 runs) are replaced too.</summary>
public static partial class SecretRedaction
{
    public const string Marker = "[redacted]";

    public static Func<string, string> ForValues(IEnumerable<string?> secretValues)
    {
        var values = secretValues.Where(v => v is { Length: >= 6 }).Select(v => v!).Distinct(StringComparer.Ordinal)
            .OrderByDescending(v => v.Length).ToArray();
        return text =>
        {
            foreach (var v in values) text = text.Replace(v, Marker, StringComparison.Ordinal);
            return text;
        };
    }

    public static string Patterns(string text) => CredentialShape().Replace(text, Marker);

    [System.Text.RegularExpressions.GeneratedRegex(
        @"(?i)\b(?:sk|pk|rk|ak)-[A-Za-z0-9_\-]{8,}|\bBearer\s+[A-Za-z0-9._~+/\-]{8,}=*|\b[A-Za-z0-9+/_\-]{40,}={0,2}")]
    private static partial System.Text.RegularExpressions.Regex CredentialShape();
}

/// <summary>One line of <c>activations.jsonl</c> (ADR-0011): version, base, rolled-back-from, UTC time, bounded
/// redacted summary, and whether boot recovered it (crash between pointer rename and journal append).</summary>
public sealed class ActivationRecord
{
    public required int Version { get; init; }
    public int? Base { get; init; }
    public int? RolledBackFrom { get; init; }
    public required DateTimeOffset Utc { get; init; }
    public string? Origin { get; init; }
    public string? Summary { get; init; }
    public bool Recovered { get; init; }

    public static ActivationRecord For(CovenantConfig c, DateTimeOffset now, bool recovered) => new()
    {
        Version = c.Version,
        Base = c.BaseVersion,
        RolledBackFrom = c.RolledBackFrom,
        Utc = now,
        Origin = c.Origin,
        Summary = ConfigRepository.BoundSummary(c.ChangeSummary),
        Recovered = recovered,
    };
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

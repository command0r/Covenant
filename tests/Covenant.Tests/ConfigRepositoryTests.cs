using System.Text;
using Covenant.Core;
using Covenant.Governance;
using Covenant.Host;
using Xunit;

namespace Covenant.Tests;

/// <summary>ADR-0011 repository guarantees: monotonic versions, write ordering (version file before pointer),
/// crash recovery (orphan version ignored + reported; corrupt pointer / hash mismatch refuse to start),
/// rollback as a NEW version, and single-writer optimistic concurrency.</summary>
public sealed class ConfigRepositoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"covenant-cfgrepo-{Guid.NewGuid():n}");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static ConfigValidationResult Ok(CovenantConfig _) => new([], []);

    private static async Task<ApplyResult> Apply(ConfigRepository repo, CovenantConfig candidate, int expected,
        Func<CovenantConfig, ConfigValidationResult>? validate = null, List<int>? activated = null)
    {
        Func<CovenantConfig, ConfigValidationResult> check = validate ?? new Func<CovenantConfig, ConfigValidationResult>(Ok);
        return await repo.ApplyAsync(candidate, expected, "console", "edit", null,
            check, c => c.Version, (c, v) => activated?.Add(v));
    }

    private ConfigRepository Bootstrapped(IConfigFileSystem? fs = null)
    {
        var repo = new ConfigRepository(_dir, fs);
        Assert.True(repo.Open().IsEmpty);
        repo.Bootstrap(V2.Hybrid());
        return repo;
    }

    [Fact]
    public async Task Versions_are_monotonic_and_reopen_restores_the_active_version()
    {
        var repo = Bootstrapped();
        var r2 = await Apply(repo, V2.Hybrid(), 1);
        var r3 = await Apply(repo, V2.Hybrid(), 2);
        Assert.Equal(ApplyStatus.Applied, r2.Status);
        Assert.Equal(2, r2.Applied!.Version);
        Assert.Equal(1, r2.Applied.BaseVersion);
        Assert.Equal(3, r3.Applied!.Version);

        var reopened = new ConfigRepository(_dir).Open();   // "restart"
        Assert.Empty(reopened.Errors);
        Assert.Equal(3, reopened.Active!.Version);
    }

    [Fact]
    public async Task Stale_expected_version_is_rejected_and_nothing_is_written()
    {
        var repo = Bootstrapped();
        await Apply(repo, V2.Hybrid(), 1);
        var stale = await Apply(repo, V2.Hybrid(), 1);
        Assert.Equal(ApplyStatus.Stale, stale.Status);
        Assert.Equal(2, stale.ActiveVersion);
        Assert.Equal(new[] { 1, 2 }, repo.ExistingVersions());
    }

    [Fact]
    public async Task Invalid_candidate_leaves_the_active_version_and_disk_unchanged()
    {
        var repo = Bootstrapped();
        var invalid = await Apply(repo, V2.Hybrid(), 1,
            validate: _ => new ConfigValidationResult([new ConfigIssue("x", "duplicate_id", "bad")], []));
        Assert.Equal(ApplyStatus.Invalid, invalid.Status);
        Assert.Equal(1, repo.Active!.Version);
        Assert.Equal(new[] { 1 }, repo.ExistingVersions());
        Assert.Equal(1, new ConfigRepository(_dir).Open().Active!.Version);
    }

    /// <summary>Crash after the version file was renamed into place but before the pointer: the file exists,
    /// the pointer still names v1. Boot keeps v1, reports v2 as never activated, and never reuses its number.</summary>
    [Fact]
    public async Task Crash_between_version_file_and_pointer_keeps_the_old_version_and_reports_the_orphan()
    {
        var faulty = new CrashingFileSystem { FailOnPointerRename = true };
        var repo = Bootstrapped(faulty);
        faulty.Armed = true;
        await Assert.ThrowsAsync<IOException>(() => Apply(repo, V2.Hybrid(), 1));

        Assert.True(File.Exists(repo.VersionPath(2)));             // version file written first …
        var reopened = new ConfigRepository(_dir);
        var open = reopened.Open();                                 // … but never activated
        Assert.Empty(open.Errors);
        Assert.Equal(1, open.Active!.Version);
        Assert.Contains(open.Warnings, w => w.Contains("v2 was written but never activated", StringComparison.Ordinal));

        var next = await Apply(reopened, V2.Hybrid(), 1);
        Assert.Equal(3, next.Applied!.Version);                     // monotonic: v2 is never overwritten or reused
        Assert.DoesNotContain(2, reopened.ListVersions(10).Select(c => c.Version));
    }

    [Fact]
    public async Task Activation_happens_only_after_both_files_are_durable()
    {
        var faulty = new CrashingFileSystem { FailOnPointerRename = true };
        var repo = Bootstrapped(faulty);
        faulty.Armed = true;
        var activated = new List<int>();
        await Assert.ThrowsAsync<IOException>(() => Apply(repo, V2.Hybrid(), 1, activated: activated));
        Assert.Empty(activated);                                    // no in-memory swap / audit event for an unpersisted version
        Assert.Equal(1, repo.Active!.Version);
        Assert.Equal(new[] { "v000002.json.tmp→v000002.json", "active.json.tmp→active.json" }, faulty.Moves.TakeLast(2).ToList());
    }

    [Fact]
    public void Corrupt_pointer_refuses_to_start()
    {
        Bootstrapped();
        File.WriteAllText(Path.Combine(_dir, ConfigRepository.PointerFile), "{ not json");
        var open = new ConfigRepository(_dir).Open();
        Assert.Null(open.Active);
        Assert.Contains(open.Errors, e => e.Contains("corrupt", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_pointer_with_existing_versions_refuses_to_start()
    {
        var repo = Bootstrapped();
        File.Delete(Path.Combine(_dir, ConfigRepository.PointerFile));
        var open = new ConfigRepository(_dir).Open();
        Assert.False(open.IsEmpty);
        Assert.Null(open.Active);
        Assert.Contains(open.Errors, e => e.Contains("missing", StringComparison.Ordinal));
        Assert.True(File.Exists(repo.VersionPath(1)));
    }

    [Fact]
    public void Altered_active_version_fails_the_hash_check()
    {
        var repo = Bootstrapped();
        var path = repo.VersionPath(1);
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"cap_usd\":100", "\"cap_usd\":999999", StringComparison.Ordinal));
        var open = new ConfigRepository(_dir).Open();
        Assert.Null(open.Active);
        Assert.Contains(open.Errors, e => e.Contains("SHA-256", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rollback_activates_the_old_content_as_a_new_version()
    {
        var repo = Bootstrapped();
        var edited = V2.Hybrid(tweak: c => c with { Settings = c.Settings with { CacheTtlSeconds = 60 } });
        await Apply(repo, edited, 1);
        var target = repo.GetVersion(1)!;
        var rb = await repo.ApplyAsync(target, 2, "rollback", "rollback to v1", 1, Ok, c => c.Version, (_, _) => { });

        Assert.Equal(3, rb.Applied!.Version);
        Assert.Equal(1, rb.Applied.RolledBackFrom);
        Assert.Equal("rollback", rb.Applied.Origin);
        Assert.Equal(0, rb.Applied.Settings.CacheTtlSeconds);       // v1 content …
        Assert.Equal(60, repo.GetVersion(2)!.Settings.CacheTtlSeconds); // … history untouched
        Assert.Equal(new[] { 3, 2, 1 }, repo.ListVersions(10).Select(c => c.Version).ToList());
    }

    [Fact]
    public async Task Concurrent_applies_with_the_same_expected_version_one_wins_one_is_stale()
    {
        var repo = Bootstrapped();
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => Apply(repo, V2.Hybrid(), 1))));
        Assert.Single(results, r => r.Status == ApplyStatus.Applied);
        Assert.Single(results, r => r.Status == ApplyStatus.Stale);
        Assert.Equal(2, repo.Active!.Version);
    }

    [Fact]
    public void Persisted_versions_contain_secret_references_never_values()
    {
        var repo = Bootstrapped();
        var json = File.ReadAllText(repo.VersionPath(1), Encoding.UTF8);
        Assert.Contains("\"secret_ref\":\"OpenAI:ApiKey\"", json, StringComparison.Ordinal);
        Assert.Contains("\"mode\":\"hybrid\"", json, StringComparison.Ordinal);
        Assert.Contains("\"input_per_1m\":0.15", json, StringComparison.Ordinal);
        Assert.Contains("\"strategy\":\"fixed\"", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"v7\"", 7)]
    [InlineData("v7", 7)]
    [InlineData("W/\"v12\"", 12)]
    [InlineData("\"7\"", null)]
    [InlineData("\"v0\"", null)]
    [InlineData("*", null)]
    public void ETag_parsing(string raw, int? expected) => Assert.Equal(expected, ConfigRepository.ParseETag(raw));

    /// <summary>Physical file system with an injectable crash at the pointer rename (the activation point).</summary>
    private sealed class CrashingFileSystem : IConfigFileSystem
    {
        public bool Armed { get; set; }
        public bool FailOnPointerRename { get; init; }
        public List<string> Moves { get; } = [];
        private readonly PhysicalConfigFileSystem _inner = PhysicalConfigFileSystem.Instance;

        public bool FileExists(string path) => _inner.FileExists(path);
        public byte[] ReadAllBytes(string path) => _inner.ReadAllBytes(path);
        public void WriteDurable(string path, byte[] content) => _inner.WriteDurable(path, content);
        public void CreateDirectory(string path) => _inner.CreateDirectory(path);
        public IEnumerable<string> EnumerateFiles(string directory, string pattern) => _inner.EnumerateFiles(directory, pattern);

        public void Move(string source, string destination, bool overwrite)
        {
            Moves.Add($"{Path.GetFileName(source)}→{Path.GetFileName(destination)}");
            if (Armed && FailOnPointerRename && Path.GetFileName(destination) == ConfigRepository.PointerFile)
                throw new IOException("simulated crash before the pointer rename");
            _inner.Move(source, destination, overwrite);
        }
    }
}

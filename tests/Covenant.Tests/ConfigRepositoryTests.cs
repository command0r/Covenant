using System.Text;
using Covenant.Core;
using Covenant.Governance;
using Covenant.Host;
using Xunit;

namespace Covenant.Tests;

/// <summary>ADR-0011 repository guarantees: monotonic versions, write ordering (compile → version file → pointer →
/// activation journal), crash recovery (orphan version excluded from history + reported + never reused; journal
/// repaired when the pointer moved but the journal line is missing; corrupt pointer / hash mismatch / corrupt journal
/// refuse to start), rollback as a NEW version, redacted bounded summaries, single-writer optimistic concurrency.</summary>
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

        Assert.Null(reopened.GetVersion(2));                        // an orphan is not history: GET n / rollback see nothing
        Assert.False(reopened.IsActivated(2));

        var next = await Apply(reopened, V2.Hybrid(), 1);
        Assert.Equal(3, next.Applied!.Version);                     // monotonic: v2 is never overwritten or reused
        // Review fix 10: history comes from the activation journal only — the orphan never appears.
        Assert.Equal(new[] { 3, 1 }, reopened.ListVersions(10).Select(c => c.Version).ToList());
        Assert.Equal(new[] { 1, 3 }, reopened.Journal().Select(j => j.Version).ToList());
        Assert.Null(reopened.GetVersion(2));

        // Still true after another restart (journal is durable), and the next number stays above the orphan.
        var again = new ConfigRepository(_dir);
        var reopen2 = again.Open();
        Assert.Empty(reopen2.Errors);
        Assert.Equal(3, reopen2.Active!.Version);
        Assert.Equal(new[] { 3, 1 }, again.ListVersions(10).Select(c => c.Version).ToList());
        Assert.Contains(reopen2.Warnings, w => w.Contains("v2 was written but never activated", StringComparison.Ordinal));
        Assert.Equal(4, (await Apply(again, V2.Hybrid(), 3)).Applied!.Version);
    }

    /// <summary>Review fix 10: crash AFTER the pointer rename but BEFORE the journal append. The pointer is the
    /// activation point, so v2 is active; boot appends a <c>recovered: true</c> journal line and history shows v2.</summary>
    [Fact]
    public async Task Crash_between_pointer_and_journal_is_recovered_at_boot()
    {
        var faulty = new CrashingFileSystem { FailOnJournalAppend = true };
        var repo = Bootstrapped(faulty);
        faulty.Armed = true;
        var activated = new List<int>();
        var r = await Apply(repo, V2.Hybrid(), 1, activated: activated);
        Assert.Equal(ApplyStatus.Applied, r.Status);                // disk says v2 is active: the process honours it
        Assert.Equal(new[] { 2 }, activated);
        Assert.Contains(r.Notes, n => n.Contains("journal", StringComparison.Ordinal));
        Assert.DoesNotContain("\"version\":2", File.ReadAllText(Path.Combine(_dir, ConfigRepository.JournalFile)), StringComparison.Ordinal);

        var reopened = new ConfigRepository(_dir);
        var open = reopened.Open();
        Assert.Empty(open.Errors);
        Assert.Equal(2, open.Active!.Version);
        Assert.Contains(open.Warnings, w => w.Contains("recorded as recovered", StringComparison.Ordinal));
        var line = Assert.Single(reopened.Journal(), j => j.Version == 2);
        Assert.True(line.Recovered);
        Assert.Equal(new[] { 2, 1 }, reopened.ListVersions(10).Select(c => c.Version).ToList());
        Assert.NotNull(reopened.GetVersion(2));
        // Durable: a third boot does not append a second recovered line.
        var third = new ConfigRepository(_dir);
        third.Open();
        Assert.Single(third.Journal(), j => j.Version == 2);
    }

    /// <summary>Review fix 10: the snapshot (provider clients included) is compiled BEFORE any durable write — a
    /// compile failure (client_unbuildable) is Invalid and writes nothing: no version file, no pointer, no journal line.</summary>
    [Fact]
    public async Task Compile_failure_writes_nothing_durable()
    {
        var repo = Bootstrapped();
        var pointerBefore = File.ReadAllBytes(Path.Combine(_dir, ConfigRepository.PointerFile));
        var journalBefore = File.ReadAllBytes(Path.Combine(_dir, ConfigRepository.JournalFile));
        var activated = new List<int>();
        var result = await repo.ApplyAsync(V2.Hybrid(), 1, "console", "edit", null, Ok,
            new Func<CovenantConfig, int>(_ => throw new ConfigCompileException([new ConfigIssue("connections[0]", "client_unbuildable", "boom")])),
            (c, v) => activated.Add(v));
        Assert.Equal(ApplyStatus.Invalid, result.Status);
        Assert.Contains(result.Validation!.Errors, e => e.Code == "client_unbuildable");
        Assert.Empty(activated);
        Assert.Equal(new[] { 1 }, repo.ExistingVersions());
        Assert.False(File.Exists(repo.VersionPath(2) + ".tmp"));
        Assert.Equal(pointerBefore, File.ReadAllBytes(Path.Combine(_dir, ConfigRepository.PointerFile)));
        Assert.Equal(journalBefore, File.ReadAllBytes(Path.Combine(_dir, ConfigRepository.JournalFile)));
        Assert.Equal(1, repo.Active!.Version);
    }

    [Fact]
    public void Journal_corruption_before_the_last_line_refuses_to_start_but_a_torn_last_line_is_tolerated()
    {
        Bootstrapped();
        var journal = Path.Combine(_dir, ConfigRepository.JournalFile);
        File.AppendAllText(journal, "{\"version\":7,\"ut");                 // torn append (crash mid-line)
        var torn = new ConfigRepository(_dir).Open();
        Assert.Empty(torn.Errors);
        Assert.Contains(torn.Warnings, w => w.Contains("torn", StringComparison.Ordinal));

        File.WriteAllText(journal, "garbage\n" + File.ReadAllText(journal));
        var corrupt = new ConfigRepository(_dir).Open();
        Assert.Null(corrupt.Active);
        Assert.Contains(corrupt.Errors, e => e.Contains("corrupt", StringComparison.Ordinal));
    }

    /// <summary>Review fix 13: config-write history is bounded (≤ 500 chars) and redacted (never a secret value).</summary>
    [Fact]
    public async Task Change_summaries_are_redacted_and_bounded()
    {
        const string secret = "sk-live-THIS-IS-A-SECRET-0123456789";
        var repo = new ConfigRepository(_dir, redact: SecretRedaction.ForValues([secret, "vault-value-42"]));
        Assert.True(repo.Open().IsEmpty);
        repo.Bootstrap(V2.Hybrid());
        var summary = $"rotated key {secret} and vault-value-42 and Bearer abcdefghijklmnopqrstuvwx " + string.Join(" ", Enumerable.Repeat("word", 500));
        var r = await repo.ApplyAsync(V2.Hybrid(), 1, "console", summary, null, Ok, c => c.Version, (_, _) => { });
        var stored = r.Applied!.ChangeSummary!;
        Assert.True(stored.Length <= ConfigRepository.MaxSummaryChars, $"{stored.Length} chars");
        Assert.DoesNotContain(secret, stored, StringComparison.Ordinal);
        Assert.DoesNotContain("vault-value-42", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwx", stored, StringComparison.Ordinal);
        Assert.Contains(SecretRedaction.Marker, stored, StringComparison.Ordinal);
        foreach (var file in Directory.GetFiles(_dir, "*", SearchOption.AllDirectories))
            Assert.DoesNotContain(secret, File.ReadAllText(file), StringComparison.Ordinal);
        Assert.True(stored.Length == ConfigRepository.MaxSummaryChars && stored.EndsWith('…'));      // truncated, marked
        Assert.True(repo.Journal().Last().Summary!.Length <= ConfigRepository.MaxSummaryChars);
        var words = string.Join(" ", Enumerable.Repeat("word", 300));                                // 1 499 chars
        Assert.Equal(500, ConfigRepository.BoundSummary(words)!.Length);
        Assert.Equal(words[..499], ConfigRepository.BoundSummary(words[..499]));                     // ≤ 500: unchanged
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
        Assert.DoesNotContain(faulty.Appends, a => a.Contains("\"version\":2", StringComparison.Ordinal));   // no journal line either
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
        public bool FailOnJournalAppend { get; init; }
        public List<string> Moves { get; } = [];
        public List<string> Appends { get; } = [];
        private readonly PhysicalConfigFileSystem _inner = PhysicalConfigFileSystem.Instance;

        public bool FileExists(string path) => _inner.FileExists(path);
        public byte[] ReadAllBytes(string path) => _inner.ReadAllBytes(path);
        public void WriteDurable(string path, byte[] content) => _inner.WriteDurable(path, content);
        public void CreateDirectory(string path) => _inner.CreateDirectory(path);
        public IEnumerable<string> EnumerateFiles(string directory, string pattern) => _inner.EnumerateFiles(directory, pattern);

        public void AppendDurable(string path, byte[] content)
        {
            if (Armed && FailOnJournalAppend) throw new IOException("simulated crash before the journal append");
            Appends.Add(Encoding.UTF8.GetString(content));
            _inner.AppendDurable(path, content);
        }

        public void Move(string source, string destination, bool overwrite)
        {
            Moves.Add($"{Path.GetFileName(source)}→{Path.GetFileName(destination)}");
            if (Armed && FailOnPointerRename && Path.GetFileName(destination) == ConfigRepository.PointerFile)
                throw new IOException("simulated crash before the pointer rename");
            _inner.Move(source, destination, overwrite);
        }
    }

    // Reviewer reproducer (docs/verification-2026-10-16/v2/torn-tail-regression.cs.txt): before the fix, the torn
    // tail was ignored on read but the next append was concatenated onto it, corrupting history at the 2nd restart.
    [Fact]
    public async Task Torn_journal_tail_is_quarantined_and_repaired_so_later_activations_and_restarts_stay_clean()
    {
        _ = Bootstrapped();
        var journalPath = Path.Combine(_dir, ConfigRepository.JournalFile);
        File.AppendAllText(journalPath, "{\"version\":7");                    // interrupted append, no newline

        var reopened = new ConfigRepository(_dir);
        Assert.Empty(reopened.Open().Errors);
        Assert.Single(Directory.GetFiles(_dir, ConfigRepository.JournalFile + ".torn-*"));   // evidence preserved
        Assert.EndsWith("\n", File.ReadAllText(journalPath));                 // repaired before any append
        await Apply(reopened, V2.Hybrid(), 1);

        var second = new ConfigRepository(_dir);
        Assert.Empty(second.Open().Errors);
        await Apply(second, V2.Hybrid(), 2);

        var third = new ConfigRepository(_dir);
        Assert.Empty(third.Open().Errors);
        Assert.Equal(new[] { 3, 2, 1 }, third.Journal().Select(j => j.Version).Reverse().ToArray());
        foreach (var line in File.ReadAllLines(journalPath).Where(l => l.Length > 0))
            Assert.StartsWith("{", line);                                     // one JSON object per line, never concatenated
    }

    [Fact]
    public void Complete_but_corrupt_last_journal_line_is_not_treated_as_torn_and_refuses_to_start()
    {
        Bootstrapped();
        File.AppendAllText(Path.Combine(_dir, ConfigRepository.JournalFile), "not json\n");   // newline-terminated ⇒ corruption
        Assert.NotEmpty(new ConfigRepository(_dir).Open().Errors);
    }
}

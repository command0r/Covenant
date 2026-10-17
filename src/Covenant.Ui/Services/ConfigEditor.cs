using System.Net;
using System.Text.Json;
using Covenant.Ui.Logic;
using Covenant.Ui.Models;

namespace Covenant.Ui.Services;

/// <summary>The stale-edit state after a 412 (or a validate warning <c>stale_base_version</c>): the draft is kept,
/// nothing is overwritten. <see cref="ServerChanges"/>/<see cref="YourChanges"/> are filled by Compare.</summary>
public sealed record ConflictInfo(int? ActiveVersion, string Message)
{
    public IReadOnlyList<string>? ServerChanges { get; init; }
    public IReadOnlyList<string>? YourChanges { get; init; }
}

/// <summary>Shared configuration editing for every admin page (ADR-0011): loads GET /admin/config (version + ETag),
/// holds ONE deep-copied draft, validates on demand (POST /admin/config/validate — never per keystroke), and saves with
/// PUT If-Match. Success is reported ONLY after the Host's 200 carrying the new version; then config, catalog,
/// workspace and status are reloaded. 412 keeps the draft and reports the conflict; 422 maps field errors onto the
/// draft. Rollback activates a historical version as a NEW version. Cleared on disconnect.</summary>
public sealed class ConfigEditor : IDisposable
{
    private readonly AppState _state;
    private readonly AdminApi _api;
    private readonly WorkspaceConfig _workspace;
    private readonly CatalogState _catalog;
    private readonly ConsoleSession _session;
    private Task? _loading;
    private string? _validatedJson;

    public ConfigEditor(AppState state, AdminApi api, WorkspaceConfig workspace, CatalogState catalog, ConsoleSession session)
    {
        _state = state;
        _api = api;
        _workspace = workspace;
        _catalog = catalog;
        _session = session;
        _state.Disconnected += Clear;
    }

    public event Action? Changed;

    // ---- applied configuration ----
    public ConfigEnvelopeDto? Applied { get; private set; }
    public string? LoadError { get; private set; }
    /// <summary>True when GET /admin/config answered 404: this Host has no versioned configuration (pre-v2).</summary>
    public bool EndpointMissing { get; private set; }
    public DateTimeOffset? LoadedUtc { get; private set; }
    public bool Loading => _loading is { IsCompleted: false };
    public int? AppliedVersion => Applied?.Version;

    // ---- draft ----
    public ConfigDraft? Session { get; private set; }
    /// <summary>Which page owns the draft ("connections", "policies", …).</summary>
    public string? Owner { get; private set; }
    public bool Editing => Session is not null;
    public bool IsDirty => Session?.IsDirty == true;
    public CovenantConfigDto? Draft => Session?.Draft;
    /// <summary>What a page shows: the draft while editing, else the applied configuration.</summary>
    public CovenantConfigDto? Current => Session?.Draft ?? Applied?.Config;

    // ---- validation / impact ----
    public bool Validating { get; private set; }
    public ConfigValidationResultDto? Validation { get; private set; }
    public string? ValidationError { get; private set; }
    /// <summary>The last preview was computed for exactly the current draft content.</summary>
    public bool ValidationCurrent => Validation is not null && Session is not null
                                     && string.Equals(_validatedJson, ConfigCopy.Canonical(Session.Draft), StringComparison.Ordinal);
    public bool CanSave => Session is not null && Session.IsDirty && ValidationCurrent && Validation!.Valid && !Saving && Conflict is null;

    // ---- save / rollback ----
    public bool Saving { get; private set; }
    public EditOutcome? LastOutcome { get; private set; }
    public ConflictInfo? Conflict { get; private set; }
    /// <summary>Shown after a confirmed activation: "Applied version v8".</summary>
    public string? AppliedNotice { get; private set; }

    // ---- history ----
    public List<ConfigVersionDto>? Versions { get; private set; }
    public string? VersionsError { get; private set; }
    public bool VersionsLoading { get; private set; }

    /// <summary>Field issues: from the last 422 when that is the latest answer, else from the last preview.</summary>
    public IssueIndex Issues
    {
        get
        {
            if (LastOutcome is { Kind: EditOutcomeKind.Invalid } inv)
                return new IssueIndex(inv.Errors ?? [], inv.Warnings ?? []);
            if (Validation is { } v) return new IssueIndex(v.Errors, v.Warnings);
            return IssueIndex.Empty;
        }
    }

    public Task EnsureLoadedAsync(bool force = false)
    {
        if (!_state.Connected) return Task.CompletedTask;
        if (_loading is { IsCompleted: false } running) return running;
        if (!force && Applied is not null) return Task.CompletedTask;
        _loading = LoadAsync();
        return _loading;
    }

    private async Task LoadAsync()
    {
        try
        {
            var env = await _api.GetConfigAsync();
            Applied = env;
            LoadError = null;
            EndpointMissing = false;
            LoadedUtc = DateTimeOffset.UtcNow;
        }
        catch (ApiException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            EndpointMissing = true;
            LoadError = "This Host does not expose versioned configuration (GET /admin/config is missing) — editing is not available.";
        }
        catch (ApiException ex) when (ex.Status == HttpStatusCode.Unauthorized)
        {
            // ConsoleSession disconnects.
        }
        catch (Exception ex)
        {
            LoadError = "Configuration could not be loaded: " + ex.Message;
        }
        finally
        {
            Notify();
        }
    }

    /// <summary>Starts (or continues) editing for <paramref name="owner"/>. A clean draft of another page is replaced.</summary>
    public bool BeginEdit(string owner)
    {
        if (Applied?.Config is not { } cfg) return false;
        if (Session is not null && Owner == owner) return true;
        if (Session is not null && Session.IsDirty) return false; // the guard asks before another page takes over
        Session = new ConfigDraft(Applied.Version, Applied.Etag, cfg);
        Owner = owner;
        ResetResults();
        AppliedNotice = null;
        Notify();
        return true;
    }

    /// <summary>Call after mutating <see cref="Draft"/>: the preview becomes stale (Save needs a fresh preview).</summary>
    public void Touch()
    {
        if (LastOutcome is { Kind: not EditOutcomeKind.Stale }) LastOutcome = null;
        AppliedNotice = null;
        Notify();
    }

    /// <summary>Discards the draft (Cancel, or confirmed navigation away). Never touches the server.</summary>
    public void Discard()
    {
        Session = null;
        Owner = null;
        ResetResults();
        Notify();
    }

    /// <summary>Releases a CLEAN draft owned by <paramref name="owner"/> (page left). A dirty draft is kept for the guard.</summary>
    public void Release(string owner)
    {
        if (Owner == owner && !IsDirty) Discard();
    }

    private void ResetResults()
    {
        Validation = null;
        ValidationError = null;
        _validatedJson = null;
        LastOutcome = null;
        Conflict = null;
    }

    /// <summary>POST /admin/config/validate for the current draft. Returns true when the preview is valid.</summary>
    public async Task<bool> ValidateAsync()
    {
        if (Session is not { } s || Validating) return false;
        Validating = true;
        ValidationError = null;
        Notify();
        var json = ConfigCopy.Canonical(s.Draft);
        try
        {
            var raw = await _api.ValidateConfigRawAsync(new ConfigValidateRequestDto { BaseVersion = s.BaseVersion, Config = ConfigCopy.Deep(s.Draft) });
            if (raw.IsSuccess && Parse(raw.Body, UiJsonContext.Default.ConfigValidationResultDto) is { } result)
            {
                if (!ReferenceEquals(Session, s)) return false;
                Validation = result;
                _validatedJson = json;
                if (LastOutcome is { Kind: EditOutcomeKind.Invalid }) LastOutcome = null;
                if (result.Warnings.Any(w => w.Code == "stale_base_version"))
                    Conflict = new ConflictInfo(result.ActiveVersion, EditOutcomes.StaleMessage(result.ActiveVersion));
                return result.Valid;
            }
            ValidationError = raw.Status switch
            {
                (int)HttpStatusCode.NotFound => "Preview is not available on this Host (POST /admin/config/validate missing).",
                (int)HttpStatusCode.Unauthorized => "Admin token rejected.",
                _ => "Preview failed: HTTP " + raw.Status.ToString(System.Globalization.CultureInfo.InvariantCulture) + ErrorReason(raw.Body),
            };
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ValidationError = "Preview failed: " + ex.Message;
            return false;
        }
        finally
        {
            Validating = false;
            Notify();
        }
    }

    /// <summary>PUT /admin/config with If-Match. Only a 200 with a version is success; then everything reloads.</summary>
    public async Task<EditOutcome> SaveAsync(string? summary = null)
    {
        if (Session is not { } s) return new EditOutcome(EditOutcomeKind.Failed, "Nothing to save.");
        if (!CanSave) return new EditOutcome(EditOutcomeKind.Failed, "Preview the impact of the current draft before saving.");
        Saving = true;
        AppliedNotice = null;
        Notify();
        EditOutcome outcome;
        try
        {
            var body = new ConfigPutRequestDto
            {
                Config = ConfigCopy.Deep(s.Draft),
                ChangeSummary = string.IsNullOrWhiteSpace(summary) ? ChangeSummary.Text(s.Base, s.Draft) : summary.Trim(),
            };
            var raw = await _api.PutConfigRawAsync(body, s.BaseEtag);
            outcome = EditOutcomes.FromResponse(raw.Status, raw.Body);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            outcome = new EditOutcome(EditOutcomeKind.Failed, "Save failed before the Host answered: " + ex.Message + " — nothing is assumed applied. Reload to see the active version.");
        }

        LastOutcome = outcome;
        if (outcome.Kind == EditOutcomeKind.Stale)
            Conflict = new ConflictInfo(outcome.Version, outcome.Message);

        if (outcome.Succeeded)
        {
            Session = null;
            Owner = null;
            Validation = null;
            _validatedJson = null;
            Conflict = null;
            await AfterActivationAsync(outcome.Version!.Value, rollback: false);
        }
        Saving = false;
        Notify();
        return outcome;
    }

    private async Task AfterActivationAsync(int version, bool rollback)
    {
        await EnsureLoadedAsync(force: true);
        try { await _workspace.RefreshAsync(); } catch (Exception) { /* each source reports its own error */ }
        try { await _catalog.RefreshAsync(); } catch (Exception) { /* idem */ }
        await _session.TryRefreshAsync();
        if (Versions is not null) await LoadVersionsAsync();
        var v = version.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var head = rollback ? "Rollback applied as new version v" + v : "Applied version v" + v;
        AppliedNotice = Applied is null
            ? head + " (server confirmed). Reloading the configuration view failed: " + (LoadError ?? "unknown error")
            : Applied.Version == version
                ? head + " — new requests now bind to v" + v + "."
                : head + " (server confirmed); the Host now reports v" + Applied.Version.ToString(System.Globalization.CultureInfo.InvariantCulture) + " — another change landed since.";
    }

    /// <summary>After a conflict: fetch the active version and list what changed there vs. what the draft changes.
    /// Nothing is merged or overwritten.</summary>
    public async Task CompareAsync()
    {
        if (Session is not { } s || Conflict is not { } c) return;
        try
        {
            var latest = await _api.GetConfigAsync();
            if (latest.Config is not { } lc) return;
            Conflict = c with
            {
                ActiveVersion = latest.Version,
                Message = EditOutcomes.StaleMessage(latest.Version),
                ServerChanges = ChangeSummary.Lines(s.Base, lc),
                YourChanges = ChangeSummary.Lines(s.Base, s.Draft),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Conflict = c with { ServerChanges = ["Could not load the active version: " + ex.Message] };
        }
        Notify();
    }

    /// <summary>Discards the draft and loads the active version (the conflict's "Reload").</summary>
    public async Task ReloadDiscardingDraftAsync()
    {
        Discard();
        await EnsureLoadedAsync(force: true);
    }

    public async Task LoadVersionsAsync()
    {
        VersionsLoading = true;
        VersionsError = null;
        Notify();
        try
        {
            Versions = await _api.GetConfigVersionsAsync(50);
        }
        catch (ApiException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            VersionsError = "Version history is not available on this Host.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            VersionsError = "Version history could not be loaded: " + ex.Message;
        }
        finally
        {
            VersionsLoading = false;
            Notify();
        }
    }

    /// <summary>POST /admin/config/rollback with the expected active version. A 412 means someone applied first.</summary>
    public async Task<EditOutcome> RollbackAsync(int targetVersion)
    {
        if (Applied is null) return new EditOutcome(EditOutcomeKind.Failed, "Configuration not loaded.");
        if (IsDirty) return new EditOutcome(EditOutcomeKind.Failed, "Save or cancel your unsaved changes before rolling back.");
        Saving = true;
        AppliedNotice = null;
        Notify();
        EditOutcome outcome;
        try
        {
            var raw = await _api.RollbackConfigRawAsync(targetVersion, Applied.Version);
            outcome = EditOutcomes.FromResponse(raw.Status, raw.Body, rollback: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            outcome = new EditOutcome(EditOutcomeKind.Failed, "Rollback failed before the Host answered: " + ex.Message + " — nothing is assumed applied.");
        }
        LastOutcome = outcome;
        if (outcome.Succeeded)
        {
            Discard();
            LastOutcome = outcome;
            await AfterActivationAsync(outcome.Version!.Value, rollback: true);
        }
        else if (outcome.Kind == EditOutcomeKind.Stale)
        {
            await EnsureLoadedAsync(force: true);
        }
        Saving = false;
        Notify();
        return outcome;
    }

    private static T? Parse<T>(string body, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) where T : class
    {
        try { return JsonSerializer.Deserialize(body, info); }
        catch (JsonException) { return null; }
    }

    private static string ErrorReason(string body)
    {
        var err = Parse(body, UiJsonContext.Default.ConfigErrorResponseDto);
        var r = err?.Reason ?? err?.Error;
        return string.IsNullOrWhiteSpace(r) ? "" : ": " + (r.Length > 300 ? r[..299] + "…" : r);
    }

    private void Clear()
    {
        Applied = null;
        LoadError = null;
        EndpointMissing = false;
        LoadedUtc = null;
        _loading = null;
        Session = null;
        Owner = null;
        Versions = null;
        VersionsError = null;
        AppliedNotice = null;
        ResetResults();
        Notify();
    }

    private void Notify() => Changed?.Invoke();

    public void Dispose() => _state.Disconnected -= Clear;
}

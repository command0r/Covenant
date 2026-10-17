using System.Net;
using Covenant.Ui.Models;

namespace Covenant.Ui.Services;

/// <summary>Server-side facts the request workspace needs, loaded with the admin token and shared by Playground,
/// the Overview composer and the header: test applications (GET /admin/applications — never credentials), the
/// active redacted configuration (GET /admin/config: profiles, connections and their boundaries, policies) and
/// budget states (GET /admin/budgets). Each source fails independently; a 404 means "this Host does not expose it
/// yet" and is reported as such, never papered over. Cleared on disconnect.</summary>
public sealed class WorkspaceConfig : IDisposable
{
    private readonly AppState _state;
    private readonly AdminApi _api;
    private Task? _inflight;

    public WorkspaceConfig(AppState state, AdminApi api)
    {
        _state = state;
        _api = api;
        _state.Disconnected += Clear;
        _state.Changed += OnStateChanged;
    }

    public event Action? Changed;

    public List<ApplicationSummaryDto>? Applications { get; private set; }
    public string? ApplicationsError { get; private set; }

    public ConfigEnvelopeDto? Config { get; private set; }
    public string? ConfigError { get; private set; }

    public List<BudgetStateDto>? Budgets { get; private set; }
    public string? BudgetsError { get; private set; }

    public DateTimeOffset? LoadedUtc { get; private set; }
    public bool Loading => _inflight is { IsCompleted: false };

    public IReadOnlyList<WorkloadProfileDto>? Profiles => Config?.Config?.Profiles;
    public IReadOnlyList<ConnectionDto>? Connections => Config?.Config?.Connections;

    /// <summary>Loads everything once per connection; concurrent callers share the in-flight load (so a component
    /// that awaits this sees the result), later calls are no-ops unless <paramref name="force"/>.</summary>
    public Task EnsureLoadedAsync(bool force = false)
    {
        if (!_state.Connected) return Task.CompletedTask;
        if (_inflight is { IsCompleted: false } running) return running;
        if (!force && LoadedUtc is not null) return Task.CompletedTask;
        _inflight = LoadAllAsync();
        return _inflight;
    }

    /// <summary>Loads on connect (also after a session restore) so every page sees the same facts.</summary>
    private void OnStateChanged()
    {
        if (_state.Connected && LoadedUtc is null && _inflight is null) _ = EnsureLoadedAsync();
    }

    private async Task LoadAllAsync()
    {
        try
        {
            var apps = LoadAsync(() => _api.GetApplicationsAsync(), "Test applications");
            var config = LoadAsync(() => _api.GetConfigAsync(), "Configuration");
            var budgets = LoadAsync(() => _api.GetBudgetsAsync(), "Budgets");
            var (a, ae) = await apps;
            var (c, ce) = await config;
            var (b, be) = await budgets;
            if (!_state.Connected) return;
            Applications = a ?? Applications;
            ApplicationsError = ae;
            Config = c ?? Config;
            ConfigError = ce;
            Budgets = b ?? Budgets;
            BudgetsError = be;
            LoadedUtc = DateTimeOffset.UtcNow;
        }
        finally
        {
            Notify();
        }
    }

    public Task RefreshAsync() => EnsureLoadedAsync(force: true);

    private static async Task<(T? Value, string? Error)> LoadAsync<T>(Func<Task<T>> call, string what) where T : class
    {
        try
        {
            return (await call(), null);
        }
        catch (ApiException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            return (null, what + ": not available on this Host yet (endpoint missing — integration pending).");
        }
        catch (ApiException ex) when (ex.Status == HttpStatusCode.Unauthorized)
        {
            return (null, null); // ConsoleSession disconnects.
        }
        catch (Exception ex)
        {
            return (null, what + " could not be loaded: " + ex.Message);
        }
    }

    private void Clear()
    {
        Applications = null;
        ApplicationsError = null;
        Config = null;
        ConfigError = null;
        Budgets = null;
        BudgetsError = null;
        LoadedUtc = null;
        _inflight = null;
        Notify();
    }

    private void Notify() => Changed?.Invoke();

    public void Dispose()
    {
        _state.Disconnected -= Clear;
        _state.Changed -= OnStateChanged;
    }
}

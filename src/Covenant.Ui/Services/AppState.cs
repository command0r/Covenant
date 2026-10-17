using Covenant.Ui.Models;

namespace Covenant.Ui.Services;

public enum FeedMode { None, Stream, Polling }

/// <summary>The one place the console keeps state across navigation: connection, the last status snapshot and
/// its freshness, pause state, and the admin token (mirrored to tab-scoped sessionStorage by ConsoleSession).
/// Console v2 (ADR-0012): the console holds NO application credential — requests run as an authorized test
/// application resolved by the Host. Pure state: no HTTP, no JS. Raises Changed; components re-render via
/// InvokeAsync(StateHasChanged).</summary>
public sealed class AppState
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(8);

    public event Action? Changed;

    /// <summary>Raised by <see cref="Disconnect"/> before Changed. Session-bound services (PlaygroundState,
    /// RequestsViewState, CatalogState) subscribe and drop everything they hold: prompts, responses,
    /// selections, the catalog. Nothing of that is ever persisted, so reload clears it too.</summary>
    public event Action? Disconnected;

    public string? AdminToken { get; private set; }
    public bool Connected { get; private set; }

    /// <summary>True until the startup check of sessionStorage has finished; pages show a skeleton instead of
    /// flashing "not connected" at a user who is about to be reconnected.</summary>
    public bool Restoring { get; private set; } = true;

    public void RestoreComplete()
    {
        if (!Restoring) return;
        Restoring = false;
        Notify();
    }

    public StatusReport? LastStatus { get; private set; }
    public DateTimeOffset? LastUpdatedUtc { get; private set; }
    public bool IsStale { get; private set; }

    public FeedMode FeedMode { get; private set; }
    /// <summary>Human-readable feed problem ("Connection lost — retrying"), null while healthy.</summary>
    public string? FeedError { get; private set; }

    public CatalogResponse? Catalog { get; private set; }
    public DateTimeOffset? CatalogUpdatedUtc { get; private set; }
    public string? CatalogError { get; private set; }

    /// <summary>Why the user is on /connect (e.g. "Token rejected", "Disconnected"); cleared on connect.</summary>
    public string? ConnectMessage { get; private set; }

    public bool Paused => LastStatus?.KillSwitch.Engaged == true;
    public string? PauseReason => LastStatus?.KillSwitch.Reason;
    public string? Environment => LastStatus?.Environment;
    public string EnvironmentLabel => string.IsNullOrWhiteSpace(Environment) ? "Environment not reported" : Environment;

    public void SetToken(string token)
    {
        AdminToken = token;
        Notify();
    }

    public void Connect(StatusReport status)
    {
        Connected = true;
        ConnectMessage = null;
        FeedError = null;
        ApplyStatus(status);
    }

    public void ApplyStatus(StatusReport status)
    {
        LastStatus = status;
        LastUpdatedUtc = DateTimeOffset.UtcNow;
        IsStale = false;
        FeedError = null;
        Notify();
    }

    public void SetFeedMode(FeedMode mode)
    {
        if (FeedMode == mode) return;
        FeedMode = mode;
        Notify();
    }

    public void MarkFeedFailure(string message)
    {
        FeedError = message;
        IsStale = true;
        Notify();
    }

    public void SetCatalog(CatalogResponse catalog)
    {
        Catalog = catalog;
        CatalogUpdatedUtc = DateTimeOffset.UtcNow;
        CatalogError = null;
        Notify();
    }

    public void SetCatalogError(string message)
    {
        CatalogError = message;
        Notify();
    }

    /// <summary>Called once a second by the feed; flips IsStale when the last snapshot ages past StaleAfter.
    /// Only notifies on a change so the shell does not re-render every tick for nothing.</summary>
    public void Tick(DateTimeOffset nowUtc)
    {
        if (!Connected) return;
        var stale = LastUpdatedUtc is null || nowUtc - LastUpdatedUtc.Value > StaleAfter;
        if (stale != IsStale)
        {
            IsStale = stale;
            Notify();
        }
    }

    /// <summary>Forgets everything that belongs to this connection. Does NOT touch the kill switch:
    /// disconnecting the console never pauses Covenant.</summary>
    public void Disconnect(string? message)
    {
        AdminToken = null;
        Connected = false;
        LastStatus = null;
        LastUpdatedUtc = null;
        IsStale = false;
        FeedMode = FeedMode.None;
        FeedError = null;
        Catalog = null;
        CatalogUpdatedUtc = null;
        CatalogError = null;
        ConnectMessage = message;
        Disconnected?.Invoke();
        Notify();
    }

    public int SecondsSinceUpdate(DateTimeOffset nowUtc)
        => LastUpdatedUtc is null ? -1 : (int)Math.Max(0, (nowUtc - LastUpdatedUtc.Value).TotalSeconds);

    private void Notify() => Changed?.Invoke();
}

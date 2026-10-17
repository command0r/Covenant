using Covenant.Ui.Models;
using Microsoft.AspNetCore.Components;

namespace Covenant.Ui.Services;

/// <summary>Connect / disconnect / restore for the console session. Owns the only two side effects of a
/// connection — mirroring the admin token to tab-scoped sessionStorage and running the status feed — and the
/// 401 handling that every page relies on: the token is dropped, the feed stops, and the user lands on
/// /connect with "Token rejected". Disconnecting never calls the kill switch.</summary>
public sealed class ConsoleSession : IDisposable
{
    public const string TokenStorageKey = "covenant.admin-token";

    private readonly AppState _state;
    private readonly AdminApi _api;
    private readonly StatusFeed _feed;
    private readonly Interop _interop;
    private readonly NavigationManager _nav;
    private bool _restored;

    public ConsoleSession(AppState state, AdminApi api, StatusFeed feed, Interop interop, NavigationManager nav)
    {
        _state = state;
        _api = api;
        _feed = feed;
        _interop = interop;
        _nav = nav;
        _api.Unauthorized += OnUnauthorized;
    }

    /// <summary>At startup: if this tab still holds a token, validate it against GET /admin/status and resume.
    /// A rejected token is removed so the console cannot sit on a page looking authenticated.</summary>
    public async Task RestoreAsync()
    {
        if (_restored) return;
        _restored = true;
        try
        {
            string? token;
            try { token = await _interop.SessionGetAsync(TokenStorageKey); }
            catch (Exception) { token = null; }
            if (string.IsNullOrWhiteSpace(token)) return;

            _state.SetToken(token);
            try
            {
                var status = await _api.GetStatusAsync();
                _state.Connect(status);
                _feed.Start();
                _ = LoadCatalogAsync();
            }
            catch (ApiException ex) when (ex.Status == System.Net.HttpStatusCode.Unauthorized)
            {
                // OnUnauthorized already disconnected and routed; nothing else to do.
            }
            catch (Exception ex)
            {
                // Host unreachable right now: keep the token (the tab is still ours) but show the honest state.
                _state.Disconnect("Connection lost — " + ex.Message);
                _state.SetToken(token);
            }
        }
        finally
        {
            _state.RestoreComplete();
        }
    }

    /// <summary>Validates the token with GET /admin/status. On success the token is kept for this tab and the
    /// feed starts. Throws ApiException("Token rejected") on 401; other failures throw with the reason.</summary>
    public async Task ConnectAsync(string token)
    {
        _state.SetToken(token);
        StatusReport status;
        try
        {
            status = await _api.GetStatusAsync();
        }
        catch (ApiException ex) when (ex.Status == System.Net.HttpStatusCode.Unauthorized)
        {
            throw; // state already cleared by OnUnauthorized
        }
        catch (Exception)
        {
            _state.Disconnect(null);
            throw;
        }

        try { await _interop.SessionSetAsync(TokenStorageKey, token); }
        catch (Exception) { /* storage unavailable: the session still works for this page load */ }

        _state.Connect(status);
        _feed.Start();
        _ = LoadCatalogAsync();
    }

    /// <summary>Clears the token (memory + sessionStorage) and the in-memory application key, stops the feed.
    /// Nothing is sent to the Host — in particular the kill switch is untouched.</summary>
    public async Task DisconnectAsync(string? message = "Disconnected")
    {
        _feed.Stop();
        _state.Disconnect(message);
        try { await _interop.SessionRemoveAsync(TokenStorageKey); }
        catch (Exception) { /* nothing to remove */ }
    }

    public async Task LoadCatalogAsync()
    {
        if (!_state.Connected) return;
        try
        {
            var catalog = await _api.GetCatalogAsync();
            _state.SetCatalog(catalog);
        }
        catch (ApiException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound)
        {
            _state.SetCatalogError("Catalog endpoint not available on this Host");
        }
        catch (ApiException ex) when (ex.Status == System.Net.HttpStatusCode.Unauthorized)
        {
            // handled by OnUnauthorized
        }
        catch (Exception ex)
        {
            _state.SetCatalogError(ex.Message);
        }
    }

    /// <summary>Pause/resume via POST /admin/kill-switch, then re-sync from the server. Returns the error text
    /// to show, or null on success; the caller never assumes success from the absence of an exception alone —
    /// the refreshed status is what the shell renders.</summary>
    public async Task<string?> SetPausedAsync(bool paused, string? reason)
    {
        try
        {
            await _api.SetKillSwitchAsync(paused, reason);
        }
        catch (Exception ex)
        {
            await TryRefreshAsync();
            return ex.Message;
        }
        await TryRefreshAsync();
        return null;
    }

    public async Task TryRefreshAsync()
    {
        try { await _feed.RefreshNowAsync(); }
        catch (Exception ex) { _state.MarkFeedFailure("Could not refresh status — " + ex.Message); }
    }

    private void OnUnauthorized()
    {
        _feed.Stop();
        _state.Disconnect("Token rejected");
        _ = RemoveStoredTokenAsync();
        _nav.NavigateTo("connect");
    }

    private async Task RemoveStoredTokenAsync()
    {
        try { await _interop.SessionRemoveAsync(TokenStorageKey); }
        catch (Exception) { /* best effort */ }
    }

    public void Dispose() => _api.Unauthorized -= OnUnauthorized;
}

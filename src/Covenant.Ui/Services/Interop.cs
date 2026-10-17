using Microsoft.JSInterop;

namespace Covenant.Ui.Services;

/// <summary>Typed wrapper over wwwroot/js/interop.js. Everything the console asks the browser for goes through
/// here, so the storage rules are auditable in one place: sessionStorage for the admin token only,
/// localStorage for the theme and the allow-listed console preferences (interop.js PREF_KEYS) only — never a
/// credential, prompt, response or server configuration.</summary>
public sealed class Interop(IJSRuntime js)
{
    private const string Prefix = "covenantInterop.";

    public ValueTask<string?> SessionGetAsync(string key) => js.InvokeAsync<string?>(Prefix + "sessionGet", key);
    public ValueTask SessionSetAsync(string key, string value) => js.InvokeVoidAsync(Prefix + "sessionSet", key, value);
    public ValueTask SessionRemoveAsync(string key) => js.InvokeVoidAsync(Prefix + "sessionRemove", key);

    public ValueTask<string> GetThemeAsync() => js.InvokeAsync<string>(Prefix + "getTheme");
    public ValueTask<string> SetThemeAsync(string theme) => js.InvokeAsync<string>(Prefix + "setTheme", theme);

    /// <summary>Browser-local console preference (allow-listed names only, e.g. "stream-default"); null when unset.</summary>
    public ValueTask<string?> PrefGetAsync(string name) => js.InvokeAsync<string?>(Prefix + "prefGet", name);
    public ValueTask<bool> PrefSetAsync(string name, string value) => js.InvokeAsync<bool>(Prefix + "prefSet", name, value);

    public ValueTask<bool> FocusElementAsync(string id) => js.InvokeAsync<bool>(Prefix + "focusElement", id);
    public ValueTask TrapFocusAsync(string containerId) => js.InvokeVoidAsync(Prefix + "trapFocus", containerId);
    public ValueTask ReleaseFocusAsync(string containerId) => js.InvokeVoidAsync(Prefix + "releaseFocus", containerId);

    /// <summary>Copies text the operator explicitly chose to copy (a request id or the response shown on screen —
    /// never a credential) to the clipboard. Returns false if the browser refused.</summary>
    public ValueTask<bool> CopyTextAsync(string text) => js.InvokeAsync<bool>(Prefix + "copyText", text);

    public ValueTask DownloadTextAsync(string fileName, string contentType, string text)
        => js.InvokeVoidAsync(Prefix + "downloadText", fileName, contentType, text);
}

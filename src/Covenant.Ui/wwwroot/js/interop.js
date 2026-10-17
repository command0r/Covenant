// Covenant console — the entire JS surface. No libraries, no network, no logging.
//
// Storage rules (TASK 01): localStorage holds ONLY the theme choice. The admin token lives in
// sessionStorage (tab-scoped; closing the tab disconnects). Application keys, prompts and responses are
// never persisted anywhere by this file — they stay in WASM memory.
(function () {
  "use strict";

  var THEME_KEY = "covenant.theme";

  function applyTheme(theme) {
    var t = theme === "light" ? "light" : "dark";
    document.documentElement.setAttribute("data-theme", t);
    return t;
  }

  // Apply the persisted theme immediately (before the runtime boots) to avoid a flash.
  try { applyTheme(window.localStorage.getItem(THEME_KEY)); } catch (e) { applyTheme("dark"); }

  var traps = {}; // containerId -> keydown handler (focus trap for drawers/dialogs)

  function focusables(container) {
    var sel = 'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';
    return Array.prototype.filter.call(container.querySelectorAll(sel), function (el) {
      return el.offsetParent !== null || el === document.activeElement;
    });
  }

  window.covenantInterop = {
    sessionGet: function (key) {
      try { return window.sessionStorage.getItem(key); } catch (e) { return null; }
    },
    sessionSet: function (key, value) {
      try { window.sessionStorage.setItem(key, value); } catch (e) { /* storage unavailable: stays in memory */ }
    },
    sessionRemove: function (key) {
      try { window.sessionStorage.removeItem(key); } catch (e) { /* nothing to remove */ }
    },

    getTheme: function () {
      return document.documentElement.getAttribute("data-theme") || "dark";
    },
    setTheme: function (theme) {
      var t = applyTheme(theme);
      try { window.localStorage.setItem(THEME_KEY, t); } catch (e) { /* preference simply won't persist */ }
      return t;
    },

    focusElement: function (id) {
      var el = document.getElementById(id);
      if (el && typeof el.focus === "function") { el.focus(); return true; }
      return false;
    },

    // Keep Tab/Shift+Tab inside the container while a drawer or dialog is open.
    trapFocus: function (containerId) {
      var container = document.getElementById(containerId);
      if (!container || traps[containerId]) return;
      var handler = function (ev) {
        if (ev.key !== "Tab") return;
        var items = focusables(container);
        if (items.length === 0) { ev.preventDefault(); container.focus(); return; }
        var first = items[0], last = items[items.length - 1];
        if (ev.shiftKey && (document.activeElement === first || document.activeElement === container)) {
          ev.preventDefault(); last.focus();
        } else if (!ev.shiftKey && document.activeElement === last) {
          ev.preventDefault(); first.focus();
        }
      };
      traps[containerId] = handler;
      document.addEventListener("keydown", handler, true);
    },
    releaseFocus: function (containerId) {
      var handler = traps[containerId];
      if (!handler) return;
      document.removeEventListener("keydown", handler, true);
      delete traps[containerId];
    },

    // Copy operator-selected text (a request id or the visible response — never a credential). Resolves true on success.
    copyText: function (text) {
      try {
        if (navigator.clipboard && navigator.clipboard.writeText) {
          return navigator.clipboard.writeText(text).then(function () { return true; }, function () { return false; });
        }
      } catch (e) { /* fall through */ }
      return false;
    },

    // Evidence export: the JSON report, exactly as the server returned it, as a download.
    downloadText: function (fileName, contentType, text) {
      var blob = new Blob([text], { type: contentType || "application/octet-stream" });
      var url = URL.createObjectURL(blob);
      var a = document.createElement("a");
      a.href = url;
      a.download = fileName;
      document.body.appendChild(a);
      a.click();
      document.body.removeChild(a);
      setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
    }
  };

  // The framework's error banner has no built-in dismiss handler.
  document.addEventListener("DOMContentLoaded", function () {
    var ui = document.getElementById("blazor-error-ui");
    if (!ui) return;
    var dismiss = ui.querySelector(".dismiss");
    if (dismiss) dismiss.addEventListener("click", function () { ui.style.display = "none"; });
  });
})();

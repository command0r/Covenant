namespace Covenant.Host;

/// <summary>
/// The FinOps + governance dashboard, embedded as a single self-contained page: no CDN, no external
/// assets, no build step — the appliance ships one artifact and makes no egress (root CLAUDE.md).
/// The page shell is public; every data call requires the admin token, entered in the UI and kept
/// in the browser only.
/// </summary>
public static class AdminUi
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Covenant — governance &amp; FinOps</title>
<style>
  :root {
    --bg: #0f1419; --card: #1a2129; --border: #2a3441; --text: #e6edf3; --muted: #8b98a5;
    --blue: #4a9eff; --orange: #ff9f43; --green: #2ecc71; --purple: #b07cc6; --red: #e74c3c;
  }
  * { box-sizing: border-box; margin: 0; }
  body { background: var(--bg); color: var(--text); font: 14px/1.5 -apple-system, "Segoe UI", Roboto, sans-serif; padding: 24px; }
  h1 { font-size: 18px; font-weight: 600; }
  h2 { font-size: 13px; font-weight: 600; color: var(--muted); text-transform: uppercase; letter-spacing: .06em; margin-bottom: 12px; }
  header { display: flex; align-items: center; gap: 16px; flex-wrap: wrap; margin-bottom: 20px; }
  header .spacer { flex: 1; }
  .badge { padding: 3px 10px; border-radius: 20px; font-size: 12px; font-weight: 600; }
  .badge.ok { background: rgba(46,204,113,.15); color: var(--green); }
  .badge.bad { background: rgba(231,76,60,.15); color: var(--red); }
  input[type=password] { background: var(--card); border: 1px solid var(--border); color: var(--text); border-radius: 8px; padding: 7px 10px; width: 220px; }
  button { background: var(--card); border: 1px solid var(--border); color: var(--text); border-radius: 8px; padding: 7px 14px; cursor: pointer; font-weight: 600; }
  button.danger { background: rgba(231,76,60,.15); border-color: var(--red); color: var(--red); }
  button.safe { background: rgba(46,204,113,.15); border-color: var(--green); color: var(--green); }
  #error { color: var(--red); font-size: 13px; min-height: 20px; margin-bottom: 8px; }
  .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(220px, 1fr)); gap: 14px; margin-bottom: 20px; }
  .card { background: var(--card); border: 1px solid var(--border); border-radius: 12px; padding: 16px; }
  .card .label { color: var(--muted); font-size: 12px; text-transform: uppercase; letter-spacing: .06em; }
  .card .value { font-size: 26px; font-weight: 700; margin: 6px 0 2px; }
  .card .sub { color: var(--muted); font-size: 12px; }
  .value.green { color: var(--green); } .value.blue { color: var(--blue); }
  .value.red { color: var(--red); } .value.purple { color: var(--purple); }
  .cols { display: grid; grid-template-columns: repeat(auto-fit, minmax(320px, 1fr)); gap: 14px; margin-bottom: 20px; }
  .bar { background: var(--bg); border-radius: 6px; height: 10px; overflow: hidden; margin-top: 8px; }
  .bar > div { height: 100%; border-radius: 6px; transition: width .4s; }
  .row { display: flex; justify-content: space-between; align-items: baseline; margin-top: 12px; font-size: 13px; }
  .row .amt { color: var(--muted); }
  table { width: 100%; border-collapse: collapse; font-size: 13px; }
  th { text-align: left; color: var(--muted); font-weight: 500; padding: 6px 8px; border-bottom: 1px solid var(--border); }
  td { padding: 6px 8px; border-bottom: 1px solid var(--border); }
  td .pill { background: var(--bg); border: 1px solid var(--border); border-radius: 6px; padding: 1px 8px; font-size: 12px; }
  .kill { display: flex; align-items: center; gap: 14px; }
  .kill .state { font-weight: 700; }
  footer { color: var(--muted); font-size: 12px; margin-top: 8px; }
</style>
</head>
<body>
<header>
  <h1>Covenant &mdash; governance &amp; FinOps</h1>
  <span id="chainBadge" class="badge ok">chain: &mdash;</span>
  <span id="killBadge" class="badge ok">serving</span>
  <span class="spacer"></span>
  <input id="token" type="password" placeholder="admin token" autocomplete="off">
  <button onclick="saveToken()">Connect</button>
</header>
<div id="error"></div>

<div class="grid">
  <div class="card">
    <div class="label">Spend (global)</div>
    <div class="value blue" id="spend">&mdash;</div>
    <div class="sub" id="spendSub">of cap</div>
    <div class="bar"><div id="spendBar" style="width:0%;background:var(--blue)"></div></div>
  </div>
  <div class="card">
    <div class="label">Estimated savings</div>
    <div class="value green" id="savings">&mdash;</div>
    <div class="sub" id="savingsSub">in-perimeter tokens at public-route rates</div>
  </div>
  <div class="card">
    <div class="label">Requests</div>
    <div class="value" id="requests">&mdash;</div>
    <div class="sub" id="requestsSub">&mdash;</div>
    <div class="bar"><div id="allowBar" style="width:0%;background:var(--green)"></div></div>
  </div>
  <div class="card">
    <div class="label">Audit chain</div>
    <div class="value purple" id="entries">&mdash;</div>
    <div class="sub">hash-chained entries &middot; every allow &amp; deny</div>
  </div>
</div>

<div class="cols">
  <div class="card">
    <h2>Kill switch</h2>
    <div class="kill">
      <span class="state" id="killState">&mdash;</span>
      <button id="killBtn" class="danger" onclick="toggleKill()">Engage</button>
    </div>
    <div class="sub" id="killReason" style="margin-top:8px"></div>
  </div>
  <div class="card">
    <h2>Team budgets</h2>
    <div id="teams"><div class="sub">no spend yet</div></div>
  </div>
  <div class="card">
    <h2>Denials by reason</h2>
    <div id="denials"><div class="sub">none</div></div>
  </div>
</div>

<div class="cols">
  <div class="card">
    <h2>Settings &mdash; routing policy (from config, read-only)</h2>
    <table>
      <thead><tr><th>Classification</th><th>Adapter</th><th>Model</th></tr></thead>
      <tbody id="routes"></tbody>
    </table>
  </div>
  <div class="card">
    <h2>Settings &mdash; appliance</h2>
    <table><tbody id="settings"></tbody></table>
  </div>
</div>

<footer id="footer">enter the admin token to connect</footer>

<script>
const $ = id => document.getElementById(id);
let token = localStorage.getItem('covenantAdminToken') || '';
let killEngaged = false;
$('token').value = token;

function saveToken() { token = $('token').value.trim(); localStorage.setItem('covenantAdminToken', token); refresh(); }
function money(v) { return '$' + Number(v).toFixed(4); }
function pct(a, b) { return b > 0 ? Math.min(100, a / b * 100) : 0; }
function utilColor(p) { return p >= 90 ? 'var(--red)' : p >= 70 ? 'var(--orange)' : 'var(--green)'; }
function esc(s) { const d = document.createElement('div'); d.textContent = s; return d.innerHTML; }

async function refresh() {
  if (!token) { $('error').textContent = ''; return; }
  let r;
  try { r = await fetch('/admin/status', { headers: { 'X-Covenant-Admin-Token': token } }); }
  catch { $('error').textContent = 'appliance unreachable'; return; }
  if (r.status === 401) { $('error').textContent = 'invalid admin token'; return; }
  $('error').textContent = '';
  render(await r.json());
}

function render(s) {
  const f = s.finops, b = s.budget;

  $('chainBadge').textContent = s.chain_valid ? 'chain: valid' : 'chain: BROKEN';
  $('chainBadge').className = 'badge ' + (s.chain_valid ? 'ok' : 'bad');

  killEngaged = s.kill_switch.engaged;
  $('killBadge').textContent = killEngaged ? 'KILL SWITCH ENGAGED' : 'serving';
  $('killBadge').className = 'badge ' + (killEngaged ? 'bad' : 'ok');
  $('killState').textContent = killEngaged ? 'ENGAGED' : 'disengaged';
  $('killState').style.color = killEngaged ? 'var(--red)' : 'var(--green)';
  $('killReason').textContent = killEngaged && s.kill_switch.reason ? 'reason: ' + s.kill_switch.reason : '';
  $('killBtn').textContent = killEngaged ? 'Disengage' : 'Engage';
  $('killBtn').className = killEngaged ? 'safe' : 'danger';

  $('spend').textContent = money(b.global_spend_usd);
  $('spendSub').textContent = 'of ' + money(b.global_cap_usd) + ' cap';
  const sp = pct(b.global_spend_usd, b.global_cap_usd);
  $('spendBar').style.width = sp + '%';
  $('spendBar').style.background = utilColor(sp);

  $('savings').textContent = money(f.estimated_savings_usd);
  $('savingsSub').textContent = f.local_requests + ' in-perimeter request(s), '
    + f.local_tokens + ' token(s), priced at public-route rates';

  $('requests').textContent = f.requests;
  $('requestsSub').textContent = f.allowed + ' allowed · ' + f.denied + ' denied';
  $('allowBar').style.width = pct(f.allowed, f.requests) + '%';

  $('entries').textContent = s.audit_entries;

  $('teams').innerHTML = b.teams.length === 0 ? '<div class="sub">no spend yet</div>' :
    b.teams.map(t => {
      const capped = t.cap_usd !== null;
      const p = capped ? pct(t.spend_usd, t.cap_usd) : 0;
      return '<div class="row"><span>' + esc(t.team) + '</span><span class="amt">' + money(t.spend_usd)
        + (capped ? ' / ' + money(t.cap_usd) : ' · global cap only') + '</span></div>'
        + (capped ? '<div class="bar"><div style="width:' + p + '%;background:' + utilColor(p) + '"></div></div>' : '');
    }).join('');

  const dr = Object.entries(f.denials_by_reason);
  $('denials').innerHTML = dr.length === 0 ? '<div class="sub">none</div>' :
    dr.sort((a, z) => z[1] - a[1]).map(([reason, n]) =>
      '<div class="row"><span>' + esc(reason) + '</span><span class="amt">' + n + '</span></div>').join('');

  $('routes').innerHTML = s.routes.map(rt =>
    '<tr><td>' + esc(rt.classification) + '</td><td><span class="pill">' + esc(rt.adapter)
    + '</span></td><td>' + esc(rt.model) + '</td></tr>').join('');

  $('settings').innerHTML =
    '<tr><td>Started</td><td>' + new Date(s.started_utc).toLocaleString() + '</td></tr>' +
    '<tr><td>Global cap</td><td>' + money(b.global_cap_usd) + '</td></tr>' +
    '<tr><td>Ledger</td><td>in-memory (resets on restart)</td></tr>' +
    '<tr><td>Budgets &amp; routes</td><td>from config — change via user-secrets / env and restart</td></tr>';

  $('footer').textContent = 'refreshed ' + new Date(s.generated_utc).toLocaleTimeString()
    + ' · auto-refreshes every 3s · read-only view over config, ledger, and the audit log';
}

async function toggleKill() {
  const engage = !killEngaged;
  const reason = engage ? (prompt('Reason for engaging the kill switch?') || 'engaged from dashboard') : '';
  if (engage && reason === null) return;
  await fetch('/admin/kill-switch', {
    method: 'POST',
    headers: { 'X-Covenant-Admin-Token': token, 'Content-Type': 'application/json' },
    body: JSON.stringify({ engaged: engage, reason: reason })
  });
  refresh();
}

refresh();
setInterval(refresh, 3000);
</script>
</body>
</html>
""";
}

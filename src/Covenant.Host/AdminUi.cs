namespace Covenant.Host;

/// <summary>Governance + FinOps dashboard, one self-contained embedded page — no CDN, no build step,
/// no egress (root CLAUDE.md). Shell is public; every data call requires the admin token.</summary>
public static class AdminUi
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Covenant</title>
<style>
  :root {
    --bg: #0b0e13; --panel: #12161d; --panel2: #171c25; --border: #232a35; --border2: #2d3542;
    --text: #e8edf4; --muted: #8a94a3; --faint: #7b8698;
    --blue: #57a6ff; --green: #3fd68c; --orange: #ffb454; --red: #ff6b6b; --purple: #c792ea;
    --mono: ui-monospace, "SF Mono", "Cascadia Code", Menlo, monospace;
  }
  * { box-sizing: border-box; margin: 0; }
  html { color-scheme: dark; }
  body { background: var(--bg); color: var(--text); font: 14px/1.55 -apple-system, "Segoe UI", Roboto, sans-serif;
         padding: 28px clamp(16px, 4vw, 48px); max-width: 1240px; margin: 0 auto; }

  header { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; margin-bottom: 6px; }
  .wordmark { display: flex; align-items: baseline; gap: 10px; margin-right: 8px; }
  .wordmark h1 { font-size: 19px; font-weight: 700; letter-spacing: -.01em; }
  .wordmark .dot { width: 9px; height: 9px; border-radius: 50%; background: var(--green); align-self: center;
                   box-shadow: 0 0 10px var(--green); }
  .wordmark .dot.down { background: var(--red); box-shadow: 0 0 10px var(--red); }
  .tagline { color: var(--faint); font-size: 12px; margin-bottom: 22px; }
  .spacer { flex: 1; }
  .badge { padding: 4px 11px; border-radius: 999px; font-size: 11.5px; font-weight: 650; letter-spacing: .02em;
           border: 1px solid transparent; }
  .badge.ok  { background: rgba(63,214,140,.10); color: var(--green); border-color: rgba(63,214,140,.25); }
  .badge.bad { background: rgba(255,107,107,.10); color: var(--red);  border-color: rgba(255,107,107,.3); }
  .badge.warn{ background: rgba(255,180,84,.10);  color: var(--orange); border-color: rgba(255,180,84,.3); }

  input[type=password] { background: var(--panel); border: 1px solid var(--border2); color: var(--text);
    border-radius: 9px; padding: 8px 12px; width: 210px; font-size: 13px; }
  input[type=password]:focus { outline: none; border-color: var(--blue); }
  button { background: var(--panel2); border: 1px solid var(--border2); color: var(--text); border-radius: 9px;
    padding: 8px 15px; cursor: pointer; font-weight: 600; font-size: 13px; transition: border-color .15s; }
  button:hover { border-color: var(--blue); }
  button.danger { background: rgba(255,107,107,.10); border-color: rgba(255,107,107,.4); color: var(--red); }
  button.danger:hover { border-color: var(--red); }
  button.safe { background: rgba(63,214,140,.10); border-color: rgba(63,214,140,.4); color: var(--green); }

  #error { color: var(--red); font-size: 13px; min-height: 20px; margin: 4px 0 10px; }
  #error.hint { color: var(--muted); }

  .kpis { display: grid; grid-template-columns: repeat(auto-fit, minmax(215px, 1fr)); gap: 14px; margin-bottom: 14px; }
  .kpi { background: var(--panel); border: 1px solid var(--border); border-radius: 14px; padding: 16px 18px 14px;
         position: relative; overflow: hidden; }
  .kpi::before { content: ""; position: absolute; inset: 0 auto 0 0; width: 3px; background: var(--accent, var(--blue)); }
  .kpi .label { color: var(--muted); font-size: 11px; font-weight: 650; text-transform: uppercase; letter-spacing: .08em; }
  .kpi .value { font-family: var(--mono); font-variant-numeric: tabular-nums; font-size: 27px; font-weight: 700;
                margin: 7px 0 3px; color: var(--accent, var(--text)); }
  .kpi .sub { color: var(--muted); font-size: 12px; }
  .meter { background: #0e1218; border: 1px solid var(--border); border-radius: 6px; height: 8px; overflow: hidden; margin-top: 10px; }
  .meter > div { height: 100%; transition: width .5s ease; }

  .panel { background: var(--panel); border: 1px solid var(--border); border-radius: 14px; padding: 18px 20px; }
  .panel h2 { font-size: 11.5px; font-weight: 700; color: var(--muted); text-transform: uppercase;
              letter-spacing: .09em; margin-bottom: 14px; }
  .cols { display: grid; grid-template-columns: repeat(auto-fit, minmax(330px, 1fr)); gap: 14px; margin-bottom: 14px; }
  .full { margin-bottom: 14px; }

  .row { display: flex; justify-content: space-between; align-items: baseline; gap: 10px; padding: 6px 0; font-size: 13px; }
  .row + .row { border-top: 1px solid var(--border); }
  .row .amt { font-family: var(--mono); font-variant-numeric: tabular-nums; color: var(--muted); white-space: nowrap; }

  table { width: 100%; border-collapse: collapse; font-size: 13px; }
  th { text-align: left; color: var(--faint); font-weight: 600; font-size: 11px; text-transform: uppercase;
       letter-spacing: .06em; padding: 6px 10px; border-bottom: 1px solid var(--border2); }
  td { padding: 8px 10px; border-bottom: 1px solid var(--border); }
  tr:last-child td { border-bottom: none; }
  .pill { background: var(--panel2); border: 1px solid var(--border2); border-radius: 6px; padding: 2px 9px;
          font-size: 12px; font-family: var(--mono); }
  .pill.local { color: var(--green); border-color: rgba(63,214,140,.35); }
  .pill.public { color: var(--blue); border-color: rgba(87,166,255,.35); }

  .kill { display: flex; align-items: center; gap: 16px; }
  .kill .state { font-weight: 800; font-size: 16px; letter-spacing: .02em; }
  .empty { color: var(--faint); font-size: 13px; padding: 6px 0; }
  #chart svg { width: 100%; height: 150px; display: block; }
  .chart-caption { display: flex; justify-content: space-between; color: var(--faint); font-size: 11px; margin-top: 6px;
                   font-family: var(--mono); }
  .legend { display: flex; gap: 16px; font-size: 12px; color: var(--muted); margin-top: 10px; }
  .legend i { display: inline-block; width: 10px; height: 10px; border-radius: 3px; margin-right: 6px; }
  .legend i.line { height: 2px; border-radius: 1px; vertical-align: middle; }
  td.num { text-align: right; font-family: var(--mono); font-variant-numeric: tabular-nums; font-size: 12.5px; }
  td.time { font-family: var(--mono); font-variant-numeric: tabular-nums; font-size: 12.5px; }
  .feedtable { table-layout: fixed; }  /* stable column widths — no jumping between refreshes */
  .feedtable td.ell { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .served { color: var(--green); font-family: var(--mono); font-size: 11.5px; margin-top: 6px; min-height: 15px; display: block; }
  .dfull { grid-column: 1 / -1; }
  .tabs { display: flex; gap: 4px; margin-bottom: 16px; border-bottom: 1px solid var(--border); }
  .tab { background: none; border: none; border-radius: 0; padding: 8px 16px; color: var(--muted);
         font-size: 13px; font-weight: 600; cursor: pointer;
         border-bottom: 2px solid transparent; margin-bottom: -1px; user-select: none; }
  .tab:hover { color: var(--text); }
  .tab.active { color: var(--text); border-bottom-color: var(--blue); }
  .pager { display: flex; align-items: center; gap: 14px; justify-content: flex-end; margin-top: 12px; }
  .pager span { color: var(--faint); font-size: 12px; font-family: var(--mono); }
  tr.rowclick { cursor: pointer; }
  tr.rowclick:hover td { background: var(--panel2); }
  tr.detail td { background: var(--panel2); border-bottom: 1px solid var(--border2); }
  .dgrid { display: grid; grid-template-columns: repeat(auto-fit, minmax(180px, 1fr)); gap: 10px 24px; padding: 6px 2px; }
  .dgrid .dl { color: var(--faint); font-size: 10.5px; text-transform: uppercase; letter-spacing: .07em; }
  .dgrid .dv { font-family: var(--mono); font-size: 12.5px; margin-top: 2px; overflow-wrap: anywhere; }
  .dnote { color: var(--faint); font-size: 11.5px; margin-top: 10px; font-style: italic; }
  .clspill { border-radius: 6px; padding: 2px 9px; font-size: 11.5px; font-weight: 650; letter-spacing: .02em; }
  .cls-Public   { background: rgba(139,148,163,.12); color: var(--muted); }
  .cls-Internal { background: rgba(87,166,255,.12);  color: var(--blue); }
  .cls-Pii      { background: rgba(255,180,84,.14);  color: var(--orange); }
  .cls-Phi      { background: rgba(255,107,107,.14); color: var(--red); }
  .flow { display: grid; grid-template-columns: repeat(7, minmax(0, 1fr)); gap: 10px; margin: 6px 0 2px; }
  @media (max-width: 1000px) { .flow { grid-template-columns: repeat(4, minmax(0, 1fr)); } }
  .stage { background: var(--panel2); border: 1px solid var(--border2); border-radius: 10px; padding: 12px 14px;
           position: relative; }
  .stage:not(:last-child)::after { content: "\2192"; position: absolute; right: -9px; top: 10px;
           color: var(--faint); font-size: 13px; z-index: 1; }
  .stage b { display: block; font-size: 13px; margin-bottom: 3px; }
  .stage small { color: var(--muted); font-size: 11px; line-height: 1.4; display: block; }
  .stage .deny { color: var(--red); font-family: var(--mono); font-size: 11.5px; margin-top: 6px; min-height: 15px; display: block; }
  .stage.s-in  { border-top: 3px solid var(--blue); }
  .stage.s-gov { border-top: 3px solid var(--orange); }
  .stage.s-prov{ border-top: 3px solid var(--green); }
  .stage.s-ev  { border-top: 3px solid var(--purple); }
  .auditwrap { border: 1px dashed rgba(199,146,234,.45); border-radius: 14px; padding: 14px; margin-top: 10px; }
  .auditwrap .wraplabel { color: var(--purple); font-size: 11.5px; font-weight: 650; letter-spacing: .05em;
                          text-transform: uppercase; margin-bottom: 10px; }
  footer { color: var(--faint); font-size: 12px; margin-top: 10px; }
</style>
</head>
<body>
<header>
  <div class="wordmark"><span id="liveDot" class="dot"></span><h1>Covenant</h1></div>
  <span id="chainBadge" class="badge ok">chain —</span>
  <span id="killBadge" class="badge ok">serving</span>
  <span id="anonBadge" class="badge warn" style="display:none">anonymous allowed</span>
  <span class="spacer"></span>
  <span id="tokenbar">
    <input id="token" type="password" placeholder="admin token" autocomplete="off">
    <button onclick="saveToken()">Connect</button>
  </span>
  <button id="signoutBtn" style="display:none" onclick="resetToken()">Sign out</button>
</header>
<div class="tagline">In-perimeter AI inference governance &amp; FinOps — every request is classified, routed, budgeted, attributed, and audited</div>
<div id="error"></div>

<nav class="tabs" role="tablist">
  <button class="tab active" id="tabbtn-overview" role="tab" onclick="switchTab('overview')">Overview</button>
  <button class="tab" id="tabbtn-requests" role="tab" onclick="switchTab('requests')">Requests</button>
  <button class="tab" id="tabbtn-pipeline" role="tab" onclick="switchTab('pipeline')">Pipeline</button>
  <button class="tab" id="tabbtn-settings" role="tab" onclick="switchTab('settings')">Settings</button>
</nav>

<div id="tab-overview">

<div class="kpis">
  <div class="kpi" style="--accent: var(--blue)">
    <div class="label">Spend · global</div>
    <div class="value" id="spend">—</div>
    <div class="sub" id="spendSub">—</div>
    <div class="meter"><div id="spendBar" style="width:0%;background:var(--blue)"></div></div>
  </div>
  <div class="kpi" style="--accent: var(--green)">
    <div class="label">Est. savings</div>
    <div class="value" id="savings">—</div>
    <div class="sub" id="savingsSub">—</div>
  </div>
  <div class="kpi" style="--accent: var(--orange)">
    <div class="label">Requests</div>
    <div class="value" id="requests">—</div>
    <div class="sub" id="requestsSub">—</div>
    <div class="meter"><div id="allowBar" style="width:0%;background:var(--green)"></div></div>
  </div>
  <div class="kpi" style="--accent: var(--purple)">
    <div class="label">Audit chain</div>
    <div class="value" id="entries">—</div>
    <div class="sub" id="chainSub">Hash-chained — every allow and deny</div>
  </div>
</div>

<div class="panel full">
  <h2>Activity — requests per minute</h2>
  <div id="chart"><div class="empty">No activity yet — run ./demo.sh</div></div>
  <div class="chart-caption"><span id="chartFrom"></span><span id="chartTo"></span></div>
  <div class="legend">
    <span><i style="background:var(--green)"></i>allowed</span>
    <span><i style="background:var(--red)"></i>denied</span>
    <span><i class="line" style="background:var(--blue)"></i>cost</span>
  </div>
</div>

<div class="cols">
  <div class="panel">
    <h2>Kill switch</h2>
    <div class="kill">
      <span class="state" id="killState">—</span>
      <button id="killBtn" class="danger" onclick="toggleKill()">Engage</button>
    </div>
    <div class="empty" id="killReason" style="margin-top:10px"></div>
  </div>
  <div class="panel">
    <h2>Team budgets</h2>
    <div id="teams"><div class="empty">No spend recorded yet</div></div>
  </div>
  <div class="panel">
    <h2>Denials by reason <span style="color:var(--faint);text-transform:none;letter-spacing:0">· history from the audit log</span></h2>
    <div id="denials"><div class="empty">None — nothing refused yet</div></div>
  </div>
</div>

</div><!-- /tab-overview -->

<div id="tab-requests" style="display:none">
  <div class="panel full">
    <h2>Request history <span style="color:var(--faint);text-transform:none;letter-spacing:0">· newest first · outcome reflects the appliance state at the moment each request was processed · click a row for details</span></h2>
    <table class="feedtable">
      <colgroup>
        <col style="width:9%"><col style="width:13%"><col style="width:11%"><col style="width:9%"><col style="width:17%">
        <col style="width:8%"><col style="width:11%"><col style="width:9%"><col style="width:13%">
      </colgroup>
      <thead><tr><th>Time</th><th>Principal</th><th>Team</th><th>Class</th><th>Model</th>
        <th style="text-align:right">Tokens</th><th style="text-align:right">Cost</th><th style="text-align:right">Latency</th><th>Outcome</th></tr></thead>
      <tbody id="feed"><tr><td colspan="9" class="empty">No requests yet — run ./demo.sh</td></tr></tbody>
    </table>
    <div class="pager">
      <button onclick="feedPrev()">&lsaquo; Newer</button>
      <span id="feedInfo"></span>
      <button onclick="feedNext()">Older &rsaquo;</button>
    </div>
  </div>
</div><!-- /tab-requests -->

<div id="tab-pipeline" style="display:none">
  <div class="panel full">
    <h2>The path every request takes <span style="color:var(--faint);text-transform:none;letter-spacing:0">· live counts from the audit log</span></h2>
    <div class="auditwrap">
      <div class="wraplabel">Audit wraps everything — exactly one hash-chained entry per request, allow or deny alike</div>
      <div class="flow">
        <div class="stage s-in"><b>Ingress</b><small>OpenAI-compatible API. Bearer key extracted; prompt content stays in the data plane.</small><span class="deny"></span></div>
        <div class="stage s-gov"><b>Auth</b><small>API key resolves principal and team. Headers can't impersonate.</small><span class="deny" id="fd-auth"></span></div>
        <div class="stage s-gov"><b>Classify</b><small>Data sensitivity: Public, Internal, PII, PHI. Unsure &rarr; most restrictive.</small><span class="deny"></span></div>
        <div class="stage s-gov"><b>Policy</b><small>Permitted routes for the classification; complexity router picks cheapest adequate model.</small><span class="deny" id="fd-policy"></span></div>
        <div class="stage s-gov"><b>Budget</b><small>Kill switch, global and team caps — checked before any money is spent.</small><span class="deny" id="fd-budget"></span></div>
        <div class="stage s-prov"><b>Provider</b><small>Allow-listed model only. Upstream failure &rarr; governed refusal, never a 500.</small><span class="deny" id="fd-provider"></span></div>
        <div class="stage s-ev"><b>Attribution</b><small>Actual tokens priced; spend recorded against the key's team.</small><span class="served" id="fd-served"></span></div>
      </div>
    </div>
    <div class="empty" style="margin-top:10px">Denied requests stop at the red stage and still produce an audit entry — that is the fail-closed guarantee. The full flow diagram lives in the repo README.</div>
  </div>
</div><!-- /tab-pipeline -->

<div id="tab-settings" style="display:none">
<div class="cols">
  <div class="panel">
    <h2>Routing policy <span style="color:var(--faint);text-transform:none;letter-spacing:0">· from config, read-only</span></h2>
    <table>
      <thead><tr><th>Classification</th><th>Adapter</th><th>Model</th></tr></thead>
      <tbody id="routes"></tbody>
    </table>
  </div>
  <div class="panel">
    <h2>Appliance</h2>
    <table><tbody id="settings"></tbody></table>
  </div>
  <div class="panel">
    <h2>Demo data</h2>
    <div class="empty" style="margin-bottom:12px">Reset archives the audit log with a timestamp (evidence is never deleted) and clears
    in-memory spend so budgets reopen. The archived chain stays independently verifiable.</div>
    <button class="danger" onclick="resetData()">Archive log &amp; reset counters</button>
    <div class="empty" id="resetMsg" style="margin-top:10px"></div>
  </div>
</div>

</div><!-- /tab-settings -->

<footer id="footer">Enter the admin token to connect.</footer>

<script>
const UI_VERSION = 'v7';
const $ = id => document.getElementById(id);
// sessionStorage, not localStorage: survives reload, dies with the tab — narrows the XSS blast
// radius for a token that controls the kill switch (independent audit finding).
let token = sessionStorage.getItem('covenantAdminToken') || '';
let killEngaged = false;
$('token').value = token;

function saveToken() {
  token = $('token').value.trim();
  sessionStorage.setItem('covenantAdminToken', token);
  location.reload();   // restart the stream cleanly with the new token
}
function money(v) {
  const n = Number(v);
  if (n === 0) return '$0';
  if (n > 0 && n < 0.00001) return '<$0.00001';         // tiny but real — never rendered as zero
  if (n < 0.01) return '$' + n.toPrecision(2);          // two significant digits for sub-cent
  return '$' + n.toFixed(n >= 100 ? 0 : n >= 1 ? 2 : 3);
}
function cap(s) { return s ? s.charAt(0).toUpperCase() + s.slice(1) : s; }
function clsPill(c) {
  const label = { Pii: 'PII', Phi: 'PHI' }[c] || c;
  return '<span class="clspill cls-' + esc(c) + '">' + esc(label) + '</span>';
}
// Turns a system denial reason into what it means and what to do about it.
function explain(reason) {
  const r = (reason || '').toLowerCase();
  if (r.includes('no adapter registered')) return 'Policy routed this to the in-perimeter model, but none is configured — so it was refused rather than sent to a public provider. That is fail-closed working. To serve this class of data, configure Local:Endpoint.';
  if (r.includes('kill switch')) return 'An operator engaged the kill switch; all inference stops until it is disengaged (Overview tab).';
  if (r.includes('global budget')) return 'The appliance-wide spend cap is exhausted. Raise Budget:GlobalCapUsd, or archive & reset in Settings.';
  if (r.includes('budget exhausted for team')) return 'This team hit its spend cap. Raise its cap, or archive & reset in Settings.';
  if (r.includes('authentication required') || r.includes('unknown api key')) return 'The caller presented no valid API key. Clients must send Authorization: Bearer <key>.';
  if (r.includes('not permitted for classification') || r.includes('no permitted route')) return 'Policy allows no route for this data classification — fail-closed by design.';
  if (r.includes('call failed') || r.includes('stream failed')) return 'The upstream provider failed (quota, network, 5xx). Covenant refused fail-closed, audited it, and returned 502 to the caller.';
  if (r.includes('no response produced')) return 'The request errored before a response existed; recorded as a denial (fail-closed evidence).';
  if (r.includes('complexity-routed')) return 'The router estimated prompt complexity and picked the cheapest adequate model from the permitted set.';
  if (r.startsWith('allowed')) return 'Served within policy; cost attributed to the team and recorded in the audit chain.';
  return '';
}
function pct(a, b) { return b > 0 ? Math.min(100, a / b * 100) : 0; }
function utilColor(p) { return p >= 90 ? 'var(--red)' : p >= 70 ? 'var(--orange)' : 'var(--green)'; }
function esc(s) { const d = document.createElement('div'); d.textContent = s; return d.innerHTML; }
function hhmm(iso) { return new Date(iso).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }); }

function chart(buckets) {
  if (!buckets.length) { $('chart').innerHTML = '<div class="empty">No activity yet — run ./demo.sh</div>';
    $('chartFrom').textContent = ''; $('chartTo').textContent = ''; return; }
  const W = 900, H = 150, max = Math.max(...buckets.map(b => b.requests));
  const maxCost = Math.max(...buckets.map(b => b.cost_usd), 1e-9);
  const gap = 3, bw = Math.max(5, Math.floor(W / buckets.length) - gap);
  let x = 0, rects = '', pts = [];
  for (const b of buckets) {
    if (b.requests === 0) {                             // quiet minute: real gap on the time axis
      pts.push((x + bw / 2) + ',' + (H - 8));
      x += bw + gap;
      continue;
    }
    const total = Math.max(1, Math.round((b.requests / max) * (H - 14)));
    const denied = Math.round(total * (b.denied / b.requests));
    const allowed = total - denied;
    const tip = hhmm(b.t) + ' — ' + b.requests + ' request(s), ' + b.denied + ' denied, '
      + money(b.cost_usd) + ', ' + Math.round(b.avg_ms) + ' ms avg';
    rects += '<g><title>' + esc(tip) + '</title>'
      + (allowed > 0 ? '<rect x="' + x + '" y="' + (H - allowed) + '" width="' + bw + '" height="' + allowed + '" rx="2" fill="var(--green)" opacity=".85"/>' : '')
      + (denied  > 0 ? '<rect x="' + x + '" y="' + (H - total)   + '" width="' + bw + '" height="' + denied  + '" rx="2" fill="var(--red)" opacity=".85"/>' : '')
      + '</g>';
    pts.push((x + bw / 2) + ',' + (H - 8 - (b.cost_usd / maxCost) * (H - 30)));
    x += bw + gap;
  }
  const costLine = pts.length > 1
    ? '<polyline points="' + pts.join(' ') + '" fill="none" stroke="var(--blue)" stroke-width="1.5" opacity=".9"/>' : '';
  $('chart').innerHTML = '<svg viewBox="0 0 ' + Math.max(W, x) + ' ' + H + '" preserveAspectRatio="none">' + rects + costLine + '</svg>';
  $('chartFrom').textContent = hhmm(buckets[0].t);
  $('chartTo').textContent = hhmm(buckets[buckets.length - 1].t) + ' (local time)';
}

function tryRender(s) {
  try { render(s); msg('', true); }
  catch (e) { msg('render error (' + UI_VERSION + '): ' + e.message, false); }
}

// Live stream: the server pushes a snapshot every 2s; fetch-with-header keeps the token out of the
// URL. Frames arrive as SSE "data: {json}\n\n"; auto-reconnects if the server goes away.
function msg(text, isHint) { const e = $('error'); e.textContent = text; e.className = isHint ? 'hint' : ''; }

async function connectStream() {
  if (!token) {
    msg('Not connected — paste the admin token (dev default: the value of Admin:Token) and press Connect', true);
    $('token').focus();
    return;
  }
  try {
    const r = await fetch('/admin/status/stream', { headers: { 'X-Covenant-Admin-Token': token } });
    if (r.status === 401) { msg('Invalid admin token — check the Admin:Token secret', false); $('liveDot').className = 'dot down'; return; }
    if (!r.ok || !r.body) throw new Error('HTTP ' + r.status);
    $('liveDot').className = 'dot';
    const reader = r.body.getReader();
    const dec = new TextDecoder();
    let buf = '';
    for (;;) {
      const { done, value } = await reader.read();
      if (done) break;
      buf += dec.decode(value, { stream: true });
      let i;
      while ((i = buf.indexOf('\n\n')) >= 0) {
        const frame = buf.slice(0, i); buf = buf.slice(i + 2);
        const line = frame.split('\n').find(l => l.startsWith('data: '));
        if (line) tryRender(JSON.parse(line.slice(6)));
      }
    }
  } catch (e) {
    msg('Stream lost — reconnecting… (' + e.message + ')', false);
  }
  $('liveDot').className = 'dot down';
  setTimeout(connectStream, 2000);
}

async function refresh() {  // one-shot fallback, used right after kill-switch toggles
  if (!token) return;
  try {
    const r = await fetch('/admin/status', { headers: { 'X-Covenant-Admin-Token': token } });
    if (r.ok) tryRender(await r.json());
  } catch { /* stream will catch up */ }
}

function render(s) {
  const f = s.finops, b = s.budget;

  $('chainBadge').textContent = s.chain_valid ? 'chain verified' : 'CHAIN BROKEN';
  $('chainBadge').className = 'badge ' + (s.chain_valid ? 'ok' : 'bad');
  $('chainSub').innerHTML = s.chain_valid ? 'Hash-chained — every allow and deny' : '<span style="color:var(--red)">tampering detected — see /admin/evidence</span>';

  killEngaged = s.kill_switch.engaged;
  $('killBadge').textContent = killEngaged ? 'KILL SWITCH ENGAGED' : 'serving';
  $('killBadge').className = 'badge ' + (killEngaged ? 'bad' : 'ok');
  $('killState').textContent = killEngaged ? 'ENGAGED' : 'disengaged';
  $('killState').style.color = killEngaged ? 'var(--red)' : 'var(--green)';
  $('killReason').textContent = killEngaged && s.kill_switch.reason ? 'Reason: ' + cap(s.kill_switch.reason) : 'All inference stops instantly when engaged; every refusal is audited.';
  $('killBtn').textContent = killEngaged ? 'Disengage' : 'Engage';
  $('killBtn').className = killEngaged ? 'safe' : 'danger';
  $('anonBadge').style.display = s.auth.allow_anonymous ? '' : 'none';

  $('spend').textContent = money(b.global_spend_usd);
  $('spendSub').textContent = 'Of ' + money(b.global_cap_usd) + ' cap — survives restarts';
  const sp = pct(b.global_spend_usd, b.global_cap_usd);
  $('spendBar').style.width = Math.max(sp, b.global_spend_usd > 0 ? 2 : 0) + '%';
  $('spendBar').style.background = utilColor(sp);

  $('tokenbar').style.display = 'none';   // connected — the token lives in this browser now
  $('signoutBtn').style.display = '';

  $('savings').textContent = money(f.estimated_savings_usd);
  const routerSplit = Object.entries(f.requests_by_model).map(([m, c]) => c + '× ' + m).join(' · ');
  $('savingsSub').textContent = 'Vs all-strong baseline: local ' + money(f.savings_local_usd)
    + ' · router ' + money(f.savings_router_usd)
    + ' · cache ' + money(f.savings_cache_usd) + ' (' + f.cache_hits + ' hits)'
    + (routerSplit ? ' — served: ' + routerSplit : '');

  $('requests').textContent = f.requests;
  $('requestsSub').textContent = f.allowed + ' allowed · ' + f.denied + ' denied · ' + money(f.total_cost_usd) + ' total';
  $('allowBar').style.width = pct(f.allowed, f.requests) + '%';

  $('entries').textContent = s.audit_entries;

  chart(f.activity);

  $('teams').innerHTML = b.teams.length === 0 ? '<div class="empty">No spend recorded yet</div>' :
    b.teams.map(t => {
      const capped = t.cap_usd !== null && t.cap_usd !== undefined;
      const p = capped ? pct(t.spend_usd, t.cap_usd) : 0;
      return '<div class="row"><span>' + esc(t.team) + '</span><span class="amt">' + money(t.spend_usd)
        + (capped ? ' / ' + money(t.cap_usd) : ' · global cap only') + '</span></div>'
        + (capped ? '<div class="meter"><div style="width:' + Math.max(p, t.spend_usd > 0 ? 2 : 0) + '%;background:' + utilColor(p) + '"></div></div>' : '');
    }).join('');

  feedData = f.recent;
  // Don't shift rows under the reader: freeze the visible page while a detail row is open or the
  // user is paging through history; fresh data lands on the next frame after they return.
  if (openReq === null && feedPage === 0) renderFeed();

  const dr = Object.entries(f.denials_by_reason);
  $('denials').innerHTML = dr.length === 0 ? '<div class="empty">None — nothing refused yet.</div>' :
    dr.sort((a, z) => z[1] - a[1]).map(([reason, n]) =>
      '<div class="row"><span title="' + esc(explain(reason)) + '">' + esc(cap(reason)) + '</span><span class="amt">&times;' + n + '</span></div>').join('');

  // Pipeline tab: live per-stage denial counts derived from audit reasons.
  const dcount = re => dr.filter(([r]) => re.test(r.toLowerCase())).reduce((a, [, n]) => a + n, 0);
  $('fd-auth').textContent = (x => x ? x + ' denied here' : '')(dcount(/authentication|unknown api key/));
  $('fd-policy').textContent = (x => x ? x + ' denied here' : '')(dcount(/permitted/));
  $('fd-budget').textContent = (x => x ? x + ' denied here' : '')(dcount(/budget|kill switch/));
  $('fd-provider').textContent = (x => x ? x + ' denied here' : '')(dcount(/adapter|failed|no response/));
  $('fd-served').textContent = f.allowed + ' served';

  $('routes').innerHTML = s.routes.map(rt =>
    '<tr><td>' + clsPill(rt.classification) + '</td><td><span class="pill ' + (rt.adapter === 'local' ? 'local' : 'public') + '">'
    + esc(rt.adapter) + '</span></td><td style="font-family:var(--mono);font-size:12px">' + esc(rt.model) + '</td></tr>').join('');

  $('settings').innerHTML =
    '<tr><td>Started</td><td>' + new Date(s.started_utc).toLocaleString() + '</td></tr>' +
    '<tr><td>Authentication</td><td>' + s.auth.key_count + ' API key(s) · ' +
      (s.auth.allow_anonymous ? '<span style="color:var(--orange)">anonymous allowed</span>' : 'anonymous denied') + '</td></tr>' +
    '<tr><td>Complexity router</td><td>escalates above ~' + s.routing_threshold_tokens + ' est. tokens (Routing:ComplexityTokenThreshold)</td></tr>' +
    '<tr><td>Telemetry</td><td>' + (s.otel_enabled ? 'OTel export on (in-perimeter)' : 'off — default') + '</td></tr>' +
    '<tr><td>Ledger</td><td>projection of the audit log · rebuilt at boot</td></tr>' +
    '<tr><td>Config</td><td>budgets &amp; routes via user-secrets / env · restart to apply</td></tr>';

  $('footer').innerHTML = 'Live · updated ' + new Date(s.generated_utc).toLocaleTimeString()
    + ' · streaming from the appliance · UI ' + UI_VERSION
    + ' · <a href="#" style="color:var(--faint)" onclick="resetToken();return false">change token</a>';
}

function resetToken() { sessionStorage.removeItem('covenantAdminToken'); location.reload(); }

function switchTab(name) {
  for (const t of ['overview', 'requests', 'pipeline', 'settings']) {
    $('tab-' + t).style.display = t === name ? '' : 'none';
    $('tabbtn-' + t).className = 'tab' + (t === name ? ' active' : '');
  }
}

// --- Request feed: 10 per page, click a row to unfold its governance details ---
let feedData = [], feedPage = 0, openReq = null;
const FEED_PAGE = 10;

function feedPrev() { if (feedPage > 0) { feedPage--; renderFeed(); } }
function feedNext() { if ((feedPage + 1) * FEED_PAGE < feedData.length) { feedPage++; renderFeed(); } }
function toggleReq(id) { openReq = openReq === id ? null : id; renderFeed(); }

function detailRow(x) {
  const d = (l, v, full) => '<div' + (full ? ' class="dfull"' : '') + '><div class="dl">' + l + '</div><div class="dv">' + v + '</div></div>';
  return '<tr class="detail"><td colspan="9"><div class="dgrid">'
    + d('Request id', esc(x.id))
    + d('Timestamp', new Date(x.t).toLocaleString())
    + d('Principal', esc(x.principal))
    + d('Team / workflow / use case', esc(x.team) + ' / ' + esc(x.workflow) + ' / ' + esc(x.use_case))
    + d('Classification', clsPill(x.classification) + (x.signal ? ' <span style="color:var(--muted)">— ' + esc(x.signal) + '</span>' : ''))
    + d('Served by', x.model ? esc(x.model) + (x.cache ? ' — from cache ($0, no provider call)' : '') : '— (not served)')
    + d('Prompt size', x.prompt_chars + ' chars (~' + Math.round(x.prompt_chars / 4) + ' tokens)')
    + d('Prompt SHA-256', x.prompt_sha256
        ? '<span title="' + esc(x.prompt_sha256) + '">' + esc(x.prompt_sha256.slice(0, 16)) + '…</span>'
        : '—')
    + d('Tokens in / out', x.tokens_in + ' / ' + x.tokens_out)
    + d('Cost', money(x.cost_usd))
    + d('Latency', Math.round(x.ms) + ' ms')
    + (x.prompt_preview ? d('Input preview <span style="text-transform:none;letter-spacing:0">(truncated — enabled via Audit:PromptPreviewChars)</span>',
        '<span style="font-family:inherit;font-style:italic">&ldquo;' + esc(x.prompt_preview) + '&rdquo;</span>', true) : '')
    + d('Outcome at processing time',
        '<span style="color:' + (x.effect === 'Allow' ? 'var(--green)' : 'var(--red)') + '">' + x.effect + '</span> — ' + esc(cap(x.reason)), true)
    + (explain(x.reason) ? d('What this means', esc(explain(x.reason)), true) : '')
    + '</div><div class="dnote">' + (x.prompt_preview
        ? 'Input preview capture is ENABLED by operator config (Audit:PromptPreviewChars) — previews are stored in the audit log. Default posture is off.'
        : 'Prompt and response content are not stored or displayed by default — the SHA-256 fingerprint proves which content this entry refers to without revealing it. An operator can opt in to input previews via Audit:PromptPreviewChars.') + '</div>'
    + '</td></tr>';
}

function renderFeed() {
  if (feedData.length === 0) {
    $('feed').innerHTML = '<tr><td colspan="9" class="empty">No requests yet — run ./demo.sh</td></tr>';
    $('feedInfo').textContent = '';
    return;
  }
  const start = feedPage * FEED_PAGE;
  const page = feedData.slice(start, start + FEED_PAGE);
  $('feedInfo').textContent = (start + 1) + '–' + Math.min(start + FEED_PAGE, feedData.length) + ' of ' + feedData.length;
  $('feed').innerHTML = page.map(x =>
    '<tr class="rowclick" onclick="toggleReq(\'' + x.id + '\')">'
    + '<td class="time">' + new Date(x.t).toLocaleTimeString() + '</td>'
    + '<td class="ell" title="' + esc(x.principal) + '">' + esc(x.principal) + '</td>'
    + '<td class="ell" title="' + esc(x.team) + '">' + esc(x.team) + '</td>'
    + '<td>' + clsPill(x.classification) + '</td>'
    + '<td class="ell">' + (x.model
      ? '<span class="pill ' + (x.cache ? 'local' : 'public') + '" title="' + esc(x.model) + (x.cache ? ' (cache hit)' : '') + '">'
        + esc(x.model) + (x.cache ? ' ⚡' : '') + '</span>'
      : '<span style="color:var(--faint)">—</span>') + '</td>'
    + '<td class="num">' + x.tokens + '</td>'
    + '<td class="num">' + money(x.cost_usd) + '</td>'
    + '<td class="num">' + Math.round(x.ms) + ' ms</td>'
    + '<td style="color:' + (x.effect === 'Allow' ? 'var(--green)' : 'var(--red)') + ';font-weight:650">' + x.effect + '</td>'
    + '</tr>'
    + (openReq === x.id ? detailRow(x) : '')
  ).join('');
}

async function resetData() {
  if (!confirm('Archive the audit log and reset all counters? The archived chain stays verifiable; nothing is deleted.')) return;
  const r = await fetch('/admin/reset', { method: 'POST', headers: { 'X-Covenant-Admin-Token': token } });
  if (r.ok) {
    const j = await r.json();
    $('resetMsg').textContent = j.archived_to ? 'Archived to ' + j.archived_to : 'Nothing to archive — already clean.';
    feedPage = 0; openReq = null;
    refresh();
  } else {
    $('resetMsg').textContent = 'Reset failed: HTTP ' + r.status;
  }
}

async function toggleKill() {
  const engage = !killEngaged;
  let reason = '';
  if (engage) {
    // Cancel must mean cancel: engaging halts ALL inference, so either step can abort it.
    if (!confirm('Engage the kill switch? All inference stops immediately, and every refusal is audited.')) return;
    const entered = prompt('Reason (recorded in the audit log):');
    if (entered === null) return;                       // Escape / Cancel aborts — nothing is engaged
    reason = entered.trim() || 'engaged from dashboard';
  }
  await fetch('/admin/kill-switch', {
    method: 'POST',
    headers: { 'X-Covenant-Admin-Token': token, 'Content-Type': 'application/json' },
    body: JSON.stringify({ engaged: engage, reason: reason })
  });
  refresh();
}

connectStream();
</script>
</body>
</html>
""";
}

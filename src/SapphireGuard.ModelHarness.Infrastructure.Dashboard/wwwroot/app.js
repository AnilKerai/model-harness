// Client for the Model Harness live feed. The SSE stream carries every loop event tagged with
// taskId + turn + a detail bag; this builds the run -> turn model in the browser and renders it.
// URLs are relative so a <base href> lets the page live under any prefix (/dashboard, /, …).
// ponytail: full re-render of the detail pane per event — fine for a dev console's event volume;
// go incremental only if it ever visibly lags.

const runs = new Map();      // taskId -> run model
const order = [];            // taskIds, newest first
const expanded = new Set();  // event seqs whose <details> are open (survive re-render)
let selected = null;
let userPinned = false;      // once the user clicks a run, stop auto-following new ones

const runsEl = document.getElementById('runs');
const detailEl = document.getElementById('detail');
const connEl = document.getElementById('conn');
const taskEl = document.getElementById('task');
const searchEl = document.getElementById('run-search');
let query = '';

const feed = new EventSource('feed');
feed.onopen = () => { connEl.textContent = 'live'; connEl.className = 'badge ok'; };
feed.onerror = () => { connEl.textContent = 'reconnecting…'; connEl.className = 'badge err'; };
feed.onmessage = ev => ingest(JSON.parse(ev.data));

document.getElementById('run').onclick = start;
taskEl.addEventListener('keydown', e => { if (e.key === 'Enter') start(); });
searchEl.addEventListener('input', () => { query = searchEl.value.trim().toLowerCase(); renderRuns(); });

function start() {
  fetch('run', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ task: taskEl.value }),
  });
}

function getRun(id) {
  let r = runs.get(id);
  if (!r) {
    r = { id, title: id, status: 'running', started: Date.now(),
          events: [], turns: new Map(),
          totals: { modelCalls: 0, in: 0, out: 0, cached: 0, cost: 0 } };
    runs.set(id, r);
    order.unshift(id);
    if (!userPinned) selected = id;
    renderRuns();
  }
  return r;
}

function turnAgg(r, t) {
  let a = r.turns.get(t);
  if (!a) { a = { turn: t, in: 0, out: 0, cached: 0, cost: 0, tools: 0 }; r.turns.set(t, a); }
  return a;
}

function ingest(e) {
  const r = getRun(e.taskId);
  e._t = Date.now();
  r.events.push(e);
  const d = e.detail || {};
  switch (e.kind) {
    case 'run':
      r.title = e.summary;
      break;
    case 'model': {
      r.totals.modelCalls++;
      r.totals.in += d.inputTokens || 0;
      r.totals.out += d.outputTokens || 0;
      r.totals.cached += d.cachedTokens || 0;
      r.totals.cost += d.cost || 0;
      const a = turnAgg(r, e.turn);
      a.in += d.inputTokens || 0; a.out += d.outputTokens || 0;
      a.cached += d.cachedTokens || 0; a.cost += d.cost || 0;
      break;
    }
    case 'tool':
      turnAgg(r, e.turn).tools++;
      break;
    case 'sensor': {
      r.totals.cost += d.cost || 0;
      const a = turnAgg(r, e.turn);
      a.in += d.inputTokens || 0; a.out += d.outputTokens || 0; a.cost += d.cost || 0;
      break;
    }
    case 'done':
      r.status = (d.status || 'done').toLowerCase();
      r.failure = d.failureReason;
      break;
  }
  renderRuns();
  if (e.taskId === selected) renderDetail();
}

function renderRuns() {
  if (order.length === 0) { runsEl.innerHTML = '<p class="empty">No runs yet.</p>'; return; }
  const ids = query ? order.filter(id => runs.get(id).title.toLowerCase().includes(query)) : order;
  if (ids.length === 0) { runsEl.innerHTML = '<p class="empty">No runs match.</p>'; return; }
  runsEl.innerHTML = ids.map(id => {
    const r = runs.get(id);
    return `<button class="taskrow ${id === selected ? 'active' : ''}" data-id="${id}">
      <span class="taskrow-top">
        <span class="taskrow-title">${escapeHtml(r.title)}</span>
        <span class="status ${statusClass(r.status)}">${escapeHtml(r.status)}</span>
      </span>
      <span class="taskrow-time">${fmtTime(r.started)}</span>
    </button>`;
  }).join('');
  runsEl.querySelectorAll('.taskrow').forEach(b =>
    b.onclick = () => { userPinned = true; selected = b.dataset.id; renderRuns(); renderDetail(); });
}

function renderDetail() {
  const r = runs.get(selected);
  if (!r) { detailEl.innerHTML = '<p class="empty">Select a run, or start one above.</p>'; return; }
  const turns = [...r.turns.values()].sort((a, b) => a.turn - b.turn);
  detailEl.innerHTML = statsHtml(r) + resultHtml(r) + (turns.length ? turnsHtml(turns) : '') + timelineHtml(r);
  detailEl.querySelectorAll('details[data-seq]').forEach(el =>
    el.ontoggle = () => { const s = +el.dataset.seq; el.open ? expanded.add(s) : expanded.delete(s); });
}

// The run's result is the final answer: the text of the last model response (present only when the
// tracer was built with enableSensitiveData). A failed run shows its failure reason instead.
function resultHtml(r) {
  if (r.status === 'failed') return `<section class="result fail"><h2>Result</h2><pre class="answer">${escapeHtml(r.failure || 'Run failed.')}</pre></section>`;
  const ans = resultText(r);
  if (!ans) return '';
  return `<section class="result"><h2>Result</h2>${renderAnswer(ans)}</section>`;
}

function resultText(r) {
  for (let i = r.events.length - 1; i >= 0; i--) {
    const e = r.events[i];
    if (e.kind === 'model' && e.detail && e.detail.text) return e.detail.text;
  }
  return null;
}

function renderAnswer(s) {
  const t = s.trim();
  if (t[0] === '{' || t[0] === '[') {
    try { return `<pre class="answer">${escapeHtml(JSON.stringify(JSON.parse(t), null, 2))}</pre>`; } catch { /* not JSON */ }
  }
  return `<p class="answer">${escapeHtml(s)}</p>`;
}

function statsHtml(r) {
  return `<section class="stats">
    <div class="stat"><span class="k">Status</span><span class="v">${escapeHtml(r.status)}</span></div>
    <div class="stat"><span class="k">Model turns</span><span class="v">${r.totals.modelCalls}</span></div>
    <div class="stat"><span class="k">Tokens in/out</span><span class="v">${fmtN(r.totals.in)} / ${fmtN(r.totals.out)}</span></div>
    <div class="stat"><span class="k">Cached</span><span class="v">${fmtN(r.totals.cached)}</span></div>
    <div class="stat"><span class="k">Cost</span><span class="v">$${r.totals.cost.toFixed(4)}</span></div>
    <div class="stat"><span class="k">Events</span><span class="v">${r.events.length}</span></div>
  </section>`;
}

function turnsHtml(turns) {
  const rows = turns.map(t => `<tr>
    <td>${t.turn + 1}</td><td>${fmtN(t.in)}</td><td>${fmtN(t.cached)}</td>
    <td>${fmtN(t.out)}</td><td>${t.tools}</td><td>$${t.cost.toFixed(4)}</td></tr>`).join('');
  return `<section class="turns"><h2>Telemetry per turn</h2>
    <div class="turns-scroll"><table class="turns-tbl">
      <thead><tr><th>Turn</th><th>Tokens in</th><th>Cached</th><th>Tokens out</th><th>Tool calls</th><th>Cost</th></tr></thead>
      <tbody>${rows}</tbody></table></div></section>`;
}

function timelineHtml(r) {
  let shown = null, rows = '';
  for (const e of r.events) {
    if (e.turn != null && e.turn !== shown) {
      shown = e.turn;
      rows += `<div class="turn-divider"><span>Turn ${e.turn + 1}</span></div>`;
    }
    rows += evtHtml(e);
  }
  return `<section class="timeline"><h2>Trace <span class="muted">(live)</span></h2>${rows}</section>`;
}

function evtHtml(e) {
  const [icon, css] = meta(e);
  const detail = e.detail
    ? `<details class="evt-detail" data-seq="${e.seq}" ${expanded.has(e.seq) ? 'open' : ''}>
         <summary>details</summary><pre>${escapeHtml(JSON.stringify(e.detail, null, 2))}</pre></details>`
    : '';
  return `<div class="evt ${css}">
    <div class="evt-head">
      <span class="evt-icon">${icon}</span>
      <span class="evt-title">${escapeHtml(e.summary)}</span>
      <span class="evt-time">${fmtTime(e._t)}</span>
    </div>${detail}</div>`;
}

function meta(e) {
  const d = e.detail || {};
  switch (e.kind) {
    case 'run': return ['▶', 'start'];
    case 'model:start': return ['⋯', 'running'];
    case 'model': return ['🧠', 'model'];
    case 'tool': return d.isError ? ['⚠️', 'tool error'] : ['🔧', 'tool'];
    case 'sensor': return d.verdict === 'error' ? ['❌', 'sensor error'] : ['🛡️', 'sensor'];
    case 'budget': return ['⏱', 'budget'];
    case 'checkpoint': return ['💾', 'checkpoint'];
    case 'ratelimit': return ['⏳', 'ratelimit'];
    case 'compaction': return ['🗜', 'compaction'];
    case 'warn': return ['⚠️', 'warn'];
    case 'error': return ['❌', 'error'];
    case 'done': return d.status === 'Done' ? ['✅', 'complete'] : ['❌', 'complete fail'];
    default: return ['•', ''];
  }
}

const statusClass = s => s === 'done' ? 'st-done' : s === 'running' ? 'st-running' : 'st-failed';
const fmtN = n => (n || 0).toLocaleString();
const fmtTime = ms => new Date(ms).toLocaleTimeString();
function escapeHtml(s) { const d = document.createElement('div'); d.textContent = s == null ? '' : String(s); return d.innerHTML; }

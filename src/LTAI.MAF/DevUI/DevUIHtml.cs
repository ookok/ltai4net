namespace LTAI.MAF;

internal static class DevUIHtml
{
    public static string Page => """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>LTAI DevUI - Agent Debugging Dashboard</title>
<style>
:root {
  --bg: #0d1117; --panel: #161b22; --border: #30363d;
  --text: #e6edf3; --dim: #8b949e; --accent: #58a6ff;
  --green: #3fb950; --red: #f85149; --yellow: #d29922;
  --purple: #a371f7; --orange: #ff9944;
}
* { margin: 0; padding: 0; box-sizing: border-box; }
body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', monospace; background: var(--bg); color: var(--text); }
.header { background: var(--panel); border-bottom: 1px solid var(--border); padding: 12px 20px; display: flex; align-items: center; gap: 12px; }
.header h1 { font-size: 18px; font-weight: 600; }
.header .badge { background: var(--accent); color: #fff; padding: 2px 8px; border-radius: 4px; font-size: 11px; }
.grid { display: grid; grid-template-columns: 2fr 1fr 1fr; gap: 12px; padding: 12px; height: calc(100vh - 54px); }
.card { background: var(--panel); border: 1px solid var(--border); border-radius: 8px; padding: 12px; overflow: auto; }
.card h2 { font-size: 13px; color: var(--dim); margin-bottom: 10px; text-transform: uppercase; letter-spacing: 1px; }
.metrics { display: grid; grid-template-columns: 1fr 1fr; gap: 8px; }
.metric { background: var(--bg); border: 1px solid var(--border); border-radius: 6px; padding: 10px; text-align: center; }
.metric .value { font-size: 22px; font-weight: 700; color: var(--accent); }
.metric .label { font-size: 10px; color: var(--dim); margin-top: 2px; }
.agent-row { display: flex; align-items: center; gap: 10px; padding: 8px; border-bottom: 1px solid var(--border); }
.agent-row:last-child { border: none; }
.agent-avatar { width: 32px; height: 32px; border-radius: 6px; display: flex; align-items: center; justify-content: center; font-size: 14px; flex-shrink: 0; }
.agent-info { flex: 1; min-width: 0; }
.agent-name { font-size: 13px; font-weight: 600; }
.agent-role { font-size: 10px; color: var(--dim); }
.agent-stats { font-size: 10px; color: var(--dim); text-align: right; }
.status-dot { width: 6px; height: 6px; border-radius: 50%; flex-shrink: 0; }
.status-idle { background: var(--dim); }
.status-running { background: var(--green); animation: pulse 1s infinite; }
.status-error { background: var(--red); }
@keyframes pulse { 0%,100% { opacity: 1; } 50% { opacity: 0.4; } }
.wf-row { padding: 6px 0; border-bottom: 1px solid var(--border); font-size: 11px; display: flex; justify-content: space-between; }
.wf-type { color: var(--purple); font-weight: 600; }
.wf-status { color: var(--green); }
.wf-latency { color: var(--dim); text-align: right; }
.canvas-wrap { flex: 1; min-height: 0; }
.event-list { font-size: 11px; }
.event { padding: 4px 8px; border-left: 2px solid var(--border); margin-bottom: 2px; }
.event.tool { border-left-color: var(--accent); }
.event.skill { border-left-color: var(--purple); }
.event.memory { border-left-color: var(--orange); }
.event.error { border-left-color: var(--red); }
.event .ts { color: var(--dim); margin-right: 6px; }
.event .type { font-weight: 600; margin-right: 4px; }
svg text { fill: var(--text); font-size: 10px; }
svg line, svg path { stroke: var(--border); stroke-width: 1.5; }
.refresh-btn { background: var(--accent); color: #fff; border: none; padding: 6px 12px; border-radius: 4px; cursor: pointer; font-size: 11px; }
.refresh-btn:hover { opacity: 0.9; }
@media (max-width: 900px) { .grid { grid-template-columns: 1fr; } }
</style>
</head>
<body>
<div class="header">
  <h1>🌳 LTAI DevUI</h1>
  <span class="badge">MAF 1.0</span>
  <span style="font-size:11px;color:var(--dim);margin-left:auto">Agent Debugging Dashboard</span>
  <button class="refresh-btn" onclick="refresh()">Refresh</button>
</div>
<div class="grid">
  <div class="card" style="grid-row:1/3">
    <h2>Workflow DAG</h2>
    <div class="canvas-wrap" id="graphContainer"></div>
    <div style="margin-top:8px">
      <h2>Event Timeline</h2>
      <div class="event-list" id="events"></div>
    </div>
  </div>
  <div class="card">
    <h2>Session</h2>
    <div class="metrics" id="metrics"></div>
  </div>
  <div class="card">
    <h2>Agents</h2>
    <div id="agents"></div>
  </div>
  <div class="card" style="grid-column:2/4">
    <h2>Workflows</h2>
    <div id="workflows"></div>
  </div>
</div>
<script>
async function refresh() {
  try {
    const resp = await fetch('/api/devui/state');
    const state = await resp.json();
    render(state);
  } catch(e) { console.error(e); }
}
function render(s) {
  document.getElementById('metrics').innerHTML = `
    <div class="metric"><div class="value">${s.session.id}</div><div class="label">Session ID</div></div>
    <div class="metric"><div class="value">${s.session.totalTokens}</div><div class="label">Tokens</div></div>
    <div class="metric"><div class="value">$${s.session.totalCost}</div><div class="label">Cost</div></div>
    <div class="metric"><div class="value">${s.session.startedAt.slice(11,19)}</div><div class="label">Started</div></div>`;
  document.getElementById('agents').innerHTML = s.agents.map(a => `
    <div class="agent-row">
      <div class="agent-avatar" style="background:${a.status==='running'?'var(--green)':'var(--panel)'}">${a.name[0]}</div>
      <div class="agent-info"><div class="agent-name">${a.name}</div><div class="agent-role">${a.role}</div></div>
      <div class="agent-stats">${a.calls} calls<br>${a.avgLatencyMs}ms avg<br>${a.tokens} tokens</div>
      <div class="status-dot status-${a.status}"></div>
    </div>`).join('');
  document.getElementById('workflows').innerHTML = s.workflows.length === 0
    ? '<div style="color:var(--dim);font-size:11px;text-align:center;padding:20px">No workflows executed yet</div>'
    : s.workflows.map(w => `<div class="wf-row"><span class="wf-type">${w.type}</span><span class="wf-status">${w.status}</span><span class="wf-latency">${w.steps} steps · ${w.latencyMs}ms</span><span style="color:var(--dim);margin-left:8px">${w.ts}</span></div>`).join('');
  document.getElementById('events').innerHTML = [
    { ts: '00:00', type: 'memory', msg: 'ContextMoE: Hot tier hit (3 blocks)' },
    { ts: '00:01', type: 'skill', msg: 'Skill resolved: code-generation' },
    { ts: '00:02', type: 'tool', msg: 'Tool invoked: web_fetch (320ms)' },
    { ts: '00:03', type: 'tool', msg: 'Tool invoked: knowledge_search (180ms)' },
    { ts: '00:05', type: 'memory', msg: 'ContextMoE: Warm tier enriched' }
  ].map(e => `<div class="event ${e.type}"><span class="ts">${e.ts}</span><span class="type">[${e.type}]</span>${e.msg}</div>`).join('');
  drawGraph(s.graph);
}
function drawGraph(g) {
  const container = document.getElementById('graphContainer');
  const w = container.clientWidth - 20;
  const h = Math.max(300, w * 0.7);
  const positions = {
    input: [w*0.05, h*0.5], governor: [w*0.22, h*0.2],
    moe: [w*0.22, h*0.5], election: [w*0.42, h*0.35],
    skills: [w*0.62, h*0.15], tools: [w*0.62, h*0.55],
    capability: [w*0.82, h*0.35], output: [w*0.95, h*0.5]
  };
  const colors = { io: 'var(--accent)', pipeline: 'var(--purple)', memory: 'var(--orange)', routing: 'var(--yellow)', tool: 'var(--green)', orchestra: 'var(--accent)' };
  let svg = `<svg width="${w}" height="${h}" style="display:block">`;
  svg += '<defs><marker id="arrow" markerWidth="8" markerHeight="6" refX="8" refY="3" orient="auto"><path d="M0,0 L8,3 L0,6 Z" fill="var(--border)"/></marker></defs>';
  g.edges.forEach(e => { const f=positions[e.from], t=positions[e.to]; if(f&&t) svg += `<line x1="${f[0]}" y1="${f[1]}" x2="${t[0]}" y2="${t[1]}" marker-end="url(#arrow)"/>`; });
  g.nodes.forEach(n => { const p=positions[n.id]; if(p) svg += `<rect x="${p[0]-40}" y="${p[1]-18}" width="80" height="36" rx="6" fill="var(--bg)" stroke="${colors[n.group]||'var(--border)'}" stroke-width="1.5"/><text x="${p[0]}" y="${p[1]-3}" text-anchor="middle" font-weight="600">${n.label}</text>`; });
  svg += '</svg>';
  container.innerHTML = svg;
}
refresh();
setInterval(refresh, 5000);
</script>
</body>
</html>
""";
}

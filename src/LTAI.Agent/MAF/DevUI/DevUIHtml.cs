namespace LTAI.Agent;

internal static class DevUIHtml
{
    public static string Page => """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>LTAI DevUI - Interactive Knowledge Graph</title>
<script src="https://d3js.org/d3.v7.min.js"></script>
<style>
:root {
  --bg: #0d1117; --panel: #161b22; --border: #30363d;
  --text: #e6edf3; --dim: #8b949e; --accent: #58a6ff;
  --green: #3fb950; --red: #f85149; --yellow: #d29922;
  --purple: #a371f7; --orange: #ff9944; --pink: #f778ba;
}
* { margin: 0; padding: 0; box-sizing: border-box; }
body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', monospace; background: var(--bg); color: var(--text); overflow: hidden; }
#app { display: flex; height: 100vh; }
#sidebar { width: 340px; background: var(--panel); border-right: 1px solid var(--border); display: flex; flex-direction: column; overflow-y: auto; }
#graph-panel { flex: 1; position: relative; }
.header { padding: 12px 16px; border-bottom: 1px solid var(--border); }
.header h1 { font-size: 16px; font-weight: 600; display: flex; align-items: center; gap: 8px; }
.badge { background: var(--accent); color: #fff; padding: 2px 8px; border-radius: 4px; font-size: 10px; }
.toolbar { padding: 8px 12px; border-bottom: 1px solid var(--border); display: flex; gap: 6px; flex-wrap: wrap; }
.toolbar button { background: var(--accent); color: #fff; border: none; padding: 5px 10px; border-radius: 4px; cursor: pointer; font-size: 11px; }
.toolbar button:hover { opacity: 0.85; }
.toolbar button.secondary { background: var(--panel); border: 1px solid var(--border); color: var(--dim); }
.search-bar { padding: 8px 12px; border-bottom: 1px solid var(--border); }
.search-bar input { width: 100%; background: var(--bg); border: 1px solid var(--border); color: var(--text); padding: 6px 10px; border-radius: 4px; font-size: 12px; outline: none; }
.search-bar input:focus { border-color: var(--accent); }
.filter-group { padding: 8px 12px; display: flex; gap: 6px; flex-wrap: wrap; }
.filter-chip { padding: 2px 8px; border-radius: 10px; font-size: 10px; cursor: pointer; border: 1px solid var(--border); color: var(--dim); user-select: none; }
.filter-chip.active { border-color: currentColor; opacity: 1; }
.filter-chip.code { color: var(--accent); }
.filter-chip.doc { color: var(--green); }
.filter-chip.config { color: var(--yellow); }
.filter-chip.ui { color: var(--purple); }
.filter-chip.test { color: var(--pink); }
.stats { padding: 8px 12px; display: flex; gap: 12px; font-size: 10px; color: var(--dim); }
.stats span { display: flex; gap: 3px; align-items: center; }
.stats .dot { width: 8px; height: 8px; border-radius: 50%; display: inline-block; }
.node-detail { padding: 12px; border-top: 1px solid var(--border); display: none; }
.node-detail.visible { display: block; }
.node-detail h3 { font-size: 13px; margin-bottom: 6px; word-break: break-all; }
.node-detail .meta { font-size: 10px; color: var(--dim); margin-bottom: 8px; }
.node-detail .relations h4 { font-size: 10px; color: var(--dim); margin: 6px 0 4px; text-transform: uppercase; }
.node-detail .relations ul { list-style: none; font-size: 10px; }
.node-detail .relations li { padding: 2px 0; color: var(--accent); cursor: pointer; }
.node-detail .relations li:hover { text-decoration: underline; }
svg text { font-size: 9px; pointer-events: none; }
svg line, svg path.link { stroke: var(--border); stroke-width: 0.5; stroke-opacity: 0.4; }
svg .node circle { cursor: pointer; }
svg .node:hover circle { stroke-width: 2px; }
.legend { position: absolute; bottom: 10px; right: 10px; background: var(--panel); border: 1px solid var(--border); border-radius: 6px; padding: 8px 12px; font-size: 10px; display: flex; flex-direction: column; gap: 4px; }
.legend-item { display: flex; align-items: center; gap: 6px; }
.legend-dot { width: 10px; height: 10px; border-radius: 50%; }
.zoom-controls { position: absolute; top: 10px; right: 10px; display: flex; flex-direction: column; gap: 4px; }
.zoom-controls button { width: 28px; height: 28px; background: var(--panel); border: 1px solid var(--border); color: var(--text); border-radius: 4px; cursor: pointer; font-size: 14px; }
.zoom-controls button:hover { background: var(--accent); }
.impact-bar { padding: 8px 12px; border-bottom: 1px solid var(--border); display: none; }
.impact-bar.visible { display: flex; align-items: center; gap: 8px; }
.impact-bar .impact-score { font-size: 20px; font-weight: 700; }
.impact-bar .impact-label { font-size: 10px; color: var(--dim); }
.high { color: var(--red); }
.medium { color: var(--yellow); }
.low { color: var(--green); }
</style>
</head>
<body>
<div id="app">
  <div id="sidebar">
    <div class="header">
      <h1><span style="font-size:20px">🌳</span> LTAI Knowledge Graph</h1>
      <div style="margin-top:4px"><span class="badge">v1.0</span>
      <span style="font-size:10px;color:var(--dim);margin-left:8px">Interactive Code Explorer</span></div>
    </div>
    <div class="toolbar">
      <button onclick="loadGraph()">Refresh</button>
      <button class="secondary" onclick="analyzeImpact()">Impact Analysis</button>
      <button class="secondary" onclick="generateTour()">Guided Tour</button>
    </div>
    <div id="impact-bar" class="impact-bar">
      <div class="impact-score" id="impactScore"></div>
      <div class="impact-label" id="impactDetails"></div>
    </div>
    <div class="search-bar">
      <input type="text" id="searchInput" placeholder="Search files, classes, functions..." oninput="searchNodes(this.value)">
    </div>
    <div class="filter-group" id="filterGroup"></div>
    <div class="stats" id="statsBar"></div>
    <div class="node-detail" id="nodeDetail">
      <h3 id="detailTitle"></h3>
      <div class="meta" id="detailMeta"></div>
      <div class="relations" id="detailRelations"></div>
    </div>
  </div>
  <div id="graph-panel">
    <svg id="graphSvg"></svg>
    <div class="zoom-controls">
      <button onclick="zoomIn()">+</button>
      <button onclick="zoomOut()">-</button>
      <button onclick="resetZoom()">⌂</button>
    </div>
    <div class="legend" id="legend"></div>
  </div>
</div>
<script>
const colors = { code: '#58a6ff', doc: '#3fb950', config: '#d29922', ui: '#a371f7', script: '#f778ba', test: '#ff9944', concept: '#f0883e', other: '#8b949e' };
const groups = ['code', 'doc', 'config', 'ui', 'script', 'test', 'concept', 'other'];
let activeFilters = new Set(groups);
let graphData = null;
let simulation = null;
let svg = null;
let g = null;
let zoom = null;
let searchHighlight = null;

async function loadGraph() {
  try {
    const resp = await fetch('/api/devui/graph').ConfigureAwait(false);
    graphData = await resp.json().ConfigureAwait(false);
    renderAll(graphData);
  } catch (e) {
    console.warn('API not available, using sample data');
    const resp = await fetch('/api/devui/state').ConfigureAwait(false);
    const state = await resp.json().ConfigureAwait(false);
    graphData = state.graph;
    renderAll(graphData);
  }
}

function renderAll(data) {
  graphData = data;
  renderLegend();
  renderStats(data);
  renderFilters();
  drawForceGraph(data);
}

function renderStats(d) {
  const counts = {};
  d.nodes.forEach(n => { counts[n.group] = (counts[n.group] || 0) + 1; });
  document.getElementById('statsBar').innerHTML = Object.entries(counts).map(([g, c]) =>
    `<span><span class="dot" style="background:${colors[g]}"></span>${g}: ${c}</span>`
  ).join('') + `<span style="margin-left:auto">${d.nodes.length} nodes · ${d.edges.length} edges</span>`;
}

function renderFilters() {
  document.getElementById('filterGroup').innerHTML = groups.map(g =>
    `<span class="filter-chip ${g}${activeFilters.has(g)?' active':''}" onclick="toggleFilter('${g}')">${g}</span>`
  ).join('');
}

function renderLegend() {
  document.getElementById('legend').innerHTML = groups.map(g =>
    `<div class="legend-item"><span class="legend-dot" style="background:${colors[g]}"></span>${g}</div>`
  ).join('');
}

function toggleFilter(group) {
  activeFilters.has(group) ? activeFilters.delete(group) : activeFilters.add(group);
  renderFilters();
  drawForceGraph(graphData);
}

function searchNodes(query) {
  if (!graphData) return;
  svg?.selectAll('.node').each(function(d) {
    const match = !query || d.label.toLowerCase().includes(query.toLowerCase()) ||
                  d.id.toLowerCase().includes(query.toLowerCase());
    d3.select(this).style('opacity', match ? 1 : 0.1);
  });
  if (query && graphData.nodes.length > 0) {
    const found = graphData.nodes.find(n => n.label.toLowerCase().includes(query.toLowerCase()));
    if (found) zoomToNode(found.id);
  }
}

function drawForceGraph(data) {
  const container = document.getElementById('graph-panel');
  const w = container.clientWidth;
  const h = container.clientHeight;

  svg?.remove();
  svg = d3.select('#graphSvg').attr('width', w).attr('height', h);
  g = svg.append('g');

  zoom = d3.zoom().scaleExtent([0.1, 4]).on('zoom', (e) => g.attr('transform', e.transform));
  svg.call(zoom);

  const filteredNodes = data.nodes.filter(n => activeFilters.has(n.group));
  const filteredIds = new Set(filteredNodes.map(n => n.id));
  const filteredEdges = data.edges.filter(e => filteredIds.has(e.from) && filteredIds.has(e.to));

  const nodes = filteredNodes.map(d => ({...d}));
  const links = filteredEdges.map(d => ({...d}));

  const link = g.append('g').selectAll('line').data(links).join('line')
    .attr('class', 'link').attr('stroke', 'var(--border)').attr('stroke-width', 0.5).attr('stroke-opacity', 0.35);

  const node = g.append('g').selectAll('.node').data(nodes).join('g').attr('class', 'node')
    .call(d3.drag().on('start', dragStart).on('drag', dragged).on('end', dragEnd))
    .on('click', (e, d) => { e.stopPropagation(); showNodeDetail(d); });

  node.append('circle').attr('r', d => d.group === 'code' ? 5 : d.group === 'doc' ? 4 : 3)
    .attr('fill', d => colors[d.group] || colors.other).attr('stroke', d => colors[d.group] || colors.other);

  node.append('title').text(d => d.id);

  simulation = d3.forceSimulation(nodes)
    .force('link', d3.forceLink(links).id(d => d.id).distance(60))
    .force('charge', d3.forceManyBody().strength(-120))
    .force('center', d3.forceCenter(w / 2, h / 2))
    .force('collision', d3.forceCollide(10))
    .on('tick', () => {
      link.attr('x1', d => d.source.x).attr('y1', d => d.source.y)
          .attr('x2', d => d.target.x).attr('y2', d => d.target.y);
      node.attr('transform', d => `translate(${d.x},${d.y})`);
    });
}

function dragStart(e, d) { if (!e.active) simulation?.alphaTarget(0.3).restart(); d.fx = d.x; d.fy = d.y; }
function dragged(e, d) { d.fx = e.x; d.fy = e.y; }
function dragEnd(e, d) { if (!e.active) simulation?.alphaTarget(0); d.fx = null; d.fy = null; }

function showNodeDetail(d) {
  document.getElementById('nodeDetail').classList.add('visible');
  document.getElementById('detailTitle').textContent = d.label;
  document.getElementById('detailMeta').innerHTML = `
    <div>Path: ${d.id}</div>
    <div>Type: <span style="color:${colors[d.group]}">${d.group}</span></div>
    <div>Ext: ${d.ext || 'N/A'}</div>`;
  const relations = [];
  if (graphData) {
    graphData.edges.filter(e => e.from === d.id).forEach(e => relations.push({dir:'depends on', id:e.to}));
    graphData.edges.filter(e => e.to === d.id).forEach(e => relations.push({dir:'depended by', id:e.from}));
  }
  document.getElementById('detailRelations').innerHTML = relations.length > 0
    ? `<h4>Relations (${relations.length})</h4><ul>${relations.map(r => `<li onclick="zoomToNode('${r.id}')">${r.dir}: ${r.id}</li>`).join('')}</ul>`
    : '<div style="font-size:10px;color:var(--dim)">No relations found</div>';
}

function zoomToNode(id) {
  if (!graphData) return;
  const d = graphData.nodes.find(n => n.id === id);
  if (!d || !d.x || !d.y) return;
  const w = document.getElementById('graph-panel').clientWidth;
  const h = document.getElementById('graph-panel').clientHeight;
  svg.transition().duration(750).call(zoom.transform, d3.zoomIdentity.translate(w/2 - d.x, h/2 - d.y).scale(2));
  showNodeDetail(d);
}

function zoomIn() { svg.transition().duration(300).call(zoom.scaleBy, 1.5); }
function zoomOut() { svg.transition().duration(300).call(zoom.scaleBy, 0.7); }
function resetZoom() { svg.transition().duration(500).call(zoom.transform, d3.zoomIdentity); }

async function analyzeImpact() {
  try {
    const resp = await fetch('/api/devui/impact').ConfigureAwait(false);
    const impact = await resp.json().ConfigureAwait(false);
    const bar = document.getElementById('impact-bar');
    bar.classList.add('visible');
    const level = impact.score > 0.7 ? 'high' : impact.score > 0.3 ? 'medium' : 'low';
    document.getElementById('impactScore').className = 'impact-score ' + level;
    document.getElementById('impactScore').textContent = Math.round(impact.score * 100) + '%';
    document.getElementById('impactDetails').innerHTML = `
      <div>${impact.changed_files} changed files</div>
      <div>${impact.affected_nodes} potentially affected</div>`;
    const resp2 = await fetch('/api/devui/graph').ConfigureAwait(false);
    if (resp2.ok) {
      graphData = await resp2.json().ConfigureAwait(false);
      drawForceGraph(graphData);
    }
  } catch (e) {
    document.getElementById('impact-bar').classList.add('visible');
    document.getElementById('impactScore').className = 'impact-score medium';
    document.getElementById('impactScore').textContent = '--';
    document.getElementById('impactDetails').innerHTML = '<div>Run /understand-diff for impact analysis</div>';
  }
}

async function generateTour() {
  try {
    const resp = await fetch('/api/devui/tour').ConfigureAwait(false);
    const tour = await resp.json().ConfigureAwait(false);
    alert('Tour generated: ' + tour.steps.length + ' steps\n' + tour.steps.map(s => s.order + '. ' + s.file).join('\n'));
  } catch (e) {
    alert('Tour generation requires the graph to be loaded. Click Refresh first.');
  }
}

document.getElementById('graph-panel').addEventListener('click', () => {
  document.getElementById('nodeDetail').classList.remove('visible');
});

loadGraph();
setInterval(loadGraph, 30000);
</script>
</body>
</html>
""";
}

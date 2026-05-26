using System.Reflection;
using Spectre.Console;
using Spectre.Console.Rendering;
using LTAI.Knowledge.Core;
using LTAI.Knowledge.Core.Models;

namespace LTAI.TUI;

public sealed class KgBrowserView
{
    private KnowledgeGraph? _graph;
    private List<Entity> _entities = new();
    private int _selectedIdx;
    private List<int> _breadcrumbs = new();
    private string _searchText = "";
    private bool _inSearchMode;
    private Dictionary<string, double>? _pageRankCache;
    private List<(string NodeId, int InDegree, int OutDegree)>? _centralityCache;

    public KgBrowserView(KnowledgeGraph? graph = null)
    {
        _graph = graph;
        if (_graph != null) RefreshEntities();
    }

    public void SetGraph(KnowledgeGraph graph)
    {
        _graph = graph;
        RefreshEntities();
    }

    public KnowledgeGraph? Graph => _graph;

    private void RefreshEntities()
    {
        if (_graph == null) return;
        try
        {
            var method = typeof(KnowledgeGraph).GetMethod("GetAllNodes",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (method != null)
                _entities = (method.Invoke(_graph, null) as List<Entity>) ?? new();
        }
        catch { _entities = new(); }

        _pageRankCache = null;
        _centralityCache = null;
    }

    private void EnsureAnalytics()
    {
        if (_pageRankCache == null && _graph != null)
        {
            try { _pageRankCache = _graph.PageRank(); } catch { _pageRankCache = new(); }
        }
        if (_centralityCache == null && _graph != null)
        {
            try { _centralityCache = _graph.Centrality(); } catch { _centralityCache = new(); }
        }
    }

    public IRenderable Render()
    {
        if (_graph == null)
            return new Panel(new Markup("[grey]Knowledge Graph not available.[/]"))
                .RoundedBorder().Header("[cyan]Knowledge Graph Explorer[/]");

        if (_inSearchMode)
            return RenderSearchMode();

        if (_breadcrumbs.Count == 0)
            return RenderRootList();
        else
            return RenderEntityDetail();
    }

    private IRenderable RenderSearchMode()
    {
        var panel = new Panel(BuildSearchResults());
        panel.Header = new PanelHeader($"[cyan]Knowledge Graph Explorer[/] [yellow]Search[/]");
        panel.Border = BoxBorder.Rounded;
        return panel;
    }

    private IRenderable BuildSearchResults()
    {
        if (_graph == null) return new Markup("");

        var tree = new Tree($"[bold cyan]Search:[/] \"[white]{_searchText}[/]\"");
        List<Entity> results;

        try
        {
            results = _graph.SearchEntities(_searchText, 30);
        }
        catch
        {
            results = _entities
                .Where(e => e.Label.Contains(_searchText, StringComparison.OrdinalIgnoreCase))
                .Take(30).ToList();
        }

        if (results.Count == 0)
        {
            tree.AddNode("[grey]No entities found.[/]");
        }
        else
        {
            tree.AddNode($"[grey]Found {results.Count} entities[/]");
            for (int i = 0; i < results.Count; i++)
            {
                var e = results[i];
                var prefix = i == _selectedIdx ? "[cyan]>[/] " : "  ";
                var label = i == _selectedIdx ? $"[bold white]{Escape(e.Label)}[/]" : $"[white]{Escape(e.Label)}[/]";
                var entityType = GetEntityType(e);
                tree.AddNode($"{prefix}{label} [dim]({entityType})[/]");
            }
        }

        if (!string.IsNullOrWhiteSpace(_searchText))
            tree.AddNode("[grey]↑↓ navigate | Enter select | Esc cancel search | Shift+S clear[/]");

        return tree;
    }

    private IRenderable RenderRootList()
    {
        EnsureAnalytics();
        var panel = new Panel(BuildRootList());
        panel.Header = new PanelHeader("[cyan]Knowledge Graph Explorer[/]");
        panel.Border = BoxBorder.Rounded;
        return panel;
    }

    private IRenderable BuildRootList()
    {
        var tree = new Tree("[bold cyan]Entities[/]");

        // Show top-N most connected entities (by PageRank)
        if (_pageRankCache != null && _pageRankCache.Count > 0)
        {
            var topRanked = _pageRankCache.OrderByDescending(kv => kv.Value).Take(15).ToList();
            var topNode = tree.AddNode($"[yellow]Top 15 by PageRank[/] ([green]{_pageRankCache.Count} total[/])");

            for (int i = 0; i < topRanked.Count; i++)
            {
                var (id, score) = topRanked[i];
                var entity = _entities.FirstOrDefault(e => e.Id == id);
                var label = entity?.Label ?? id;
                var prefix = i == _selectedIdx ? "[cyan]>[/] " : "  ";
                var labelMarkup = i == _selectedIdx
                    ? $"[bold white]{Escape(label)}[/]"
                    : $"[white]{Escape(label)}[/]";
                var entityType = entity != null ? GetEntityType(entity) : "node";
                topNode.AddNode($"{prefix}{labelMarkup} [dim]({entityType})[/] [grey]PR:{score:F3}[/]");
            }
        }

        // Show by centrality
        if (_centralityCache != null && _centralityCache.Count > 0)
        {
            var byCentrality = _centralityCache.Take(10).ToList();
            var centralNode = tree.AddNode($"[yellow]Top 10 by Degree Centrality[/]");
            int offset = _pageRankCache != null ? Math.Min(_pageRankCache.Count, 15) : 0;

            for (int i = 0; i < byCentrality.Count; i++)
            {
                var (id, inDeg, outDeg) = byCentrality[i];
                var entity = _entities.FirstOrDefault(e => e.Id == id);
                var label = entity?.Label ?? id;
                var idx = offset + i;
                var prefix = idx == _selectedIdx ? "[cyan]>[/] " : "  ";
                var labelMarkup = idx == _selectedIdx
                    ? $"[bold white]{Escape(label)}[/]"
                    : $"[white]{Escape(label)}[/]";
                centralNode.AddNode($"{prefix}{labelMarkup} [dim]in:{inDeg} out:{outDeg}[/]");
            }
        }

        // By entity type grouping
        var byType = _entities
            .GroupBy(e => GetEntityType(e))
            .OrderByDescending(g => g.Count())
            .Take(8);

        if (byType.Any())
        {
            var typeNode = tree.AddNode($"[yellow]By Type[/]");
            foreach (var group in byType)
            {
                typeNode.AddNode($"[white]{Escape(group.Key)}[/] [grey]({group.Count()})[/]");
            }
        }

        if (_entities.Count > 0)
            tree.AddNode($"[dim]Total: {_entities.Count} entities | ↑↓ navigate | Enter expand | S search | Esc back[/]");

        return tree;
    }

    private IRenderable RenderEntityDetail()
    {
        var entityIdx = _breadcrumbs[^1];
        var entity = _entities[entityIdx];

        var breadcrumbPath = string.Join(" → ",
            _breadcrumbs.Select(i =>
            {
                var e = i < _entities.Count ? _entities[i] : null;
                return e?.Label ?? "?";
            }));

        var panel = new Panel(BuildEntityDetailPanel(entity));
        panel.Header = new PanelHeader($"[cyan]Graph Explorer[/]  [dim]{breadcrumbPath}[/]");
        panel.Border = BoxBorder.Rounded;
        return panel;
    }

    private IRenderable BuildEntityDetailPanel(Entity entity)
    {
        var tree = new Tree($"[bold white]{Escape(entity.Label)}[/]");
        tree.AddNode($"[yellow]ID:[/] [dim]{entity.Id}[/]");

        var entityType = GetEntityType(entity);
        tree.AddNode($"[yellow]Type:[/] [cyan]{Escape(entityType)}[/]");

        if (entity.Properties != null && entity.Properties.Count > 0)
        {
            var propsNode = tree.AddNode("[yellow]Properties:[/]");
            foreach (var (key, val) in entity.Properties.Take(8))
                propsNode.AddNode($"[grey]{Escape(key)}:[/] [white]{Escape(val?.ToString() ?? "null")}[/]");
        }

        try
        {
            if (_graph != null)
            {
                var triplets = _graph.GetTriplets();

                var outgoing = triplets
                    .Where(t => t.Subject == entity.Label)
                    .Take(15).ToList();

                if (outgoing.Count > 0)
                {
                    var outNode = tree.AddNode($"[green]Outgoing Relations ({outgoing.Count}):[/]");
                    int i = 0;
                    foreach (var t in outgoing)
                    {
                        var line = $"[cyan]{Escape(t.Predicate)}[/] → [white]{Escape(t.Object)}[/] [dim]({t.Confidence:F2})[/]";
                        if (_breadcrumbs.Count == 1 && i == _selectedIdx)
                            outNode.AddNode($"[bold]{line}[/]");
                        else
                            outNode.AddNode(line);
                        i++;
                    }
                }
                else
                {
                    tree.AddNode("[grey]No outgoing relations[/]");
                }

                var incoming = triplets
                    .Where(t => t.Object == entity.Label)
                    .Take(10).ToList();

                if (incoming.Count > 0)
                {
                    var inNode = tree.AddNode($"[blue]Referenced By ({incoming.Count}):[/]");
                    foreach (var t in incoming)
                        inNode.AddNode($"[white]{Escape(t.Subject)}[/] [cyan]{Escape(t.Predicate)}[/] [dim]({t.Confidence:F2})[/]");
                }
            }

            // Stats
            if (_centralityCache != null)
            {
                var central = _centralityCache.FirstOrDefault(c => c.NodeId == entity.Id);
                if (central.NodeId != null)
                    tree.AddNode($"[yellow]Degree:[/] in={central.InDegree} out={central.OutDegree} total={central.InDegree + central.OutDegree}");
            }

            if (_pageRankCache != null && _pageRankCache.TryGetValue(entity.Id, out var pr))
                tree.AddNode($"[yellow]PageRank:[/] [grey]{pr:F4}[/]");
        }
        catch { }

        tree.AddNode("[grey]↑↓ select relation | Enter navigate | B back | S search | B to root[/]");

        return tree;
    }

    public void HandleKey(ConsoleKeyInfo key)
    {
        var maxItems = _inSearchMode
            ? (_graph?.SearchEntities(_searchText, 30).Count ?? 0)
            : (_breadcrumbs.Count == 0 ? GetRootItemCount() : 0);

        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                _selectedIdx = Math.Max(_selectedIdx - 1, 0);
                break;
            case ConsoleKey.DownArrow:
                _selectedIdx = Math.Min(_selectedIdx + 1, Math.Max(0, maxItems - 1));
                break;
            case ConsoleKey.Enter:
                HandleEnter();
                break;
            case ConsoleKey.B:
                GoBack();
                break;
            case ConsoleKey.S when key.Modifiers == 0:
                if (_inSearchMode)
                {
                    // Execute search
                    _inSearchMode = true;
                }
                else
                {
                    EnterSearchMode();
                }
                break;
            case ConsoleKey.Escape:
                if (_inSearchMode)
                {
                    _inSearchMode = false;
                    _searchText = "";
                    _breadcrumbs.Clear();
                    _selectedIdx = 0;
                }
                else
                {
                    GoBack();
                }
                break;
        }
    }

    public void EnterSearchMode()
    {
        _inSearchMode = true;
        _searchText = "";
        _selectedIdx = 0;
    }

    public void AppendSearchChar(char c)
    {
        if (!_inSearchMode) return;

        if (char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) || c == '_' || c == '-' || c == '.')
        {
            _searchText += c;
            _selectedIdx = 0;
        }
    }

    public void DeleteSearchChar()
    {
        if (!_inSearchMode || _searchText.Length == 0) return;
        _searchText = _searchText[..^1];
        _selectedIdx = 0;
    }

    public bool InSearchMode => _inSearchMode;

    private int GetRootItemCount()
    {
        if (_pageRankCache != null)
            return Math.Min(_pageRankCache.Count, 15)
                + (_centralityCache != null ? Math.Min(_centralityCache.Count, 10) : 0);
        return _entities.Count;
    }

    private void HandleEnter()
    {
        if (_inSearchMode)
        {
            var results = _graph?.SearchEntities(_searchText, 30) ?? new();
            if (_selectedIdx < results.Count)
            {
                var entity = results[_selectedIdx];
                var idx = _entities.FindIndex(e => e.Id == entity.Id);
                if (idx >= 0)
                {
                    _breadcrumbs.Add(idx);
                    _selectedIdx = 0;
                    _inSearchMode = false;
                }
            }
            return;
        }

        if (_breadcrumbs.Count == 0)
        {
            // Select entity from root list
            var entityIdx = GetEntityIndexFromRootList();
            if (entityIdx >= 0 && entityIdx < _entities.Count)
            {
                _breadcrumbs.Add(entityIdx);
                _selectedIdx = 0;
            }
        }
        else if (_breadcrumbs.Count == 1)
        {
            // Navigate to relation target
            var entity = _entities[_breadcrumbs[0]];
            if (_graph != null)
            {
                var triplets = _graph.GetTriplets()
                    .Where(t => t.Subject == entity.Label)
                    .Take(15).ToList();
                if (_selectedIdx < triplets.Count)
                {
                    var targetLabel = triplets[_selectedIdx].Object;
                    var targetIdx = _entities.FindIndex(e =>
                        e.Label.Equals(targetLabel, StringComparison.OrdinalIgnoreCase));
                    if (targetIdx >= 0)
                    {
                        _breadcrumbs.Add(targetIdx);
                        _selectedIdx = 0;
                    }
                }
            }
        }
    }

    private int GetEntityIndexFromRootList()
    {
        if (_pageRankCache != null)
        {
            var topRanked = _pageRankCache.OrderByDescending(kv => kv.Value).Take(15).ToList();
            var prCount = Math.Min(_pageRankCache.Count, 15);
            if (_selectedIdx < prCount)
            {
                var id = topRanked[_selectedIdx].Key;
                return _entities.FindIndex(e => e.Id == id);
            }

            if (_centralityCache != null)
            {
                var centralIdx = _selectedIdx - prCount;
                var byCentrality = _centralityCache.Take(10).ToList();
                if (centralIdx < byCentrality.Count)
                {
                    var id = byCentrality[centralIdx].NodeId;
                    return _entities.FindIndex(e => e.Id == id);
                }
            }
        }

        return _entities.Count > 0 && _selectedIdx < _entities.Count ? _selectedIdx : -1;
    }

    private void GoBack()
    {
        if (_breadcrumbs.Count > 0)
        {
            _breadcrumbs.RemoveAt(_breadcrumbs.Count - 1);
            _selectedIdx = 0;
        }
    }

    private static string GetEntityType(Entity entity)
    {
        if (entity.Properties != null && entity.Properties.TryGetValue("type", out var typeObj))
        {
            var typeStr = typeObj?.ToString();
            if (!string.IsNullOrWhiteSpace(typeStr)) return typeStr;
        }
        return "entity";
    }

    private static string Escape(string text) =>
        text.Replace("[", "[[").Replace("]", "]]");
}

using System.Collections.Generic;
using System.Linq;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace LTAI.Desktop;

public sealed class KnowledgeGraphExplorer : UserControl
{
    private readonly LTAIService _svc;
    private readonly TextBox _searchBox;
    private readonly Button _searchBtn;
    private readonly ListBox _resultsList;
    private readonly TextBlock _detailText;
    private readonly ListBox _relatedList;
    private readonly ListBox _breadcrumbList;
    private readonly Button _exportDotBtn;

    private readonly List<string> _breadcrumbs = new();
    private readonly Dictionary<string, string> _entityLabels = new();

    public KnowledgeGraphExplorer(LTAIService svc)
    {
        _svc = svc;
        Background = LtaiTheme.Sbb(LtaiTheme.Bg);

        var root = new DockPanel { Margin = new(16) };

        var header = new TextBlock
        {
            Text = "Knowledge Graph Explorer",
            FontSize = 20,
            FontWeight = FontWeight.Bold,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary)
        };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var sep = new Border { Height = 1, Background = LtaiTheme.Sbb(LtaiTheme.Border), Margin = new(0, 8) };
        DockPanel.SetDock(sep, Dock.Top);
        root.Children.Add(sep);

        var searchRow = new DockPanel { Margin = new(0, 0, 0, 8) };
        _searchBox = new TextBox
        {
            Watermark = "Search entities...",
            Background = LtaiTheme.Sbb(LtaiTheme.BgInput),
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
            FontFamily = new("Consolas"),
            FontSize = 13
        };
        _searchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) PerformSearch();
        };
        _searchBtn = new Button
        {
            Content = "Search",
            Background = LtaiTheme.Sbb(LtaiTheme.AccentDNA),
            Foreground = LtaiTheme.Sbb("#ffffff"),
            FontSize = 12,
            Width = 70,
            FontWeight = FontWeight.Bold
        };
        _searchBtn.Click += (_, _) => PerformSearch();

        DockPanel.SetDock(_searchBtn, Dock.Right);
        searchRow.Children.Add(_searchBtn);
        searchRow.Children.Add(_searchBox);
        DockPanel.SetDock(searchRow, Dock.Top);
        root.Children.Add(searchRow);

        var breadcrumbRow = new DockPanel { Margin = new(0, 0, 0, 4) };
        _breadcrumbList = new ListBox
        {
            Height = 30,
            Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
            SelectionMode = SelectionMode.Single
        };
        _breadcrumbList.SelectionChanged += (_, _) =>
        {
            if (_breadcrumbList.SelectedItem is string sel)
            {
                var idx = _breadcrumbs.IndexOf(sel);
                if (idx >= 0 && idx < _breadcrumbs.Count - 1)
                {
                    _breadcrumbs.RemoveRange(idx + 1, _breadcrumbs.Count - idx - 1);
                    ShowEntity(sel);
                }
            }
        };

        var homeBtn = new Button
        {
            Content = "Home",
            Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
            FontSize = 11,
            Width = 50,
            Height = 28
        };
        homeBtn.Click += (_, _) =>
        {
            _breadcrumbs.Clear();
            UpdateBreadcrumbs();
            _detailText!.Text = "Enter a search query to explore the knowledge graph.";
            _relatedList!.ItemsSource = Array.Empty<RelatedEntry>();
        };

        DockPanel.SetDock(homeBtn, Dock.Left);
        breadcrumbRow.Children.Add(homeBtn);
        breadcrumbRow.Children.Add(_breadcrumbList);
        DockPanel.SetDock(breadcrumbRow, Dock.Top);
        root.Children.Add(breadcrumbRow);

        var mainGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("1.5*,1.5*,1*")
        };

        var leftPanel = new StackPanel { Spacing = 4, Margin = new(0, 0, 6, 0) };
        leftPanel.Children.Add(new TextBlock
        {
            Text = "Results",
            Foreground = LtaiTheme.Sbb(LtaiTheme.AccentDNA),
            FontSize = 12,
            FontWeight = FontWeight.Bold
        });
        _resultsList = new ListBox
        {
            Background = LtaiTheme.Sbb(LtaiTheme.BgInput),
            MinHeight = 200
        };
        _resultsList.SelectionChanged += (_, _) =>
        {
            if (_resultsList.SelectedItem is LTAI.Knowledge.Core.Models.Entity entity)
            {
                _breadcrumbs.Add(entity.Id);
                UpdateBreadcrumbs();
                ShowEntity(entity.Id);
            }
        };
        leftPanel.Children.Add(_resultsList);

        var centerPanel = new StackPanel { Spacing = 4, Margin = new(6, 0, 6, 0) };
        centerPanel.Children.Add(new TextBlock
        {
            Text = "Entity Detail",
            Foreground = LtaiTheme.Sbb(LtaiTheme.AccentSystem),
            FontSize = 12,
            FontWeight = FontWeight.Bold
        });
        _detailText = new TextBlock
        {
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
            FontFamily = new("Consolas"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };
        var detailScroll = new ScrollViewer
        {
            Content = _detailText,
            Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
            Padding = new(8)
        };
        centerPanel.Children.Add(detailScroll);

        _exportDotBtn = new Button
        {
            Content = "Export DOT",
            Background = LtaiTheme.Sbb(LtaiTheme.AccentWarning),
            Foreground = LtaiTheme.Sbb("#ffffff"),
            FontSize = 11,
            Margin = new(0, 4, 0, 0)
        };
        _exportDotBtn.Click += (_, _) => ExportDotGraph();
        centerPanel.Children.Add(_exportDotBtn);

        var rightPanel = new StackPanel { Spacing = 4, Margin = new(6, 0, 0, 0) };
        rightPanel.Children.Add(new TextBlock
        {
            Text = "Related",
            Foreground = LtaiTheme.Sbb(LtaiTheme.AccentInfo),
            FontSize = 12,
            FontWeight = FontWeight.Bold
        });
        _relatedList = new ListBox
        {
            Background = LtaiTheme.Sbb(LtaiTheme.BgInput),
            MinHeight = 200
        };
        _relatedList.SelectionChanged += (_, _) =>
        {
            if (_relatedList.SelectedItem is RelatedEntry entry)
            {
                _breadcrumbs.Add(entry.Id);
                UpdateBreadcrumbs();
                ShowEntity(entry.Id);
            }
        };
        rightPanel.Children.Add(_relatedList);

        var statsBtn = new Button
        {
            Content = "Graph Stats",
            Background = LtaiTheme.Sbb(LtaiTheme.AccentInfo),
            Foreground = LtaiTheme.Sbb("#ffffff"),
            FontSize = 11,
            Margin = new(0, 4, 0, 0)
        };
        statsBtn.Click += (_, _) => ShowStats();
        rightPanel.Children.Add(statsBtn);

        Grid.SetColumn(leftPanel, 0);
        Grid.SetColumn(centerPanel, 1);
        Grid.SetColumn(rightPanel, 2);
        mainGrid.Children.Add(leftPanel);
        mainGrid.Children.Add(centerPanel);
        mainGrid.Children.Add(rightPanel);
        root.Children.Add(mainGrid);

        Content = root;

        _detailText.Text = "Enter a search query to explore the knowledge graph.";
    }

    private void PerformSearch()
    {
        var query = _searchBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(query)) return;

        var kg = TryGetKnowledgeGraph();
        if (kg == null)
        {
            _detailText.Text = "Knowledge Graph is not available.";
            return;
        }

        try
        {
            var results = kg.SearchEntities(query, 20);
            foreach (var e in results)
                _entityLabels[e.Id] = e.Label;

            _resultsList.ItemsSource = results.Select(e => new SearchResultItem
            {
                Entity = e,
                Display = e.Label
            }).ToList();

            _breadcrumbs.Clear();
            UpdateBreadcrumbs();
            _detailText.Text = $"Found {results.Count} entities for '{query}'.";
        }
        catch (Exception ex)
        {
            _detailText.Text = $"Search error: {ex.Message}";
        }
    }

    private void ShowEntity(string entityId)
    {
        var kg = TryGetKnowledgeGraph();
        if (kg == null)
        {
            _detailText.Text = "Knowledge Graph is not available.";
            return;
        }

        try
        {
            if (!_entityLabels.TryGetValue(entityId, out var label))
                label = entityId;

            var sb = new StringBuilder();
            sb.AppendLine($"ID: {entityId}");
            sb.AppendLine($"Label: {label}");

            var stats = kg.GetStats();
            if (stats.TryGetValue("predictability", out var pi) && pi != null)
            {
                sb.AppendLine();
                sb.AppendLine($"PI: {pi}");
            }

            _detailText.Text = sb.ToString();

            var related = new List<RelatedEntry>();
            var triplets = kg.SearchTriplets(label, 100);

            foreach (var t in triplets)
            {
                if (t.Subject.Equals(label, StringComparison.OrdinalIgnoreCase))
                {
                    var targetId = LTAI.Knowledge.Core.KnowledgeGraph.EntityId(t.Object);
                    if (targetId != entityId)
                    {
                        related.Add(new RelatedEntry
                        {
                            Id = targetId,
                            Label = t.Object,
                            Relation = t.Predicate
                        });
                        _entityLabels[targetId] = t.Object;
                    }
                }
            }

            _relatedList.ItemsSource = related.DistinctBy(r => r.Id).ToList();
        }
        catch (Exception ex)
        {
            _detailText.Text = $"Error loading entity: {ex.Message}";
        }
    }

    private void UpdateBreadcrumbs()
    {
        if (_breadcrumbs.Count == 0)
        {
            _breadcrumbList.ItemsSource = null;
            return;
        }

        _breadcrumbList.ItemsSource = _breadcrumbs
            .Select(id => _entityLabels.TryGetValue(id, out var label) ? label : id)
            .ToList();
    }

    private void ShowStats()
    {
        var kg = TryGetKnowledgeGraph();
        if (kg == null)
        {
            _detailText.Text = "Knowledge Graph is not available.";
            return;
        }

        try
        {
            var stats = kg.GetStats();
            var sb = new StringBuilder();
            sb.AppendLine("Knowledge Graph Statistics");
            sb.AppendLine("===========================");
            foreach (var (k, v) in stats)
            {
                if (v is Dictionary<string, int> dict)
                {
                    sb.AppendLine($"{k}:");
                    foreach (var (rk, rv) in dict)
                        sb.AppendLine($"  {rk}: {rv}");
                }
                else
                {
                    sb.AppendLine($"{k}: {v}");
                }
            }

            var centrality = kg.Centrality();
            if (centrality.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Top 10 by Centrality:");
                foreach (var (nodeId, inDeg, outDeg) in centrality.Take(10))
                    sb.AppendLine($"  {nodeId}: in={inDeg} out={outDeg}");
            }

            var orphans = kg.DetectOrphans();
            if (orphans.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"Orphaned Nodes: {orphans.Count}");
            }

            var contradictions = kg.DetectContradictions();
            if (contradictions.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"Contradictions: {contradictions.Count}");
            }

            _detailText.Text = sb.ToString();
        }
        catch (Exception ex)
        {
            _detailText.Text = $"Error: {ex.Message}";
        }
    }

    private void ExportDotGraph()
    {
        var kg = TryGetKnowledgeGraph();
        if (kg == null) return;

        try
        {
            var dot = kg.ExportDot();
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "knowledge_graph.dot");
            System.IO.File.WriteAllText(path, dot);
            _detailText.Text += $"\n\nDOT exported to: {path}";
        }
        catch (Exception ex)
        {
            _detailText.Text = $"Export error: {ex.Message}";
        }
    }

    private static LTAI.Knowledge.Core.KnowledgeGraph? TryGetKnowledgeGraph()
    {
        try
        {
            return ServiceLocator.Get<LTAI.Knowledge.Core.KnowledgeGraph>();
        }
        catch { return null; }
    }
}

public sealed class SearchResultItem
{
    public LTAI.Knowledge.Core.Models.Entity Entity { get; init; } = null!;
    public string Display { get; init; } = "";
    public override string ToString() => Display;
}

public sealed class RelatedEntry
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public string Relation { get; init; } = "";
    public override string ToString() => $"[{Relation}] {Label}";
}

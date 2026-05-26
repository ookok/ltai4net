using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using LTAI.Agent.Skills;
using LTAI.Models;

namespace LTAI.Desktop;

public sealed class SkillEvolutionView : UserControl
{
    private readonly LTAIService _svc;
    private readonly TreeView _skillTree;
    private readonly StackPanel _detailPanel;
    private readonly TextBlock _detailTitle;
    private readonly StackPanel _detailContent;
    private readonly TextBlock _summaryBar;
    private readonly DispatcherTimer _timer;
    private SkillRegistry? _registry;

    public SkillEvolutionView(LTAIService svc)
    {
        _svc = svc;
        Background = LtaiTheme.Sbb(LtaiTheme.Bg);

        var root = new DockPanel { Margin = new(16) };

        var header = new TextBlock
        {
            Text = "Skill Evolution",
            FontSize = 20,
            FontWeight = FontWeight.Bold,
            Foreground = LtaiTheme.Sbb(LtaiTheme.AccentSystem)
        };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var sep = new Border { Height = 1, Background = LtaiTheme.Sbb(LtaiTheme.Border), Margin = new(0, 8) };
        DockPanel.SetDock(sep, Dock.Top);
        root.Children.Add(sep);

        var mainGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("2*,*"),
            RowDefinitions = new RowDefinitions("*,Auto")
        };

        _skillTree = new TreeView
        {
            Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
            BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border),
            BorderThickness = new(1),
            Margin = new(0, 0, 8, 0)
        };
        _skillTree.SelectionChanged += (_, _) =>
        {
            if (_skillTree.SelectedItem is TreeViewItem tvi && tvi.Tag is Skill skill)
                ShowSkillDetail(skill);
        };
        Grid.SetColumn(_skillTree, 0);
        Grid.SetRow(_skillTree, 0);
        mainGrid.Children.Add(_skillTree);

        _detailPanel = new StackPanel { Spacing = 8 };
        var detailBorder = new Border
        {
            Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
            BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border),
            BorderThickness = new(1),
            CornerRadius = new(6),
            Padding = new(10),
            Child = _detailPanel
        };

        _detailTitle = new TextBlock
        {
            Text = "Skill Detail",
            FontSize = 15,
            FontWeight = FontWeight.Bold,
            Foreground = LtaiTheme.Sbb(LtaiTheme.AccentInfo)
        };
        _detailPanel.Children.Add(_detailTitle);

        _detailPanel.Children.Add(new Border { Height = 1, Background = LtaiTheme.Sbb(LtaiTheme.Border) });

        _detailContent = new StackPanel { Spacing = 4 };
        _detailPanel.Children.Add(new ScrollViewer { Content = _detailContent });

        Grid.SetColumn(detailBorder, 1);
        Grid.SetRow(detailBorder, 0);
        mainGrid.Children.Add(detailBorder);

        _summaryBar = new TextBlock
        {
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
            FontFamily = new("Consolas"),
            FontSize = 12,
            Margin = new(0, 8, 0, 0)
        };
        Grid.SetColumn(_summaryBar, 0);
        Grid.SetRow(_summaryBar, 1);
        Grid.SetColumnSpan(_summaryBar, 2);
        mainGrid.Children.Add(_summaryBar);

        root.Children.Add(mainGrid);

        Content = new ScrollViewer { Content = root };

        _timer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) => Refresh());
        _timer.Start();
        DetachedFromVisualTree += (_, _) => _timer.Stop();

        try { _registry = ServiceLocator.Get<SkillRegistry>(); } catch { }

        Refresh();
    }

    private void Refresh()
    {
        _skillTree.Items.Clear();

        if (_registry == null)
        {
            try { _registry = ServiceLocator.Get<SkillRegistry>(); } catch { }
        }

        if (_registry == null)
        {
            _summaryBar.Text = "Skill Registry: offline";
            _detailContent.Children.Clear();
            _detailContent.Children.Add(new TextBlock
            {
                Text = "No skill registry available",
                Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
                FontSize = 12
            });
            return;
        }

        var allSkills = _registry.All.Values.ToList();
        var stats = _registry.GetStats();
        var total = stats.GetValueOrDefault("total_skills", 0) is int t ? t : allSkills.Count;
        var active = stats.GetValueOrDefault("active", 0) is int a ? a : allSkills.Count(s => s.IsActive);
        var reliable = stats.GetValueOrDefault("reliable", 0) is int r ? r : allSkills.Count(s => s.IsReliable);

        _summaryBar.Text = $"Total: {total}  |  Active: {active}  |  Reliable: {reliable}";

        var layers = new[] { SkillLayer.L0, SkillLayer.L1, SkillLayer.L2, SkillLayer.L3, SkillLayer.L4 };
        var layerNames = new[] { "L0 Atomic", "L1 Task", "L2 Workflow", "L3 Domain", "L4 Meta" };
        var rootItems = new List<TreeViewItem>();

        for (int i = 0; i < layers.Length; i++)
        {
            var layerSkills = _registry.GetByLayer(layers[i]);
            if (layerSkills.Count == 0)
            {
                rootItems.Add(new TreeViewItem
                {
                    Header = new TextBlock
                    {
                        Text = $"{layerNames[i]} (0)",
                        Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
                        FontSize = 12,
                        FontStyle = FontStyle.Italic
                    }
                });
                continue;
            }

            var layerNode = new TreeViewItem
            {
                Header = new TextBlock
                {
                    Text = $"{layerNames[i]} ({layerSkills.Count})",
                    Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
                    FontSize = 13,
                    FontWeight = FontWeight.Bold
                },
                IsExpanded = i == 0
            };

            foreach (var skill in layerSkills.OrderByDescending(s => s.Evolution.SuccessRate))
            {
                var rate = (int)(skill.Evolution.SuccessRate * 100);
                var barColor = rate >= 70 ? LtaiTheme.AccentSystem :
                               rate >= 40 ? LtaiTheme.AccentWarning : LtaiTheme.AccentDanger;

                var itemPanel = new DockPanel { Margin = new(0, 1) };
                var nameBlock = new TextBlock
                {
                    Text = skill.Name,
                    Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
                    FontSize = 12,
                    Width = 140
                };
                var bar = new Border
                {
                    Width = Math.Max(4, rate),
                    Height = 8,
                    Background = LtaiTheme.Sbb(barColor),
                    CornerRadius = new(3),
                    Margin = new(6, 0, 4, 0)
                };
                var rateBlock = new TextBlock
                {
                    Text = $"{rate}%",
                    Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
                    FontSize = 10,
                    FontFamily = new("Consolas")
                };

                DockPanel.SetDock(nameBlock, Dock.Left);
                DockPanel.SetDock(rateBlock, Dock.Right);
                DockPanel.SetDock(bar, Dock.Right);
                itemPanel.Children.Add(nameBlock);
                itemPanel.Children.Add(rateBlock);
                itemPanel.Children.Add(bar);

                layerNode.Items.Add(new TreeViewItem
                {
                    Header = itemPanel,
                    Tag = skill
                });
            }

            rootItems.Add(layerNode);
        }

        _skillTree.Items.Clear();
        foreach (var item in rootItems)
            _skillTree.Items.Add(item);
    }

    private void ShowSkillDetail(Skill skill)
    {
        _detailContent.Children.Clear();

        void AddRow(string label, string value, Color? accent = null)
        {
            var row = new StackPanel { Margin = new(0, 2) };
            row.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = LtaiTheme.Sbb(accent ?? LtaiTheme.TextDim),
                FontSize = 11,
                FontWeight = FontWeight.Bold
            });
            row.Children.Add(new TextBlock
            {
                Text = value ?? "—",
                Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            });
            _detailContent.Children.Add(row);
        }

        _detailTitle.Text = skill.Name;

        AddRow("Domain", skill.Domain);
        AddRow("Layer", skill.Layer.ToString());
        AddRow("Version", skill.Version);
        AddRow("Intent", skill.Intent);
        AddRow("Confidence", $"{skill.Confidence:F2}");
        AddRow("Success Rate", $"{skill.Evolution.SuccessRate * 100:F0}% ({skill.Evolution.SuccessCount}/{skill.Evolution.TotalUses})");

        if (skill.Triggers.Count > 0)
            AddRow("Triggers", string.Join(", ", skill.Triggers.Select(t => t.Pattern)), LtaiTheme.AccentDNA);

        if (skill.Requires.Count > 0)
            AddRow("Requires", string.Join(", ", skill.Requires), LtaiTheme.AccentWarning);

        if (skill.Steps.Count > 0)
        {
            var stepsText = string.Join("\n", skill.Steps.Select(s => $"  {s.Index}. {s.Action}"));
            AddRow("Steps", stepsText, LtaiTheme.AccentSystem);
        }

        if (skill.VersionHistory.Count > 0)
        {
            var histText = string.Join("\n", skill.VersionHistory.Select(v =>
                $"  v{v.Version} @ {v.SavedAt:yyyy-MM-dd HH:mm} — {v.Reason}"));
            AddRow("Version History", histText, LtaiTheme.AccentInfo);
        }

        if (skill.Tags.Count > 0)
            AddRow("Tags", string.Join(", ", skill.Tags));
    }
}

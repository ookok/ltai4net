using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using LTAI.DNA;

namespace LTAI.Desktop;

public sealed class DnaEvolutionView : UserControl
{
    private readonly LTAIService _svc;
    private readonly Grid _statusGrid;
    private readonly TreeView _rulesTree;
    private readonly ListBox _mutationList;
    private readonly DispatcherTimer _timer;

    public DnaEvolutionView(LTAIService svc)
    {
        _svc = svc;
        Background = LtaiTheme.Sbb(LtaiTheme.Bg);

        var root = new DockPanel { Margin = new(16) };

        var header = new TextBlock
        {
            Text = "DNA Evolution",
            FontSize = 20,
            FontWeight = FontWeight.Bold,
            Foreground = LtaiTheme.Sbb(LtaiTheme.AccentDNA)
        };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var sep = new Border { Height = 1, Background = LtaiTheme.Sbb(LtaiTheme.Border), Margin = new(0, 8) };
        DockPanel.SetDock(sep, Dock.Top);
        root.Children.Add(sep);

        var mainGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,120")
        };

        _statusGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            RowDefinitions = new RowDefinitions("Auto"),
            Margin = new(0, 0, 0, 8)
        };
        var statusBorder = new Border
        {
            Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
            BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border),
            BorderThickness = new(1),
            CornerRadius = new(6),
            Padding = new(10),
            Child = _statusGrid
        };
        Grid.SetRow(statusBorder, 0);
        mainGrid.Children.Add(statusBorder);

        _rulesTree = new TreeView
        {
            Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
            BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border),
            BorderThickness = new(1),
            Margin = new(0, 0, 0, 4)
        };
        Grid.SetRow(_rulesTree, 1);
        mainGrid.Children.Add(_rulesTree);

        var mutationHeader = new TextBlock
        {
            Text = "Recent Mutations",
            FontSize = 13,
            FontWeight = FontWeight.Bold,
            Foreground = LtaiTheme.Sbb(LtaiTheme.AccentWarning),
            Margin = new(0, 4, 0, 2)
        };
        _mutationList = new ListBox
        {
            Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
            BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border),
            BorderThickness = new(1),
            FontSize = 12
        };
        var mutationPanel = new StackPanel { Margin = new(0, 4, 0, 0) };
        mutationPanel.Children.Add(mutationHeader);
        mutationPanel.Children.Add(_mutationList);
        Grid.SetRow(mutationPanel, 2);
        mainGrid.Children.Add(mutationPanel);

        root.Children.Add(mainGrid);

        Content = new ScrollViewer { Content = root };

        _timer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => Refresh());
        _timer.Start();
        DetachedFromVisualTree += (_, _) => _timer.Stop();
        Refresh();
    }

    private void Refresh()
    {
        var dna = _svc.DNA;
        if (dna == null)
        {
            _statusGrid.Children.Clear();
            _statusGrid.RowDefinitions = new RowDefinitions("Auto");
            _statusGrid.Children.Add(new TextBlock
            {
                Text = "DNA: Offline",
                Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
                FontSize = 13
            });
            _rulesTree.Items.Clear();
            _mutationList.ItemsSource = null;
            return;
        }

        var status = dna.GetStatus();
        var props = typeof(DNAStatus).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        _statusGrid.Children.Clear();
        _statusGrid.RowDefinitions = new RowDefinitions(string.Join(",", Enumerable.Repeat("Auto", props.Length)));

        int rowIdx = 0;
        foreach (var prop in props)
        {
            var val = prop.GetValue(status);
            var displayVal = val switch
            {
                (double prec, double recall, double calib) => $"prec={prec:F2} recall={recall:F2} calib={calib:F2}",
                _ => val?.ToString() ?? "—"
            };

            var keyBlock = new TextBlock
            {
                Text = prop.Name + ":",
                Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
                FontSize = 12,
                FontFamily = new("Consolas"),
                Width = 220
            };
            Grid.SetColumn(keyBlock, 0);
            Grid.SetRow(keyBlock, rowIdx);

            var valBlock = new TextBlock
            {
                Text = displayVal,
                Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
                FontSize = 12,
                FontFamily = new("Consolas")
            };
            Grid.SetColumn(valBlock, 1);
            Grid.SetRow(valBlock, rowIdx);

            _statusGrid.Children.Add(keyBlock);
            _statusGrid.Children.Add(valBlock);
            rowIdx++;
        }

        var rules = dna.SelfEvo.Rules;
        var rootNode = new TreeViewItem
        {
            Header = new TextBlock
            {
                Text = $"Safety Rules ({rules.Count})",
                Foreground = LtaiTheme.Sbb(LtaiTheme.AccentDanger),
                FontSize = 13,
                FontWeight = FontWeight.Bold
            },
            IsExpanded = true
        };

        foreach (var (name, rule) in rules)
        {
            var strengthPct = (int)(rule.Strength * 100);
            var barColor = rule.Strength > 0.7 ? LtaiTheme.AccentSystem :
                           rule.Strength > 0.4 ? LtaiTheme.AccentWarning : LtaiTheme.AccentDanger;
            var barWidth = Math.Max(4, (int)(rule.Strength * 100));

            var itemPanel = new DockPanel { Margin = new(0, 2) };
            var nameBlock = new TextBlock
            {
                Text = name,
                Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
                FontSize = 12,
                Width = 120
            };
            var bar = new Border
            {
                Width = barWidth,
                Height = 10,
                Background = LtaiTheme.Sbb(barColor),
                CornerRadius = new(3),
                Margin = new(8, 0, 4, 0)
            };
            var pctBlock = new TextBlock
            {
                Text = $"{strengthPct}%",
                Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
                FontSize = 11,
                FontFamily = new("Consolas")
            };

            DockPanel.SetDock(nameBlock, Dock.Left);
            DockPanel.SetDock(bar, Dock.Right);
            DockPanel.SetDock(pctBlock, Dock.Right);
            itemPanel.Children.Add(nameBlock);
            itemPanel.Children.Add(pctBlock);
            itemPanel.Children.Add(bar);

            rootNode.Items.Add(new TreeViewItem
            {
                Header = itemPanel
            });
        }

        _rulesTree.Items.Clear();
        _rulesTree.Items.Add(rootNode);

        var safety = dna.Safety;
        var safetyStatus = safety.GetStatus();
        var events = new[]
        {
            $"Safety: {safetyStatus.Posture}",
            $"Threats Known: {safetyStatus.KnownThreats}",
            $"Alignment: {safetyStatus.AlignmentScore:F2}",
            $"Mutation Rate: {dna.SelfEvo.MutationRate:F3}",
            $"Generation: {status.Generation}",
            $"Fitness: {status.FitnessScore:F2}",
            $"Awareness: {status.AwarenessScore:F2}",
            $"Biorhythm: {status.BiorhythmPhase}",
            $"Energy: {status.EnergyLevel:F2}",
            $"Active Thoughts: {status.ActiveThoughts}",
            $"Habits: {status.HabitCount}",
            $"Emergence: {status.EmergencePhase} ({status.EmergenceReadiness:F2})",
            $"Shesha Heads: {status.SheshaHeadCount}",
            $"Compiled Paths: {status.CompiledPathCount}",
            $"Surprise Bypass: {status.SurpriseGateBypassRatio:F3}"
        };

        _mutationList.ItemsSource = events.Select(e => new TextBlock
        {
            Text = e,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
            FontSize = 12,
            FontFamily = new("Consolas"),
            Margin = new(4, 1)
        } as Control).ToList();
    }
}

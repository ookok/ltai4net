using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using LTAI.Core.Interfaces;

namespace LTAI.Desktop;

public sealed class ParliamentView : UserControl
{
    private readonly LTAIService _svc;
    private readonly StackPanel _headerPanel;
    private readonly Ellipse _statusDot;
    private readonly TextBlock _statusLabel;
    private readonly ListBox _sessionList;
    private readonly TextBlock _statsText;
    private readonly DispatcherTimer _timer;
    private IParliamentBridge? _bridge;

    public ParliamentView(LTAIService svc)
    {
        _svc = svc;
        Background = LtaiTheme.Sbb(LtaiTheme.Bg);

        var root = new DockPanel { Margin = new(16) };

        var header = new TextBlock
        {
            Text = "Parliament",
            FontSize = 20,
            FontWeight = FontWeight.Bold,
            Foreground = LtaiTheme.Sbb(LtaiTheme.AccentWarning)
        };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var sep = new Border { Height = 1, Background = LtaiTheme.Sbb(LtaiTheme.Border), Margin = new(0, 8) };
        DockPanel.SetDock(sep, Dock.Top);
        root.Children.Add(sep);

        _headerPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new(0, 0, 0, 8) };
        _statusDot = new Ellipse { Width = 12, Height = 12 };
        _statusLabel = new TextBlock
        {
            FontSize = 14,
            FontWeight = FontWeight.Bold,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary)
        };
        _headerPanel.Children.Add(_statusDot);
        _headerPanel.Children.Add(_statusLabel);
        DockPanel.SetDock(_headerPanel, Dock.Top);
        root.Children.Add(_headerPanel);

        var sessionHeader = new TextBlock
        {
            Text = "Debate Sessions",
            FontSize = 13,
            FontWeight = FontWeight.Bold,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
            Margin = new(0, 0, 0, 4)
        };
        DockPanel.SetDock(sessionHeader, Dock.Top);
        root.Children.Add(sessionHeader);

        _sessionList = new ListBox
        {
            Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
            BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border),
            BorderThickness = new(1),
            MinHeight = 300
        };

        var sessionBorder = new Border
        {
            Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
            BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border),
            BorderThickness = new(1),
            Child = _sessionList
        };
        root.Children.Add(sessionBorder);

        _statsText = new TextBlock
        {
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
            FontFamily = new("Consolas"),
            FontSize = 12,
            Margin = new(0, 8, 0, 0)
        };
        DockPanel.SetDock(_statsText, Dock.Bottom);
        root.Children.Add(_statsText);

        Content = root;

        InitBridge();

        _timer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) => Refresh());
        _timer.Start();
        DetachedFromVisualTree += (_, _) => _timer.Stop();
        Refresh();
    }

    private void InitBridge()
    {
        try
        {
            var ltsType = _svc.LTS.GetType();
            var field = ltsType.GetField("_parliament",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _bridge = field?.GetValue(_svc.LTS) as IParliamentBridge;
        }
        catch { }
    }

    private void Refresh()
    {
        if (_bridge == null)
        {
            InitBridge();
        }

        if (_bridge == null)
        {
            _statusDot.Fill = LtaiTheme.Sbb(LtaiTheme.TextDim);
            _statusLabel.Text = "Parliament: Unavailable";
            _sessionList.ItemsSource = null;
            _statsText.Text = "No parliament bridge found";
            return;
        }

        var available = _bridge.IsAvailable;

        _statusDot.Fill = LtaiTheme.Sbb(available ? LtaiTheme.AccentSystem : LtaiTheme.AccentDanger);
        _statusLabel.Text = available ? "Parliament: Active" : "Parliament: Inactive";
        _statusLabel.Foreground = LtaiTheme.Sbb(available ? LtaiTheme.AccentSystem : LtaiTheme.AccentDanger);

        _sessionList.ItemsSource = new List<Control>
        {
            new Border
            {
                Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
                Padding = new(8, 16),
                Child = new TextBlock
                {
                    Text = "Parliament is ready for multi-agent deliberation.\n\nUse the Chat view to trigger debates via high-stakes verification.",
                    Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap
                }
            }
        };

        _statsText.Text = $"Status: {(available ? "Active" : "Inactive")}  |  Consensus threshold: 0.9  |  Max revision rounds: 2";
    }
}

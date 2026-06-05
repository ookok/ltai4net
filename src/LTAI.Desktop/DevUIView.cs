using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using LTAI.Agent.DevUI;
using LTAI.Desktop.ViewModels;

namespace LTAI.Desktop;

public sealed class DevUIView : UserControl
{
    private readonly DevUIViewModel _vm;
    private readonly TextBlock _statusLine;
    private readonly ListBox _cardList;
    private readonly StackPanel _detailPanel;
    private readonly TextBlock _detailName;
    private readonly TextBlock _detailMeta;
    private readonly WrapPanel _permPillsPanel;
    private readonly WrapPanel _toolPillsPanel;
    private readonly StackPanel _chatLog;
    private readonly ScrollViewer _chatScroll;
    private readonly TextBox _chatInput;
    private readonly Button _sendBtn;
    private readonly StackPanel _spansHeader;
    private readonly StackPanel _spansBody;
    private readonly ScrollViewer _spansScroll;
    private readonly DispatcherTimer _refreshTimer;

    public DevUIView()
    {
        _vm = new DevUIViewModel();
        DataContext = _vm;
        Background = LtaiTheme.Sbb(LtaiTheme.Bg);

        var root = new DockPanel();

        // Left panel: agent cards
        var leftPanel = new StackPanel { MinWidth = 250, MaxWidth = 350 };

        _statusLine = new TextBlock { FontSize = 11, Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim), Margin = new(4) };
        _statusLine.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(_vm.StatusLine)));
        leftPanel.Children.Add(_statusLine);

        _cardList = new ListBox
        {
            Background = LtaiTheme.Sbb(LtaiTheme.Bg),
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
            MinHeight = 200,
        };
        _cardList.SelectionChanged += (_, _) =>
        {
            if (_cardList.SelectedItem is LTAIAgentCard card)
            {
                _vm.SelectCard(card);
                ShowDetail(card);
            }
        };

        // Build card list from VM
        foreach (var card in _vm.Cards)
        {
            var text = $"{card.Name}";
            if (!string.IsNullOrEmpty(card.ModelId)) text += $" ({card.ModelId})";
            var item = new ListBoxItem { Content = text, Tag = card };
            _cardList.Items.Add(item);
        }
        leftPanel.Children.Add(_cardList);

        // Spans panel
        _spansHeader = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new(4) };
        _spansHeader.Children.Add(new TextBlock { Text = "🔍 Spans", FontSize = 10, Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim) });
        leftPanel.Children.Add(_spansHeader);

        _spansScroll = new ScrollViewer { MaxHeight = 200 };
        _spansBody = new StackPanel { Spacing = 1 };
        _spansScroll.Content = _spansBody;
        leftPanel.Children.Add(_spansScroll);

        // Right panel: detail + chat
        var rightPanel = new DockPanel { Margin = new(8, 0, 0, 0) };

        _detailPanel = new StackPanel { Spacing = 4, Margin = new(0, 0, 0, 8) };
        _detailName = new TextBlock { FontSize = 16, FontWeight = FontWeight.Bold, Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary) };
        _detailPanel.Children.Add(_detailName);
        _detailMeta = new TextBlock { FontSize = 11, Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim), TextWrapping = TextWrapping.Wrap };
        _detailPanel.Children.Add(_detailMeta);
        _permPillsPanel = new WrapPanel { };
        _detailPanel.Children.Add(_permPillsPanel);
        _toolPillsPanel = new WrapPanel { };
        _detailPanel.Children.Add(_toolPillsPanel);

        DockPanel.SetDock(_detailPanel, Dock.Top);
        rightPanel.Children.Add(_detailPanel);

        // Chat area
        var chatArea = new DockPanel();
        _chatScroll = new ScrollViewer();
        _chatLog = new StackPanel { Spacing = 4 };
        _chatScroll.Content = _chatLog;

        var chatBottom = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new(0, 4, 0, 0) };
        _chatInput = new TextBox
        {
            PlaceholderText = "输入消息...",
            FontSize = 12,
            Height = 24,
            Background = LtaiTheme.Sbb(LtaiTheme.Bg),
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
        };
        _chatInput.Bind(TextBox.TextProperty, new Avalonia.Data.Binding(nameof(_vm.ChatInput)));
        chatBottom.Children.Add(_chatInput);

        _sendBtn = new Button { Content = "发送", FontSize = 11, Height = 24, Width = 50,
            Background = LtaiTheme.Sbb(LtaiTheme.AccentDNA), Foreground = LtaiTheme.Sbb(LtaiTheme.TextOnAccent) };
        _sendBtn.Click += async (_, _) => await _vm.SendChatCommand.ExecuteAsync(null);
        _sendBtn.Bind(Button.IsVisibleProperty, new Avalonia.Data.Binding(nameof(_vm.SelectedAgentName)) { Converter = new StringNotEmptyConverter() });
        chatBottom.Children.Add(_sendBtn);

        DockPanel.SetDock(chatBottom, Dock.Bottom);
        chatArea.Children.Add(_chatScroll);
        chatArea.Children.Add(chatBottom);

        rightPanel.Children.Add(chatArea);

        // Split left/right
        var splitGrid = new Grid();
        splitGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(300)));
        splitGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        Grid.SetColumn(leftPanel, 0);
        Grid.SetColumn(rightPanel, 1);
        splitGrid.Children.Add(leftPanel);
        splitGrid.Children.Add(rightPanel);

        Content = splitGrid;

        _refreshTimer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background,
            (_, _) => RefreshSpans());
        _refreshTimer.Start();
    }

    private void ShowDetail(LTAIAgentCard card)
    {
        _detailName.Text = card.Name;
        var meta = "";
        if (!string.IsNullOrEmpty(card.ModelId)) meta += $"模型: {card.ModelId}  ";
        meta += $"温度: {card.Temperature}  ";
        meta += $"工具: {card.Tools?.Count ?? 0}";
        _detailMeta.Text = meta;

        _permPillsPanel.Children.Clear();
        if (card.Permissions != null)
            foreach (var p in card.Permissions)
            {
                var color = p switch
                {
                    "R" => new Color(255, 86, 211, 100),
                    "W" => new Color(255, 240, 136, 62),
                    "L" => new Color(255, 88, 166, 255),
                    "X" => new Color(255, 255, 123, 114),
                    _ => new Color(255, 100, 100, 100),
                };
                _permPillsPanel.Children.Add(new Border
                {
                    Background = LtaiTheme.Sbb(color),
                    CornerRadius = LtaiTheme.Radius.Sm,
                    Padding = new(4, 1),
                    Child = new TextBlock { Text = p, FontSize = 10, Foreground = LtaiTheme.Sbb(Colors.White) }
                });
            }

        _toolPillsPanel.Children.Clear();
        if (card.Tools != null)
            foreach (var t in card.Tools.Take(15))
                _toolPillsPanel.Children.Add(new Border
                {
                    Background = LtaiTheme.Sbb(new Color(30, 88, 166, 255)),
                    CornerRadius = LtaiTheme.Radius.Sm,
                    Padding = new(4, 1),
                    Child = new TextBlock { Text = t, FontSize = 10, Foreground = LtaiTheme.Sbb(Colors.White) }
                });
    }

    private void RefreshSpans()
    {
        _vm.RefreshSpans();
        _spansBody.Children.Clear();
        foreach (var span in _vm.Spans)
        {
            var text = $"{span.Status} {span.Name} [{span.Source}] {span.Kind} {span.Duration}";
            _spansBody.Children.Add(new TextBlock
            {
                Text = text, FontSize = 10, FontFamily = LtaiTheme.CodeFont,
                Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim)
            });
        }
    }
}

internal sealed class StringNotEmptyConverter : Avalonia.Data.Converters.IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is string s && !string.IsNullOrEmpty(s);
    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

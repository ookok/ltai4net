using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using LTAI.Agent.DevUI;
using LTAI.AI;
using LTAI.Core.Configuration;
using LTAI.Desktop.DevUI;
using Microsoft.Extensions.DependencyInjection;

namespace LTAI.Desktop;

public sealed class DevUIView : UserControl
{
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

    private LTAIDevUIService? _devUi;
    private DevUISpanCollectorDesktop? _collector;
    private EmbeddingClient? _embedder;
    private ToolEmbeddingCache? _embedCache;
    private IReadOnlyList<LTAIAgentCard> _cards = [];
    private LTAIAgentCard? _selectedCard;
    private string _selectedAgentName = "";
    private CancellationTokenSource? _chatCts;

    private static readonly Color PermRead   = Color.Parse("#56d364");
    private static readonly Color PermWrite  = Color.Parse("#f0883e");
    private static readonly Color PermList   = Color.Parse("#58a6ff");
    private static readonly Color PermExec   = Color.Parse("#ff7b72");
    private static readonly Color SpanOk     = Color.Parse("#3fb950");
    private static readonly Color SpanErr    = Color.Parse("#f85149");
    private static readonly Color SpanLive   = Color.Parse("#58a6ff");

    public DevUIView()
    {
        Background = LtaiTheme.Sbb(LtaiTheme.Bg);

        _statusLine = new TextBlock
        {
            FontSize = 11,
            FontFamily = LtaiTheme.CodeFont,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
            TextWrapping = TextWrapping.Wrap,
            Margin = new(0, 0, 0, 4),
        };

        _cardList = new ListBox
        {
            Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
            BorderThickness = new(1),
            BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border),
            MinWidth = 260,
        };

        _detailName = new TextBlock
        {
            FontSize = 16,
            FontWeight = FontWeight.Bold,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
        };
        _detailMeta = new TextBlock
        {
            FontSize = 11,
            FontFamily = LtaiTheme.CodeFont,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
            TextWrapping = TextWrapping.Wrap,
        };
        _permPillsPanel = new WrapPanel { Margin = new(0, 4) };
        _toolPillsPanel = new WrapPanel { Margin = new(0, 4) };

        _chatLog = new StackPanel { Spacing = 4 };
        _chatScroll = new ScrollViewer
        {
            Content = _chatLog,
            Height = 200,
        };

        _chatInput = new TextBox
        {
            PlaceholderText = "发消息给此 agent...",
            FontSize = 12,
            Height = 28,
            Background = LtaiTheme.Sbb(LtaiTheme.BgInput),
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
            BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border),
            BorderThickness = new(1),
        };
        _sendBtn = new Button
        {
            Content = "Send",
            Width = 70,
            Height = 28,
            Margin = new(4, 0, 0, 0),
        };

        _detailPanel = new StackPanel
        {
            Spacing = 4,
            Margin = new(4, 0, 0, 0),
            Children =
            {
                _detailName,
                _detailMeta,
                _permPillsPanel,
                _toolPillsPanel,
                new TextBlock
                {
                    Text = "── Inline Chat ──",
                    FontSize = 11,
                    Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new(0, 8, 0, 2),
                },
                _chatScroll,
                new DockPanel
                {
                    Children =
                    {
                        _sendBtn.Apply(d => DockPanel.SetDock(d, Dock.Right)),
                        _chatInput,
                    }
                },
            }
        };

        _spansHeader = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new(0, 0, 0, 2),
        };
        _spansBody = new StackPanel { Spacing = 1 };
        _spansScroll = new ScrollViewer
        {
            Content = _spansBody,
            Height = 140,
        };

        var spansSection = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock
                {
                    Text = "Spans / Trace",
                    FontWeight = FontWeight.Bold,
                    FontSize = 12,
                    Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
                },
                _spansHeader,
                _spansScroll,
            }
        };

        _cardList.SelectionChanged += (_, _) =>
        {
            if (_cardList.SelectedItem is LTAIAgentCard card)
                ShowDetail(card);
        };

        _sendBtn.Click += async (_, _) => await SendChatAsync();
        _chatInput.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                e.Handled = true;
                _ = SendChatAsync();
            }
        };

        var splitter = new GridSplitter
        {
            Width = 3,
            Background = LtaiTheme.Sbb(LtaiTheme.Border),
        };

        var mainGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,3,*"),
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
        };
        Grid.SetRow(_statusLine, 0);
        Grid.SetColumnSpan(_statusLine, 3);
        Grid.SetRow(_cardList, 1);
        Grid.SetRow(splitter, 1);
        Grid.SetColumn(splitter, 1);
        Grid.SetRow(_detailPanel, 1);
        Grid.SetColumn(_detailPanel, 2);
        var spansBorder = new Border
        {
            BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border),
            BorderThickness = new(0, 1, 0, 0),
            Padding = new(4),
            Child = spansSection,
        };
        Grid.SetRow(spansBorder, 2);
        Grid.SetColumnSpan(spansBorder, 3);
        mainGrid.Children.AddRange([_statusLine, _cardList, splitter, _detailPanel, spansBorder]);

        Content = mainGrid;

        _refreshTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => Refresh());
        _refreshTimer.Start();
        AttachedToVisualTree += async (_, _) =>
        {
            await ResolveAsync();
            Refresh();
            if (!_refreshTimer.IsEnabled) _refreshTimer.Start();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _refreshTimer.Stop();
            _chatCts?.Cancel();
            _collector?.Dispose();
            _collector = null;
        };
    }

    private async Task ResolveAsync()
    {
        var sp = App.Services;
        if (sp is null) return;
        _devUi = sp.GetService<LTAIDevUIService>();
        _embedder = sp.GetService<EmbeddingClient>();
        _embedCache = sp.GetService<ToolEmbeddingCache>();
        if (_collector is null)
        {
            _collector = new DevUISpanCollectorDesktop();
            _collectorTask = Task.Run(async () =>
            {
                while (_collector is not null)
                {
                    await Task.Delay(1000);
                    Dispatcher.UIThread.Post(RefreshSpans);
                }
            });
        }
        var agentDefs = Agent.AgentRegistry.LoadAll();
        var cards = _devUi?.ListAgentCards() ?? [];
        _cards = cards;
        _cardList.ItemsSource = cards;
        _cardList.DisplayMemberBinding = null;
        _cardList.ItemTemplate = new FuncDataTemplate<LTAIAgentCard>((card, _) =>
        {
            if (card is null) return null;
            var permColors = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase)
            {
                ["read"] = PermRead, ["r"] = PermRead,
                ["write"] = PermWrite, ["w"] = PermWrite,
                ["list"] = PermList, ["l"] = PermList,
                ["exec"] = PermExec, ["x"] = PermExec,
            };
            var perms = new WrapPanel();
            foreach (var p in card.Permissions)
            {
                var c = permColors.TryGetValue(p, out var col) ? col : LtaiTheme.TextDim;
                perms.Children.Add(new Border
                {
                    Margin = new(0, 0, 3, 0),
                    Background = LtaiTheme.Sbb(c, 40),
                    BorderBrush = LtaiTheme.Sbb(c),
                    BorderThickness = new(1),
                    CornerRadius = LtaiTheme.Radius.Sm,
                    Padding = new(4, 1),
                    Child = new TextBlock
                    {
                        Text = p.ToUpperInvariant(),
                        FontSize = 10,
                        FontFamily = LtaiTheme.CodeFont,
                        Foreground = LtaiTheme.Sbb(c),
                    }
                });
            }
            var toolPills = new TextBlock
            {
                Text = $"{card.ToolCount} tools",
                FontSize = 10,
                Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
                FontFamily = LtaiTheme.CodeFont,
            };
            return new Border
            {
                BorderThickness = new(1),
                BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border),
                CornerRadius = LtaiTheme.Radius.Md,
                Padding = new(8),
                Margin = new(2),
                Background = LtaiTheme.Sbb(LtaiTheme.SurfaceOverlay),
                Child = new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock { Text = card.Name, FontWeight = FontWeight.Bold, FontSize = 13, Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary) },
                        new TextBlock { Text = card.ModelId ?? "—", FontSize = 10, Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim), FontFamily = LtaiTheme.CodeFont },
                        new TextBlock { Text = card.Description, FontSize = 11, Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 240 },
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { perms, toolPills } },
                    }
                }
            };
        });
    }

    private Task? _collectorTask;

    private void Refresh()
    {
        var embedder = _embedder;
        var local = embedder?.Local;
        var ep = local?.ActiveExecutionProvider ?? "—";
        var quant = local is not null
            ? (local.UsingQuantizedModel ? "INT8" : "FP32")
            : "—";
        var disabled = LocalEmbedder.DefaultDisabled;
        var epColor = ep is "DML" or "CUDA" ? LtaiTheme.AccentSystem : LtaiTheme.TextDim;
        var quantColor = quant == "INT8" ? LtaiTheme.AccentSystem : LtaiTheme.AccentWarning;

        var modelName = local?.CurrentModelName ?? "—";
        var agentCount = _cards.Count;
        var tokens = $"{UsageTracker.TotalTokens:N0}";
        var requests = UsageTracker.Requests;
        var cost = UsageTracker.CostDisplay;

        var cacheHit = _embedCache?.CacheHits ?? 0;
        var cacheMiss = _embedCache?.CacheMisses ?? 0;
        var cacheRate = _embedCache?.HitRate ?? 0;
        var cacheLine = _embedCache is not null
            ? $"embed cache: {cacheHit}h/{cacheMiss}m ({cacheRate:P1})"
            : "embed cache: —";

        _statusLine.Text = $"🔬 LTAI DevUI  ·  {agentCount} agents  ·  {tokens} tokens  ·  {requests} req  ·  {cost}  ·  {cacheLine}";
        _statusLine.Text += $"\nmodel: {modelName}  ·  EP: {ep}  ·  quant: {quant}  ·  disabled: {disabled}";

        if (_selectedCard != null)
        {
            _detailName.Text = _selectedCard.Name;
            _detailMeta.Text = $"model: {_selectedCard.ModelId ?? "—"}  ·  T={_selectedCard.Temperature}  ·  topP={_selectedCard.TopP}  ·  v{_selectedCard.Version}";
            if (_selectedCard.Tools.Count > 0)
            {
                _detailMeta.Text += $"  ·  {_selectedCard.ToolCount} tools";
            }
        }
    }

    private void RefreshSpans()
    {
        var spans = _collector?.Snapshot() ?? [];
        _spansHeader.Children.Clear();
        _spansHeader.Children.Add(new TextBlock { Text = "Name", Width = 200, FontWeight = FontWeight.Bold, FontSize = 10, Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim) });
        _spansHeader.Children.Add(new TextBlock { Text = "Source", Width = 120, FontWeight = FontWeight.Bold, FontSize = 10, Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim) });
        _spansHeader.Children.Add(new TextBlock { Text = "Duration", Width = 60, FontWeight = FontWeight.Bold, FontSize = 10, Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim) });
        _spansHeader.Children.Add(new TextBlock { Text = "Status", Width = 50, FontWeight = FontWeight.Bold, FontSize = 10, Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim) });
        _spansBody.Children.Clear();
        if (spans.Count == 0)
        {
            _spansBody.Children.Add(new TextBlock { Text = "(no spans yet)", FontSize = 10, Foreground = LtaiTheme.Sbb(LtaiTheme.TextMuted), Margin = new(4) });
            return;
        }
        foreach (var s in spans.Take(60))
        {
            var statusColor = s.IsLive ? SpanLive : s.Status == "ERROR" ? SpanErr : SpanOk;
            var duration = s.IsLive ? "active..." : s.Duration.TotalMilliseconds >= 1000
                ? $"{s.Duration.TotalSeconds:F1}s" : $"{s.Duration.TotalMilliseconds:F0}ms";
            var durColor = s.IsLive ? LtaiTheme.TextMuted
                : s.Duration.TotalMilliseconds > 2000 ? LtaiTheme.AccentDanger
                : s.Duration.TotalMilliseconds > 500 ? LtaiTheme.AccentWarning
                : LtaiTheme.TextDim;

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = s.Name, Width = 200, FontSize = 10, Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary), FontFamily = LtaiTheme.CodeFont, TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = s.Source, Width = 120, FontSize = 10, Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim), FontFamily = LtaiTheme.CodeFont },
                    new TextBlock { Text = duration, Width = 60, FontSize = 10, Foreground = LtaiTheme.Sbb(durColor), FontFamily = LtaiTheme.CodeFont },
                    new TextBlock { Text = s.IsLive ? "●" : s.Status == "ERROR" ? "✗" : "✓", Width = 50, FontSize = 10, Foreground = LtaiTheme.Sbb(statusColor) },
                }
            };
            _spansBody.Children.Add(row);
        }
    }

    private void ShowDetail(LTAIAgentCard card)
    {
        _selectedCard = card;
        _selectedAgentName = card.Name;
        _chatLog.Children.Clear();
        _chatInput.Text = "";
        _detailName.Text = card.Name;
        _detailMeta.Text = $"model: {card.ModelId ?? "—"}  ·  T={card.Temperature}  ·  topP={card.TopP}  ·  v{card.Version}";
        if (card.Tools.Count > 0)
            _detailMeta.Text += $"  ·  {card.ToolCount} tools";

        _permPillsPanel.Children.Clear();
        var permColors = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase)
        {
            ["read"] = PermRead, ["r"] = PermRead,
            ["write"] = PermWrite, ["w"] = PermWrite,
            ["list"] = PermList, ["l"] = PermList,
            ["exec"] = PermExec, ["x"] = PermExec,
        };
        foreach (var p in card.Permissions)
        {
            var c = permColors.TryGetValue(p, out var col) ? col : LtaiTheme.TextDim;
            _permPillsPanel.Children.Add(new Border
            {
                Background = LtaiTheme.Sbb(c, 30),
                BorderBrush = LtaiTheme.Sbb(c),
                BorderThickness = new(1),
                CornerRadius = LtaiTheme.Radius.Sm,
                Padding = new(6, 2),
                Child = new TextBlock
                {
                    Text = p.ToUpperInvariant(),
                    FontSize = 10,
                    FontFamily = LtaiTheme.CodeFont,
                    Foreground = LtaiTheme.Sbb(c),
                }
            });
        }

        _toolPillsPanel.Children.Clear();
        foreach (var t in card.Tools)
        {
            _toolPillsPanel.Children.Add(new Border
            {
                Background = LtaiTheme.Sbb(LtaiTheme.SurfaceOverlay),
                BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border),
                BorderThickness = new(1),
                CornerRadius = LtaiTheme.Radius.Sm,
                Padding = new(6, 2),
                Child = new TextBlock
                {
                    Text = t,
                    FontSize = 10,
                    FontFamily = LtaiTheme.CodeFont,
                    Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
                }
            });
        }
    }

    private async Task SendChatAsync()
    {
        var text = _chatInput.Text?.Trim();
        if (string.IsNullOrEmpty(text) || _devUi is null || string.IsNullOrEmpty(_selectedAgentName))
            return;

        _chatInput.Text = "";
        _chatInput.IsEnabled = false;
        _sendBtn.IsEnabled = false;

        var userBubble = MakeBubble(text, true);
        _chatLog.Children.Add(userBubble);
        _chatScroll.ScrollToEnd();

        try
        {
            _chatCts?.Cancel();
            _chatCts = new CancellationTokenSource();
            var response = new System.Text.StringBuilder();
            var aiBubble = new StackPanel { Margin = new(0, 0, 40, 0) };
            var aiText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
                FontSize = 12,
            };
            aiBubble.Children.Add(new Border
            {
                Background = LtaiTheme.Sbb(LtaiTheme.BubbleAIBg),
                BorderBrush = LtaiTheme.Sbb(LtaiTheme.BubbleAIBorder),
                BorderThickness = new(1),
                CornerRadius = LtaiTheme.Radius.Lg,
                Padding = new(8),
                Child = aiText,
            });
            _chatLog.Children.Add(aiBubble);
            _chatScroll.ScrollToEnd();

            await foreach (var update in _devUi.RunStreamingAsync(
                _selectedAgentName, text, null, _chatCts.Token))
            {
                if (update?.ToString() is { Length: > 0 } delta)
                {
                    response.Append(delta);
                    aiText.Text = response.ToString();
                    _chatScroll.ScrollToEnd();
                }
                await Task.Delay(10);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _chatLog.Children.Add(new TextBlock
            {
                Text = $"Error: {ex.Message}",
                Foreground = LtaiTheme.Sbb(LtaiTheme.AccentDanger),
                FontSize = 11,
            });
        }
        finally
        {
            _chatInput.IsEnabled = true;
            _sendBtn.IsEnabled = true;
            _chatInput.Focus();
        }
    }

    private static Border MakeBubble(string text, bool isUser)
    {
        var bg = isUser ? LtaiTheme.BubbleUserBg : LtaiTheme.BubbleAIBg;
        var brd = isUser ? LtaiTheme.BubbleUserBorder : LtaiTheme.BubbleAIBorder;
        return new Border
        {
            Background = LtaiTheme.Sbb(bg),
            BorderBrush = LtaiTheme.Sbb(brd),
            BorderThickness = new(1),
            CornerRadius = LtaiTheme.Radius.Lg,
            Padding = new(8),
            Margin = isUser ? new(40, 0, 0, 0) : new(0, 0, 40, 0),
            Child = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
                FontSize = 12,
            },
        };
    }
}

internal static class ControlExtensions
{
    public static T Apply<T>(this T c, Action<T> act) where T : Avalonia.Controls.Control
    {
        act(c);
        return c;
    }
}

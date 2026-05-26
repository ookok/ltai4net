using System.Collections.Generic;
using System.Text;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace LTAI.Desktop;

public sealed class PromptLabView : UserControl
{
    private readonly LTAIService _svc;
    private readonly TextBox _promptA;
    private readonly TextBox _promptB;
    private readonly TextBox _testInput;
    private readonly TextBlock _responseA;
    private readonly TextBlock _responseB;
    private readonly TextBlock _diffText;
    private readonly Button _compareBtn;
    private CancellationTokenSource? _cts;

    public PromptLabView(LTAIService svc)
    {
        _svc = svc;
        Background = LtaiTheme.Sbb(LtaiTheme.Bg);

        var root = new DockPanel { Margin = new(16) };

        var header = new TextBlock
        {
            Text = "Prompt Lab",
            FontSize = 20,
            FontWeight = FontWeight.Bold,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary)
        };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var sep = new Border { Height = 1, Background = LtaiTheme.Sbb(LtaiTheme.Border), Margin = new(0, 8) };
        DockPanel.SetDock(sep, Dock.Top);
        root.Children.Add(sep);

        var promptGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            RowDefinitions = new RowDefinitions("Auto")
        };

        var panelA = new StackPanel { Spacing = 4, Margin = new(0, 0, 6, 0) };
        panelA.Children.Add(new TextBlock
        {
            Text = "Prompt A",
            Foreground = LtaiTheme.Sbb(LtaiTheme.AccentDNA),
            FontSize = 12,
            FontWeight = FontWeight.Bold
        });
        _promptA = new TextBox
        {
            Watermark = "Prompt template A...",
            Background = LtaiTheme.Sbb(LtaiTheme.BgInput),
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
            FontFamily = new("Consolas"),
            FontSize = 12,
            Height = 80,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap
        };
        panelA.Children.Add(_promptA);

        var panelB = new StackPanel { Spacing = 4, Margin = new(6, 0, 0, 0) };
        panelB.Children.Add(new TextBlock
        {
            Text = "Prompt B",
            Foreground = LtaiTheme.Sbb(LtaiTheme.AccentInfo),
            FontSize = 12,
            FontWeight = FontWeight.Bold
        });
        _promptB = new TextBox
        {
            Watermark = "Prompt template B...",
            Background = LtaiTheme.Sbb(LtaiTheme.BgInput),
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
            FontFamily = new("Consolas"),
            FontSize = 12,
            Height = 80,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap
        };
        panelB.Children.Add(_promptB);

        Grid.SetColumn(panelA, 0);
        Grid.SetColumn(panelB, 1);
        promptGrid.Children.Add(panelA);
        promptGrid.Children.Add(panelB);

        DockPanel.SetDock(promptGrid, Dock.Top);
        root.Children.Add(promptGrid);

        var testRow = new DockPanel { Margin = new(0, 8) };
        testRow.Children.Add(new TextBlock
        {
            Text = "Test Input",
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            Width = 70
        });
        _testInput = new TextBox
        {
            Watermark = "Enter test input for both prompts...",
            Background = LtaiTheme.Sbb(LtaiTheme.BgInput),
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
            FontFamily = new("Consolas"),
            FontSize = 12,
            MinHeight = 40,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap
        };
        testRow.Children.Add(_testInput);

        _compareBtn = new Button
        {
            Content = "Compare",
            Background = LtaiTheme.Sbb(LtaiTheme.AccentDNA),
            Foreground = LtaiTheme.Sbb("#ffffff"),
            FontSize = 13,
            FontWeight = FontWeight.Bold,
            Width = 90,
            Height = 36,
            Margin = new(8, 0, 0, 0)
        };
        _compareBtn.Click += (_, _) => _ = CompareAsync();
        DockPanel.SetDock(_compareBtn, Dock.Right);
        testRow.Children.Add(_compareBtn);
        DockPanel.SetDock(testRow, Dock.Top);
        root.Children.Add(testRow);

        var responseGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*")
        };

        var respPanelA = new StackPanel { Spacing = 4, Margin = new(0, 0, 6, 0) };
        respPanelA.Children.Add(new TextBlock
        {
            Text = "Response A",
            Foreground = LtaiTheme.Sbb(LtaiTheme.AccentDNA),
            FontSize = 11,
            FontWeight = FontWeight.Bold
        });
        _responseA = new TextBlock
        {
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
            FontFamily = new("Consolas"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };
        var scrollA = new ScrollViewer
        {
            Content = _responseA,
            Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
            Padding = new(8)
        };
        respPanelA.Children.Add(scrollA);

        var respPanelB = new StackPanel { Spacing = 4, Margin = new(6, 0, 0, 0) };
        respPanelB.Children.Add(new TextBlock
        {
            Text = "Response B",
            Foreground = LtaiTheme.Sbb(LtaiTheme.AccentInfo),
            FontSize = 11,
            FontWeight = FontWeight.Bold
        });
        _responseB = new TextBlock
        {
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
            FontFamily = new("Consolas"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };
        var scrollB = new ScrollViewer
        {
            Content = _responseB,
            Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
            Padding = new(8)
        };
        respPanelB.Children.Add(scrollB);

        Grid.SetColumn(respPanelA, 0);
        Grid.SetColumn(respPanelB, 1);
        responseGrid.Children.Add(respPanelA);
        responseGrid.Children.Add(respPanelB);
        root.Children.Add(responseGrid);

        var diffBorder = new Border
        {
            BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border),
            BorderThickness = new(0, 1, 0, 0),
            Margin = new(0, 8, 0, 0),
            Padding = new(0, 8, 0, 0)
        };
        _diffText = new TextBlock
        {
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
            FontFamily = new("Consolas"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        };
        var diffStack = new StackPanel { Spacing = 4 };
        diffStack.Children.Add(new TextBlock
        {
            Text = "Differences",
            Foreground = LtaiTheme.Sbb(LtaiTheme.AccentWarning),
            FontSize = 11,
            FontWeight = FontWeight.Bold
        });
        diffStack.Children.Add(_diffText);
        diffBorder.Child = diffStack;
        DockPanel.SetDock(diffBorder, Dock.Bottom);
        root.Children.Add(diffBorder);

        Content = root;

        LtaiTheme.ThemeChanged += () =>
        {
            Background = LtaiTheme.Sbb(LtaiTheme.Bg);
            _promptA.Background = LtaiTheme.Sbb(LtaiTheme.BgInput);
            _promptB.Background = LtaiTheme.Sbb(LtaiTheme.BgInput);
            _testInput.Background = LtaiTheme.Sbb(LtaiTheme.BgInput);
        };
    }

    private async Task CompareAsync()
    {
        var promptA = _promptA.Text?.Trim();
        var promptB = _promptB.Text?.Trim();
        var testInput = _testInput.Text?.Trim();

        if (string.IsNullOrWhiteSpace(promptA) || string.IsNullOrWhiteSpace(promptB))
        {
            _diffText.Text = "Both prompt templates are required.";
            return;
        }
        if (string.IsNullOrWhiteSpace(testInput))
        {
            _diffText.Text = "Test input is required.";
            return;
        }

        _compareBtn.Content = "Stop";
        _compareBtn.Background = LtaiTheme.Sbb(LtaiTheme.AccentDanger);

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        var queryA = promptA.Replace("{{input}}", testInput);
        var queryB = promptB.Replace("{{input}}", testInput);

        try
        {
            var taskA = CollectStreamAsync(queryA, token);
            var taskB = CollectStreamAsync(queryB, token);

            var results = await Task.WhenAll(taskA, taskB);

            var textA = results[0];
            var textB = results[1];

            _responseA.Text = textA;
            _responseB.Text = textB;

            _diffText.Text = GenerateDiff(textA, textB);
        }
        catch (OperationCanceledException)
        {
            _diffText.Text = "Comparison cancelled.";
        }
        catch (Exception ex)
        {
            _diffText.Text = $"Error: {ex.Message}";
        }
        finally
        {
            _compareBtn.Content = "Compare";
            _compareBtn.Background = LtaiTheme.Sbb(LtaiTheme.AccentDNA);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task<string> CollectStreamAsync(string query, CancellationToken ct)
    {
        var sb = new StringBuilder();
        try
        {
            await foreach (var token in _svc.LTS.StreamChatAsync(query).WithCancellation(ct))
            {
                sb.Append(token);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            sb.Append($"\n[Error: {ex.Message}]");
        }
        return sb.ToString();
    }

    private static string GenerateDiff(string a, string b)
    {
        if (a == b) return "No differences detected.";
        if (string.IsNullOrEmpty(a)) return "Response A is empty.";
        if (string.IsNullOrEmpty(b)) return "Response B is empty.";

        var aLen = a.Length;
        var bLen = b.Length;

        var sim = ComputeSimilarity(a, b);
        var lines = new List<string>
        {
            $"Length: A={aLen} chars, B={bLen} chars",
            $"Similarity: {sim:F1}%"
        };

        var aWords = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var bWords = b.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var onlyA = aWords.Except(bWords, StringComparer.OrdinalIgnoreCase).Take(20).ToArray();
        var onlyB = bWords.Except(aWords, StringComparer.OrdinalIgnoreCase).Take(20).ToArray();

        if (onlyA.Length > 0)
            lines.Add($"Only in A: {string.Join(", ", onlyA)}");
        if (onlyB.Length > 0)
            lines.Add($"Only in B: {string.Join(", ", onlyB)}");

        return string.Join("\n", lines);
    }

    private static double ComputeSimilarity(string a, string b)
    {
        var maxLen = Math.Max(a.Length, b.Length);
        if (maxLen == 0) return 100;

        var setA = new HashSet<string>(a.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
        var setB = new HashSet<string>(b.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

        if (setA.Count == 0 && setB.Count == 0) return 100;
        if (setA.Count == 0 || setB.Count == 0) return 0;

        var intersection = setA.Intersect(setB, StringComparer.OrdinalIgnoreCase).Count();
        var union = setA.Union(setB, StringComparer.OrdinalIgnoreCase).Count();

        return union == 0 ? 0 : (double)intersection / union * 100;
    }
}

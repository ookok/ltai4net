// Copyright (c) LTAI. All rights reserved.

using LTAI.Agent.DevUI;
using LTAI.Agent.Suggestions;
using System.Text;
using Avalonia.Controls;
using Avalonia.Media;

namespace LTAI.Desktop.DevUI;

public sealed class SuggestionsPanelView
{
    private readonly LTAIDevUIService _devUi;
    private readonly StackPanel _panel;

    public StackPanel Panel => _panel;

    public SuggestionsPanelView(LTAIDevUIService devUi)
    {
        _devUi = devUi;
        _panel = new StackPanel { Spacing = 8 };

        // Header
        _panel.Children.Add(new TextBlock
        {
            Text = "💡 Code Suggestions",
            FontSize = 16,
            FontWeight = FontWeight.Bold,
        });

        BuildContent();

        // Listen for updates
        devUi.OnSuggestionsUpdated += () =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _panel.Children.Clear();
                _panel.Children.Add(new TextBlock
                {
                    Text = "💡 Code Suggestions",
                    FontSize = 16,
                    FontWeight = FontWeight.Bold,
                });
                BuildContent();
            });
        };
    }

    private void BuildContent()
    {
        var stats = _devUi.GetSuggestionStats();

        // Stats bar
        var statsBar = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        statsBar.Children.Add(new TextBlock { Text = $"Total: {stats.Total}", Foreground = Brushes.Cyan });
        statsBar.Children.Add(new TextBlock { Text = $"Critical: {stats.Critical}", Foreground = Brushes.Red });
        statsBar.Children.Add(new TextBlock { Text = $"Warnings: {stats.Warnings}", Foreground = Brushes.Yellow });
        _panel.Children.Add(statsBar);

        // Category bar chart
        if (stats.ByCategory.Count > 0)
        {
            var catText = new StringBuilder("Categories: ");
            foreach (var (cat, count) in stats.ByCategory.OrderByDescending(kv => kv.Value))
                catText.Append($"{cat}={count}  ");
            _panel.Children.Add(new TextBlock
            {
                Text = catText.ToString(),
                Foreground = Brushes.Gray,
                FontSize = 12,
            });
        }

        // Suggestions table
        var suggestions = _devUi.GetSuggestions();
        if (suggestions.Count == 0)
        {
            _panel.Children.Add(new TextBlock
            {
                Text = "✓ No issues found — workspace looks clean!",
                Foreground = Brushes.Green,
                FontStyle = FontStyle.Italic,
            });
            return;
        }

        // Top issues
        var headerRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 4,
        };
        headerRow.Children.Add(new TextBlock { Text = "Severity", FontWeight = FontWeight.Bold, Width = 60 });
        headerRow.Children.Add(new TextBlock { Text = "Category", FontWeight = FontWeight.Bold, Width = 80 });
        headerRow.Children.Add(new TextBlock { Text = "Issue", FontWeight = FontWeight.Bold, Width = 200 });
        headerRow.Children.Add(new TextBlock { Text = "File", FontWeight = FontWeight.Bold, Width = 100 });
        _panel.Children.Add(headerRow);

        foreach (var issue in suggestions.Take(10))
        {
            var sev = issue.Severity switch
            {
                IssueSeverity.Critical => "CRIT",
                IssueSeverity.Warning => "WARN",
                _ => "INFO",
            };
            var sevColor = issue.Severity switch
            {
                IssueSeverity.Critical => Brushes.Red,
                IssueSeverity.Warning => Brushes.Yellow,
                _ => Brushes.Gray,
            };

            var row = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 4,
            };
            row.Children.Add(new TextBlock { Text = sev, Foreground = sevColor, Width = 60 });
            row.Children.Add(new TextBlock { Text = issue.Category, Width = 80 });
            row.Children.Add(new TextBlock { Text = Truncate(issue.Title, 60), Width = 200 });
            row.Children.Add(new TextBlock { Text = $"{issue.File}:{issue.Line}", Foreground = Brushes.Gray, Width = 100 });
            _panel.Children.Add(row);
        }

        if (suggestions.Count > 10)
            _panel.Children.Add(new TextBlock
            {
                Text = $"... and {suggestions.Count - 10} more issues",
                FontStyle = FontStyle.Italic,
                Foreground = Brushes.Gray,
            });
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..(max - 3)] + "...";
}

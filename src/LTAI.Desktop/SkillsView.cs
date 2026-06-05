using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using LTAI.Desktop.ViewModels;

namespace LTAI.Desktop;

public sealed class SkillsView : UserControl
{
    private readonly SkillsViewModel _vm;
    private readonly StackPanel _listPanel;
    private readonly TextBlock _statusText;

    public SkillsView(string? skillsDir = null)
    {
        _vm = new SkillsViewModel(skillsDir);
        DataContext = _vm;
        Background = LtaiTheme.Sbb(LtaiTheme.Bg);

        var root = new StackPanel { Margin = new(16), Spacing = 8 };

        root.Children.Add(new TextBlock
        { Text = "🧠 技能列表", FontSize = 16, FontWeight = FontWeight.Bold,
          Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary) });

        _statusText = new TextBlock { FontSize = 11, Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim) };
        _statusText.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(_vm.StatusText)));
        root.Children.Add(_statusText);

        var scroll = new ScrollViewer();
        _listPanel = new StackPanel { Spacing = 4 };
        scroll.Content = _listPanel;
        root.Children.Add(scroll);

        Content = root;
        RefreshList();
    }

    private void RefreshList()
    {
        _listPanel.Children.Clear();
        foreach (var skill in _vm.Skills)
        {
            var card = new Border
            {
                Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
                BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border),
                BorderThickness = new(1),
                CornerRadius = LtaiTheme.Radius.Sm,
                Padding = new(8, 4),
                Margin = new(0, 1),
                Child = new TextBlock
                {
                    Text = $"[{skill.Name}] {skill.Description}",
                    FontSize = 12,
                    Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
                    TextWrapping = TextWrapping.Wrap,
                }
            };
            _listPanel.Children.Add(card);
        }
    }
}

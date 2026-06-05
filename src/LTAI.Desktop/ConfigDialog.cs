using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using LTAI.Core.Configuration;

namespace LTAI.Desktop;

public sealed class ConfigDialog : Window
{
    private readonly ViewModels.ConfigViewModel _vm;
    private readonly TextBlock _content;
    private readonly TextBlock _statusBar;

    public ConfigDialog()
    {
        _vm = new ViewModels.ConfigViewModel();
        DataContext = _vm;
        Title = "配置管理";
        Width = 480;
        Height = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = LtaiTheme.Sbb(LtaiTheme.Bg);

        var root = new StackPanel { Spacing = 8, Margin = new(16) };

        root.Children.Add(new TextBlock
        { Text = "配置管理", FontSize = 18, FontWeight = FontWeight.Bold,
          Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary) });

        _content = new TextBlock
        { Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary), TextWrapping = TextWrapping.Wrap };
        root.Children.Add(_content);

        var l1Label = new TextBlock { Text = "L1 (Flash) 模型:", FontWeight = FontWeight.Bold,
          Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary), Margin = new(0, 8, 0, 0) };
        root.Children.Add(l1Label);
        var l1Box = new ComboBox { MinWidth = 300, Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary) };
        l1Box.Bind(ComboBox.ItemsSourceProperty, new Avalonia.Data.Binding(nameof(_vm.L1Models)));
        l1Box.Bind(ComboBox.SelectedItemProperty, new Avalonia.Data.Binding(nameof(_vm.SelectedL1Model)));
        root.Children.Add(l1Box);

        var l2Label = new TextBlock { Text = "L2 (Pro) 模型:", FontWeight = FontWeight.Bold,
          Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary), Margin = new(0, 4, 0, 0) };
        root.Children.Add(l2Label);
        var l2Box = new ComboBox { MinWidth = 300, Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary) };
        l2Box.Bind(ComboBox.ItemsSourceProperty, new Avalonia.Data.Binding(nameof(_vm.L2Models)));
        l2Box.Bind(ComboBox.SelectedItemProperty, new Avalonia.Data.Binding(nameof(_vm.SelectedL2Model)));
        root.Children.Add(l2Box);

        var fetchBtn = new Button
        { Content = "🔄 获取可用模型", Background = LtaiTheme.Sbb(LtaiTheme.AccentDNA),
          Foreground = LtaiTheme.Sbb(LtaiTheme.TextOnAccent), Margin = new(0, 4, 0, 0) };
        fetchBtn.Click += (_, _) => _vm.FetchModelsCommand.Execute(null);
        root.Children.Add(fetchBtn);

        _statusBar = new TextBlock
        { Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary), FontSize = 11 };
        _statusBar.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(_vm.StatusBarText)));
        root.Children.Add(_statusBar);

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new(0, 4, 0, 0) };
        var clearAllBtn = new Button
        { Content = "🗑 清除所有 Key", Background = LtaiTheme.Sbb(LtaiTheme.AccentWarning),
          Foreground = LtaiTheme.Sbb(LtaiTheme.TextOnAccent) };
        clearAllBtn.Click += (_, _) => _vm.ClearAllKeysCommand.Execute(null);
        btnRow.Children.Add(clearAllBtn);

        var closeBtn = new Button
        { Content = "关闭", Width = 80, Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
          Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary), BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border) };
        closeBtn.Click += (_, _) => Close();
        btnRow.Children.Add(closeBtn);
        root.Children.Add(btnRow);

        Content = root;
        RefreshKeys();
    }

    private void RefreshKeys()
    {
        var lines = new List<string>();
        foreach (var k in LTAI.Core.Configuration.KnownKeys.All)
        {
            var hasKey = LTAI.Core.Configuration.SecretManager.Has(k.EnvVar);
            lines.Add($"{(hasKey ? "🟢" : "⚪")} {k.EnvVar} — {k.Service}");
        }
        _content.Text = string.Join("\n", lines);
    }
}

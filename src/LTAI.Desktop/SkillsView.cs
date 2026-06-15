using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
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
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

            var runBtn = new Button
            {
                Content = "▶ 运行",
                FontSize = 10,
                Background = LtaiTheme.Sbb(LtaiTheme.AccentDNA),
                Foreground = LtaiTheme.Sbb(LtaiTheme.TextOnAccent),
                BorderThickness = new(0),
            };
            runBtn.Click += async (_, _) =>
            {
                _statusText.Text = $"⚡ 正在运行: {skill.Name}...";
                try
                {
                    var scriptPath = skill.Path;
                    if (string.IsNullOrEmpty(scriptPath) || !File.Exists(scriptPath))
                    {
                        _statusText.Text = $"❌ {skill.Name}: 脚本文件不存在";
                        return;
                    }
                    var (shell, args) = OperatingSystem.IsWindows()
                        ? ("pwsh", $"-NoProfile -File \"{scriptPath}\"")
                        : ("bash", scriptPath);
                    var psi = new System.Diagnostics.ProcessStartInfo(shell, args)
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    using var p = new System.Diagnostics.Process { StartInfo = psi };
                    p.Start();
                    var outputTask = p.StandardOutput.ReadToEndAsync();
                    var errorTask = p.StandardError.ReadToEndAsync();
                    var exitTask = p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                    await exitTask;
                    _statusText.Text = $"✅ {skill.Name}: 完成 (exit={p.ExitCode})";
                }
                catch (TimeoutException) { _statusText.Text = $"⏱ {skill.Name}: 超时"; }
                catch (Exception ex) { _statusText.Text = $"❌ {skill.Name}: {ex.Message}"; }
            };

            row.Children.Add(runBtn);

            var textBlock = new TextBlock
            {
                Text = $"[{skill.Name}] {skill.Description}",
                FontSize = 11,
                Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            row.Children.Add(textBlock);

            var card = new Border
            {
                Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
                BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border),
                BorderThickness = new(1),
                CornerRadius = LtaiTheme.Radius.Sm,
                Padding = new(8, 4),
                Margin = new(0, 1),
                Child = row,
            };
            _listPanel.Children.Add(card);
        }
    }
}

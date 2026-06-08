using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using LTAI.Agent.Tools;

namespace LTAI.Desktop;

public sealed class OrchestrationView : UserControl
{
    private readonly TextBlock _statusText;
    private readonly TextBlock _planText;

    public OrchestrationView()
    {
        Background = LtaiTheme.Sbb(LtaiTheme.Bg);

        var root = new StackPanel { Margin = new(16), Spacing = 8 };

        root.Children.Add(new TextBlock
        { Text = "🎭 编配中心", FontSize = 16, FontWeight = FontWeight.Bold,
          Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary) });

        _statusText = new TextBlock
        { FontSize = 11, Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim) };
        root.Children.Add(_statusText);

        _planText = new TextBlock
        { FontSize = 12, Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
          TextWrapping = TextWrapping.Wrap };
        root.Children.Add(_planText);

        var refreshBtn = new Button { Content = "🔄 刷新", Background = LtaiTheme.Sbb(LtaiTheme.AccentDNA),
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextOnAccent) };
        refreshBtn.Click += (_, _) => RefreshPlan();
        root.Children.Add(refreshBtn);

        Content = root;
        RefreshPlan();
    }

    private void RefreshPlan()
    {
        try
        {
            var status = PlanTools.PlanStatus();
            _planText.Text = string.IsNullOrEmpty(status) ? "暂无活跃计划" : status;
            _statusText.Text = $"最后更新: {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            _planText.Text = $"❌ 获取计划状态失败: {ex.Message}";
        }
    }
}

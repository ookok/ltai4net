using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace LTAI.Desktop;

public sealed class PromptEditorView : UserControl
{
    private readonly string _agentsDir;
    private readonly TextBlock _contentText;
    private readonly ComboBox _agentCombo;
    private readonly TextBox _editBox;
    private readonly StackPanel _editPanel;

    public PromptEditorView(string? agentsDir = null)
    {
        _agentsDir = agentsDir ?? Path.Combine(Environment.CurrentDirectory, "agents");
        Background = LtaiTheme.Sbb(LtaiTheme.Bg);

        var root = new StackPanel { Margin = new(16), Spacing = 8 };

        root.Children.Add(new TextBlock
        { Text = "📝 Agent Prompt 编辑器", FontSize = 16, FontWeight = FontWeight.Bold,
          Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary) });

        var topRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _agentCombo = new ComboBox { Width = 200, PlaceholderText = "选择 Agent..." };
        _agentCombo.SelectionChanged += (_, _) => LoadSelectedPrompt();

        var loadBtn = new Button { Content = "📂 加载", Background = LtaiTheme.Sbb(LtaiTheme.AccentDNA),
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextOnAccent) };
        loadBtn.Click += (_, _) => LoadSelectedPrompt();
        topRow.Children.Add(_agentCombo);
        topRow.Children.Add(loadBtn);

        var saveBtn = new Button { Content = "💾 保存", Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary) };
        saveBtn.Click += (_, _) => SavePrompt();
        topRow.Children.Add(saveBtn);
        root.Children.Add(topRow);

        _editPanel = new StackPanel { Spacing = 4, IsVisible = false };
        _contentText = new TextBlock { FontSize = 11, Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
            TextWrapping = TextWrapping.Wrap };
        _editPanel.Children.Add(_contentText);

        _editBox = new TextBox { MinHeight = 200, AcceptsReturn = true,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
            Background = LtaiTheme.Sbb(LtaiTheme.BgPanel) };
        _editPanel.Children.Add(_editBox);
        root.Children.Add(_editPanel);

        Content = root;
        LoadAgentList();
    }

    private void LoadAgentList()
    {
        try
        {
            if (!Directory.Exists(_agentsDir)) return;
            foreach (var f in Directory.GetFiles(_agentsDir, "*.agent.md").OrderBy(f => f))
                _agentCombo.Items.Add(Path.GetFileNameWithoutExtension(f).Replace(".agent", ""));
        }
        catch { }
    }

    private void LoadSelectedPrompt()
    {
        var name = _agentCombo.SelectedItem as string;
        if (name == null) return;

        try
        {
            var path = Path.Combine(_agentsDir, name + ".agent.md");
            if (!File.Exists(path)) return;
            var content = File.ReadAllText(path);
            _contentText.Text = $"📄 {name}.agent.md ({content.Length} chars)";
            _editBox.Text = content;
            _editPanel.IsVisible = true;
        }
        catch (Exception ex) { _contentText.Text = $"❌ {ex.Message}"; }
    }

    private void SavePrompt()
    {
        var name = _agentCombo.SelectedItem as string;
        if (name == null || string.IsNullOrEmpty(_editBox.Text)) return;

        try
        {
            var path = Path.Combine(_agentsDir, name + ".agent.md");
            File.WriteAllText(path, _editBox.Text);
            _contentText.Text = $"✅ 已保存 {name}.agent.md";
        }
        catch (Exception ex) { _contentText.Text = $"❌ {ex.Message}"; }
    }
}

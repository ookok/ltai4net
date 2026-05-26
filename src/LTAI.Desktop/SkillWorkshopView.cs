using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace LTAI.Desktop;

public sealed class SkillWorkshopView : UserControl
{
    private readonly LTAIService _svc;
    private readonly ListBox _stepList;
    private readonly ObservableCollection<SkillStep> _steps = new();
    private readonly TextBox _nameBox;
    private readonly TextBox _descBox;
    private readonly ComboBox _langCombo;
    private readonly TextBox _codeBox;
    private readonly StackPanel _validationPanel;
    private readonly TextBox _testInput;
    private readonly TextBlock _testOutput;
    private readonly Button _runBtn;

    public SkillWorkshopView(LTAIService svc)
    {
        _svc = svc;
        Background = LtaiTheme.Sbb(LtaiTheme.Bg);

        var root = new DockPanel { Margin = new(16) };

        var header = new TextBlock
        {
            Text = "Skill Workshop",
            FontSize = 20,
            FontWeight = FontWeight.Bold,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary)
        };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var sep = new Border { Height = 1, Background = LtaiTheme.Sbb(LtaiTheme.Border), Margin = new(0, 8) };
        DockPanel.SetDock(sep, Dock.Top);
        root.Children.Add(sep);

        var mainGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("1*,2*,1.5*"),
            RowDefinitions = new RowDefinitions("Auto,*")
        };

        var nameRow = new DockPanel { Margin = new(0, 0, 0, 6) };
        nameRow.Children.Add(new TextBlock
        {
            Text = "Skill Name",
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
            FontSize = 11,
            Width = 70
        });
        _nameBox = new TextBox
        {
            Watermark = "my-skill",
            Background = LtaiTheme.Sbb(LtaiTheme.BgInput),
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
            FontFamily = new("Consolas"),
            FontSize = 13
        };
        nameRow.Children.Add(_nameBox);

        var descRow = new DockPanel { Margin = new(0, 0, 0, 6) };
        descRow.Children.Add(new TextBlock
        {
            Text = "Description",
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
            FontSize = 11,
            Width = 70
        });
        _descBox = new TextBox
        {
            Watermark = "What this skill does...",
            Background = LtaiTheme.Sbb(LtaiTheme.BgInput),
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
            FontFamily = new("Consolas"),
            FontSize = 13
        };
        descRow.Children.Add(_descBox);

        var langRow = new DockPanel { Margin = new(0, 0, 0, 6) };
        langRow.Children.Add(new TextBlock
        {
            Text = "Template",
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
            FontSize = 11,
            Width = 70
        });
        _langCombo = new ComboBox
        {
            ItemsSource = new[] { "C#", "Python", "Shell" },
            SelectedIndex = 0,
            Width = 100
        };
        langRow.Children.Add(_langCombo);

        var metaPanel = new StackPanel { Spacing = 4, Margin = new(0, 0, 0, 8) };
        metaPanel.Children.Add(nameRow);
        metaPanel.Children.Add(descRow);
        metaPanel.Children.Add(langRow);

        var stepHeader = new TextBlock
        {
            Text = "Steps",
            Foreground = LtaiTheme.Sbb(LtaiTheme.AccentDNA),
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            Margin = new(0, 0, 0, 4)
        };

        _stepList = new ListBox
        {
            ItemsSource = _steps,
            Background = LtaiTheme.Sbb(LtaiTheme.BgInput),
            MinHeight = 200
        };
        _stepList.ItemTemplate = new FuncDataTemplate<SkillStep>((item, _) =>
        {
            var row = new DockPanel { Margin = new(4) };
            var label = new TextBlock
            {
                Text = item?.Display ?? "",
                Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
                FontFamily = new("Consolas"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(label);
            return row;
        });

        var addBtn = new Button
        {
            Content = "+ Add Step",
            Background = LtaiTheme.Sbb(LtaiTheme.AccentSystem),
            Foreground = LtaiTheme.Sbb("#ffffff"),
            FontSize = 11,
            Margin = new(0, 4, 0, 0)
        };
        addBtn.Click += (_, _) =>
        {
            _steps.Add(new SkillStep { Index = _steps.Count + 1, Description = $"Step {_steps.Count + 1}" });
        };

        var removeBtn = new Button
        {
            Content = "- Remove",
            Background = LtaiTheme.Sbb(LtaiTheme.AccentDanger),
            Foreground = LtaiTheme.Sbb("#ffffff"),
            FontSize = 11,
            Margin = new(4, 4, 0, 0)
        };
        removeBtn.Click += (_, _) =>
        {
            if (_stepList.SelectedItem is SkillStep sel)
                _steps.Remove(sel);
            else if (_steps.Count > 0)
                _steps.RemoveAt(_steps.Count - 1);
        };

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        btnRow.Children.Add(addBtn);
        btnRow.Children.Add(removeBtn);

        var saveBtn = new Button
        {
            Content = "Save .md",
            Background = LtaiTheme.Sbb(LtaiTheme.AccentDNA),
            Foreground = LtaiTheme.Sbb("#ffffff"),
            FontSize = 11,
            FontWeight = FontWeight.Bold,
            Margin = new(0, 8, 0, 0)
        };
        saveBtn.Click += async (_, _) => await SaveSkillAsync();

        var leftPanel = new StackPanel { Spacing = 2 };
        leftPanel.Children.Add(metaPanel);
        leftPanel.Children.Add(stepHeader);
        leftPanel.Children.Add(_stepList);
        leftPanel.Children.Add(btnRow);
        leftPanel.Children.Add(saveBtn);

        var centerHeader = new TextBlock
        {
            Text = "Validation",
            Foreground = LtaiTheme.Sbb(LtaiTheme.AccentInfo),
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            Margin = new(0, 0, 0, 4)
        };

        _validationPanel = new StackPanel { Spacing = 4 };
        var centerScroll = new ScrollViewer { Content = _validationPanel };

        var validateBtn = new Button
        {
            Content = "Validate",
            Background = LtaiTheme.Sbb(LtaiTheme.AccentInfo),
            Foreground = LtaiTheme.Sbb("#ffffff"),
            FontSize = 11,
            Margin = new(0, 8, 0, 0)
        };
        validateBtn.Click += (_, _) => ValidateSkill();

        var centerPanel = new StackPanel { Spacing = 4 };
        centerPanel.Children.Add(centerHeader);
        centerPanel.Children.Add(centerScroll);
        centerPanel.Children.Add(validateBtn);

        var rightHeader = new TextBlock
        {
            Text = "Test",
            Foreground = LtaiTheme.Sbb(LtaiTheme.AccentSystem),
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            Margin = new(0, 0, 0, 4)
        };

        _testInput = new TextBox
        {
            Watermark = "Test input...",
            Background = LtaiTheme.Sbb(LtaiTheme.BgInput),
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
            FontFamily = new("Consolas"),
            FontSize = 12,
            Height = 60,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap
        };

        _testOutput = new TextBlock
        {
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
            FontFamily = new("Consolas"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new(0, 4, 0, 0)
        };

        _runBtn = new Button
        {
            Content = "Run Test",
            Background = LtaiTheme.Sbb(LtaiTheme.AccentSystem),
            Foreground = LtaiTheme.Sbb("#ffffff"),
            FontSize = 11,
            FontWeight = FontWeight.Bold,
            Margin = new(0, 8, 0, 0)
        };
        _runBtn.Click += async (_, _) => await RunTestAsync();

        var rightPanel = new StackPanel { Spacing = 4 };
        rightPanel.Children.Add(rightHeader);
        rightPanel.Children.Add(_testInput);
        rightPanel.Children.Add(_testOutput);
        rightPanel.Children.Add(_runBtn);

        _codeBox = new TextBox
        {
            Watermark = "// Script body for selected template...",
            Background = LtaiTheme.Sbb(LtaiTheme.CodeBg),
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
            FontFamily = new("Consolas"),
            FontSize = 12,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 100,
            Margin = new(0, 8, 0, 0)
        };
        rightPanel.Children.Add(_codeBox);

        Grid.SetColumn(leftPanel, 0);
        Grid.SetColumn(centerPanel, 1);
        Grid.SetColumn(rightPanel, 2);
        Grid.SetRow(leftPanel, 1);
        Grid.SetRow(centerPanel, 1);
        Grid.SetRow(rightPanel, 1);

        mainGrid.Children.Add(leftPanel);
        mainGrid.Children.Add(centerPanel);
        mainGrid.Children.Add(rightPanel);

        root.Children.Add(mainGrid);
        Content = root;
    }

    private void ValidateSkill()
    {
        _validationPanel.Children.Clear();

        var name = _nameBox.Text?.Trim();

        if (string.IsNullOrWhiteSpace(name))
            AddValidation(false, "Skill name is required");
        else if (name.Contains(' '))
            AddValidation(false, "Name should not contain spaces (use hyphens)");
        else
            AddValidation(true, $"Name '{name}' is valid");

        if (string.IsNullOrWhiteSpace(_descBox.Text?.Trim()))
            AddValidation(false, "Description is required");
        else
            AddValidation(true, "Description provided");

        if (_steps.Count == 0)
            AddValidation(false, "At least one step is required");
        else
            AddValidation(true, $"{_steps.Count} step(s) defined");

        var template = _langCombo.SelectedItem as string ?? "C#";
        AddValidation(true, $"Template: {template}");

        if (!string.IsNullOrWhiteSpace(_codeBox.Text?.Trim()))
            AddValidation(true, "Script body present");
        else
            AddValidation(false, "Script body is empty");
    }

    private void AddValidation(bool ok, string msg)
    {
        var icon = ok ? "  [OK] " : "  [FAIL] ";
        var row = new TextBlock
        {
            Text = icon + msg,
            Foreground = ok ? LtaiTheme.Sbb(LtaiTheme.AccentSystem) : LtaiTheme.Sbb(LtaiTheme.AccentDanger),
            FontFamily = new("Consolas"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };
        _validationPanel.Children.Add(row);
    }

    private async Task RunTestAsync()
    {
        var input = _testInput.Text?.Trim();
        if (string.IsNullOrWhiteSpace(input)) return;

        _testOutput.Text = "Running...";
        _runBtn.IsEnabled = false;

        try
        {
            var result = await _svc.LTS.ChatAsync(input);
            _testOutput.Text = result;
        }
        catch (Exception ex)
        {
            _testOutput.Text = $"Error: {ex.Message}";
        }
        finally
        {
            _runBtn.IsEnabled = true;
        }
    }

    private async Task SaveSkillAsync()
    {
        var name = _nameBox.Text?.Trim() ?? "untitled-skill";
        var desc = _descBox.Text?.Trim() ?? "";
        var template = _langCombo.SelectedItem as string ?? "C#";
        var steps = string.Join("\n", _steps.Select(s => $"- {s.Description}"));
        var code = _codeBox.Text?.Trim() ?? "";

        var md = $$"""
            # {{name}}
            
            {{desc}}
            
            ## Template
            {{template}}
            
            ## Steps
            {{steps}}
            
            ## Script
            ```{{template.ToLowerInvariant() switch { "c#" => "csharp", var t => t }}}
            {{code}}
            ```
            """;

        try
        {
            var skillsDir = Path.Combine(AppContext.BaseDirectory, "skills");
            Directory.CreateDirectory(skillsDir);
            var path = Path.Combine(skillsDir, $"{name}.md");
            await File.WriteAllTextAsync(path, md);

            var dlg = new Window
            {
                Title = "Saved",
                Width = 300,
                Height = 100,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new TextBlock
                {
                    Text = $"Saved to: {path}",
                    Foreground = LtaiTheme.Sbb(LtaiTheme.AccentSystem),
                    Margin = new(16),
                    TextWrapping = TextWrapping.Wrap
                }
            };
            dlg.Show();
        }
        catch (Exception ex)
        {
            var dlg = new Window
            {
                Title = "Error",
                Width = 300,
                Height = 100,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new TextBlock
                {
                    Text = $"Failed: {ex.Message}",
                    Foreground = LtaiTheme.Sbb(LtaiTheme.AccentDanger),
                    Margin = new(16),
                    TextWrapping = TextWrapping.Wrap
                }
            };
            dlg.Show();
        }
    }
}

public sealed class SkillStep
{
    public int Index { get; set; }
    public string Description { get; set; } = "";
    public string Display => $"{Index}. {Description}";
}

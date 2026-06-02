using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using LTAI.Agent.Tools;

namespace LTAI.Desktop.Dialogs;

/// <summary>
/// P17.5: Avalonia dialog for rendering LLM questions and collecting user answers.
/// Shown when <see cref="QuestionService.QuestionPosted"/> fires during a chat.
/// </summary>
public sealed class QuestionDialog : Window
{
    private readonly StackPanel _container;
    private readonly List<QuestionPanel> _panels = [];

    public List<IReadOnlyList<string>> Answers { get; private set; } = [];

    public QuestionDialog(QuestionPost post)
    {
        Title = "LTAI — 需要确认";
        Width = 520;
        Height = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var scroll = new ScrollViewer();
        _container = new StackPanel { Spacing = 12, Margin = new Avalonia.Thickness(16) };
        scroll.Content = _container;

        for (int i = 0; i < post.Questions.Count; i++)
        {
            var q = post.Questions[i];
            var panel = new QuestionPanel(q, i + 1, post.Questions.Count);
            _panels.Add(panel);
            _container.Children.Add(panel);
        }

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Avalonia.Thickness(0, 8, 0, 0),
        };

        var cancelBtn = new Button { Content = "取消", Width = 80 };
        cancelBtn.Click += (_, _) => { Close(); };

        var okBtn = new Button { Content = "确认", Width = 80 };
        okBtn.Click += (_, _) =>
        {
            Answers = _panels.ConvertAll(p => p.GetAnswers());
            Close();
        };

        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(okBtn);
        _container.Children.Add(btnRow);

        Content = scroll;
    }

    /// <summary>Show the dialog on the UI thread and wait for answer.</summary>
    public static async Task<List<IReadOnlyList<string>>> ShowAsync(Window owner, QuestionPost post)
    {
        var dialog = new QuestionDialog(post);
        await dialog.ShowDialog(owner);
        return dialog.Answers;
    }

    private sealed class QuestionPanel : StackPanel
    {
        private readonly QuestionPrompt _q;
        private readonly List<CheckBox> _checkBoxes = [];
        private readonly TextBox? _customBox;

        public QuestionPanel(QuestionPrompt q, int idx, int total)
        {
            _q = q;
            Spacing = 6;

            var header = new TextBlock
            {
                Text = $"❓ {idx}/{total} {q.Header}",
                FontWeight = Avalonia.Media.FontWeight.Bold,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            };
            Children.Add(header);

            if (!string.IsNullOrEmpty(q.Question))
            {
                Children.Add(new TextBlock
                {
                    Text = q.Question,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Foreground = Avalonia.Media.Brushes.Gray,
                });
            }

            if (q.Options.Count > 0)
            {
                foreach (var opt in q.Options)
                {
                    var cb = new CheckBox
                    {
                        Content = $"{opt.Label}",
                        Tag = opt.Label,
                    };
                    if (!string.IsNullOrEmpty(opt.Description))
                    {
                        cb.Content = $"{opt.Label}  —  {opt.Description}";
                    }
                    _checkBoxes.Add(cb);
                    Children.Add(cb);
                }

                if (q.Multiple)
                {
                    Children.Add(new TextBlock
                    {
                        Text = "也可勾选「自定义」后输入:",
                        FontSize = 12,
                        Foreground = Avalonia.Media.Brushes.Gray,
                    });
                }
            }

            _customBox = new TextBox
            {
                Watermark = "自定义回答…",
                Height = 32,
            };
            Children.Add(_customBox);
        }

        public IReadOnlyList<string> GetAnswers()
        {
            var result = new List<string>();
            foreach (var cb in _checkBoxes)
            {
                if (cb.IsChecked == true)
                    result.Add(cb.Tag?.ToString() ?? "");
            }
            var custom = _customBox?.Text?.Trim();
            if (!string.IsNullOrEmpty(custom))
                result.Add(custom);
            if (result.Count == 0)
                result.Add(_customBox?.Text?.Trim() ?? "(未选择)");
            return result;
        }
    }
}

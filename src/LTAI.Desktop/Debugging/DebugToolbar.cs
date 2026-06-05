using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Layout;
using LTAI.Core.Debugging;

namespace LTAI.Desktop.Debugging;

public sealed class DebugToolbar : StackPanel
{
    private readonly DapSession _session;
    private readonly Button _continueBtn;
    private readonly Button _stepOverBtn;
    private readonly Button _stepIntoBtn;
    private readonly Button _stepOutBtn;
    private readonly Button _stopBtn;
    private readonly Button _pauseBtn;
    private readonly TextBlock _statusText;

    public DebugToolbar(DapSession session)
    {
        _session = session;
        Orientation = Orientation.Horizontal;
        Spacing = 4;
        Background = LtaiTheme.Sbb(LtaiTheme.BgPanel);
        Margin = new(0);
        IsVisible = false;

        _continueBtn = MakeButton("▶", "Continue (F5)");
        _stepOverBtn = MakeButton("⤵", "Step Over (F10)");
        _stepIntoBtn = MakeButton("↷", "Step Into (F11)");
        _stepOutBtn = MakeButton("↶", "Step Out (Shift+F11)");
        _pauseBtn = MakeButton("⏸", "Pause");
        _stopBtn = MakeButton("■", "Stop (Shift+F5)");

        _continueBtn.Click += async (_, _) => { try { await _session.ContinueAsync(); } catch { } };
        _stepOverBtn.Click += async (_, _) => { try { await _session.StepOverAsync(); } catch { } };
        _stepIntoBtn.Click += async (_, _) => { try { await _session.StepIntoAsync(); } catch { } };
        _stepOutBtn.Click += async (_, _) => { try { await _session.StepOutAsync(); } catch { } };
        _pauseBtn.Click += async (_, _) => { try { await _session.PauseAsync(); } catch { } };
        _stopBtn.Click += async (_, _) => { try { await _session.TerminateAsync(); } catch { } };

        _statusText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
            FontSize = 11,
            Margin = new(8, 0),
        };

        Children.AddRange([_continueBtn, _stepOverBtn, _stepIntoBtn, _stepOutBtn, _pauseBtn, _stopBtn, _statusText]);

        _session.StateChanged += OnStateChanged;
        OnStateChanged(_session.State);
    }

    private void OnStateChanged(DebugState state)
    {
        IsVisible = state is DebugState.Launching or DebugState.Running or DebugState.Paused;
        var enabled = state is DebugState.Paused;
        _continueBtn.IsEnabled = enabled;
        _stepOverBtn.IsEnabled = enabled;
        _stepIntoBtn.IsEnabled = enabled;
        _stepOutBtn.IsEnabled = enabled;
        _pauseBtn.IsEnabled = state is DebugState.Running;
        _stopBtn.IsEnabled = state is DebugState.Running or DebugState.Paused;

        _statusText.Text = state switch
        {
            DebugState.Launching => "正在启动调试器...",
            DebugState.Running => "▶ 运行中",
            DebugState.Paused => $"⏸ 已暂停 · {_session.CurrentFile}:{_session.CurrentLine}",
            DebugState.Terminating => "正在停止...",
            _ => "",
        };

        if (state is DebugState.Terminated)
            IsVisible = false;
    }

    private static Button MakeButton(string text, string tooltip)
    {
        var btn = new Button
        {
            Content = new TextBlock
            {
                Text = text,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            },
            Width = 32,
            Height = 26,
            IsEnabled = false,
            Background = LtaiTheme.Sbb(LtaiTheme.Bg),
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
            BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border),
        };
        ToolTip.SetTip(btn, tooltip);
        return btn;
    }
}

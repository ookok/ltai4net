using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LTAI.Desktop.Services;

namespace LTAI.Desktop.ViewModels;

public sealed record ChatMessage(string Role, string Text);

public partial class ChatViewModel : ObservableObject
{
    private readonly ILlmClient _llm;
    private readonly DesktopCommandService _cmd;
    private CancellationTokenSource? _cts;

    public ChatViewModel(ILlmClient llm, DesktopCommandService? cmd = null)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _cmd = cmd ?? new DesktopCommandService();
    }

    [ObservableProperty]
    private string _input = "";

    [ObservableProperty]
    private bool _isSending;

    public ObservableCollection<ChatMessage> Messages { get; } = new();

    /// <summary>Fired when the VM wants the window to close.</summary>
    public event Action? ExitRequested;

    [RelayCommand]
    private async Task SendAsync()
    {
        var query = Input.Trim();
        if (string.IsNullOrWhiteSpace(query)) return;

        Input = "";

        if (query.StartsWith('/'))
        {
            var result = _cmd.Execute(query);
            if (result.RequestExit)
            {
                Messages.Add(new ChatMessage("system", result.StatusMessage ?? "👋"));
                ExitRequested?.Invoke();
                return;
            }
            if (result.ClearMessages)
                Messages.Clear();
            if (result.StatusMessage != null)
                Messages.Add(new ChatMessage("system", result.StatusMessage));
            return;
        }

        Messages.Add(new ChatMessage("user", query));
        IsSending = true;

        _cts?.Dispose();
        _cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var sb = new StringBuilder();
        try
        {
            await foreach (var token in _llm.ChatStreamingAsync(query, _cts.Token))
            {
                _cts.Token.ThrowIfCancellationRequested();
                sb.Append(token);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation detected during streaming (timeout or user cancel)
        }
        catch (Exception ex)
        {
            Messages.Add(new ChatMessage("system", $"⚠️ 错误: {ex.Message}"));
            return;
        }
        finally
        {
            IsSending = false;
        }

        var text = sb.ToString();
        if (_cts.IsCancellationRequested)
        {
            if (text.Length > 0)
                Messages.Add(new ChatMessage("assistant", text + "\n\n*[已取消]*"));
            else
                Messages.Add(new ChatMessage("system", "⏹️ 已取消"));
        }
        else if (text.Length > 0)
        {
            Messages.Add(new ChatMessage("assistant", text));
        }

        _cts?.Dispose();
        _cts = null;
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}

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

        // Atomically replace CTS — cancel + dispose old one, create new
        var oldCts = Interlocked.Exchange(ref _cts, new CancellationTokenSource(TimeSpan.FromSeconds(60)));
        if (oldCts != null) { try { oldCts.Cancel(); } catch { } oldCts.Dispose(); }

        var sb = new StringBuilder();
        try
        {
            await foreach (var token in _llm.ChatStreamingAsync(query, _cts.Token))
            {
                if (_cts.Token.IsCancellationRequested) break;
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
        // Capture CTS state atomically — Cancel() may have run and set _cts to null
        var ctsSnapshot = _cts;
        if (ctsSnapshot != null && ctsSnapshot.IsCancellationRequested)
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

        // Cleanup the CTS we created (but not if Cancel() already took ownership)
        Interlocked.CompareExchange(ref _cts, null, ctsSnapshot)?.Dispose();
    }

    [RelayCommand]
    private void Cancel()
    {
        var old = Interlocked.Exchange(ref _cts, null);
        if (old != null) { try { old.Cancel(); } catch { } old.Dispose(); }
    }
}

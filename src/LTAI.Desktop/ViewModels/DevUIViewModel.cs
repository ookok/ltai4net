using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LTAI.Agent.DevUI;

namespace LTAI.Desktop.ViewModels;

public sealed partial class DevUIViewModel : ViewModelBase
{
    private readonly LTAIDevUIService? _devUi;
    private readonly LTAI.AI.EmbeddingClient? _embedder;
    private readonly LTAI.AI.ToolEmbeddingCache? _embedCache;

    public ObservableCollection<LTAIAgentCard> Cards { get; } = new();

    [ObservableProperty]
    private LTAIAgentCard? _selectedCard;

    [ObservableProperty]
    private string _selectedAgentName = "";

    [ObservableProperty]
    private string _statusLine = "";

    [ObservableProperty]
    private string _chatLogText = "";

    [ObservableProperty]
    private string _chatInput = "";

    [ObservableProperty]
    private bool _isSending;

    public ObservableCollection<SpanItem> Spans { get; } = new();

    private CancellationTokenSource? _chatCts;

    public sealed record SpanItem(string Status, string Name, string Source, string Kind, string Duration, string Trace);

    public DevUIViewModel()
    {
        var sp = App.Ltais?.Services;
        _devUi = sp?.GetService(typeof(LTAIDevUIService)) as LTAIDevUIService;
        _embedder = sp?.GetService(typeof(LTAI.AI.EmbeddingClient)) as LTAI.AI.EmbeddingClient;
        _embedCache = sp?.GetService(typeof(LTAI.AI.ToolEmbeddingCache)) as LTAI.AI.ToolEmbeddingCache;

        var cards = _devUi?.ListAgentCards() ?? [];
        foreach (var c in cards) Cards.Add(c);
        StatusLine = $"共 {cards.Count} 个 agent";
    }

    public void SelectCard(LTAIAgentCard card)
    {
        SelectedCard = card;
        SelectedAgentName = card.Name;
    }

    public void RefreshSpans()
    {
        Spans.Clear();
    }

    [RelayCommand]
    private async Task SendChatAsync()
    {
        var text = ChatInput.Trim();
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(SelectedAgentName) || _devUi == null) return;
        ChatInput = "";
        IsSending = true;
        ChatLogText += $"\n[你]: {text}";

        _chatCts?.Dispose();
        _chatCts = new CancellationTokenSource();
        try
        {
            await foreach (var update in _devUi.RunStreamingAsync(SelectedAgentName, text, null, null, _chatCts.Token))
            {
                if (update.Text != null)
                    ChatLogText += update.Text;
            }
            ChatLogText += "\n";
        }
        catch (OperationCanceledException) { ChatLogText += "\n*已取消*"; }
        catch (Exception ex) { ChatLogText += $"\n错误: {ex.Message}"; }
        finally { IsSending = false; }
    }

    [RelayCommand]
    private void CancelChat()
    {
        if (_chatCts != null) { _chatCts.Cancel(); _chatCts.Dispose(); _chatCts = null; }
        IsSending = false;
    }

}

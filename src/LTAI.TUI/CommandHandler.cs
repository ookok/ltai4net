using System.Text;
using LTAI.Agent.Vector;
using LTAI.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LTAI.TUI;

/// <summary>Pure command execution logic extracted from MainWindow.
/// All methods return result strings or delegate to callbacks for UI operations.
/// Fully unit-testable without Terminal.Gui dependencies.</summary>
public sealed class CommandHandler
{
    private readonly List<string> _conv;
    private readonly StringBuilder _markdownCache;
    private int _aiMsgCachePos;
    private readonly string _modelLabelText;
    private readonly IServiceProvider? _sp;
    private int _convCount;
    private readonly Func<string> _getActiveInput;
    private readonly Action<string, string> _addMsg;
    private readonly Action _cancelStream;
    private readonly Action _showSessionPicker;
    private readonly Action _showSearchDialog;
    private readonly Action _handleModelCommand;
    private readonly Action _handleToolCommand;
    private readonly Action _requestStop;
    private readonly Func<Task>? _queryImpact;
    private readonly Func<Task>? _listContracts;

    public int AiMsgCachePos { get => _aiMsgCachePos; set => _aiMsgCachePos = value; }
    public List<string> Conv => _conv;
    public StringBuilder MarkdownCache => _markdownCache;

    public CommandHandler(
        List<string> conv,
        StringBuilder markdownCache,
        int aiMsgCachePos,
        string modelLabelText,
        IServiceProvider? sp,
        Func<string> getActiveInput,
        Action<string, string> addMsg,
        Action cancelStream,
        Action showSessionPicker,
        Action showSearchDialog,
        Action handleModelCommand,
        Action handleToolCommand,
        Action requestStop,
        Func<Task>? queryImpact = null,
        Func<Task>? listContracts = null)
    {
        _conv = conv;
        _markdownCache = markdownCache;
        _aiMsgCachePos = aiMsgCachePos;
        _modelLabelText = modelLabelText;
        _sp = sp;
        _getActiveInput = getActiveInput;
        _addMsg = addMsg;
        _cancelStream = cancelStream;
        _showSessionPicker = showSessionPicker;
        _showSearchDialog = showSearchDialog;
        _handleModelCommand = handleModelCommand;
        _handleToolCommand = handleToolCommand;
        _requestStop = requestStop;
        _queryImpact = queryImpact;
        _listContracts = listContracts;
    }

    /// <summary>Execute a slash command. Returns true if handled.</summary>
    public bool Execute(string cmd)
    {
        switch (cmd)
        {
            case "new":
            case "clear":
                _cancelStream();
                _conv.Clear();
                _markdownCache.Clear();
                _aiMsgCachePos = -1;
                return true;
            case "sessions":
                _showSessionPicker();
                return true;
            case "search":
                _showSearchDialog();
                return true;
            case "retry":
                _addMsg("System", "重发暂未实现");
                return true;
            case "model":
                _handleModelCommand();
                return true;
            case "status":
                _addMsg("System", BuildStatusText());
                return true;
            case "commands":
                _addMsg("System", "**可用命令**\n\n`/model` 配置模型\n`/new` 新建会话\n`/sessions` 历史会话\n`/clear` 清空对话\n`/theme` 切换主题\n`/retry` 重试\n`/status` 状态\n`/help` 帮助\n`/exit` 退出");
                return true;
            case "theme":
                ExecuteTheme();
                return true;
            case "savings":
                _addMsg("System", BuildSavingsText());
                return true;
            case "impact":
                _ = ExecuteImpactAsync();
                return true;
            case "contracts":
                _ = ExecuteContractsAsync();
                return true;
            case "tool":
                _handleToolCommand();
                return true;
            case "help":
                _addMsg("System", "输入 `/commands` 查看全部命令\n快捷键: `Ctrl+N` 新建 · `Ctrl+L` 清空 · `Ctrl+P` 命令\n`Ctrl+R` 搜索 · `Ctrl+↑/↓` 翻阅历史 · `Shift+Enter` 换行\n`/tool list` 当前可用工具 · `/tool disable <name>` 禁用 · `/tool enable <name>` 启用");
                return true;
            case "exit":
                _requestStop();
                return true;
            default:
                return false;
        }
    }

    private string BuildStatusText()
    {
        return $"**状态**\n- 消息数: {_conv.Count}\n- 模型: {_modelLabelText}\n- 会话: {_sp?.GetService<LTAI.Core.Session.SessionManager>()?.CurrentHandle?.Name ?? "—"}\n- 工具调用: {UsageTracker.ToolCalls}";
    }

    private static string BuildSavingsText()
    {
        var saved = TokenSavingsTracker.TotalTokensSaved;
        return $"**Token 节省**\n- 总计节省: {saved:N0} tokens\n- 累计原始: {TokenSavingsTracker.TotalTokensNaive:N0} tokens\n- 节省比例: {TokenSavingsTracker.SavingsRatio:P1}\n- 查询次数: {TokenSavingsTracker.TotalLookups:N0}\n- 平均节省: {TokenSavingsTracker.AvgSavedPerLookup:F0}/次\n- 估算费用: ~${saved * 3e-6:F2}";
    }

    private void ExecuteTheme()
    {
        try
        {
            var themeNames = Terminal.Gui.Configuration.ThemeManager.GetThemeNames().ToList();
            var curTheme = Terminal.Gui.Configuration.ThemeManager.Theme;
            var nextTheme = themeNames.FirstOrDefault(n => n != curTheme) ?? curTheme;
            Terminal.Gui.Configuration.ThemeManager.Theme = nextTheme;
            if (!Terminal.Gui.Configuration.ConfigurationManager.IsEnabled)
                Terminal.Gui.Configuration.ConfigurationManager.Enable(Terminal.Gui.Configuration.ConfigLocations.None);
            Terminal.Gui.Configuration.ConfigurationManager.Apply();
            _addMsg("System", $"主题: {curTheme} → {nextTheme}");
        }
        catch (Exception ex) { _addMsg("System", $"主题切换失败: {ex.Message}"); }
    }

    private async Task ExecuteImpactAsync()
    {
        try
        {
            if (_queryImpact != null) { await _queryImpact(); return; }
            var cg = _sp?.GetService<CgGraph>();
            if (cg == null) { _addMsg("System", "代码图不可用"); return; }
            var parts = _getActiveInput().TrimStart('/').Split(' ');
            var symbol = parts.Length > 1 ? string.Join(" ", parts.Skip(1)) : "";
            if (string.IsNullOrWhiteSpace(symbol))
            {
                _addMsg("System", "用法: `/impact <符号名>` — 分析修改该符号会影响哪些代码");
                return;
            }
            var result = await cg.QueryImpactAsync(symbol).ConfigureAwait(false);
            _addMsg("System", result);
        }
        catch (Exception ex) { _addMsg("System", $"影响分析失败: {ex.Message}"); }
    }

    private async Task ExecuteContractsAsync()
    {
        try
        {
            if (_listContracts != null) { await _listContracts(); return; }
            var contracts = _sp?.GetService<ContractRegistry>();
            if (contracts == null) { _addMsg("System", "合约注册表不可用"); return; }
            _addMsg("System", contracts.ToString());
        }
        catch (Exception ex) { _addMsg("System", $"合约查询失败: {ex.Message}"); }
    }
}

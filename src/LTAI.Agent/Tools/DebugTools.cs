using System.ComponentModel;
using System.Text;
using LTAI.AI;
using LTAI.Core.Debugging;

namespace LTAI.Agent.Tools;

[ToolDomain("debug")]
public sealed class DebugTools
{
    private readonly IDebugBridge? _bridge;

    public DebugTools(IDebugBridge? bridge) => _bridge = bridge;

    private IDebugSession? S => _bridge?.CurrentSession;

    [Description("查询当前调试会话状态：是否在调试、暂停在哪一行、哪个文件。"
        + "适用场景：开始调试前确认调试器是否就绪；调试中断后查看当前暂停位置。"
        + "如果返回[未在调试]，先让用户用调试按钮或 DebugStart 启动调试。")]
    [ToolExample("当前调试状态怎么样")]
    public string DebugStatus()
    {
        var sb = new StringBuilder();
        if (_bridge == null) return "调试桥不可用（仅在桌面端支持调试）。";
        var s = S;
        if (s == null) return "未在调试。请先启动调试会话。";

        sb.AppendLine($"状态: {s.State}");
        if (s.State == DebugState.Paused)
        {
            sb.AppendLine($"暂停位置: {s.CurrentFile}:{s.CurrentLine}");
            sb.AppendLine($"栈帧数: {s.CurrentStack.Length}");
            sb.AppendLine($"变量数: {s.CurrentScope.Length}");
        }
        var bps = _bridge.GetAllBreakpoints();
        sb.AppendLine($"断点数: {bps.Count}");
        return sb.ToString();
    }

    [Description("在指定文件的某行设置断点。支持可选的条件表达式（C# 语法）。"
        + "适用场景：定位 Bug 时在可疑代码行设断点，暂停后检查变量状态。"
        + "条件示例：i == 5, user == null, items.Count == 0, result != null && result.Code != 200")]
    [ToolExample("在 Program.cs 第 42 行设断点")]
    [ToolExample("在 UserService.cs 第 100 行设条件断点，条件是 user == null")]
    public string SetBreakpoint(string file, int line, string? condition = null)
    {
        if (_bridge == null) return "调试桥不可用。";
        _bridge.ToggleBreakpoint(file, line);
        var bp = new DebugBreakpoint(file, line, true, condition);
        return $"已{(condition != null ? $"条件" : "" )}断点: {file}:{line}"
               + (condition != null ? $" (条件: {condition})" : "");
    }

    [Description("移除指定文件某行的断点。"
        + "适用场景：断点已命中完成排查后清理。")]
    [ToolExample("移除 UserService.cs 第 100 行的断点")]
    public string RemoveBreakpoint(string file, int line)
    {
        if (_bridge == null) return "调试桥不可用。";
        _bridge.ToggleBreakpoint(file, line);
        return $"已移除断点: {file}:{line}";
    }

    [Description("列出当前所有已设置的断点。"
        + "适用场景：查看当前项目哪些行有断点，了解调试计划。")]
    [ToolExample("看看有哪些断点")]
    public string ListBreakpoints()
    {
        if (_bridge == null) return "调试桥不可用。";
        var all = _bridge.GetAllBreakpoints();
        if (all.Count == 0) return "当前没有设置断点。";
        var sb = new StringBuilder($"断点列表 ({all.Count}):\n");
        foreach (var bp in all)
            sb.AppendLine($"  - {bp.File}:{bp.Line}" + (bp.Condition != null ? $" [条件: {bp.Condition}]" : ""));
        return sb.ToString();
    }

    [Description("让调试器继续执行（从暂停状态恢复运行）。"
        + "适用场景：断点命中后你已查看变量状态，需要程序继续跑。")]
    [ToolExample("继续运行")]
    public async Task<string> DebugContinue()
    {
        if (S == null) return "未在调试。";
        if (S.State != DebugState.Paused) return "调试器未处于暂停状态（当前: " + S.State + "）。";
        await S.ContinueAsync();
        return "已继续执行。";
    }

    [Description("单步跳过：执行当前行，跳到下一行（不进入函数内部）。"
        + "适用场景：逐行跟踪代码，确认每步行为符合预期。")]
    [ToolExample("单步跳过")]
    public async Task<string> DebugStepOver()
    {
        if (S == null) return "未在调试。";
        if (S.State != DebugState.Paused) return "调试器未处于暂停状态。";
        await S.StepOverAsync();
        return "已单步跳过。";
    }

    [Description("单步进入：进入当前行调用的函数内部。"
        + "适用场景：需要查看被调用函数的内部逻辑。")]
    [ToolExample("进入这个函数看看")]
    public async Task<string> DebugStepInto()
    {
        if (S == null) return "未在调试。";
        if (S.State != DebugState.Paused) return "调试器未处于暂停状态。";
        await S.StepIntoAsync();
        return "已单步进入。";
    }

    [Description("单步跳出：执行完当前函数剩余部分，返回到调用者。"
        + "适用场景：已经确认函数内部没问题，想快速回到调用方。")]
    [ToolExample("跳出当前函数")]
    public async Task<string> DebugStepOut()
    {
        if (S == null) return "未在调试。";
        if (S.State != DebugState.Paused) return "调试器未处于暂停状态。";
        await S.StepOutAsync();
        return "已单步跳出。";
    }

    [Description("停止当前调试会话。"
        + "适用场景：调试完成或确定需要重新启动调试。")]
    [ToolExample("停止调试")]
    public async Task<string> DebugStop()
    {
        if (S == null) return "未在调试。";
        await S.TerminateAsync();
        return "调试会话已终止。";
    }

    [Description("获取当前暂停位置的调用栈帧列表。每帧显示函数名、文件和行号。"
        + "适用场景：理解异常是如何传播的，定位源头。")]
    [ToolExample("看看调用栈")]
    public string DebugGetStack()
    {
        if (S == null) return "未在调试。";
        if (S.State != DebugState.Paused || S.CurrentStack.Length == 0)
            return "暂停后才可查看调用栈。";
        var sb = new StringBuilder("调用栈:\n");
        foreach (var f in S.CurrentStack)
        {
            var loc = f.File != null ? $"{Path.GetFileName(f.File)}:{f.Line}" : "[native]";
            sb.AppendLine($"  ▸ {f.Name} — {loc}");
        }
        return sb.ToString();
    }

    [Description("获取当前暂停位置的局部变量列表。显示变量名、值、类型。"
        + "适用场景：定位 Bug 时检查变量值是否符合预期。")]
    [ToolExample("局部变量有哪些")]
    public string DebugGetVariables()
    {
        if (S == null) return "未在调试。";
        if (S.State != DebugState.Paused || S.CurrentScope.Length == 0)
            return "暂停后才可查看变量。";
        var sb = new StringBuilder("局部变量:\n");
        foreach (var v in S.CurrentScope)
        {
            sb.AppendLine($"  {v.Name} = {v.Value}  ({v.Type})");
        }
        return sb.ToString();
    }

    [Description("在调试会话中求值表达式（C# 语法）。可以计算任意变量或表达式。"
        + "适用场景：调试暂停时临时查看某个表达式的结果，不修改代码加日志。"
        + "示例：user.Name, items.Count, customers.Where(c => c.Age > 18).ToList()")]
    [ToolExample("看看 users.Length 是多少")]
    public async Task<string> DebugEvaluate(string expression)
    {
        if (S == null) return "未在调试。";
        if (S.State != DebugState.Paused) return "暂停后才可求值。";
        var result = await S.EvaluateAsync(expression);
        return result != null ? $"{expression} = {result}" : $"无法求值: {expression}";
    }

    [Description("列出当前进程所有线程。每个线程显示 ID、名称和是否暂停状态。"
        + "适用场景：多线程调试时查看有哪些线程、哪个线程触发了断点。"
        + "如果线程名称为空，表示是后台工作线程。")]
    [ToolExample("当前有哪些线程")]
    public async Task<string> DebugGetThreads()
    {
        if (S == null) return "未在调试。";
        var threads = await S.GetThreadsAsync();
        if (threads.Length == 0) return "没有活跃线程。";
        var sb = new StringBuilder("线程列表:\n");
        foreach (var t in threads)
        {
            var marker = t.IsPaused ? " ← 当前暂停" : "";
            var name = t.Name ?? $"(Thread #{t.Id})";
            sb.AppendLine($"  {(t.IsPaused ? "▶" : " ")} #{t.Id} {name}{marker}");
        }
        sb.AppendLine("\n用 DebugSwitchThread <id> 切换到指定线程查看栈和变量。");
        return sb.ToString();
    }

    [Description("切换到指定线程。切换后自动刷新该线程的调用栈和局部变量。"
        + "适用场景：多线程调试时，断点命中了线程 A，但你想检查线程 B 的变量。"
        + "先用 DebugGetThreads 查看所有线程及其 ID。")]
    [ToolExample("切换到线程 2")]
    [ToolExample("看看另一个线程的状态")]
    public async Task<string> DebugSwitchThread(int threadId)
    {
        if (S == null) return "未在调试。";
        if (S.State != DebugState.Paused) return "暂停后才可切换线程。";
        await S.SwitchThreadAsync(threadId);
        var threadName = (await S.GetThreadsAsync()).FirstOrDefault(t => t.Id == threadId)?.Name ?? $"#{threadId}";
        return $"已切换到线程 {threadName}。\n栈帧数: {S.CurrentStack.Length}\n变量数: {S.CurrentScope.Length}";
    }

    [Description("根据异常信息智能推荐断点位置。传入异常栈或错误描述，"
        + "AI 分析后在最可能的根因代码行设置断点（含条件）。"
        + "适用场景：快速定位异常的根因。参数 errorInfo 可以是异常栈文本或错误描述。")]
    [ToolExample("这个 NullReferenceException 的根因在哪")]
    public async Task<string> DebugAnalyzeFailure(string errorInfo)
    {
        if (_bridge == null) return "调试桥不可用。";

        var lines = errorInfo.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var suggestions = new List<(string File, int Line, string? Condition, string Reason)>();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            var m1 = System.Text.RegularExpressions.Regex.Match(trimmed,
                @"at\s+.+?\(.*?\)\s+in\s+(.+?):line\s+(\d+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m1.Success)
            {
                var file = m1.Groups[1].Value;
                var lineNum = int.Parse(m1.Groups[2].Value);
                var cond = trimmed.Contains("NullReference") || trimmed.Contains(".Value") || trimmed.Contains("null")
                    ? "== null" : null;
                suggestions.Add((file, lineNum, cond, $"异常栈帧: {trimmed}"));
            }
        }

        if (suggestions.Count == 0)
        {
            // 没有匹配到栈帧，在输入中搜索文件名:行号模式
            var m2 = System.Text.RegularExpressions.Regex.Match(errorInfo,
                @"([\w./\\]+\.cs)[:\(](\d+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m2.Success)
            {
                var file = m2.Groups[1].Value;
                var lineNum = int.Parse(m2.Groups[2].Value);
                suggestions.Add((file, Math.Max(1, lineNum - 3), null, $"错误附近: {errorInfo[..Math.Min(100, errorInfo.Length)]}"));
            }
        }

        if (suggestions.Count == 0)
            return "无法从错误信息中解析出断点建议。请提供更完整的异常栈。";

        var sb = new StringBuilder("智能断点建议:\n");
        foreach (var (file, line, cond, reason) in suggestions)
        {
            _bridge.ToggleBreakpoint(file, line);
            sb.AppendLine($"  [已设置] {file}:{line}" + (cond != null ? $" [条件: {cond}]" : ""));
            sb.AppendLine($"    原因: {reason}");
        }
        sb.AppendLine($"\n已自动设置 {suggestions.Count} 个断点。请运行 DebugStatus 确认状态，然后恢复运行。");
        return sb.ToString();
    }
}

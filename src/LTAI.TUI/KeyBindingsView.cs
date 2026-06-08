using Spectre.Console;

namespace LTAI.TUI;

public sealed class KeyBindingsView
{
    public void Render()
    {
        AnsiConsole.Clear();
        AnsiConsole.Write(new Rule("[bold]⌨️ 快捷键一览[/]") { Style = Style.Parse("bold") });

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("[bold]快捷键[/]");
        table.AddColumn("[bold]功能[/]");
        table.AddColumn("[bold]场景[/]");

        var bindings = new (string key, string desc, string ctx)[]
        {
            ("1-0", "切换视图 (1=chat, 2=dashboard, 3=llm, 4=pad, 5=skills, 6=sessions, 7=jobs, 8=memory, 9=workflows, 0=graph)", "全局"),
            ("Enter", "发送消息 / 换行(Shift+Enter)", "聊天"),
            ("Ctrl+C", "取消当前 AI 响应", "聊天"),
            ("Ctrl+E", "展开/折叠推理过程", "聊天"),
            ("Ctrl+U", "清空输入行", "聊天"),
            ("Ctrl+L", "清除历史消息", "聊天"),
            ("Ctrl+Shift+C", "复制最近代码块到剪贴板", "聊天"),
            ("Ctrl+V", "粘贴 (大文本 >3行 弹出预览)", "聊天"),
            ("Ctrl+F", "内联搜索当前消息", "聊天"),
            ("↑/↓", "历史消息 / 切换输入历史", "聊天"),
            ("Shift+↑/↓", "滚动消息历史", "聊天"),
            ("PgUp/PgDn", "上/下翻页", "聊天"),
            ("Alt+Shift+/", "上下文命令面板", "聊天"),
            ("Tab", "切换聊天/文件编辑模式", "文件面板"),
            ("Ctrl+F (Pad)", "文件中搜索", "文件面板"),
            ("F5", "构建当前项目", "文件面板"),
            ("F6", "运行当前文件", "文件面板"),
            ("F7", "测试当前项目", "文件面板"),
            ("D-diff", "Git diff 对比", "文件面板"),
            ("Ctrl+O", "打开文件树选择", "文件面板"),
            ("/?", "打开命令选择器", "全局"),
            ("Esc", "关闭选择器 / 退出视图", "全局"),
            ("Ctrl+Shift+M", "当前 token / 费用统计", "全局"),
            ("/theme", "切换浅色/深色主题", "命令"),
            ("/lang", "切换中/英文界面", "命令"),
            ("/undo", "撤销上一步历史操作", "命令"),
            ("/retry", "重试最后一条 AI 消息", "命令"),
            ("/compact", "压缩当前会话历史", "命令"),
        };

        foreach (var (key, desc, ctx) in bindings)
            table.AddRow($"[cyan]{key.EscapeMarkup()}[/]", desc.EscapeMarkup(), $"[grey]{ctx.EscapeMarkup()}[/]");

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("\n[grey]按任意键返回...[/]");
        Console.ReadKey(true);
    }
}

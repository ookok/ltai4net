using LTAI.Core.Commands;
using Spectre.Console;

namespace LTAI.TUI.Services;

public sealed class FileCommandService : ICommandService
{
    public CommandResult Execute(Command command) => command switch
    {
        LsCommand lc => HandleListDir(lc.Args),
        CdCommand cd => HandleChangeDir(cd.Args),
        PwdCommand => HandlePwd(),
        _ => new SuccessResult("ok"),
    };

    private static CommandResult HandlePwd()
    {
        return new SuccessResult($"当前目录: {Directory.GetCurrentDirectory()}");
    }

    private static CommandResult HandleChangeDir(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new SuccessResult($"当前目录: {Directory.GetCurrentDirectory()}");
        try
        {
            var newDir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), path));
            if (!Directory.Exists(newDir)) return new SuccessResult($"目录不存在: {newDir}");
            Directory.SetCurrentDirectory(newDir);
            return new SuccessResult($"已切换到: {newDir}");
        }
        catch (Exception ex) { return new SuccessResult($"切换失败: {ex.Message}"); }
    }

    private static CommandResult HandleListDir(string path)
    {
        try
        {
            var dir = !string.IsNullOrWhiteSpace(path)
                ? Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), path))
                : Directory.GetCurrentDirectory();

            if (!Directory.Exists(dir)) return new SuccessResult($"[red]目录不存在:[/] {dir}");

            var root = new Tree($"[bold yellow]📂 {dir}[/]");
            var dirs = Directory.GetDirectories(dir)
                .Select(d => (name: Path.GetFileName(d), info: new DirectoryInfo(d)))
                .OrderBy(x => x.name);
            var files = Directory.GetFiles(dir)
                .Select(f => (name: Path.GetFileName(f), info: new FileInfo(f)))
                .OrderBy(x => x.name);

            foreach (var d in dirs)
            {
                var subCount = Directory.GetDirectories(d.info.FullName).Length;
                var fileCount = Directory.GetFiles(d.info.FullName).Length;
                var label = subCount + fileCount > 0
                    ? $"[cyan]📁 {d.name}[/]  [grey]({subCount} 子目录, {fileCount} 文件)[/]"
                    : $"[cyan]📁 {d.name}[/]";
                root.AddNode(label);
            }

            foreach (var f in files)
            {
                var size = f.info.Length switch
                {
                    < 1024 => $"{f.info.Length} B",
                    < 1024 * 1024 => $"{f.info.Length / 1024.0:F1} KB",
                    _ => $"{f.info.Length / (1024.0 * 1024):F1} MB"
                };
                root.AddNode($"[green]📄 {f.name}[/]  [grey]{size}[/]");
            }

            var totalDirs = dirs.Count();
            var totalFiles = files.Count();
            AnsiConsole.Write(root);
            return new SuccessResult($"[grey]共 {totalDirs} 个目录, {totalFiles} 个文件[/]");
        }
        catch (Exception ex)
        {
            return new SuccessResult($"[red]列目录失败:[/] {ex.Message}");
        }
    }
}

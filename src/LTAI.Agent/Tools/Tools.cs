using System.ComponentModel;
using System.Text.RegularExpressions;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.AI;

namespace LTAI.Agent.Tools;

public sealed class FileSystemTools
{
    private readonly string _ws;
    public FileSystemTools(string ws) => _ws = ws;

    [Description("Read a file")]
    public async Task<string> ReadFile([Description("Path")] string path)
    {
        var fp = Path.GetFullPath(Path.Combine(_ws, path));
        if (!fp.StartsWith(_ws, StringComparison.OrdinalIgnoreCase)) return "Error: path escape";
        return await File.ReadAllTextAsync(fp);
    }

    [Description("Write a file")]
    public async Task<string> WriteFile([Description("Path")] string path, [Description("Content")] string content)
    {
        var fp = Path.GetFullPath(Path.Combine(_ws, path));
        if (!fp.StartsWith(_ws, StringComparison.OrdinalIgnoreCase)) return "Error: path escape";
        Directory.CreateDirectory(Path.GetDirectoryName(fp)!);
        await File.WriteAllTextAsync(fp, content);
        return $"Written {content.Length} bytes";
    }

    [Description("List directory")]
    public string[] ListFiles([Description("Path")] string path)
    {
        var fp = Path.GetFullPath(Path.Combine(_ws, path));
        if (!fp.StartsWith(_ws)) return ["Error: path escape"];
        return Directory.Exists(fp) ? Directory.GetFileSystemEntries(fp).Select(Path.GetFileName).OfType<string>().ToArray() : [];
    }
}

public sealed class ShellTools
{
    private readonly string _ws;
    public ShellTools(string ws) => _ws = ws;

    private static readonly Regex[] BlockedPatterns =
    {
        new(@"[;&|`$(){}\[\]<>]", RegexOptions.Compiled),
        new(@"\|\s*(bash|sh|powershell|pwsh|cmd)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"`[^`]+`", RegexOptions.Compiled),
        new(@"\$\([^)]+\)", RegexOptions.Compiled),
        new(@"(curl|wget)\s+\S+\s*\|\s*(bash|sh)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"rm\s+(-rf\s+)?[/\\]", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\beval\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bexec\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    };

    private static readonly HashSet<string> DangerousCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "shutdown","reboot","halt","poweroff","init","kill","pkill",
        "dd","mkfs","fdisk","parted","chmod","chown","passwd","sudo","su","iptables"
    };

    [Description("Execute a shell command and return stdout+stderr")]
    public async Task<string> ExecuteCommand(
        [Description("Command to execute")] string command,
        [Description("Optional timeout in seconds")] int timeoutSec = 60)
    {
        var (allowed, reason) = ValidateCommand(command);
        if (!allowed) return $"Blocked: {reason}";

        var (shell, argPrefix) = OperatingSystem.IsWindows()
            ? ("cmd.exe", "/c")
            : ("/bin/bash", "-c");

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
            var result = await Cli.Wrap(shell)
                .WithArguments($"{argPrefix} \"{command.Replace("\"", "\\\"")}\"")
                .WithWorkingDirectory(_ws)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(cts.Token)
                .ConfigureAwait(false);

            var output = result.StandardOutput;
            if (!string.IsNullOrEmpty(result.StandardError))
                output += $"\nSTDERR:\n{result.StandardError}";
            return output;
        }
        catch (OperationCanceledException)
        {
            return $"Command timed out after {timeoutSec}s";
        }
    }

    private static (bool Allowed, string Reason) ValidateCommand(string cmd)
    {
        if (string.IsNullOrEmpty(cmd)) return (true, "");
        if (cmd.Length > 10_000) return (false, "Command too long");

        var firstWord = cmd.TrimStart().Split(' ', '\t')[0];
        if (DangerousCommands.Contains(firstWord))
            return (false, $"Dangerous command: {firstWord}");

        foreach (var pat in BlockedPatterns)
            if (pat.IsMatch(cmd))
                return (false, $"Injection pattern: {pat}");

        return (true, "");
    }
}

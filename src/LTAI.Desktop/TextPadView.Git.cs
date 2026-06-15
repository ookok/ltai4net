using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace LTAI.Desktop;

partial class TextPadView
{
    private void UpdateGitInfo()
    {
        _ = UpdateGitInfoAsync();
    }

    private async Task UpdateGitInfoAsync()
    {
        try
        {
            var gitDir = FindGitDir(_rootDir);
            if (gitDir == null) { _gitBranchLabel.IsVisible = false; return; }

            var branch = (await RunGitAsync("rev-parse --abbrev-ref HEAD"))?.Trim();
            _gitBranch = branch ?? "unknown";
            _gitBranchLabel.Text = $"🌿 {_gitBranch}";
            _gitBranchLabel.IsVisible = true;

            var status = await RunGitAsync("status --porcelain --untracked-files=normal");
            _gitFileStatus = new Dictionary<string, string>();
            if (status != null)
            {
                foreach (var line in status.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.Length < 4) continue;
                    var state = line[..2].Trim();
                    var file = line[3..].Trim();
                    if (file.StartsWith('"') && file.EndsWith('"')) file = file[1..^1];
                    _gitFileStatus[file] = state;
                }
            }

            _gitCommitBtn.IsVisible = true;
            _gitPullBtn.IsVisible = true;
            _gitPushBtn.IsVisible = true;
            _gitBlameBtn.IsVisible = _currentFile != null;
            RefreshTreeGitStatus();
        }
        catch { _gitBranchLabel.IsVisible = false; }
    }

    private static string? FindGitDir(string dir)
    {
        var d = new DirectoryInfo(dir);
        while (d != null)
        {
            if (Directory.Exists(Path.Combine(d.FullName, ".git"))) return d.FullName;
            d = d.Parent;
        }
        return null;
    }

    private string? RunGit(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("git", args)
            {
                WorkingDirectory = _rootDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = new Process { StartInfo = psi };
            process.Start();
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(5000);
            return process.ExitCode == 0 ? output : null;
        }
        catch { return null; }
    }

    private async Task<string?> RunGitAsync(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("git", args)
            {
                WorkingDirectory = _rootDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = new Process { StartInfo = psi };
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch { return null; }
    }

    private async Task RunGitCmdAsync(string args)
    {
        if (_gitBranch == null) { _statusBar.Text = "⚠️ 不在 Git 仓库中"; return; }
        try
        {
            _buildOutput.IsVisible = false;
            _gitOutputPanel.IsVisible = true;
            ShowGitLoading($"git {args}");
            await Task.Delay(50);

            var psi = new ProcessStartInfo("git", args)
            {
                WorkingDirectory = _rootDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = new Process { StartInfo = psi };
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));

            if (!string.IsNullOrEmpty(output))
                RenderGitOutput(output, isError: false, args);
            else if (!string.IsNullOrEmpty(error))
                RenderGitOutput(error, isError: true, args);
            else
                RenderGitOutput("✅ 执行成功（无输出）", isError: false, args);

            _statusBar.Text = process.ExitCode == 0
                ? $"✅ git {args}: 成功"
                : $"❌ git {args}: {error[..Math.Min(error.Length, 80)]}";
            if (process.ExitCode != 0)
            {
                _lastErrorCommand = $"git {args}";
                _lastError = $"[stderr]\n{error}\n[stdout]\n{output}";
                _errorFixBtn.IsVisible = true;
            }
            UpdateGitInfo();
        }
        catch (Exception ex) { _statusBar.Text = $"❌ git {args}: {ex.Message}"; }
    }

    private void ShowGitLoading(string label)
    {
        _gitOutputPanel.IsVisible = true;
        _gitOutputText.Inlines?.Clear();
        _gitOutputText.Inlines!.Add(new Avalonia.Controls.Documents.Run($"⏳ {label}...")
        {
            Foreground = LtaiTheme.Sbb(LtaiTheme.AccentDNA),
            FontWeight = FontWeight.Bold,
        });
    }

    private void RenderGitOutput(string output, bool isError, string? command = null)
    {
        _gitOutputPanel.IsVisible = true;
        _gitOutputText.Inlines?.Clear();

        if (string.IsNullOrWhiteSpace(output))
        {
            _gitOutputText.Text = isError ? "❌ 命令执行失败" : "✅ 执行成功（无输出）";
            return;
        }

        if (output.Length < 2000 && output.Replace("\r\n", "\n").Split('\n').Take(5).All(l => string.IsNullOrEmpty(l) || (l.Length >= 3 && l[2] == ' ')))
            { RenderGitStatusStructured(output); return; }

        var lines = output.Replace("\r\n", "\n").Split('\n');
        foreach (var line in lines)
        {
            var run = new Avalonia.Controls.Documents.Run(line + "\n");
            if (isError || line.Contains("error:") || line.Contains("fatal:") || line.StartsWith("fatal:"))
                run.Foreground = LtaiTheme.Sbb(LtaiTheme.AccentDanger);
            else if (line.StartsWith("On branch ") || line.StartsWith("HEAD detached at "))
                run.Foreground = LtaiTheme.Sbb(LtaiTheme.AccentDNA);
            else if (line.Contains("nothing to commit") || line.Contains("up-to-date") || line.StartsWith("Already up"))
                run.Foreground = LtaiTheme.Sbb(LtaiTheme.AccentSystem);
            else if (line.Contains("Your branch is ahead"))
                run.Foreground = LtaiTheme.Sbb(LtaiTheme.AccentInfo);
            else if (line.Contains("(use \"git") || line.Contains("(use \"git add") || line.Contains("no changes added"))
                run.Foreground = LtaiTheme.Sbb(Color.Parse("#8b949e"));
            else if (line.TrimStart().StartsWith("modified:") || line.TrimStart().StartsWith("modified:"))
                run.Foreground = LtaiTheme.Sbb(Color.Parse("#d29922"));
            else if (line.TrimStart().StartsWith("new file:"))
                run.Foreground = LtaiTheme.Sbb(LtaiTheme.AccentSystem);
            else if (line.TrimStart().StartsWith("deleted:"))
                run.Foreground = LtaiTheme.Sbb(LtaiTheme.AccentDanger);
            else if (line.TrimStart().StartsWith("renamed:"))
                run.Foreground = LtaiTheme.Sbb(Color.Parse("#d29922"));
            else if (line.StartsWith("\t"))
                run.Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary);
            else if (line.StartsWith("  ") && line.Length > 2)
                run.Foreground = LtaiTheme.Sbb(Color.Parse("#8b949e"));
            else
                run.Foreground = LtaiTheme.Sbb(Color.Parse("#8b949e"));
            _gitOutputText.Inlines!.Add(run);
        }
    }

    private void RenderGitStatusStructured(string status)
    {
        var lines = status.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var rowCount = lines.Length;
        _gitOutputText.Inlines?.Clear();
        _gitOutputText.Inlines!.Add(new Avalonia.Controls.Documents.Run($"📊 {rowCount} 个文件变更\n")
        {
            Foreground = LtaiTheme.Sbb(LtaiTheme.AccentInfo),
            FontWeight = FontWeight.Bold,
        });

        foreach (var line in lines)
        {
            if (line.Length < 4) continue;
            var code = line[..2].Trim();
            var file = line[3..].Trim();
            if (file.StartsWith('"') && file.EndsWith('"')) file = file[1..^1];

            var (icon, color) = code switch
            {
                "M" or "MM" => ("📝", Color.Parse("#d29922")),
                "A" or "A " => ("➕", Color.Parse("#28a745")),
                "D" or "D " => ("🗑", Color.Parse("#f85149")),
                "R" or "R " => ("🔀", Color.Parse("#d29922")),
                "?" or "??" => ("❓", Color.Parse("#8b949e")),
                _ => ("📄", Color.Parse("#8b949e")),
            };

            _gitOutputText.Inlines!.Add(new Avalonia.Controls.Documents.Run($"{icon} {file}\n")
            {
                Foreground = LtaiTheme.Sbb(color),
                FontFamily = LtaiTheme.CodeFont,
                FontSize = 12,
            });
        }
    }

    private void ShowGitCommitDialog()
    {
        if (_gitBranch == null) { _statusBar.Text = "⚠️ 不在 Git 仓库中"; return; }
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is not Window owner) return;

        var msgBox = new TextBox { AcceptsReturn = true, MinHeight = 60, MinWidth = 300, MaxWidth = 500, PlaceholderText = "Commit message..." };
        var stageAll = new CheckBox { Content = "暂存所有变更 (git add -A)", IsChecked = true };
        var yesBtn = new Button { Content = "提交", Width = 80 };
        var noBtn = new Button { Content = "取消", Width = 80 };
        var dialog = new Window
        {
            Title = $"💾 Commit — 🌿 {_gitBranch}", Width = 450, Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new(15), Spacing = 8,
                Children =
                {
                    new TextBlock { Text = $"提交到 {_gitBranch}", FontWeight = FontWeight.Bold },
                    msgBox, stageAll,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { yesBtn, noBtn } },
                }
            }
        };
        yesBtn.Click += async (_, _) =>
        {
            var msg = msgBox.Text?.Trim();
            if (string.IsNullOrEmpty(msg)) return;
            var stage = stageAll.IsChecked == true ? "add -A && " : "";
            var result = await RunGitAsync($"{stage}commit -m \"{msg.Replace("\"", "\\\"")}\"");
            _statusBar.Text = result != null ? $"✅ 已提交: {msg[..Math.Min(msg.Length, 50)]}" : "❌ 提交失败";
            dialog.Close();
            UpdateGitInfo();
        };
        noBtn.Click += (_, _) => dialog.Close();
        dialog.ShowDialog(owner);
    }

    private async void ToggleBlame()
    {
        if (_currentFile == null) return;
        if (_blameData != null) { _blameData = null; _statusBar.Text = "Blame 已关闭"; return; }
        try
        {
            var output = await RunGitAsync($"blame --line-porcelain \"{_currentFile}\"");
            if (output == null) { _statusBar.Text = "❌ Blame 不可用"; return; }
            _blameData = new Dictionary<string, string>();
            string? currentAuthor = null;
            int currentLine = 0;
            foreach (var line in output.Split('\n'))
            {
                if (line.StartsWith("author ")) currentAuthor = line[7..].Trim();
                else if (line.Length > 0 && !line.StartsWith('\t') && !line.Contains(' '))
                {
                    var parts = line.Split(' ');
                    if (parts.Length >= 4 && int.TryParse(parts[2], out var ln)) currentLine = ln;
                }
                else if (line.StartsWith('\t'))
                {
                    if (currentLine > 0 && currentAuthor != null) _blameData[currentLine.ToString()] = currentAuthor;
                    currentLine = 0;
                }
            }
            _statusBar.Text = $"👤 Blame: 已加载 {_blameData.Count} 行作者信息";
        }
        catch (Exception ex) { _statusBar.Text = $"❌ Blame 失败: {ex.Message}"; }
    }

    private void RefreshTreeGitStatus()
    {
        try { ApplyGitStatusToTree(_tree.Items, ""); }
        catch { }
    }

    private void ApplyGitStatusToTree(ItemCollection items, string prefix)
    {
        foreach (var item in items.OfType<TreeViewItem>())
        {
            item.Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary);
            if (item.Tag is string path)
            {
                var relPath = Path.GetRelativePath(_rootDir, path).Replace('\\', '/');
                if (_gitFileStatus.TryGetValue(relPath, out var status))
                {
                    var borderColor = status switch
                    {
                        "M" or "M " => LtaiTheme.AccentDNA,
                        "A" or "A " => LtaiTheme.AccentSystem,
                        "D" or "D " => LtaiTheme.AccentDanger,
                        "R" or "R " or "?" or "? " => LtaiTheme.AccentWarning,
                        _ => (Color?)null
                    };
                    item.BorderThickness = borderColor.HasValue ? new Thickness(3, 0, 0, 0) : new Thickness(0);
                    item.BorderBrush = borderColor.HasValue ? LtaiTheme.Sbb(borderColor.Value) : null;
                }
                else item.BorderThickness = new Thickness(0);
            }
            if (item.Items.Count > 0) ApplyGitStatusToTree(item.Items, prefix + "/");
        }
    }
}

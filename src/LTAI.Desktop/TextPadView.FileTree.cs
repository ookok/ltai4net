using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;

namespace LTAI.Desktop;

partial class TextPadView
{
    private void ToggleEdit()
    {
        _isReadOnly = !_isReadOnly;
        _editor.IsReadOnly = _isReadOnly;
        _toggleBtn.Content = _isReadOnly ? "🔓 编辑" : "🔒 只读";
        _editor.Background = _isReadOnly
            ? LtaiTheme.Sbb(LtaiTheme.Bg)
            : LtaiTheme.Sbb(LtaiTheme.SurfaceOverlay);
    }

    private void BuildTree(ItemCollection items, string dir)
    {
        try
        {
            foreach (var d in Directory.GetDirectories(dir).OrderBy(Path.GetFileName))
            {
                var node = new TreeViewItem
                {
                    Header = $"📁 {Path.GetFileName(d)}",
                    Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
                    Tag = d,
                    ContextMenu = MakeDirContextMenu(d),
                };
                AddDragSupport(node);
                var capturedNode = node;
                var capturedDir = d;
                capturedNode.Expanded += (_, _) => { if (capturedNode.Items.Count == 0) BuildTree(capturedNode.Items, capturedDir); };
                items.Add(node);
            }
            foreach (var f in Directory.GetFiles(dir).Where(f => TextExts.Contains(Path.GetExtension(f))).OrderBy(Path.GetFileName))
            {
                var icon = f.EndsWith(".md") ? "📝" : f.EndsWith(".cs") ? "📄" : f.EndsWith(".py") ? "🐍" : "📄";
                var node = new TreeViewItem { Header = $"{icon} {Path.GetFileName(f)}", Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary), Tag = f, ContextMenu = MakeFileContextMenu(f) };
                AddDragSupport(node);
                items.Add(node);
            }
        }
        catch { }
    }

    private static void AddDragSupport(TreeViewItem node) { }

    private ContextMenu MakeTreeContextMenu()
    {
        var menu = new ContextMenu();
        var multiDelete = new MenuItem { Header = "🗑️ 批量删除选中", IsEnabled = false };
        int? lastMultiCount = null;
        _tree.SelectionChanged += (_, _) =>
        {
            var count = _tree.SelectedItems.Count;
            multiDelete.Header = count > 1 ? $"🗑️ 批量删除 ({count} 项)" : "🗑️ 批量删除选中";
            multiDelete.IsEnabled = count > 1;
            if (count > 1 && count != lastMultiCount) { lastMultiCount = count; _statusBar.Text = $"✅ 已选中 {count} 项"; }
        };
        multiDelete.Click += async (_, _) =>
        {
            var top = TopLevel.GetTopLevel(this) as Window;
            if (top == null) return;
            var count = _tree.SelectedItems.Count;
            var dlg = new Window { Title = $"批量删除 {count} 项", Width = 400, Height = 150, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var yesBtn = new Button { Content = "删除", Width = 80 };
            var noBtn = new Button { Content = "取消", Width = 80 };
            dlg.Content = new StackPanel { Margin = new(15), Spacing = 8, Children = { new TextBlock { Text = $"确认删除 {count} 个选中的文件/目录？", TextWrapping = TextWrapping.Wrap }, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { yesBtn, noBtn } } } };
            var confirmed = false;
            yesBtn.Click += (_, _) => { confirmed = true; dlg.Close(); };
            noBtn.Click += (_, _) => dlg.Close();
            await dlg.ShowDialog(top);
            if (!confirmed) return;
            int deleted = 0, failed = 0;
            foreach (var sel in _tree.SelectedItems.OfType<TreeViewItem>().ToList())
            {
                if (sel.Tag is string fp)
                {
                    try { if (Directory.Exists(fp)) Directory.Delete(fp, true); else if (File.Exists(fp)) File.Delete(fp); deleted++; }
                    catch { failed++; }
                }
            }
            _statusBar.Text = failed > 0 ? $"✅ 已删除 {deleted} 项，{failed} 项失败" : $"✅ 已删除 {deleted} 项";
            RefreshTree();
        };
        menu.Items.Add(multiDelete);
        return menu;
    }

    private ContextMenu MakeFileContextMenu(string path)
    {
        var menu = new ContextMenu();
        menu.Items.Add(WithClick(new MenuItem { Header = "🤖 问 AI" }, (_, _) => AskAiWithContext(path)));
        menu.Items.Add(WithClick(new MenuItem { Header = "📋 复制路径" }, (_, _) =>
        {
            try { CopyToClipboard(path); }
            catch { }
        }));

        var rename = new MenuItem { Header = "✏️ 重命名" };
        rename.Click += async (_, _) =>
        {
            var top = TopLevel.GetTopLevel(this) as Window;
            if (top == null) return;
            var input = new TextBox { Text = Path.GetFileName(path) };
            var ok = new Button { Content = "确定" };
            var dlg = new Window { Title = "重命名", Width = 350, Height = 120, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new StackPanel { Margin = new(15), Spacing = 8, Children = { input, ok } } };
            string? result = null;
            ok.Click += (_, _) => { result = input.Text?.Trim(); dlg.Close(); };
            await dlg.ShowDialog(top);
            if (string.IsNullOrEmpty(result)) return;
            try { File.Move(path, Path.Combine(Path.GetDirectoryName(path)!, result)); RefreshTree(); }
            catch (Exception ex) { _statusBar.Text = $"❌ 重命名失败: {ex.Message}"; }
        };
        menu.Items.Add(rename);

        var del = new MenuItem { Header = "🗑️ 删除" };
        del.Click += async (_, _) =>
        {
            var top = TopLevel.GetTopLevel(this) as Window;
            if (top == null) return;
            var yes = new Button { Content = "删除" }; var no = new Button { Content = "取消" };
            var dlg = new Window { Title = "确认删除", Width = 350, Height = 130, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new StackPanel { Margin = new(15), Spacing = 8, Children = { new TextBlock { Text = $"确认删除 {Path.GetFileName(path)}?" }, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { yes, no } } } } };
            var confirmed = false;
            yes.Click += (_, _) => { confirmed = true; dlg.Close(); }; no.Click += (_, _) => dlg.Close();
            await dlg.ShowDialog(top);
            if (!confirmed) return;
            try { File.Delete(path); RefreshTree(); _statusBar.Text = $"✅ 已删除: {path}"; }
            catch (Exception ex) { _statusBar.Text = $"❌ 删除失败: {ex.Message}"; }
        };
        menu.Items.Add(del);
        return menu;
    }

    private ContextMenu MakeDirContextMenu(string path)
    {
        var menu = new ContextMenu();
        menu.Items.Add(WithClick(new MenuItem { Header = "🤖 问 AI (文件夹)" }, (_, _) => AskAiWithContext(path)));
        menu.Items.Add(WithClick(new MenuItem { Header = "📋 复制路径" }, (_, _) =>
        {
            try { CopyToClipboard(path); }
            catch { }
        }));

        var newFile = new MenuItem { Header = "📄 新建文件" };
        newFile.Click += (_, _) =>
        {
            for (int n = 1; n < 1000; n++)
            {
                var name = $"新建文件{n}.txt"; var fp = Path.Combine(path, name);
                if (!File.Exists(fp)) { try { File.WriteAllText(fp, ""); RefreshTree(); _statusBar.Text = $"✅ 已创建: {name}"; } catch (Exception ex) { _statusBar.Text = $"❌ 创建失败: {ex.Message}"; } break; }
            }
        };
        menu.Items.Add(newFile);

        var newDir = new MenuItem { Header = "📁 新建文件夹" };
        newDir.Click += (_, _) =>
        {
            for (int n = 1; n < 1000; n++)
            {
                var name = $"新建文件夹{n}"; var fp = Path.Combine(path, name);
                if (!Directory.Exists(fp)) { try { Directory.CreateDirectory(fp); RefreshTree(); _statusBar.Text = $"✅ 已创建: {name}"; } catch (Exception ex) { _statusBar.Text = $"❌ 创建失败: {ex.Message}"; } break; }
            }
        };
        menu.Items.Add(newDir);

        menu.Items.Add(new MenuItem { Header = "-" });
        var files = Directory.GetFiles(path);
        var allFiles = new HashSet<string>(files.Select(f => Path.GetFileName(f) ?? ""), StringComparer.OrdinalIgnoreCase);
        if (allFiles.Any(f => f?.EndsWith(".csproj") == true || f?.EndsWith(".sln") == true))
        {
            menu.Items.Add(WithClick(new MenuItem { Header = "🛠 Build" }, (_, _) => RunShell("dotnet build")));
            menu.Items.Add(WithClick(new MenuItem { Header = "🧪 Test" }, (_, _) => RunShell("dotnet test")));
            menu.Items.Add(WithClick(new MenuItem { Header = "▶ Run" }, (_, _) => RunShell("dotnet run")));
        }
        else if (allFiles.Contains("Cargo.toml"))
            menu.Items.Add(WithClick(new MenuItem { Header = "🛠 cargo build" }, (_, _) => RunShell("cargo build")));
        else if (allFiles.Contains("package.json"))
            menu.Items.Add(WithClick(new MenuItem { Header = "🛠 npm run build" }, (_, _) => RunShell("npm run build")));
        if (Directory.Exists(Path.Combine(path, ".git")) || FindGitDir(path) != null)
        {
            menu.Items.Add(WithClick(new MenuItem { Header = "🌿 git status" }, async (_, _) => await RunGitCmdAsync("status")));
            menu.Items.Add(WithClick(new MenuItem { Header = "💾 git commit..." }, (_, _) => ShowGitCommitDialog()));
        }
        return menu;
    }

    private void AskAiWithContext(string path)
    {
        var hasSelection = !string.IsNullOrWhiteSpace(_selectedText);
        var isDir = Directory.Exists(path);
        var isCode = !isDir && CodeExts.Contains(Path.GetExtension(path));

        string prompt;
        if (hasSelection && isCode)
            prompt = $"解释以下代码（来自 {Path.GetFileName(path)}），重点关注内存管理、性能问题和潜在 Bug：\n\n```\n{_selectedText}\n```";
        else if (hasSelection)
            prompt = $"分析以下文本（来自 {Path.GetFileName(path)}）：\n\n{_selectedText}";
        else if (!isDir)
        {
            try
            {
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length < 50000)
                {
                    var content = File.ReadAllText(path);
                    var lang = (Path.GetExtension(path)?.ToLowerInvariant()) switch
                    {
                        ".cs" => "C#", ".py" => "Python", ".js" => "JavaScript",
                        ".ts" => "TypeScript", ".go" => "Go", ".rs" => "Rust",
                        ".java" => "Java", ".md" => "Markdown", ".json" => "JSON",
                        _ => "代码",
                    };
                    var truncated = content.Length > 4000 ? content[..4000] + "\n// ...（截断）" : content;
                    prompt = $"分析以下 {lang} 文件（{Path.GetFileName(path)}），列出其作用、结构、潜在 Bug 和改进建议：\n\n```\n{truncated}\n```";
                }
                else
                    prompt = $"分析文件 {Path.GetFileName(path)} 的作用（大小：{FormatSize(fi.Length)}），给出审查建议和改进方向。";
            }
            catch { prompt = $"分析文件 {Path.GetFileName(path)} 的作用和代码质量，给出详细建议。"; }
        }
        else
        {
            try
            {
                var files = Directory.GetFiles(path, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(path, f)).Take(100);
                var dirs = Directory.GetDirectories(path, "*", SearchOption.AllDirectories).Select(d => Path.GetRelativePath(path, d) + "/").Take(30);
                var structure = string.Join("\n", dirs.Concat(files).OrderBy(x => x));
                prompt = $"分析以下目录结构（{Path.GetFileName(path)}），给出架构建议、代码组织改进和潜在问题：\n\n{structure}";
            }
            catch { prompt = $"分析目录 {Path.GetFileName(path)} 的结构、主要模块和架构建议。"; }
        }
        AskAiRequested?.Invoke(prompt);
    }

    private static MenuItem WithClick(MenuItem item, EventHandler<EventArgs> handler) { item.Click += handler; return item; }

    private void RefreshTree()
    {
        var expanded = new HashSet<string>();
        CaptureExpanded(_tree.Items, "", expanded);
        _tree.Items.Clear();
        BuildTree(_tree.Items, _rootDir);
        RestoreExpanded(_tree.Items, expanded);
    }

    private void OnTreeSelectionChanged(object? s, SelectionChangedEventArgs e)
    {
        if (_tree.SelectedItems.Count == 1 && _tree.SelectedItem is TreeViewItem item && item.Tag is string path && File.Exists(path))
            OpenFile(path);
    }

    private void ToggleSplitView()
    {
        _showSplit = !_showSplit;
        if (_showSplit)
        {
            if (_splitEditor == null)
            {
                _splitEditor = new TextEditor
                {
                    IsReadOnly = true, ShowLineNumbers = true,
                    FontFamily = LtaiTheme.CodeFont, FontSize = 13,
                    Background = LtaiTheme.Sbb(LtaiTheme.Bg),
                    Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
                    LineNumbersForeground = LtaiTheme.Sbb(LtaiTheme.TextDim),
                };
                try { _splitEditor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("C#"); } catch { }
                _splitEditor.TextArea.TextView.CurrentLineBackground = LtaiTheme.Sbb(LtaiTheme.SurfaceOverlay);
                _splitEditor.TextArea.TextView.CurrentLineBorder = new Pen(LtaiTheme.Sbb(LtaiTheme.CurrentLineBorder), 1);
                _splitEditor.TextArea.SelectionBrush = LtaiTheme.Sbb(LtaiTheme.SelectionBg);
                _splitEditor.TextArea.SelectionCornerRadius = 2;
            }
            _editorGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(4)));
            _editorGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            _editorGrid.Children.Add(new GridSplitter { Width = 4, Background = LtaiTheme.Sbb(LtaiTheme.Border), HorizontalAlignment = HorizontalAlignment.Stretch });
            Grid.SetColumn(_splitEditor, 2);
            _editorGrid.Children.Add(_splitEditor);
            if (_currentFile != null) { try { _splitEditor.Load(_currentFile); } catch { } }
        }
        else
        {
            _editorGrid.Children.RemoveRange(1, _editorGrid.Children.Count - 1);
            _editorGrid.ColumnDefinitions.RemoveRange(1, _editorGrid.ColumnDefinitions.Count - 1);
        }
    }

    private void OpenFile(string path)
    {
        try
        {
            _currentFile = path;
            var fi = new FileInfo(path);
            if (fi.Length > MaxEditorSize)
            {
                _editor.Text = $"[文件过大: {FormatSize(fi.Length)}]\nAvaloniaEdit 将整文件读入内存 (UTF-16 编码, 实际内存 ≈ 文件大小 × 2)。\n\n建议使用 ReadFileContent 工具按需读取。\n\n路径: {path}";
                _editor.IsReadOnly = true; _isReadOnly = true; _toggleBtn.Content = "🔓 编辑";
                _statusBar.Text = $"{path}  |  {FormatSize(fi.Length)}  |  过大，无法编辑";
                return;
            }
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
            _editor.Load(fs);
            var ext = Path.GetExtension(path);
            var hlName = ext switch
            {
                ".cs" => "C#", ".py" => "Python", ".js" => "JavaScript", ".ts" => "TypeScript",
                ".go" => "Go", ".rs" => "Rust", ".java" => "Java", ".xml" => "XML",
                ".html" or ".htm" => "HTML", ".css" => "CSS", ".json" => "JSON",
                ".md" => "Markdown", ".yaml" or ".yml" => "YAML",
                ".sh" or ".bash" => "PowerShell", _ => null,
            };
            try { _editor.SyntaxHighlighting = hlName != null ? HighlightingManager.Instance.GetDefinition(hlName) : null; } catch { }
            _editor.IsReadOnly = !CodeExts.Contains(ext);
            _isReadOnly = _editor.IsReadOnly;
            _toggleBtn.Content = _isReadOnly ? "🔓 编辑" : "🔒 只读";
            _statusBar.Text = $"{path}  |  {FormatSize(fi.Length)}  |  ~{(fi.Length > 0 ? (int)(fi.Length / 60) : 0)} 行  |  {DetectEncoding(path)}";
            RefreshSymbols();
            DetectProject();
            UpdateGitInfo();
            StartFileWatcher(path);
        }
        catch { _statusBar.Text = $"无法打开: {path}"; }
    }

    public void OpenFileAndScrollTo(string path, int line)
    {
        OpenFile(path);
        if (line > 0 && line <= _editor.Document.LineCount)
        {
            _editor.TextArea.Caret.Line = line; _editor.TextArea.Caret.Column = 1;
            _editor.TextArea.Caret.BringCaretToView(); _editor.Focus();
        }
    }

    private void StartFileWatcher(string path)
    {
        try
        {
            _fileWatcher?.Dispose();
            var dir = Path.GetDirectoryName(path);
            if (dir == null) return;
            _fileWatcher = new FileSystemWatcher(dir, Path.GetFileName(path)) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size, EnableRaisingEvents = true };
            _fileWatcher.Changed += OnExternalFileChange;
        }
        catch { }
    }

    private DateTime _lastFileChange = DateTime.MinValue;

    private void OnExternalFileChange(object s, FileSystemEventArgs e)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastFileChange).TotalMilliseconds < 1000) return;
        _lastFileChange = now;
        var timer = new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(200), Avalonia.Threading.DispatcherPriority.Background, (s, _) =>
        {
            ((Avalonia.Threading.DispatcherTimer?)s)?.Stop();
            if (_currentFile == e.FullPath && File.Exists(e.FullPath)) OpenFile(e.FullPath);
        });
        timer.Start();
    }

    private void RunSyntaxCheck()
    {
        if (_currentFile == null) { _statusBar.Text = "请先打开一个文件"; return; }
        try
        {
            var ext = Path.GetExtension(_currentFile);
            if (ext != ".cs" && ext != ".py" && ext != ".js" && ext != ".ts" && ext != ".go" && ext != ".rs" && ext != ".java")
            { _statusBar.Text = "语法检查仅支持 .cs/.py/.js/.ts/.go/.rs/.java"; return; }
            using var parser = new LTAI.Agent.Tools.TreeSitterParser();
            var symbols = parser.ExtractSymbols(_editor.Text, ext);
            if (symbols.Count == 0) _statusBar.Text = "⚠️ 未解析出符号，可能存在语法错误";
            else _statusBar.Text = $"✅ 语法检查通过: {symbols.Count} 个符号 ({string.Join(", ", symbols.Select(s => s.kind).Distinct())})";
        }
        catch (Exception ex) { _statusBar.Text = $"❌ 语法错误: {ex.Message}"; }
    }

    private void SaveFile()
    {
        if (_currentFile == null) return;
        try { File.WriteAllText(_currentFile, _editor.Text); _statusBar.Text = $"✅ 已保存: {_currentFile}"; }
        catch (Exception ex) { _statusBar.Text = $"❌ 保存失败: {ex.Message}"; }
    }

    private void StartWatching(string dir)
    {
        try
        {
            _watcher?.Dispose();
            _watcher = new FileSystemWatcher(dir) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite, IncludeSubdirectories = true, EnableRaisingEvents = false };
            _watcher.Created += OnFileChanged; _watcher.Deleted += OnFileChanged;
            _watcher.Renamed += OnFileRenamed; _watcher.Changed += OnFileChanged;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[TextPadView] {ex.Message}"); }
    }

    private void StopWatching()
    {
        if (_watcher != null) { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); _watcher = null; }
        _fileWatcher?.Dispose(); _fileWatcher = null;
    }

    private DateTime _lastTreeRefresh = DateTime.MinValue;

    private void OnFileChanged(object s, FileSystemEventArgs e)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastTreeRefresh).TotalMilliseconds < 500) return;
        _lastTreeRefresh = now;
        var name = Path.GetFileName(e.Name ?? "");
        if (name.StartsWith('.')) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var expandedDirs = new HashSet<string>(); CaptureExpanded(_tree.Items, "", expandedDirs);
                _tree.Items.Clear(); BuildTree(_tree.Items, _rootDir);
                RestoreExpanded(_tree.Items, expandedDirs);
            }
            catch { }
        });
    }

    private void OnFileRenamed(object s, RenamedEventArgs e) => OnFileChanged(s, e);

    private void CaptureExpanded(ItemCollection items, string prefix, HashSet<string> result)
    {
        foreach (var item in items.OfType<TreeViewItem>())
        {
            if (item.IsExpanded && item.Tag is string tag && Directory.Exists(tag)) result.Add(tag);
            if (item.Items.Count > 0) CaptureExpanded(item.Items, prefix, result);
        }
    }

    private void RestoreExpanded(ItemCollection items, HashSet<string> expanded)
    {
        foreach (var item in items.OfType<TreeViewItem>())
        {
            if (item.Tag is string tag && expanded.Contains(tag))
            {
                item.IsExpanded = true;
                if (item.Items.Count == 0 && Directory.Exists(tag)) BuildTree(item.Items, tag);
            }
            if (item.Items.Count > 0) RestoreExpanded(item.Items, expanded);
        }
    }

    private static void CopyToClipboard(string text)
    {
        if (OperatingSystem.IsWindows())
        {
            using var p = new Process();
            p.StartInfo = new ProcessStartInfo("powershell", $"-command \"Set-Clipboard -Value '{text.Replace("'", "''")}'\"")
            { CreateNoWindow = true, UseShellExecute = false };
            p.Start();
        }
        else if (OperatingSystem.IsLinux())
        {
            using var p = new Process();
            p.StartInfo = new ProcessStartInfo("xclip", $"-selection clipboard")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardInput = true };
            p.Start();
            p.StandardInput.Write(text);
            p.StandardInput.Close();
        }
        else if (OperatingSystem.IsMacOS())
        {
            using var p = new Process();
            p.StartInfo = new ProcessStartInfo("pbcopy")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardInput = true };
            p.Start();
            p.StandardInput.Write(text);
            p.StandardInput.Close();
        }
    }
}

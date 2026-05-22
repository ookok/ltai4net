using Spectre.Console.Rendering;
using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;
using LTAI.AI.Governors;
using LTAI.DNA;
using LTAI.Tools.Reasoning;
using LTAI.Tools.CodeEngine;
using LTAI.Core.Configuration;
using LTAI.Core.System;
using Microsoft.Extensions.Options;

namespace LTAI.TUI;

public enum TuiView { Dashboard, Chat, Code, Git, Help, Session, LLMConfig, Models, Service }

public enum TuiTheme { Dark, Light, HighContrast }

public sealed class TuiApp
{
    private readonly LivingTreeSystem _lts;
    private readonly DNAOrchestrator? _dna;
    private readonly ReasoningOrchestrator? _reasoning;
    private readonly MultiLangCodeAnalyzer? _analyzer;
    private readonly LLMConfigPanel _llmConfig;
    private readonly SessionTracker _session;
    private readonly TaskPulseRenderer _taskPulse;
    private readonly TuiInputBox _inputBox;
    private readonly InnovationViews _innovation;
    private readonly PromptLibrary _prompts;
    private readonly TaskDagView _dagView;
    private readonly ContextWindowView _ctxView;
    private readonly NotificationService _notify;
    private readonly SessionSearch _search;
    private readonly ServiceManager? _service;
    private readonly ModelManager? _modelMgr;

    private TuiView _currentView = TuiView.Dashboard;
    private TuiTheme _theme = TuiTheme.Dark;
    private readonly List<string> _activityLog = new();
    private readonly string _projectRoot;
    private bool _running = true;
    private readonly List<(string role, string text)> _chatHistory = new();
    private string? _lastAnalyzedFile;
    private CodeAnalysisResult? _lastAnalysisResult;
    private readonly List<string> _knowledgeItems = new();
    private bool _showLLMPanel;
    private string? _loadedFileContent;
    private string? _loadedFilePath;
    private bool _diffEnabled = true;
    private bool _diffSplitView;

    private static readonly string[] TaskPhases = { "input", "context", "routing", "reasoning", "generation", "review", "output" };
    private string _currentPhase = "";

    public TuiApp(
        LivingTreeSystem lts,
        DNAOrchestrator? dna = null,
        ReasoningOrchestrator? reasoning = null,
        MultiLangCodeAnalyzer? analyzer = null,
        IOptions<LTAIOptions>? options = null,
        ServiceManager? service = null,
        ModelManager? modelMgr = null)
    {
        _lts = lts;
        _dna = dna;
        _reasoning = reasoning;
        _analyzer = analyzer;
        _service = service;
        _modelMgr = modelMgr;
        _projectRoot = Directory.GetCurrentDirectory();
        _llmConfig = new LLMConfigPanel(options);
        _session = new SessionTracker();
        _taskPulse = new TaskPulseRenderer();
        _inputBox = new TuiInputBox(_projectRoot);
        _innovation = new InnovationViews();
        _prompts = new PromptLibrary(_projectRoot);
        _dagView = new TaskDagView();
        _ctxView = new ContextWindowView();
        _notify = new NotificationService();
        _search = new SessionSearch(_chatHistory);
        _activityLog.Add($"LTAI TUI started at {DateTime.Now:HH:mm:ss}");
    }

    public async Task RunAsync()
    {
        AnsiConsole.Clear();
        ApplyTheme();
        AnsiConsole.Write(new FigletText("LTAI TUI").Color(Color.Cyan1));

        try
        {
            var currentFont = ConsoleFont.GetCurrentFont();
            AnsiConsole.MarkupLine($"[grey]Font: {currentFont}[/]");
        }
        catch { /* non-fatal */ }

        AnsiConsole.Write(new Rule("LivingTree AI Agent — Dev Console"));
        AnsiConsole.MarkupLine("[grey]Recommended: Maple Mono NF | Press ? for help, Ctrl+T theme[/]");

        while (_running)
        {
            await RenderAsync();
            if (!_running) break;
            var key = await ReadKeyAsync();
            await HandleKeyAsync(key);
        }

        AnsiConsole.Clear();
        AnsiConsole.MarkupLine("[yellow]LTAI TUI exited. Session stats saved.[/]");
    }

    private void ApplyTheme()
    {
        if (_theme == TuiTheme.HighContrast)
        {
            Console.BackgroundColor = ConsoleColor.Black;
            Console.ForegroundColor = ConsoleColor.White;
        }
        else
        {
            Console.ResetColor();
        }
    }

    private Color ThemeColor(Color dark, Color light, Color hc) => _theme switch
    {
        TuiTheme.Light => light,
        TuiTheme.HighContrast => hc,
        _ => dark
    };

    private async Task RenderAsync()
    {
        AnsiConsole.Clear();
        RenderHeader();

        if (_showLLMPanel)
        {
            AnsiConsole.Write(_llmConfig.Render());
            AnsiConsole.Write(new Rule());
        }

        switch (_currentView)
        {
            case TuiView.Dashboard: RenderDashboard(); break;
            case TuiView.Chat: await RenderChatAsync(); break;
            case TuiView.Code: RenderCodeView(); break;
            case TuiView.Git: RenderGitView(); break;
            case TuiView.Help: RenderHelp(); break;
            case TuiView.Session: RenderSessionView(); break;
            case TuiView.LLMConfig: RenderLLMView(); break;
            case TuiView.Models: RenderModelsView(); break;
            case TuiView.Service: RenderServiceView(); break;
        }

        AnsiConsole.Write(new Rule());
        RenderFooter();
    }

    private void RenderHeader()
    {
        var dnaStatus = _dna != null
            ? $"[cyan]DNA:{_dna.Consciousness.State.Level}[/] [green]Gen:{_dna.GetStatus().Generation}[/]"
            : "[grey]DNA:off[/]";
        var sysInfo = $"[green]Mode:{_lts.Mode}[/] [blue]v5.5[/]";
        var proc = $"[grey]CPU:{Environment.ProcessorCount}c MEM:{Environment.WorkingSet / 1024 / 1024}MB[/]";
        AnsiConsole.MarkupLine($"[bold cyan]LTAI Dev Console[/]  {dnaStatus}  {sysInfo}  {proc}");
    }

    private void RenderDashboard()
    {
        var mainGrid = new Grid().AddColumns(3);
        mainGrid.AddRow(CreateDnaPanel(), CreateSystemPanel(), CreateHealthPanel());
        AnsiConsole.Write(mainGrid);

        AnsiConsole.Write(new Grid().AddColumns(2)
            .AddRow(_session.RenderPanel(_lts, _dna), CreateCommandsPanel()));

        if (_dna != null)
            AnsiConsole.Write(CreateBarChart());

        var tasks = RenderTaskPanel();
        AnsiConsole.Write(tasks);

        if (_session.ActiveTasks.Count > 0)
            AnsiConsole.Write(_dagView.RenderFlowChart(_session.ActiveTasks));

        AnsiConsole.Write(_ctxView.Render(_session));

        if (!string.IsNullOrEmpty(_currentPhase))
            AnsiConsole.Write(_taskPulse.RenderPhaseIndicator(_currentPhase, TaskPhases));
    }

    private IRenderable CreateDnaPanel()
    {
        if (_dna == null) return MkPanel("DNA module not loaded", "[cyan]DNA[/]");
        var c = _dna.Consciousness.State;
        var e = _dna.SelfEvo;
        var l = _dna.Life;
        return MkPanel($$"""
            [cyan]Consciousness:[/] {{c.Level}} ({{c.AwarenessScore:F2}})
            [green]Evolution:[/] active
            [magenta]Safety:[/] {{_dna.Safety.Posture}}
            [yellow]Biorhythm:[/] {{l.Biorhythm.Phase}} E:{{l.Biorhythm.EnergyLevel:F2}}
            [blue]Dopamine:[/] {{l.Hormones.Dopamine:F2}} Serotonin:{{l.Hormones.Serotonin:F2}}
            [grey]Thoughts:[/] {{c.ActiveThoughts.Count}} Habits:{{_dna.Life.Habits.Count}}
            """, "[cyan]DNA[/]");
    }

    private IRenderable CreateSystemPanel()
    {
        var p = System.Diagnostics.Process.GetCurrentProcess();
        return MkPanel($$"""
            [green]Mode:[/] {{_lts.Mode}}
            [green]DNA:[/] {{(_dna != null ? "Active" : "Disabled")}}
            [green]Reasoning:[/] {{(_reasoning != null ? "Active" : "Disabled")}}
            [grey]PID:[/] {{p.Id}} Th:{{p.Threads.Count}}
            [grey]MEM:[/] {{p.WorkingSet64/1024/1024}}MB
            [grey]Uptime:[/] {{Fmt(DateTime.Now-p.StartTime)}}
            """, "[green]System[/]");
    }

    private IRenderable CreateHealthPanel()
    {
        return MkPanel($$"""
            [blue]Runtime:[/] .NET {{Environment.Version}}
            [blue]GC Memory:[/] {{GC.GetTotalMemory(false)/1024/1024}}MB
            [grey]Heap:[/] {{GC.GetGCMemoryInfo().HeapSizeBytes/1024/1024}}MB
            """, "[blue]Health[/]");
    }

    private IRenderable CreateCommandsPanel()
    {
        return MkPanel("""
            [yellow]1-9[/] Dashboard/Chat/Code/Git/Help/Session/LLM/Models/Service
            [yellow]c[/] Chat   [yellow]l[/] LLM config   [yellow]t[/] Thought chain
            [yellow]a[/] Analyze   [yellow]g[/] Git   [yellow]q[/] Quit
            [yellow]Enter[/] Send   [yellow]Esc[/] Back
            [yellow]Ctrl+V[/] Paste path   [yellow]@path[/] Load file
            [yellow]d[/] Toggle diff   [yellow]s[/] Split diff
            [yellow]e[/] Export session   [yellow]m[/] Memory consolidate
            [yellow]k[/] Knowledge graph   [yellow]b[/] Multi-model branch
            [yellow]p[/] Prompt templates   [yellow]n[/] Notifications
            [yellow]Ctrl+F[/] Search   [yellow]F3[/] Next match
            [yellow]Ctrl+T[/] Theme ({_theme})
            """, "[yellow]Commands[/]");
    }

    private IRenderable RenderTaskPanel()
    {
        if (_session.ActiveTasks.Count == 0)
            return new Markup("[grey](No active tasks)[/]");

        var grid = new Grid().AddColumns(2);
        grid.AddRow(
            MkPanel(_taskPulse.RenderTasks(_session.ActiveTasks), "[cyan]Tasks[/]"),
            _innovation.RenderThoughtChain());
        return grid;
    }

    private IRenderable CreateBarChart()
    {
        if (_dna == null) return new Text("");
        return new BarChart().Width(60).Label("[bold]DNA State[/]")
            .AddItem("Awareness", _dna.Consciousness.State.AwarenessScore, Color.Cyan1)
            .AddItem("Fitness", _dna.GetStatus().FitnessScore, Color.Green)
            .AddItem("Energy", _dna.Life.Biorhythm.EnergyLevel, Color.Yellow)
            .AddItem("Dopamine", _dna.Life.Hormones.Dopamine, Color.Magenta1);
    }

    private async Task RenderChatAsync()
    {
        AnsiConsole.MarkupLine("[bold cyan]Chat Mode[/] — Enter to send, Esc to exit, Ctrl+V paste, @path load file");
        AnsiConsole.Write(new Rule("[cyan]Conversation[/]"));

        if (_chatHistory.Count == 0)
            AnsiConsole.MarkupLine("[grey]Start typing. Use @path to load files, @@folder to load folders.[/]");
        else
            foreach (var (role, text) in _chatHistory)
                AnsiConsole.MarkupLine($"[bold {RoleColor(role)}]{role}:[/] {text}");

        if (_knowledgeItems.Count > 0)
            AnsiConsole.Write(_innovation.RenderKnowledgePreview("", _knowledgeItems));

        AnsiConsole.Write(new Rule());

        var input = await _inputBox.ReadInputAsync("You");
        if (string.IsNullOrWhiteSpace(input)) return;

        _chatHistory.Add(("You", input));
        _activityLog.Add($"[Chat] Q: {input[..Math.Min(input.Length, 80)]}");

        if (input.StartsWith("@") || input.Contains("[File:") || input.Contains("[Folder:"))
        {
            _knowledgeItems.Add(Path.GetFileName(input));
            if (_knowledgeItems.Count > 10) _knowledgeItems.RemoveAt(0);

            // Extract actual file path for diff context
            var pathMatch = System.Text.RegularExpressions.Regex.Match(input, @"@(\S+)");
            if (pathMatch.Success)
            {
                var fp = pathMatch.Groups[1].Value.Trim();
                if (!File.Exists(fp)) fp = Path.Combine(_projectRoot, fp);
                if (File.Exists(fp))
                {
                    _loadedFilePath = fp;
                    _loadedFileContent = File.ReadAllText(fp);
                }
            }
        }

        _session.AddTask("chat", "running");
        _currentPhase = "generation";
        var startTime = DateTime.Now;

        var renderer = new StreamRenderer(_loadedFileContent);

        if (!_diffEnabled)
            renderer.ToggleDiffMode();
        if (_diffSplitView)
            renderer.CycleDiffMode();
        try
        {
            var stream = _lts.StreamChatAsync(input);
            await renderer.RenderStreamAsync(stream);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error: {ex.Message}[/]");
        }

        _currentPhase = "";
        var fullResponse = renderer.GetFullText();
        _chatHistory.Add(("LTAI", fullResponse));
        _activityLog.Add($"[Chat] A: {fullResponse[..Math.Min(fullResponse.Length, 80)]}");
        _innovation.RecordInteraction(input, fullResponse);

        var task = _session.ActiveTasks.Find(t => t.Name == "chat" && t.Status == "running");
        if (task != null) { task.Status = "done"; task.CompletedAt = DateTime.Now; task.Result = "completed"; }

        var latency = (DateTime.Now - startTime).TotalMilliseconds;
        _session.RecordTurn(input.Length / 4, fullResponse.Length / 4, latency);

        _innovation.AddThought("input", input, ThoughtType.Action);
        _innovation.AddThought("response", fullResponse[..Math.Min(fullResponse.Length, 120)], ThoughtType.Reasoning);

        _notify.Notify("LTAI", $"Response ready ({fullResponse.Length} chars, {latency:F0}ms)");

        AnsiConsole.MarkupLine("[grey](Press any key to continue)[/]");
        while (!Console.KeyAvailable) await Task.Delay(50);
        Console.ReadKey(true);
    }

    private void RenderCodeView()
    {
        AnsiConsole.MarkupLine($"[bold cyan]Code View[/] — {_projectRoot}");
        AnsiConsole.Write(new Rule());
        var tree = new Tree($"[yellow]{new DirectoryInfo(_projectRoot).Name}[/]");
        AddDirectory(tree, new DirectoryInfo(_projectRoot), 0, 2);
        AnsiConsole.Write(tree);

        if (_lastAnalyzedFile != null && _lastAnalysisResult != null)
        {
            AnsiConsole.Write(new Rule("[green]Last Analysis[/]"));
            AnsiConsole.MarkupLine($"[green]File:[/] {_lastAnalyzedFile}");
            AnsiConsole.MarkupLine($"[grey]Lines:{_lastAnalysisResult.TotalLines} Fn:{_lastAnalysisResult.Functions.Count} Cls:{_lastAnalysisResult.Classes.Count} Cx:{_lastAnalysisResult.Complexity}[/]");
        }
    }

    private void RenderGitView()
    {
        AnsiConsole.MarkupLine("[bold cyan]Git View[/]");
        AnsiConsole.Write(new Rule());
        try
        {
            var status = RunGit("status --short");
            var branch = RunGit("branch --show-current").Trim();
            AnsiConsole.MarkupLine($"[yellow]Branch:[/] {branch}");
            if (string.IsNullOrWhiteSpace(status))
                AnsiConsole.MarkupLine("[green]Working tree clean[/]");
            else
            {
                AnsiConsole.Write(new Rule("[yellow]Changes[/]"));
                foreach (var l in status.Split('\n').Where(x => !string.IsNullOrWhiteSpace(x)))
                    AnsiConsole.MarkupLine($"[grey]{l}[/]");
            }
            AnsiConsole.Write(new Rule("[yellow]Recent[/]"));
            foreach (var l in RunGit("log --oneline -10").Split('\n').Where(x => !string.IsNullOrWhiteSpace(x)).Take(10))
                AnsiConsole.MarkupLine($"[grey]{l}[/]");
        }
        catch (Exception ex) { AnsiConsole.MarkupLine($"[red]{ex.Message}[/]"); }
    }

    private void RenderSessionView()
    {
        AnsiConsole.MarkupLine("[bold cyan]Session View[/]");
        AnsiConsole.Write(new Rule());
        AnsiConsole.Write(_session.RenderPanel(_lts, _dna));
        AnsiConsole.Write(new Rule("[cyan]Task Timeline[/]"));
        AnsiConsole.Write(_taskPulse.RenderTasks(_session.ActiveTasks));
        AnsiConsole.Write(new Rule("[blue]Innovation[/]"));
        AnsiConsole.Write(_innovation.RenderInnovationSuggestions());
    }

    private void RenderLLMView()
    {
        AnsiConsole.MarkupLine("[bold cyan]LLM Configuration[/]");
        AnsiConsole.Write(new Rule());
        AnsiConsole.Write(_llmConfig.Render());
    }

    private void RenderModelsView()
    {
        AnsiConsole.MarkupLine("[bold cyan]Model Registry[/]");
        AnsiConsole.Write(new Rule());

        if (_modelMgr == null)
        {
            AnsiConsole.MarkupLine("[red]Model manager not available[/]");
            return;
        }

        var models = _modelMgr.ListAll();
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[cyan]Provider[/]")
            .AddColumn("[yellow]Tier[/]")
            .AddColumn("[white]Model[/]")
            .AddColumn("[grey]Capabilities[/]");

        foreach (var m in models.OrderBy(m => m.Provider).Take(40))
        {
            table.AddRow(
                new Markup($"[cyan]{m.Provider}[/]"),
                new Markup($"[yellow]{m.TierName}[/]"),
                new Markup($"[white]{m.ModelName}[/]"),
                new Markup($"[grey]{string.Join(", ", m.Capabilities.Take(4))}[/]"));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[grey]Total: {models.Count} models across {models.Select(m => m.Provider).Distinct().Count()} providers[/]");
        AnsiConsole.Write(new Rule());
        AnsiConsole.MarkupLine("[grey]y[/] Sync info  [grey]f[/] Filter  [grey]Esc[/] Back");
    }

    private async void RenderServiceView()
    {
        AnsiConsole.MarkupLine("[bold cyan]Service Management[/]");
        AnsiConsole.Write(new Rule());

        if (_service == null)
        {
            AnsiConsole.MarkupLine("[red]Service manager not available[/]");
            return;
        }

        if (!_service.IsWindows)
        {
            AnsiConsole.MarkupLine("[yellow]Windows Service management only available on Windows.[/]");
            AnsiConsole.MarkupLine("[grey]On Linux/macOS, use systemd or launchd directly.[/]");
            return;
        }

        var status = await _service.StatusAsync();
        AnsiConsole.MarkupLine($"[cyan]Service:[/] LTAIService (LivingTree AI Agent)");
        AnsiConsole.MarkupLine(status.Success
            ? $"[green]Status:[/] {status.Output}"
            : $"[red]Status:[/] {status.Message}");

        AnsiConsole.Write(new Rule());
        AnsiConsole.MarkupLine("[yellow]i[/] Install  [yellow]u[/] Uninstall  [yellow]s[/] Start  [yellow]t[/] Stop  [yellow]r[/] Restart  [yellow]Esc[/] Back");
    }

    private void RenderHelp()
    {
        AnsiConsole.Write(MkPanel("""
            [bold cyan]LTAI TUI — Commands[/]

            [yellow]Views (1-9):[/]
              1 Dashboard  2 Chat  3 Code  4 Git
              5 Help  6 Session  7 LLM Config
              8 Models  9 Service

            [yellow]Chat:[/]
              Enter  - Send   Esc - Exit
              Ctrl+V - Paste file/folder path
              @path  - Load file content
              @@path - Load folder structure
              ↑↓     - History navigation

            [yellow]LLM Control:[/]
              l      - Toggle LLM panel
              ↑↓     - Switch model (in LLM view)
              +/-    - Adjust temperature
              ←→     - Double/halve max tokens
              m      - Quick cycle model

            [yellow]Innovation:[/]
              t      - Toggle thought chain
              k      - Knowledge preview
              h      - History replay

            [yellow]Global:[/]
              r      - Refresh   ? - Help   q - Quit
            """, "[cyan]Help[/]"));
    }

    private void RenderFooter()
    {
        var name = _currentView switch
        {
            TuiView.Dashboard => "Dashboard", TuiView.Chat => "Chat", TuiView.Code => "Code",
            TuiView.Git => "Git", TuiView.Help => "Help", TuiView.Session => "Session",
            TuiView.LLMConfig => "LLM Config", TuiView.Models => "Models", TuiView.Service => "Service",
            _ => ""
        };
        AnsiConsole.MarkupLine($"[grey]View: {name} | Session: {_session.SessionId} | Turns: {_session.TotalTurns} | Tokens: {_session.TotalTokens} | ? help | q quit[/]");
    }

    private async Task<ConsoleKeyInfo> ReadKeyAsync()
    {
        while (!Console.KeyAvailable) await Task.Delay(100);
        return Console.ReadKey(true);
    }

    private async Task HandleKeyAsync(ConsoleKeyInfo key)
    {
        if (_showLLMPanel && (_currentView == TuiView.Dashboard || _currentView == TuiView.LLMConfig))
        {
            _llmConfig.HandleKey(key);
        }

        switch (key.Key)
        {
            case ConsoleKey.D1 or ConsoleKey.NumPad1: _currentView = TuiView.Dashboard; break;
            case ConsoleKey.D2 or ConsoleKey.NumPad2: _currentView = TuiView.Chat; break;
            case ConsoleKey.D3 or ConsoleKey.NumPad3: _currentView = TuiView.Code; break;
            case ConsoleKey.D4 or ConsoleKey.NumPad4: _currentView = TuiView.Git; break;
            case ConsoleKey.D5 or ConsoleKey.NumPad5: _currentView = TuiView.Help; break;
            case ConsoleKey.D6 or ConsoleKey.NumPad6: _currentView = TuiView.Session; break;
            case ConsoleKey.D7 or ConsoleKey.NumPad7: _currentView = TuiView.LLMConfig; break;
            case ConsoleKey.D8 or ConsoleKey.NumPad8: _currentView = TuiView.Models; break;
            case ConsoleKey.D9 or ConsoleKey.NumPad9: _currentView = TuiView.Service; break;
            case ConsoleKey.C when key.Modifiers == 0: _currentView = TuiView.Chat; break;
            case ConsoleKey.L when key.Modifiers == 0: _showLLMPanel = !_showLLMPanel; _currentView = _showLLMPanel ? TuiView.LLMConfig : _currentView; break;
            case ConsoleKey.M when key.Modifiers == 0: _llmConfig.CycleModel(); _activityLog.Add($"[LLM] Switched to {_llmConfig.SelectedModel}"); break;
            case ConsoleKey.T when key.Modifiers == 0: _innovation.ToggleThoughtChain(); break;
            case ConsoleKey.D when key.Modifiers == 0: _diffEnabled = !_diffEnabled; _activityLog.Add($"[Diff] mode: {(_diffEnabled ? "on" : "off")}"); break;
            case ConsoleKey.S when key.Modifiers == 0: _diffSplitView = !_diffSplitView; break;
            case ConsoleKey.E when key.Modifiers == 0: ExportSession(); break;
            case ConsoleKey.M when key.Modifiers == 0: await MemoryConsolidateAsync(); break;
            case ConsoleKey.K when key.Modifiers == 0: await KnowledgeGraphPreviewAsync(); break;
            case ConsoleKey.B when key.Modifiers == 0: await MultiModelBranchAsync(); break;
            case ConsoleKey.P when key.Modifiers == 0: await PromptTemplateAsync(); break;
            case ConsoleKey.N when key.Modifiers == 0: _notify.Enabled = !_notify.Enabled; _activityLog.Add($"[Notify] {(_notify.Enabled ? "on" : "off")}"); break;
            case ConsoleKey.T when key.Modifiers == ConsoleModifiers.Control: _theme = (TuiTheme)(((int)_theme + 1) % 3); _activityLog.Add($"[Theme] {_theme}"); break;
            case ConsoleKey.F when key.Modifiers == ConsoleModifiers.Control: _search.Search(); break;
            case ConsoleKey.F3 when key.Modifiers == 0: _search.NextMatch(); break;
            case ConsoleKey.F3 when key.Modifiers == ConsoleModifiers.Shift: _search.PrevMatch(); break;
            case ConsoleKey.G: _currentView = TuiView.Git; break;
            case ConsoleKey.Oem2 or ConsoleKey.Divide: _currentView = TuiView.Help; break;
            case ConsoleKey.Q: _running = false; break;
            case ConsoleKey.Enter when _currentView == TuiView.Chat:
            case ConsoleKey.Enter when _currentView == TuiView.Dashboard:
            case ConsoleKey.Enter when _currentView == TuiView.Session:
                _currentView = TuiView.Chat;
                await HandleChatInputAsync();
                break;
            case ConsoleKey.Escape when _currentView == TuiView.Chat: _currentView = TuiView.Dashboard; break;
            case ConsoleKey.Escape when _showLLMPanel: _showLLMPanel = false; break;
            case ConsoleKey.A when _currentView == TuiView.Code: await PromptAnalyzeFileAsync(); break;

            case ConsoleKey.I when _currentView == TuiView.Service: await ServiceActionAsync("install"); break;
            case ConsoleKey.U when _currentView == TuiView.Service: await ServiceActionAsync("uninstall"); break;
            case ConsoleKey.S when _currentView == TuiView.Service: await ServiceActionAsync("start"); break;
            case ConsoleKey.T when _currentView == TuiView.Service: await ServiceActionAsync("stop"); break;
            case ConsoleKey.R when _currentView == TuiView.Service: await ServiceActionAsync("restart"); break;
            case ConsoleKey.F when _currentView == TuiView.Models: await FilterModelsAsync(); break;
            case ConsoleKey.Y when _currentView == TuiView.Models: await SyncModelsAsync(); break;
        }
    }

    private async Task HandleChatInputAsync()
    {
        if (_currentView != TuiView.Chat) return;
        await Task.CompletedTask;
    }

    private async Task PromptAnalyzeFileAsync()
    {
        var filePath = AnsiConsole.Ask<string>("[cyan]File path:[/] ");
        if (string.IsNullOrWhiteSpace(filePath)) return;
        if (!File.Exists(filePath)) { filePath = Path.Combine(_projectRoot, filePath); if (!File.Exists(filePath)) { AnsiConsole.MarkupLine("[red]Not found[/]"); return; } }
        if (_analyzer == null) { AnsiConsole.MarkupLine("[red]Analyzer unavailable[/]"); return; }
        await AnsiConsole.Status().StartAsync("Analyzing...", async _ =>
        {
            var code = await File.ReadAllTextAsync(filePath);
            _lastAnalysisResult = await _analyzer.Analyze(code, LanguageRegistry.Detect(filePath));
            _lastAnalyzedFile = filePath;
        });
        _activityLog.Add($"[Code] Analyzed {Path.GetFileName(filePath)}");
    }

    private void ExportSession()
    {
        var exportDir = Path.Combine(_projectRoot, ".livingtree", "exports");
        Directory.CreateDirectory(exportDir);
        var fileName = $"session-{_session.SessionId}.md";
        var filePath = Path.Combine(exportDir, fileName);

        var md = new StringBuilder();
        md.AppendLine($"# LTAI Session Export");
        md.AppendLine($"- **Session**: {_session.SessionId}");
        md.AppendLine($"- **Date**: {_session.StartedAt:yyyy-MM-dd HH:mm:ss}");
        md.AppendLine($"- **Turns**: {_session.TotalTurns}");
        md.AppendLine($"- **Tokens**: in={_session.InputTokens} out={_session.OutputTokens} total={_session.TotalTokens}");
        md.AppendLine($"- **Latency**: {_session.AvgLatencyMs:F0}ms avg");
        md.AppendLine($"- **Tasks**: {_session.ActiveTasks.Count(t => t.Status == "done")} completed");
        md.AppendLine();

        md.AppendLine("## Conversation");
        foreach (var (role, text) in _chatHistory)
        {
            md.AppendLine($"### {role}");
            md.AppendLine(text);
            md.AppendLine();
        }

        if (_innovation.GetThoughts().Count > 0)
        {
            md.AppendLine("## Thought Chain");
            foreach (var t in _innovation.GetThoughts())
                md.AppendLine($"- [{t.Type}] {t.Step}: {t.Content}");
        }

        File.WriteAllText(filePath, md.ToString());
        _activityLog.Add($"[Export] Session exported to {fileName}");
        AnsiConsole.MarkupLine($"[green]Exported:[/] {filePath}");
    }

    private async Task MemoryConsolidateAsync()
    {
        if (_dna == null)
        {
            AnsiConsole.MarkupLine("[red]DNA module not loaded, memory consolidation unavailable[/]");
            return;
        }

        AnsiConsole.MarkupLine("[cyan]Consolidating memories...[/]");
        await AnsiConsole.Status().Spinner(Spinner.Known.Dots).StartAsync("Consolidating...", async _ =>
        {
            var life = _dna.Life;
            life.ProcessInteraction("memory_consolidation", "Triggered manual consolidation", "positive");
            await _dna.SelfEvo.EvolveAsync(new Dictionary<string, double> { ["stability"] = 0.9, ["efficiency"] = 0.8 }, CancellationToken.None);
            await Task.Delay(500);
            _dna.Consciousness.State.LastReflection = DateTime.UtcNow;
            _dna.Consciousness.State.Level = LTAI.DNA.Models.ConsciousnessLevel.Reflective;
        });

        _activityLog.Add("[Memory] Consolidation triggered");
        AnsiConsole.MarkupLine($"[green]Memory consolidated:[/] {_dna.Life.Habits.Count} habits, " +
            $"consciousness={_dna.Consciousness.State.Level}, fitness={_dna.GetStatus().FitnessScore:F3}");
    }

    private async Task KnowledgeGraphPreviewAsync()
    {
        if (_knowledgeItems.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No knowledge items loaded. Use @path to load files first.[/]");
            return;
        }

        AnsiConsole.MarkupLine("[cyan]Knowledge Graph Preview[/]");
        AnsiConsole.Write(new Rule());

        var tree = new Tree("[yellow]Knowledge Graph[/]");
        var root = tree.AddNode("[green]Session Context[/]");

        foreach (var item in _knowledgeItems)
        {
            var node = root.AddNode($"[white]{item}[/]");
            if (File.Exists(item))
            {
                node.AddNode($"[grey]Size: {new FileInfo(item).Length / 1024}KB[/]");
                var ext = Path.GetExtension(item);
                if (ext is ".cs" or ".py" or ".js" or ".ts")
                {
                    try
                    {
                        var code = File.ReadAllText(item);
                        var functions = System.Text.RegularExpressions.Regex.Matches(code, ext == ".cs" ? @"\b(\w+)\s*\(.*\)\s*\{?" : @"def (\w+)\(");
                        node.AddNode($"[blue]{functions.Count} functions detected[/]");
                    }
                    catch { /* non-fatal */ }
                }
            }
            else
            {
                node.AddNode("[grey](text input)[/]");
            }
        }

        AnsiConsole.Write(tree);

        if (_loadedFilePath != null && _loadedFileContent != null)
        {
            AnsiConsole.Write(new Rule("[yellow]Document Preview[/]"));
            var preview = _loadedFileContent.Length > 500 ? _loadedFileContent[..497] + "..." : _loadedFileContent;
            AnsiConsole.MarkupLine($"[white]{EscapeM(preview)}[/]");
        }

        _activityLog.Add($"[KG] Previewed {_knowledgeItems.Count} items");
    }

    private async Task MultiModelBranchAsync()
    {
        var lastQuery = _chatHistory.LastOrDefault().role == "You" ? _chatHistory.Last().text : null;
        if (string.IsNullOrWhiteSpace(lastQuery))
        {
            AnsiConsole.MarkupLine("[grey]No query to branch. Send a message first.[/]");
            return;
        }

        var providers = _llmConfig.GetProviders();
        if (providers.Count < 2)
        {
            AnsiConsole.MarkupLine("[red]Need at least 2 providers configured for branching[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[cyan]Branching query across {Math.Min(providers.Count, 3)} models...[/]");
        AnsiConsole.Write(new Rule());

        var selectedProviders = providers.Take(3).ToList();
        var results = new Dictionary<string, string>();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[grey]Model[/]")
            .AddColumn("[grey]Response[/]");

        await AnsiConsole.Live(table).StartAsync(async ctx =>
        {
            var tasks = selectedProviders.Select(async p =>
            {
                var sb = new StringBuilder();
                await foreach (var token in _lts.StreamWithModelAsync(lastQuery, p))
                {
                    sb.Append(token);
                    table.AddRow(
                        new Markup($"[cyan]{p}[/]"),
                        new Markup($"[white]{EscapeM(sb.ToString()[..Math.Min(sb.Length, 100)])}[/]"));
                    ctx.Refresh();
                }
                results[p] = sb.ToString();
            });

            await Task.WhenAll(tasks);
        });

        AnsiConsole.Write(new Rule("[green]Final Comparison[/]"));
        foreach (var (model, response) in results)
        {
            var panel = new Panel(new Markup($"[white]{EscapeM(response[..Math.Min(response.Length, 500)])}[/]"))
            {
                Header = new PanelHeader($"[cyan]{model}[/]"),
                Border = BoxBorder.Rounded
            };
            AnsiConsole.Write(panel);
        }
        _activityLog.Add($"[Branch] Compared {results.Count} models on last query");
    }

    private async Task PromptTemplateAsync()
    {
        AnsiConsole.MarkupLine("[cyan]Prompt Templates[/]");
        var choices = new List<string> { "[green]Load template[/]", "[yellow]Save new[/]", "[grey]Cancel[/]" };
        var choice = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("Action:").AddChoices(choices));

        if (choice.Contains("Load"))
        {
            var template = _prompts.SelectPrompt();
            if (template != null)
            {
                AnsiConsole.MarkupLine($"[green]Loaded:[/] {template[..Math.Min(template.Length, 100)]}...");
                AnsiConsole.MarkupLine("[grey]Press Enter in chat to use this template[/]");
                await Task.Delay(500);
            }
        }
        else if (choice.Contains("Save"))
        {
            await _prompts.AddTemplateAsync();
        }
    }

    private static void AddDirectory(IHasTreeNodes parent, DirectoryInfo dir, int depth, int maxDepth)
    {
        if (depth >= maxDepth) return;
        foreach (var sd in dir.GetDirectories().Take(8))
        {
            if (sd.Name.StartsWith('.') || sd.Name is "bin" or "obj") continue;
            var n = parent.AddNode($"[blue]{sd.Name}/[/]");
            AddDirectory(n, sd, depth + 1, maxDepth);
        }
        foreach (var f in dir.GetFiles().Take(15).OrderBy(x => x.Extension).ThenBy(x => x.Name))
        {
            var icon = f.Extension switch { ".cs" => "[green]C#[/]", ".py" => "[yellow]Py[/]", ".js" => "[yellow]JS[/]", ".ts" => "[blue]TS[/]", ".md" => "[cyan]MD[/]", ".csproj" => "[magenta]prj[/]", ".sln" => "[magenta]sln[/]", _ => "   " };
            parent.AddNode($"{icon} {f.Name}");
        }
    }

    private static Panel MkPanel(string content, string header)
    {
        var p = new Panel(content); p.Header = new PanelHeader(header); return p;
    }

    private static Panel MkPanel(IRenderable content, string header)
    {
        var p = new Panel(content); p.Header = new PanelHeader(header); return p;
    }

    private static string RoleColor(string role) => role == "You" ? "green" : "cyan1";
    private static string Fmt(TimeSpan ts) => ts.TotalHours >= 1 ? $"{ts.Hours}h{ts.Minutes}m" : $"{ts.Minutes}m{ts.Seconds}s";
    private static string EscapeM(string text) => text.Replace("[", "[[").Replace("]", "]]");

    private static string RunGit(string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", args)
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = System.Diagnostics.Process.Start(psi); if (p == null) return "";
        var o = p.StandardOutput.ReadToEnd(); p.WaitForExit(5000); return o;
    }

    private async Task ServiceActionAsync(string action)
    {
        if (_service == null) return;

        var result = await AnsiConsole.Status().StartAsync($"Running {action}...", async _ =>
        {
            return action switch
            {
                "install" => await _service.InstallAsync(),
                "uninstall" => await _service.UninstallAsync(),
                "start" => await _service.StartAsync(),
                "stop" => await _service.StopAsync(),
                "restart" => await _service.RestartAsync(),
                _ => new ServiceResult { Success = false, Message = $"Unknown action: {action}" }
            };
        });

        if (result.Success)
            AnsiConsole.MarkupLine($"[green]{action} succeeded:[/] {result.Message}");
        else
            AnsiConsole.MarkupLine($"[red]{action} failed:[/] {result.Message}");

        if (!string.IsNullOrWhiteSpace(result.Output))
            AnsiConsole.MarkupLine($"[grey]{result.Output}[/]");

        _activityLog.Add($"[Service] {action}: {(result.Success ? "OK" : "FAIL")}");
        await Task.Delay(1500);
    }

    private async Task FilterModelsAsync()
    {
        if (_modelMgr == null) return;
        var keyword = AnsiConsole.Ask<string>("[cyan]Filter keyword:[/] ");
        if (string.IsNullOrWhiteSpace(keyword)) return;

        var results = _modelMgr.Search(keyword);
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine($"[bold cyan]Models matching '{keyword}' ({results.Count})[/]");
        AnsiConsole.Write(new Rule());

        foreach (var m in results)
        {
            AnsiConsole.MarkupLine($"  [cyan]{m.Provider}[/]/[yellow]{m.TierName}[/] → [white]{m.ModelName}[/] [grey]({string.Join(", ", m.Capabilities)})[/]");
        }

        AnsiConsole.MarkupLine("[grey](Press any key to return)[/]");
        while (!Console.KeyAvailable) await Task.Delay(50);
        Console.ReadKey(true);
    }

    private async Task SyncModelsAsync()
    {
        if (_modelMgr == null) return;
        var info = _modelMgr.SyncInfo();
        AnsiConsole.MarkupLine($"[green]Synced:[/] {info.GetType().GetProperty("total_providers")?.GetValue(info)} providers");
        _activityLog.Add("[Models] Synced registry info");
        await Task.Delay(800);
    }
}

using System.Text.Json.Nodes;
using LTAI.Core.Debugging;

namespace LTAI.Desktop.Debugging;

public sealed class DapSession : IDebugSession
{
    private DapClient? _client;
    private DebugState _state = DebugState.Idle;
    private int _currentThreadId;
    public DebugStackFrame[] CurrentStack { get; private set; } = [];
    public DebugVariable[] CurrentScope { get; private set; } = [];
    public DebugState State => _state;
    public int CurrentLine { get; private set; }
    public string? CurrentFile { get; private set; }

    public event Action<DebugState>? StateChanged;
    public event Action<int, string?>? Stopped;
    public event Action<string>? OutputReceived;

    public async Task LaunchAsync(string command, string[] args, string workingDir, JsonObject? launchArgs = null)
    {
        _state = DebugState.Launching;
        StateChanged?.Invoke(_state);

        _client = new DapClient(command, args, workingDir);
        _client.OutputReceived += msg => OutputReceived?.Invoke(msg);
        _client.EventReceived += OnEvent;
        _client.Disconnected += () =>
        {
            _state = DebugState.Terminated;
            StateChanged?.Invoke(_state);
        };

        await _client.CallAsync("initialize", new JsonObject
        {
            ["clientID"] = "ltai-desktop",
            ["adapterID"] = "dotnet",
            ["linesStartAt1"] = true,
            ["columnsStartAt1"] = true,
            ["supportsRunInTerminalRequest"] = false,
        });

        await _client.CallAsync("launch", launchArgs ?? new JsonObject
        {
            ["type"] = "coreclr",
            ["name"] = ".NET Launch",
            ["program"] = args.Length > 0 ? args[0] : "",
            ["cwd"] = workingDir,
            ["stopAtEntry"] = false,
        });

        await _client.CallAsync("setExceptionBreakpoints", new JsonObject
        {
            ["filters"] = new JsonArray("uncaught"),
        });

        await _client.CallAsync("configurationDone");

        _state = DebugState.Running;
        StateChanged?.Invoke(_state);
    }

    public async Task SetBreakpointsAsync(string file, int[] lines)
    {
        if (_client == null) return;
        await _client.CallAsync("setBreakpoints", new JsonObject
        {
            ["source"] = new JsonObject { ["path"] = file },
            ["breakpoints"] = new JsonArray(lines.Select(l => new JsonObject { ["line"] = l }).ToArray()),
        });
    }

    public async Task ContinueAsync()
    {
        if (_client == null || _state != DebugState.Paused) return;
        await _client.CallAsync("continue", new JsonObject { ["threadId"] = _currentThreadId });
        _state = DebugState.Running;
        CurrentStack = [];
        CurrentScope = [];
        StateChanged?.Invoke(_state);
    }

    public async Task StepOverAsync()
    {
        if (_client == null || _state != DebugState.Paused) return;
        await _client.CallAsync("next", new JsonObject { ["threadId"] = _currentThreadId });
        _state = DebugState.Running;
        StateChanged?.Invoke(_state);
    }

    public async Task StepIntoAsync()
    {
        if (_client == null || _state != DebugState.Paused) return;
        await _client.CallAsync("stepIn", new JsonObject { ["threadId"] = _currentThreadId });
        _state = DebugState.Running;
        StateChanged?.Invoke(_state);
    }

    public async Task StepOutAsync()
    {
        if (_client == null || _state != DebugState.Paused) return;
        await _client.CallAsync("stepOut", new JsonObject { ["threadId"] = _currentThreadId });
        _state = DebugState.Running;
        StateChanged?.Invoke(_state);
    }

    public async Task PauseAsync()
    {
        if (_client == null || _state != DebugState.Running) return;
        await _client.CallAsync("pause", new JsonObject { ["threadId"] = _currentThreadId });
    }

    public async Task RefreshStackAsync()
    {
        if (_client == null || _state != DebugState.Paused) return;
        var st = await _client.CallAsync("stackTrace", new JsonObject
        {
            ["threadId"] = _currentThreadId,
            ["levels"] = 20,
        });
        var frames = st["body"]?["stackFrames"]?.AsArray();
        if (frames == null) return;

        CurrentStack = frames.Select(f =>
        {
            var fo = f!.AsObject();
            var src = fo["source"] as JsonObject;
            return new DebugStackFrame(
                (int)fo["id"]!.GetValue<long>(),
                fo["name"]!.GetValue<string>(),
                src?["path"]?.GetValue<string>(),
                (int)fo["line"]!.GetValue<long>(),
                (int)fo["column"]!.GetValue<long>());
        }).ToArray();

        if (CurrentStack.Length > 0)
        {
            CurrentFile = CurrentStack[0].File;
            CurrentLine = CurrentStack[0].Line;
            Stopped?.Invoke(CurrentLine, CurrentFile);
            await RefreshScopeAsync(CurrentStack[0].Id);
        }
        StateChanged?.Invoke(_state);
    }

    private async Task RefreshScopeAsync(int frameId)
    {
        if (_client == null) return;
        var resp = await _client.CallAsync("scopes", new JsonObject { ["frameId"] = frameId });
        var scopes = resp["body"]?["scopes"]?.AsArray();
        if (scopes == null || scopes.Count == 0) return;

        var scope = scopes[0]!.AsObject();
        var varsRef = (int)(scope["variablesReference"]?.GetValue<long>() ?? 0);
        if (varsRef == 0) { CurrentScope = []; return; }

        await RefreshVariablesAsync(varsRef);
    }

    private async Task RefreshVariablesAsync(int varsRef)
    {
        if (_client == null) return;
        var resp = await _client.CallAsync("variables", new JsonObject { ["variablesReference"] = varsRef });
        CurrentScope = resp["body"]?["variables"]?.AsArray()?.Select(v =>
        {
            var vo = v!.AsObject();
            return new DebugVariable(
                vo["name"]!.GetValue<string>(),
                vo["value"]?.GetValue<string>() ?? "",
                vo["type"]?.GetValue<string>() ?? "",
                (int)(vo["variablesReference"]?.GetValue<long>() ?? 0));
        }).ToArray() ?? [];
    }

    public async Task<DebugThreadInfo[]> GetThreadsAsync()
    {
        if (_client == null) return [];
        var resp = await _client.CallAsync("threads");
        var list = resp["body"]?["threads"]?.AsArray();
        if (list == null) return [];
        return list.Select(t =>
        {
            var to = t!.AsObject();
            return new DebugThreadInfo(
                (int)to["id"]!.GetValue<long>(),
                to["name"]?.GetValue<string>(),
                to["id"]!.GetValue<long>() == _currentThreadId);
        }).ToArray();
    }

    public async Task SwitchThreadAsync(int threadId)
    {
        if (_client == null) return;
        _currentThreadId = threadId;
        await RefreshStackAsync();
    }

    public async Task<DebugVariable[]> ExpandVariableAsync(int varsRef)
    {
        if (_client == null) return [];
        var resp = await _client.CallAsync("variables", new JsonObject { ["variablesReference"] = varsRef });
        return resp["body"]?["variables"]?.AsArray()?.Select(v =>
        {
            var vo = v!.AsObject();
            return new DebugVariable(
                vo["name"]!.GetValue<string>(),
                vo["value"]?.GetValue<string>() ?? "",
                vo["type"]?.GetValue<string>() ?? "",
                (int)(vo["variablesReference"]?.GetValue<long>() ?? 0));
        }).ToArray() ?? [];
    }

    public async Task<string?> EvaluateAsync(string expression)
    {
        if (_client == null || CurrentStack.Length == 0) return null;
        var resp = await _client.CallAsync("evaluate", new JsonObject
        {
            ["expression"] = expression,
            ["frameId"] = CurrentStack[0].Id,
            ["context"] = "watch",
        });
        return resp["body"]?["result"]?.GetValue<string>();
    }

    public async Task TerminateAsync()
    {
        _state = DebugState.Terminating;
        StateChanged?.Invoke(_state);
        if (_client != null)
            await _client.DisconnectAsync();
        _client = null;
        _state = DebugState.Terminated;
        CurrentStack = [];
        CurrentScope = [];
        StateChanged?.Invoke(_state);
    }

    private void OnEvent(string evt, JsonObject? body)
    {
        switch (evt)
        {
            case "stopped":
                var reason = body?["reason"]?.GetValue<string>() ?? "breakpoint";
                _currentThreadId = (int)(body?["threadId"]?.GetValue<long>() ?? 0);
                _state = DebugState.Paused;
                _ = RefreshStackAsync();
                break;

            case "continued":
                _state = DebugState.Running;
                StateChanged?.Invoke(_state);
                break;

            case "output":
                var output = body?["output"]?.GetValue<string>();
                if (output != null)
                    OutputReceived?.Invoke(output);
                var category = body?["category"]?.GetValue<string>();
                if ((category == "stdout" || category == "stderr") && output != null)
                    OutputReceived?.Invoke(output);
                break;

            case "terminated":
            case "exited":
                _state = DebugState.Terminated;
                StateChanged?.Invoke(_state);
                break;
        }
    }
}

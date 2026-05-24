using System.Reflection;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
using Spectre.Console;

namespace LTAI.Cli;

public static class CompatibilityGate
{
    public static async Task<int> RunAsync(string[] args)
    {
        // build + test flags (for local CI simulation)
        var runBuild = args.Contains("--build");
        var runTests = args.Contains("--test");

        Console.WriteLine("=== Agent Framework Compatibility Gate ===\n");

        var results = new List<GateCheck>();

        // 1. API surface checks (always fast)
        results.AddRange(CheckApiSurface());

        // 2. AIFunction factory
        results.Add(CheckAIFunctionSupport());

        // 3. Build (opt-in)
        if (runBuild)
            results.Add(await CheckProjectBuildAsync());

        // 4. Tests (opt-in)
        if (runTests)
            results.Add(await CheckTestsPassingAsync());

        Console.WriteLine(new string('-', 40));
        foreach (var r in results)
            AnsiConsole.MarkupLine(r.Passed
                ? $"  [green][[PASS]][/] {r.Name}: [dim]{r.Detail}[/]"
                : $"  [red][[FAIL]][/] {r.Name}: [dim]{r.Detail}[/]");

        var passed = results.Count(r => r.Passed);
        var failed = results.Count(r => !r.Passed);
        Console.WriteLine($"  ── {passed} passed, {failed} failed ──");

        if (failed > 0) { AnsiConsole.MarkupLine($"\n[red bold]⛔ Gate FAILED. {failed}/{results.Count} checks failed.[/]"); return 1; }
        AnsiConsole.MarkupLine($"\n[green bold]✅ Gate PASSED. {passed}/{results.Count} checks OK.[/]");
        return 0;
    }

    private static List<GateCheck> CheckApiSurface()
    {
        var checks = new List<GateCheck>();

        checks.Add(Exists("AIAgent", typeof(AIAgent)));
        checks.Add(Exists("AgentResponse", typeof(AgentResponse)));
        checks.Add(Exists("AgentResponseUpdate", typeof(AgentResponseUpdate)));
        checks.Add(Exists("AgentSession", typeof(AgentSession)));
        checks.Add(Exists("AgentRunOptions", typeof(AgentRunOptions)));
        checks.Add(Exists("AIAgentBuilder", typeof(AIAgentBuilder)));
        checks.Add(Exists("AIFunctionFactory", typeof(AIFunctionFactory)));
        checks.Add(Exists("ChatOptions", typeof(ChatOptions)));

        // NuGet version checks via assembly info
        checks.Add(CheckNuGetVersion("Microsoft.Agents.AI"));
        checks.Add(CheckNuGetVersion("Microsoft.Agents.AI.Workflows"));
        checks.Add(CheckNuGetVersion("Microsoft.Agents.AI.Hyperlight"));
        checks.Add(CheckNuGetVersion("Microsoft.Agents.AI.Hosting.A2A.AspNetCore"));
        checks.Add(CheckNuGetVersion("Microsoft.Agents.AI.Harness"));

        return checks;
    }

    private static GateCheck CheckNuGetVersion(string packageName)
    {
        var csprojPath = FindCsprojWithPackage(packageName);
        if (csprojPath == null)
            return new GateCheck(false, packageName, "not referenced in any .csproj");

        try
        {
            var content = File.ReadAllText(csprojPath);
            var match = System.Text.RegularExpressions.Regex.Match(content,
                $@"<PackageReference Include=""{System.Text.RegularExpressions.Regex.Escape(packageName)}""\s+Version=""([^""]+)""");
            if (match.Success)
                return new GateCheck(true, packageName, $"v{match.Groups[1].Value} ({Path.GetFileName(csprojPath)})");
            return new GateCheck(false, packageName, "version not found");
        }
        catch
        {
            return new GateCheck(false, packageName, "csproj read error");
        }
    }

    private static string? FindCsprojWithPackage(string packageName)
    {
        var srcDir = FindProjectRoot() ?? AppContext.BaseDirectory;
        if (!Directory.Exists(srcDir))
            return null;

        foreach (var csproj in Directory.GetFiles(srcDir, "*.csproj", SearchOption.AllDirectories))
        {
            if (File.ReadAllText(csproj).Contains(packageName))
                return csproj;
        }
        return null;
    }

    private static string? FindProjectRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "LTAI.sln")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    private static GateCheck Exists(string label, Type type) =>
        new GateCheck(true, label, $"v{type.Assembly.GetName().Version}");

    private static GateCheck ExistsInAsm(Assembly asm, string label, string fullName) =>
        asm.GetType(fullName) != null
            ? new GateCheck(true, label, $"resolved")
            : new GateCheck(false, label, "not found");

    private static GateCheck CheckAIFunctionSupport()
    {
        try
        {
            Func<Task<object?>> h = () => Task.FromResult<object?>("ok");
            var f = AIFunctionFactory.Create(h, "compat_test", "Test");
            return new GateCheck(true, "AIFunctionFactory", $"created '{f.Name}'");
        }
        catch (Exception ex) { return new GateCheck(false, "AIFunctionFactory", ex.Message); }
    }

    private static async Task<GateCheck> CheckProjectBuildAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "build src/LTAI.Agent/LTAI.Agent.csproj --no-restore",
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        });
        if (p == null) return new GateCheck(false, "Build Agent", "failed to start");
        await p.WaitForExitAsync();
        sw.Stop();
        return p.ExitCode == 0
            ? new GateCheck(true, "Build Agent", $"{sw.ElapsedMilliseconds}ms")
            : new GateCheck(false, "Build Agent", $"exit {p.ExitCode}");
    }

    private static async Task<GateCheck> CheckTestsPassingAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "test tests/LTAI.Tests/LTAI.Tests.csproj --no-restore --filter FullyQualifiedName~AIAgent",
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        });
        if (p == null) return new GateCheck(false, "Tests", "failed to start");
        await p.WaitForExitAsync();
        sw.Stop();
        return p.ExitCode == 0
            ? new GateCheck(true, "Tests", $"{sw.ElapsedMilliseconds}ms")
            : new GateCheck(false, "Tests", $"exit {p.ExitCode}");
    }

    private record struct GateCheck(bool Passed, string Name, string Detail);
}

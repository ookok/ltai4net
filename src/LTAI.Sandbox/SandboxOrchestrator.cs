using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace LTAI.Sandbox;

public sealed class SandboxOrchestrator
{
    private readonly ISandbox _primary;
    private readonly ISandbox? _fallback;
    private readonly ILogger<SandboxOrchestrator> _logger;
    private readonly SandboxSecurityAuditor _auditor;

    public SandboxOrchestrator(
        IEnumerable<ISandbox> sandboxes,
        ILogger<SandboxOrchestrator> logger)
    {
        var list = sandboxes.ToList();
        _primary = list.FirstOrDefault(s => s is ProcessSandbox) ?? list.First();
        _fallback = list.FirstOrDefault(s => s is DockerSandbox);
        _logger = logger;
        _auditor = new SandboxSecurityAuditor();
    }

    public async Task<SandboxResult> ExecuteAsync(
        string code,
        SandboxLanguage language = SandboxLanguage.Python,
        int timeoutSeconds = 30,
        int memoryMb = 256,
        bool allowNetwork = false,
        CancellationToken cancellationToken = default)
    {
        var audit = _auditor.Audit(code, language);
        if (!audit.Allowed)
            return new SandboxResult { Success = false, Error = $"Security audit failed: {audit.Reason}", Stderr = audit.Reason };

        var request = new SandboxRequest
        {
            Code = code, Language = language,
            TimeoutSeconds = timeoutSeconds, MemoryLimitMb = memoryMb,
            NetworkEnabled = allowNetwork, ReadOnlyFilesystem = true
        };

        _logger.LogInformation("Sandbox exec: {Lang}, timeout={TO}s, mem={MEM}MB, network={NET}",
            language, timeoutSeconds, memoryMb, allowNetwork);

        var sandbox = await SelectSandboxAsync(language, cancellationToken);
        return await sandbox.ExecuteAsync(request, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ExecuteTemplateAsync(
        string template, string code, SandboxLanguage language,
        CancellationToken cancellationToken = default)
    {
        var wrapped = template.Replace("{code}", code);
        var result = await ExecuteAsync(wrapped, language, cancellationToken: cancellationToken);

        if (!result.Success && _fallback != null)
        {
            _logger.LogInformation("Falling back to Docker sandbox");
            result = await _fallback.ExecuteAsync(new SandboxRequest
            {
                Code = wrapped, Language = language,
                TimeoutSeconds = 30, MemoryLimitMb = 256,
                ReadOnlyFilesystem = true
            }, cancellationToken);
        }

        return new List<string>
        {
            result.Success ? "Success" : "Failed",
            result.Stdout,
            result.Stderr,
            $"ExitCode: {result.ExitCode}, Time: {result.ExecutionTimeMs}ms, Memory: {result.PeakMemoryKb}KB"
        };
    }

    private async Task<ISandbox> SelectSandboxAsync(SandboxLanguage language, CancellationToken ct)
    {
        if (await _primary.IsAvailableAsync(ct) && _primary.Capability.HasFlag(language.ToCapability()))
            return _primary;

        if (_fallback != null && await _fallback.IsAvailableAsync(ct))
            return _fallback;

        return _primary;
    }

    public string GenerateTemplate(SandboxLanguage language, string taskDescription)
    {
        return language switch
        {
            SandboxLanguage.Python => $$"""
# Task: {{taskDescription}}
import sys, json, math

def solve():
    {code}

if __name__ == "__main__":
    try:
        result = solve()
        print(json.dumps({"result": result}))
    except Exception as e:
        print(json.dumps({"error": str(e)}), file=sys.stderr)
        sys.exit(1)
""",
            SandboxLanguage.JavaScript => $$"""
// Task: {{taskDescription}}
try {
    function solve() {
        {code}
    }
    const result = solve();
    console.log(JSON.stringify({ result }));
} catch (e) {
    console.error(JSON.stringify({ error: e.message }));
    process.exit(1);
}
""",
            _ => string.Empty
        };
    }
}

public sealed class SandboxSecurityAuditor
{
    private static readonly HashSet<string> BlockedPython = new(StringComparer.OrdinalIgnoreCase)
    {
        "import os", "import subprocess", "import shutil", "import socket",
        "__import__", "eval(", "exec(", "compile(", "open(",
        "remove(", "rmdir(", "unlink(", "chmod(",
        "urllib", "requests.", "ftplib", "smtplib"
    };

    private static readonly HashSet<string> BlockedJS = new(StringComparer.OrdinalIgnoreCase)
    {
        "require('child_process')", "require('fs')", "require('net')",
        "require('http')", "require('https')",
        "process.exit", "process.kill", "global.gc",
        "eval(", "Function("
    };

    public (bool Allowed, string? Reason) Audit(string code, SandboxLanguage language)
    {
        var blocked = language switch
        {
            SandboxLanguage.Python => BlockedPython,
            SandboxLanguage.JavaScript => BlockedJS,
            _ => new HashSet<string>()
        };

        foreach (var pattern in blocked)
        {
            if (code.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return (false, $"Blocked pattern: {pattern}");
        }

        if (code.Length > 50000)
            return (false, "Code exceeds 50KB limit");

        return (true, null);
    }
}

public static class SandboxLanguageExtensions
{
    public static SandboxCapability ToCapability(this SandboxLanguage lang) => lang switch
    {
        SandboxLanguage.Python => SandboxCapability.Python,
        SandboxLanguage.JavaScript => SandboxCapability.JavaScript,
        SandboxLanguage.CSharp => SandboxCapability.CSharp,
        SandboxLanguage.Shell => SandboxCapability.Shell,
        _ => SandboxCapability.None
    };
}

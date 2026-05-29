using System;
using Microsoft.Agents.AI; using Microsoft.Extensions.AI; using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Scriban;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using NuGet.Common;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;

namespace LTAI.Agent.Agents;

public sealed class CodeAgent : BaseAgent
{
    private static readonly SourceRepository NuGetOrg = Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json");

    public CodeAgent(AgentCard card, IChatClient brain, SkillRegistry skills, ILogger<CodeAgent> logger)
        : base(card, brain, skills, logger) { }

    protected override async Task<AgentResponse> ExecuteLogicAsync(AgentContext context, CancellationToken ct)
    {
        var q = context.UserQuery;
        if (q.Contains("diff", OrdinalIgnoreCase)) return await DiffCodeAsync(context, ct);
        if (q.Contains("template", OrdinalIgnoreCase) || q.Contains("generate", OrdinalIgnoreCase)) return await GenerateCodeAsync(context, ct);
        if (q.Contains("nuget", OrdinalIgnoreCase) || q.Contains("package", OrdinalIgnoreCase)) return await SearchNuGetAsync(context, ct);
        if (q.Contains("analyze", OrdinalIgnoreCase) || q.Contains("syntax", OrdinalIgnoreCase) || q.Contains("class ", OrdinalIgnoreCase)) return await AnalyzeCodeAsync(context, ct);
        return await CallBrainAsync(context.FullHistory, ct: ct);
    }

    private async Task<AgentResponse> AnalyzeCodeAsync(AgentContext context, CancellationToken ct)
    {
        var code = ExtractCodeBlock(context.UserQuery) ?? context.UserQuery;
        var tree = CSharpSyntaxTree.ParseText(code);
        var root = await tree.GetRootAsync(ct);
        var comp = CSharpCompilation.Create("a").AddSyntaxTrees(tree)
            .WithOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var diags = comp.GetDiagnostics().Where(d => d.Severity >= DiagnosticSeverity.Warning).Take(20).ToList();
        var sb = new StringBuilder($"AST nodes: {root.DescendantNodes().Count():N0}\n");
        foreach (var d in diags) sb.AppendLine($"[{d.Severity}] L{d.Location.GetLineSpan().StartLinePosition.Line + 1}: {d.GetMessage()}");
        if (diags.Count == 0) sb.AppendLine("✅ Clean");
        return new AgentResponse(new ChatMessage(ChatRole.Assistant, sb.ToString()));
    }

    private async Task<AgentResponse> DiffCodeAsync(AgentContext context, CancellationToken ct)
    {
        var parts = context.UserQuery.Split("---", StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return Fail("Need old/new code separated by ---");
        var diff = InlineDiffBuilder.Diff(parts[0].Trim(), parts[1].Trim());
        var sb = new StringBuilder($"\n+{diff.Lines.Count(l => l.Type == ChangeType.Inserted)} -{diff.Lines.Count(l => l.Type == ChangeType.Deleted)}\n");
        foreach (var l in diff.Lines.Where(l => l.Type != ChangeType.Unchanged))
            sb.AppendLine($"{(l.Type == ChangeType.Inserted ? "+" : "-")} {l.Text}");
        return new AgentResponse(new ChatMessage(ChatRole.Assistant, sb.ToString()));
    }

    private async Task<AgentResponse> GenerateCodeAsync(AgentContext context, CancellationToken ct)
    {
        var tm = Regex.Match(context.UserQuery, @"```template\n([\s\S]*?)```");
        if (!tm.Success) return Fail("Provide template in ```template ...```");
        var vm = Regex.Match(context.UserQuery, @"```json\n([\s\S]*?)```");
        var vars = vm.Success ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(vm.Groups[1].Value) ?? [] : [];
        var result = Template.Parse(tm.Groups[1].Value).Render(vars, m => m.Name);
        return new AgentResponse(new ChatMessage(ChatRole.Assistant, $"```\n{result}\n```"));
    }

    private async Task<AgentResponse> SearchNuGetAsync(AgentContext context, CancellationToken ct)
    {
        var qm = Regex.Match(context.UserQuery, @"""(.+?)""");
        var q = qm.Success ? qm.Groups[1].Value : context.UserQuery;
        var r = await NuGetOrg.GetResourceAsync<PackageSearchResource>(ct);
        var res = await r.SearchAsync(q, new SearchFilter(false), 0, 10, NullLogger.Instance, ct);
        var sb = new StringBuilder($"NuGet: {q}\n");
        foreach (var p in res) sb.AppendLine($"  {p.Identity.Id,-45} {p.Identity.Version,-10} {p.DownloadCount:N0}");
        return new AgentResponse(new ChatMessage(ChatRole.Assistant, sb.ToString()));
    }

    private static string? ExtractCodeBlock(string text)
    {
        var m = Regex.Match(text, @"```(?:\w+)?\n([\s\S]*?)```");
        return m.Success ? m.Groups[1].Value : null;
    }
}



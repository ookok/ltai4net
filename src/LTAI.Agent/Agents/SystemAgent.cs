using System;
using Microsoft.Agents.AI; using Microsoft.Extensions.AI; using Microsoft.Extensions.Logging;
using Renci.SshNet;
using DnsClient;

namespace LTAI.Agent.Agents;

public sealed class SystemAgent : BaseAgent
{
    public SystemAgent(AgentCard card, IChatClient brain, SkillRegistry skills, ILogger<SystemAgent> logger)
        : base(card, brain, skills, logger) { }

    protected override async Task<AgentResponse> ExecuteLogicAsync(AgentContext context, CancellationToken ct)
    {
        var q = context.UserQuery;

        if (q.Contains("dns", OrdinalIgnoreCase) || q.Contains("resolve", OrdinalIgnoreCase))
            return await DnsLookupAsync(q, ct);
        if (q.Contains("ssh", OrdinalIgnoreCase) || q.Contains("remote", OrdinalIgnoreCase))
            return await SshExecAsync(q, ct);
        if (q.Contains("info", OrdinalIgnoreCase) || q.Contains("system", OrdinalIgnoreCase))
            return SystemInfo();

        return await CallBrainAsync(context.FullHistory, ct: ct);
    }

    private async Task<AgentResponse> DnsLookupAsync(string query, CancellationToken ct)
    {
        var lookup = new LookupClient();
        var host = query.Split(' ').LastOrDefault()?.Trim() ?? "localhost";
        var result = await lookup.QueryAsync(host, QueryType.A, cancellationToken: ct);
        var ips = result.Answers.ARecords().Select(a => a.Address.ToString()).ToList();
        return new AgentResponse(new ChatMessage(ChatRole.Assistant,
            $"DNS: {host}\n" + (ips.Count > 0 ? string.Join("\n", ips) : "No records found")));
    }

    private async Task<AgentResponse> SshExecAsync(string query, CancellationToken ct)
    {
        var parts = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var host = parts.Length > 1 ? parts[1] : "localhost";
        var cmd = string.Join(" ", parts.Skip(2));
        if (string.IsNullOrEmpty(cmd)) return Fail("Usage: ssh <host> <command>");

        try
        {
            using var client = new SshClient(host, Environment.UserName, "");
            client.Connect();
            var result = client.RunCommand(cmd);
            client.Disconnect();
            return new AgentResponse(new ChatMessage(ChatRole.Assistant,
                $"$ {cmd}\n{result.Result}\n{(result.ExitStatus == 0 ? "✅ OK" : $"❌ Exit: {result.ExitStatus}")}"));
        }
        catch (Exception ex)
        {
            return Fail($"SSH failed: {ex.Message}");
        }
    }

    private static AgentResponse SystemInfo()
    {
        var info = $"""
            OS: {Environment.OSVersion}
            Machine: {Environment.MachineName}
            User: {Environment.UserName}
            .NET: {Environment.Version}
            Process: {Environment.ProcessPath}
            Directory: {Environment.CurrentDirectory}
            """;
        return new AgentResponse(new ChatMessage(ChatRole.Assistant, info));
    }
}



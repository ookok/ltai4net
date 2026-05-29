using System;
using Microsoft.Agents.AI; using Microsoft.Extensions.AI; using Microsoft.Extensions.Logging;
using Docker.DotNet;
using k8s;

namespace LTAI.Agent.Agents;

public sealed class ContainerAgent : BaseAgent
{
    public ContainerAgent(AgentCard card, IChatClient brain, SkillRegistry skills, ILogger<ContainerAgent> logger)
        : base(card, brain, skills, logger) { }

    protected override async Task<AgentResponse> ExecuteLogicAsync(AgentContext context, CancellationToken ct)
    {
        var q = context.UserQuery;

        if (q.Contains("docker", OrdinalIgnoreCase))
            return await DockerExecAsync(q, ct);
        if (q.Contains("k8s", OrdinalIgnoreCase) || q.Contains("kubernetes", OrdinalIgnoreCase) || q.Contains("pod", OrdinalIgnoreCase))
            return await K8sExecAsync(q, ct);

        return await CallBrainAsync(context.FullHistory, ct: ct);
    }

    private async Task<AgentResponse> DockerExecAsync(string query, CancellationToken ct)
    {
        try
        {
            using var client = new DockerClientConfiguration(new Uri("unix:///var/run/docker.sock")).CreateClient();
            var containers = await client.Containers.ListContainersAsync(
                new Docker.DotNet.Models.ContainersListParameters { Limit = 20 }, ct);
            var info = string.Join("\n", containers.Select(c =>
                $"  {c.ID[..12]} {c.Image} {(c.State == "running" ? "🟢" : "🔴")}"));
            return new AgentResponse(new ChatMessage(ChatRole.Assistant,
                $"Containers ({containers.Count}):\n{info}"));
        }
        catch (Exception ex)
        {
            return Fail($"Docker: {ex.Message}");
        }
    }

    private async Task<AgentResponse> K8sExecAsync(string query, CancellationToken ct)
    {
        try
        {
            var config = KubernetesClientConfiguration.BuildDefaultConfig();
            using var client = new Kubernetes(config);
            var pods = await client.CoreV1.ListPodForAllNamespacesAsync(cancellationToken: ct);
            var info = string.Join("\n", pods.Items.Select(p =>
                $"  {p.Metadata.Namespace}/{p.Metadata.Name} ({p.Status.Phase})"));
            return new AgentResponse(new ChatMessage(ChatRole.Assistant,
                $"Pods ({pods.Items.Count}):\n{info}"));
        }
        catch (Exception ex)
        {
            return Fail($"K8s: {ex.Message}");
        }
    }
}



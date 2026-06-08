// Copyright (c) LTAI. All rights reserved.

using System.Text;
using LTAI.Agent.Context;
using LTAI.Agent.DevUI;
using LTAI.Agent.Memory;
using LTAI.Agent.Tasks;
using LTAI.Agent.Tools;
using LTAI.Agent.Workflows;
using LTAI.Core.Configuration;
using LTAI.TUI.Services;
using Spectre.Console;
using LTAI.AI;

namespace LTAI.TUI.DevUI;

/// <summary>
/// Three-panel TUI dashboard backed by <see cref="LTAIDevUIService"/> (agent
/// enumeration + AgentCard) and <see cref="DevUISpanCollector"/> (live OTel
/// spans). Bound to <see cref="TuiView.Dashboard"/> in <c>TuiApp.ShowDashboard</c>.
/// </summary>
public static class DevUIDashboardView
{
    private static int _tabCycle;

    public static void Render(
        LTAIDevUIService devUi,
        DevUISpanCollector spans,
        UsageTracker? usage,
        YAMLWorkflowRegistry? workflows = null,
        LocalEmbedder? embedder = null,
        ToolEmbeddingCache? cache = null,
        RemoteEmbeddingCache? remoteCache = null,
        EmbeddingClient? embeddingClient = null,
        ModelMetadataProvider? provider = null,
        CacheAlignerProvider? aligner = null,
        TaskQueue? taskQueue = null,
        BackgroundJobService? bgjs = null,
        WorkflowHealthTracker? wfHealth = null,
        PalaceStore? palace = null)
    {
        var cards = devUi.ListAgentCards();
        var recent = spans.Snapshot().TakeLast(15).Reverse().ToList();
        var workflowList = workflows?.List() ?? (IReadOnlyList<WorkflowInfo>)[];
        var tab = (_tabCycle++ / 2) % 2; // toggle every ~10s

        var layout = new Layout("root")
            .SplitRows(
                new Layout("header").Size(3),
                new Layout("body"),
                new Layout("footer").Size(5));
        layout["header"].Update(BuildHeaderPanel(cards.Count, spans.Count, recent, workflowList, embedder, cache, remoteCache, embeddingClient, provider, aligner, taskQueue, bgjs));
        layout["body"].Update(tab == 0 ? BuildAgentPanel(cards) : BuildSpanPanel(recent));
        layout["footer"].Update(BuildUsagePanel(usage, workflowList, wfHealth, palace));
        AnsiConsole.Write(layout);
    }

    public static void RenderWithAutoRefresh(
        LTAIDevUIService devUi,
        DevUISpanCollector spanCollector,
        UsageTracker usage,
        YAMLWorkflowRegistry? workflows = null,
        LocalEmbedder? embedder = null,
        ToolEmbeddingCache? cache = null,
        RemoteEmbeddingCache? remoteCache = null,
        EmbeddingClient? embeddingClient = null,
        ModelMetadataProvider? provider = null,
        CacheAlignerProvider? aligner = null,
        TaskQueue? taskQueue = null,
        BackgroundJobService? bgjs = null,
        WorkflowHealthTracker? wfHealth = null,
        PalaceStore? palace = null)
    {
        for (int i = 0; i < 120; i++)
        {
            AnsiConsole.Clear();
            Render(devUi, spanCollector, usage, workflows, embedder, cache, remoteCache, embeddingClient, provider, aligner, taskQueue, bgjs, wfHealth, palace);
            Thread.Sleep(5000);
        }
    }

    private static Panel BuildHeaderPanel(
        int agentCount,
        int spanCount,
        IReadOnlyList<DevUISpan> recent,
        IReadOnlyList<WorkflowInfo> workflows,
        LocalEmbedder? embedder,
        ToolEmbeddingCache? cache,
        RemoteEmbeddingCache? remoteCache,
        EmbeddingClient? embeddingClient,
        ModelMetadataProvider? provider = null,
        CacheAlignerProvider? aligner = null,
        TaskQueue? taskQueue = null,
        BackgroundJobService? bgjs = null)
    {
        var sep = $"[{ThemeService.MutedTag}]·[/]";
        var line1 =
            $"[bold]LTAI DevUI[/]  {sep}  " +
            $"[{ThemeService.PrimaryTag}]{agentCount}[/] agents  {sep}  " +
            $"[{ThemeService.PrimaryTag}]{recent.Count(s => s.IsLive)}[/] live  {sep}  " +
            $"[{ThemeService.PrimaryTag}]{workflows.Count}[/] WFs";
        var line2 = BuildEmbedStatusLine(embedder);
        var line3 = BuildCacheStatusLine(cache);
        var lines = $"{line1}\n{line2}\n{line3}";
        try
        {
            return new Panel(new Markup(lines))
            {
                Border = BoxBorder.None,
                Expand = true,
            };
        }
        catch
        {
            return new Panel(new Markup(Markup.Escape(lines)))
            {
                Border = BoxBorder.None,
                Expand = true,
            };
        }
    }

    private static string BuildCacheStatusLine(ToolEmbeddingCache? cache)
    {
        if (cache is null)
            return $"[{ThemeService.MutedTag}]Cache: (not registered)[/]";

        var entries = cache.CachedEntryCount;
        var rate = cache.HitRate;
        var ratePct = (rate * 100).ToString("F0");
        var rateColor = rate >= 0.80 ? ThemeService.AccentTag : rate >= 0.50 ? ThemeService.WarningTag : ThemeService.ErrorTag;
        return $"[{ThemeService.MutedTag}]Cache:[/] [{ThemeService.PrimaryTag}]{entries}[/] entries  hit [{rateColor}]{ratePct}%[/]";
    }

    private static string BuildEmbedStatusLine(LocalEmbedder? embedder)
    {
        if (embedder is null)
            return $"[{ThemeService.MutedTag}]Embed: (not registered)[/]";

        var ep = embedder.ActiveExecutionProvider;
        var quant = embedder.UsingQuantizedModel;
        var epStr = ep is null ? "?" : ep;
        var qStr = quant ? "INT8" : "FP32";
        var disabled = LocalEmbedder.DefaultDisabled ? " [disabled]" : "";
        return $"[{ThemeService.MutedTag}]Embed:[/] [{ThemeService.PrimaryTag}]{epStr}[/] {qStr}{disabled}";
    }

    private static Panel BuildAgentPanel(IReadOnlyList<LTAIAgentCard> cards)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[bold {ThemeService.WarningTag}]● Agents[/]  [{ThemeService.MutedTag}]Tab: Ctrl+P 切换视图[/]");
        foreach (var c in cards.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            var perms = c.Permissions.Count == 0
                ? ""
                : $"  [{ThemeService.MutedTag}]╴[/] {string.Join(" ", c.Permissions.Select(ColorizePerm))}";
            sb.AppendLine($"  [{ThemeService.PrimaryTag}]•[/] [bold]{Markup.Escape(c.Name)}[/]  [{ThemeService.MutedTag}]{Markup.Escape(c.ModelId ?? "—")}[/]  [{ThemeService.AccentTag}]{c.ToolCount} tools[/]{perms}");
        }
        return new Panel(new Markup(sb.ToString().TrimEnd()))
        {
            Border = BoxBorder.None,
            Expand = true,
        };
    }

    private static Panel BuildSpanPanel(IReadOnlyList<DevUISpan> recent)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[bold {ThemeService.PrimaryTag}]● Spans[/]  [{ThemeService.MutedTag}]Tab: Ctrl+P 切换视图[/]");

        if (recent.Count == 0)
        {
            sb.AppendLine($"  [{ThemeService.MutedTag}](no spans yet)[/]");
        }
        else
        {
            foreach (var s in recent.Take(15))
            {
                var status = s.IsLive ? $"[{ThemeService.WarningTag}]●[/]"
                    : s.Status == "ERROR" ? $"[{ThemeService.ErrorTag}]✖[/]"
                    : $"[{ThemeService.AccentTag}]✓[/]";
                var dur = s.IsLive ? "..."
                    : s.Duration.TotalMilliseconds < 1 ? $"{s.Duration.TotalMilliseconds:F1}ms"
                    : s.Duration.TotalMilliseconds < 1000 ? $"{(int)s.Duration.TotalMilliseconds}ms"
                    : $"{s.Duration.TotalSeconds:F1}s";
                var durColor = s.IsLive ? ThemeService.WarningTag
                    : s.Duration > TimeSpan.FromSeconds(2) ? ThemeService.ErrorTag
                    : s.Duration > TimeSpan.FromMilliseconds(500) ? ThemeService.WarningTag
                    : ThemeService.MutedTag;
                sb.AppendLine($"  {status} [bold]{Markup.Escape(Truncate(s.Name, 40))}[/] [{durColor}]{dur}[/]");
            }

            var completed = recent.Where(s => !s.IsLive && s.Duration > TimeSpan.Zero).ToList();
            if (completed.Count >= 3)
            {
                var msDurs = completed.Select(s => s.Duration.TotalMilliseconds).OrderBy(x => x).ToList();
                var p50 = msDurs[(int)(msDurs.Count * 0.50)];
                var p95 = msDurs[(int)(msDurs.Count * 0.95)];
                sb.AppendLine($"  [{ThemeService.MutedTag}]P50={p50:F0}ms  P95={p95:F0}ms  ({completed.Count} samples)[/]");
            }
        }

        return new Panel(new Markup(sb.ToString().TrimEnd()))
        {
            Border = BoxBorder.None,
            Expand = true,
        };
    }

    private static Panel BuildUsagePanel(UsageTracker? usage, IReadOnlyList<WorkflowInfo> workflows, WorkflowHealthTracker? wfHealth = null, PalaceStore? palace = null)
    {
        var sep = $" [{ThemeService.MutedTag}]·[/] ";
        var sb = new StringBuilder();

        if (usage is not null)
        {
            sb.Append($"In: [{ThemeService.PrimaryTag}]{UsageTracker.PromptTokens:N0}[/]{sep}");
            sb.Append($"Out: [{ThemeService.PrimaryTag}]{UsageTracker.CompletionTokens:N0}[/]{sep}");
            sb.Append($"Total: [{ThemeService.PrimaryTag}]{UsageTracker.TotalTokens:N0}[/]{sep}");
            sb.Append($"Cost: {UsageTracker.CostDisplay}{sep}");
            sb.Append($"Requests: [{ThemeService.PrimaryTag}]{UsageTracker.Requests:N0}[/]\n");
        }

        var process = System.Diagnostics.Process.GetCurrentProcess();
        var memMB = process.PrivateMemorySize64 / (1024.0 * 1024.0);
        sb.Append($"Mem: [{ThemeService.PrimaryTag}]{memMB:F1} MB[/]{sep}");
        sb.Append($"CPU: [{ThemeService.PrimaryTag}]{process.TotalProcessorTime.TotalSeconds:F1}s[/]");

        if (usage is not null)
            sb.Append($"{sep}Uptime: [{ThemeService.PrimaryTag}]{UsageTracker.Uptime:mm\\:ss}[/]");

        if (palace != null)
        {
            var totalCount = palace.Count();
            var wingCount = palace.ListWings().Count;
            sb.Append($"\nMemStore: [{ThemeService.PrimaryTag}]{totalCount}[/] drawers  [{ThemeService.PrimaryTag}]{wingCount}[/] wings");
        }

        if (workflows.Count > 0)
        {
            sb.Append($"\nWFs: {string.Join(sep, workflows.Select(w => $"[{ThemeService.PrimaryTag}]{Markup.Escape(w.Name)}[/] [{ThemeService.MutedTag}]v{w.Version} {w.Type}[/]"))}");
            if (wfHealth != null)
            {
                var health = BuildWorkflowHealthLine(wfHealth);
                sb.Append($"  {health}");
            }
        }

        return new Panel(new Markup(sb.ToString().TrimEnd()))
        {
            Border = BoxBorder.None,
            Expand = true,
        };
    }

    private static string BuildWorkflowHealthLine(WorkflowHealthTracker health)
    {
        var success = health.SuccessCount;
        var failure = health.FailureCount;
        var total = success + failure;
        var successStr = $"[green]{success} ok[/]";
        var failureStr = failure > 0 ? $"[red]{failure} fail[/]" : "[dim]0 fail[/]";

        var lastReload = health.LastReloaded;
        var lastFail = health.LastFailure;
        var agoStr = "";

        if (lastReload.name != "")
        {
            var ago = DateTime.UtcNow - lastReload.utc;
            agoStr = ago.TotalSeconds < 60
                ? $"{(int)ago.TotalSeconds}s ago"
                : ago.TotalMinutes < 60
                    ? $"{(int)ago.TotalMinutes}m ago"
                    : $"{(int)ago.TotalHours}h ago";
        }

        var failInfo = "";
        if (lastFail.name != "")
        {
            var failAgo = DateTime.UtcNow - lastFail.utc;
            var failAgoStr = failAgo.TotalSeconds < 60
                ? $"{(int)failAgo.TotalSeconds}s ago"
                : failAgo.TotalMinutes < 60
                    ? $"{(int)failAgo.TotalMinutes}m ago"
                    : $"{(int)failAgo.TotalHours}h ago";
            failInfo = $"  [red]✖ {Markup.Escape(lastFail.name)}: {Markup.Escape(lastFail.reason)} ({failAgoStr})[/]";
        }

        var reloadInfo = lastReload.name != ""
            ? $"  [grey]·[/]  last: [cyan]{Markup.Escape(lastReload.name)}[/] ({agoStr})"
            : "";

        return $"{successStr}  [grey]·[/]  {failureStr}{reloadInfo}{failInfo}";
    }

    private static string ColorizePerm(string perm)
    {
        return perm.ToLowerInvariant() switch
        {
            "read" => "[green]R[/]",
            "write" => "[yellow]W[/]",
            "list" => "[blue]L[/]",
            "exec" => "[red]X[/]",
            _ => $"[grey]{Markup.Escape(perm)}[/]",
        };
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }
}

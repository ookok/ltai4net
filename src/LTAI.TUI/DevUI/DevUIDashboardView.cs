// Copyright (c) LTAI. All rights reserved.

using LTAI.Agent.DevUI;
using LTAI.Agent.Workflows;
using LTAI.Core.Configuration;
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
    public static void Render(
        LTAIDevUIService devUi,
        DevUISpanCollector spans,
        UsageTracker? usage,
        YAMLWorkflowRegistry? workflows = null,
        LocalEmbedder? embedder = null,
        ToolEmbeddingCache? cache = null,
        RemoteEmbeddingCache? remoteCache = null,
        EmbeddingClient? embeddingClient = null,
        ModelMetadataProvider? provider = null)
    {
        var cards = devUi.ListAgentCards();
        var recent = spans.Snapshot().TakeLast(15).Reverse().ToList();
        var workflowList = workflows?.List() ?? (IReadOnlyList<WorkflowInfo>)[];

        var layout = new Layout("root")
            .SplitRows(
                new Layout("header").Size(8),
                new Layout("body").SplitColumns(
                    new Layout("agents").Ratio(2),
                    new Layout("spans").Ratio(3)),
                new Layout("footer").Size(7));

        layout["header"].Update(BuildHeaderPanel(cards.Count, spans.Count, recent, workflowList, embedder, cache, remoteCache, embeddingClient, provider));

        layout["agents"].Update(BuildAgentTable(cards));
        layout["spans"].Update(BuildSpanTable(recent));
        layout["footer"].Update(BuildUsagePanel(usage, workflowList));

        AnsiConsole.Write(layout);
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
        ModelMetadataProvider? provider = null)
    {
        var topLine =
            $"[bold]LTAI DevUI Dashboard[/]  [grey]·[/]  " +
            $"[aqua]{agentCount}[/] agents  [grey]·[/]  " +
            $"[aqua]{spanCount}[/] spans  [grey]·[/]  " +
            $"[aqua]{recent.Count(s => s.IsLive)}[/] live  [grey]·[/]  " +
            $"[aqua]{workflows.Count}[/] workflows";
        var embedLine = BuildEmbedStatusLine(embedder);
        var cacheLine = BuildCacheStatusLine(cache);
        var remoteLine = BuildRemoteCacheStatusLine(remoteCache);
        var fallbackLine = BuildEmbeddingClientLine(embeddingClient);
        var modelsLine = BuildModelMetadataLine(provider);
        return new Panel(new Markup($"{topLine}\n{embedLine}\n{cacheLine}\n{remoteLine}\n{fallbackLine}\n{modelsLine}"))
        {
            Border = BoxBorder.Heavy,
            Header = new PanelHeader("[green] P9 Live Inspector [/]"),
            Expand = true,
        };
    }

    /// <summary>
    /// P14.10: surface <see cref="EmbeddingClient.ConsecutiveAllProviderFailures"/>
    /// + <see cref="EmbeddingClient.LocalFallbackActivated"/> so users can
    /// see when the safety net has kicked in. Only rendered when relevant
    /// (failures &gt; 0 or fallback already active) to avoid noise in the
    /// happy path.
    /// </summary>
    private static string BuildEmbeddingClientLine(EmbeddingClient? ec)
    {
        if (ec is null)
            return "[grey][[/][bold]EmbedClient[/][grey]][/]  [dim](not registered)[/]";

        var fails = ec.ConsecutiveAllProviderFailures;
        var activated = ec.LocalFallbackActivated;
        if (fails == 0 && !activated)
            return $"[grey][[/][bold]EmbedClient[/][grey]][/]  [green]all providers healthy[/]";

        var failsStr = $"[yellow]{fails}[/] consecutive failures";
        if (activated && ec.LocalFallbackActivatedAtUtc is DateTime at)
        {
            var ago = DateTime.UtcNow - at;
            var agoStr = ago.TotalMinutes < 1 ? "just now"
                : ago.TotalHours < 1 ? $"{(int)ago.TotalMinutes}m ago"
                : $"{(int)ago.TotalHours}h ago";
            return $"[grey][[/][bold]EmbedClient[/][grey]][/]  {failsStr}  [grey]·[/]  " +
                   $"[red]local fallback ON[/] (activated {agoStr})";
        }
        return $"[grey][[/][bold]EmbedClient[/][grey]][/]  {failsStr}  [grey]·[/]  " +
               $"[dim](threshold 3 → local fallback)[/]";
    }

    private static string BuildModelMetadataLine(ModelMetadataProvider? mp)
    {
        if (mp is null)
            return "[grey][[/][bold]Models[/][grey]][/]  [dim](not registered)[/]";

        var all = mp.AllModels;
        if (all.Count == 0)
            return "[grey][[/][bold]Models[/][grey]][/]  [yellow]refreshing...[/]";

        var providers = all.Select(m => m.Provider).Distinct().Count();
        var ctxCount = all.Count(m => m.ContextWindow != null);
        var suppTools = all.Count(m => m.SupportsTools);
        return $"[grey][[/][bold]Models[/][grey]][/]  " +
               $"[aqua]{all.Count}[/] models  [grey]·[/]  " +
               $"[aqua]{providers}[/] providers  [grey]·[/]  " +
               $"[green]{suppTools}[/] tool-call  [grey]·[/]  " +
               $"[dim]{ctxCount}[/] ctx known";
    }

    /// <summary>
    /// P14.6: surface <see cref="ToolEmbeddingCache.CachedEntryCount"/> + hit
    /// rate so users can verify the persistent tool/agent embedding cache is
    /// actually saving ONNX calls. Color rules: hit rate &gt;= 80% green,
    /// &gt;= 50% yellow, &lt; 50% red (low = many descriptions changed →
    /// re-embed storms).
    /// </summary>
    private static string BuildCacheStatusLine(ToolEmbeddingCache? cache)
    {
        if (cache is null)
            return "[grey][[/][bold]EmbedCache[/][grey]][/]  [dim](not registered)[/]";

        var entries = cache.CachedEntryCount;
        var hits = cache.CacheHits;
        var misses = cache.CacheMisses;
        var rate = cache.HitRate;
        var ratePct = (rate * 100).ToString("F0");
        var rateColor = rate >= 0.80 ? "green" : rate >= 0.50 ? "yellow" : "red";
        return $"[grey][[/][bold]EmbedCache[/][grey]][/]  " +
               $"[aqua]{entries}[/] entries  [grey]·[/]  " +
               $"[green]{hits}[/] hits  [grey]·[/]  " +
               $"[yellow]{misses}[/] misses  [grey]·[/]  " +
               $"hit rate [{rateColor}]{ratePct}%[/]";
    }

    /// <summary>
    /// P14.5: surface <see cref="RemoteEmbeddingCache"/> stats for the
    /// remote API embedding path. Distinct from the persistent
    /// <see cref="ToolEmbeddingCache"/> — this cache holds transient
    /// per-request task text embeddings with a 24h TTL.
    /// </summary>
    private static string BuildRemoteCacheStatusLine(RemoteEmbeddingCache? remoteCache)
    {
        if (remoteCache is null)
            return "[grey][[/][bold]RemoteCache[/][grey]][/]  [dim](not registered)[/]";

        var entries = remoteCache.CachedEntryCount;
        var hits = remoteCache.CacheHits;
        var misses = remoteCache.CacheMisses;
        var evictions = remoteCache.Evictions;
        var rate = remoteCache.HitRate;
        var ratePct = (rate * 100).ToString("F0");
        var rateColor = rate >= 0.80 ? "green" : rate >= 0.50 ? "yellow" : "red";
        return $"[grey][[/][bold]RemoteCache[/][grey]][/]  " +
               $"[aqua]{entries}[/] entries  [grey]·[/]  " +
               $"[green]{hits}[/] hits  [grey]·[/]  " +
               $"[yellow]{misses}[/] misses  [grey]·[/]  " +
               $"hit rate [{rateColor}]{ratePct}%[/]  [grey]·[/]  " +
               $"[grey]{evictions}[/] evicted";
    }

    /// <summary>
    /// P14.2: surface <see cref="LocalEmbedder.ActiveExecutionProvider"/> and
    /// <see cref="LocalEmbedder.UsingQuantizedModel"/> in the dashboard so users
    /// can verify GPU/CPU + INT8/FP32 from a glance. Color rules:
    /// EP = DML/CUDA = green (GPU), CPU = grey; Quant = INT8 = green, FP32 = yellow.
    /// </summary>
    private static string BuildEmbedStatusLine(LocalEmbedder? embedder)
    {
        if (embedder is null)
            return "[grey][[/][bold]Embed[/][grey]][/]  [dim](not registered — remote API only)[/]";

        var ep = embedder.ActiveExecutionProvider;
        var quant = embedder.UsingQuantizedModel;
        var models = LocalEmbedder.ListAvailableModels();
        var currentModel = models.FirstOrDefault(m =>
            m.Downloaded && m.Id == embedder.CurrentModelName)
            ?? models.FirstOrDefault(m => m.Downloaded);

        var epStr = ep is null
            ? "[grey](not loaded yet)[/]"
            : (ep.Equals("CPU", StringComparison.OrdinalIgnoreCase)
                ? $"[grey]{Markup.Escape(ep)}[/]"
                : $"[green]{Markup.Escape(ep)}[/]");
        var quantStr = quant
            ? "[green]INT8[/]"
            : "[yellow]FP32[/]";
        var modelStr = currentModel is null
            ? "[red](no model on disk)[/]"
            : $"[cyan]{Markup.Escape(currentModel.Id)}[/] [grey]{currentModel.Dimension}d[/]";

        var disabled = LocalEmbedder.DefaultDisabled
            ? "  [yellow](disabled — remote API)[/]"
            : "";

        return $"[grey][[/][bold]Embed[/][grey]][/]  model={modelStr}  [grey]·[/]  " +
               $"EP={epStr}  [grey]·[/]  quant={quantStr}{disabled}";
    }

    private static Panel BuildAgentTable(IReadOnlyList<LTAIAgentCard> cards)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey35)
            .Title("[bold yellow] Agents (LTAIAgentCard) [/]")
            .Expand();
        table.AddColumn(new TableColumn("[bold]Name[/]").NoWrap());
        table.AddColumn(new TableColumn("[bold]Model[/]"));
        table.AddColumn(new TableColumn("[bold]T[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Tools[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Perms[/]").NoWrap());
        table.AddColumn(new TableColumn("[bold]Description[/]"));

        foreach (var c in cards.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            var perms = c.Permissions.Count == 0
                ? "[grey]—[/]"
                : string.Join(" ", c.Permissions.Select(ColorizePerm));
            table.AddRow(
                $"[bold]{Markup.Escape(c.Name)}[/]",
                Markup.Escape(c.ModelId ?? "—"),
                c.Temperature.ToString("F1"),
                c.ToolCount.ToString(),
                perms,
                Markup.Escape(Truncate(c.Description, 40)));
        }
        return new Panel(table) { Expand = true };
    }

    private static Panel BuildSpanTable(IReadOnlyList<DevUISpan> recent)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey35)
            .Title("[bold cyan] OpenTelemetry Spans (live tail) [/]")
            .Expand();
        table.AddColumn(new TableColumn("[bold]Status[/]").NoWrap());
        table.AddColumn(new TableColumn("[bold]Name[/]"));
        table.AddColumn(new TableColumn("[bold]Source[/]"));
        table.AddColumn(new TableColumn("[bold]Kind[/]").NoWrap());
        table.AddColumn(new TableColumn("[bold]Duration[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Trace[/]").NoWrap());

        if (recent.Count == 0)
        {
            table.AddRow("[grey]·[/]", "[grey](no spans yet — start a chat to see traces)[/]", "", "", "", "");
        }
        else
        {
            foreach (var s in recent)
            {
                var statusTag = s.IsLive
                    ? "[yellow]● live[/]"
                    : s.Status == "ERROR" ? "[red]✖ ERR[/]"
                    : "[green]✓ OK [/]";
                var duration = s.IsLive ? "[yellow]...[/]" : FormatMs(s.Duration);
                var color = s.IsLive ? "yellow" :
                            s.Duration > TimeSpan.FromSeconds(2) ? "red" :
                            s.Duration > TimeSpan.FromMilliseconds(500) ? "yellow" : "grey";
                table.AddRow(
                    statusTag,
                    $"[{color}]{Markup.Escape(Truncate(s.Name, 36))}[/]",
                    Markup.Escape(Truncate(s.Source, 28)),
                    s.Kind,
                    duration,
                    Markup.Escape(s.TraceId.Length >= 8 ? s.TraceId[..8] : s.TraceId));
            }
        }
        return new Panel(table) { Expand = true };
    }

    private static Panel BuildUsagePanel(UsageTracker? usage, IReadOnlyList<WorkflowInfo> workflows)
    {
        if (usage is null && workflows.Count == 0)
        {
            return new Panel(new Markup("[grey](usage tracker not available)[/]"))
            {
                Border = BoxBorder.Rounded,
                Header = new PanelHeader("[green] Token Usage [/]"),
                Expand = true,
            };
        }
        var grid = new Grid()
            .AddColumn(new GridColumn().RightAligned())
            .AddColumn()
            .AddColumn(new GridColumn().RightAligned())
            .AddColumn();
        if (usage is not null)
        {
            grid.AddRow(
                "[bold]In[/]", $"{UsageTracker.PromptTokens:N0}",
                "[bold]Out[/]", $"{UsageTracker.CompletionTokens:N0}");
            grid.AddRow(
                "[bold]Total[/]", $"{UsageTracker.TotalTokens:N0}",
                "[bold]Requests[/]", $"{UsageTracker.Requests:N0}");
            grid.AddRow(
                "[bold]Cost[/]", UsageTracker.CostDisplay,
                "[bold]Uptime[/]", UsageTracker.Uptime.ToString(@"hh\:mm\:ss"));
        }
        // P15.6: workflow hot-reload health shown in the dashboard footer.
        // "Edit .livingtree/workflows/*.yaml — reload is automatic."
        if (workflows.Count > 0)
        {
            var summary = string.Join("  [grey]·[/]  ", workflows.Select(w =>
                $"[cyan]{Markup.Escape(w.Name)}[/] [grey]v{w.Version} {w.Type}[/]"));
            grid.AddRow("[bold]WF[/]", summary.EscapeMarkup(), "", "");
        }
        return new Panel(grid)
        {
            Border = BoxBorder.Rounded,
            Header = new PanelHeader("[green] Token Usage (this session) [/]"),
            Expand = true,
        };
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

    private static string FormatMs(TimeSpan d) => d.TotalMilliseconds switch
    {
        < 1 => $"{d.TotalMilliseconds:F2} ms",
        < 1000 => $"{d.TotalMilliseconds:F0} ms",
        < 60_000 => $"{d.TotalSeconds:F2} s",
        _ => $"{d.TotalMinutes:F1} m",
    };

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }
}

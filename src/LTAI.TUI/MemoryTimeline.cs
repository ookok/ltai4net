using System.Reflection;
using Spectre.Console;
using Spectre.Console.Rendering;
using LTAI.AI.Governors;
using LTAI.Knowledge.Core;
using LTAI.Models;

namespace LTAI.TUI;

public sealed class MemoryTimeline
{
    private readonly DualMemoryStore? _dualStore;
    private readonly SynapticMemory? _synapticMemory;
    private readonly MemoryFilesService? _memoryFiles;
#pragma warning disable CS0169
    private int _scrollOffset;
#pragma warning restore CS0169

    public MemoryTimeline(DualMemoryStore? dual = null, SynapticMemory? syn = null, MemoryFilesService? mf = null)
    {
        _dualStore = dual;
        _synapticMemory = syn;
        _memoryFiles = mf;
    }

    public IRenderable Render()
    {
        var items = CollectMemories();
        if (items.Count == 0)
            return new Markup("[grey]No memories collected yet.[/]");

        var panel = new Panel(BuildTimeline(items));
        panel.Header = new PanelHeader("[cyan]Memory Timeline[/]");
        panel.Border = BoxBorder.Rounded;
        panel.BorderColor(Color.Cyan1);
        return panel;
    }

    private List<MemoryItem> CollectMemories()
    {
        var items = new List<MemoryItem>();

        if (_dualStore != null)
        {
            try
            {
                var episodesField = typeof(DualMemoryStore).GetField("_episodes",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (episodesField?.GetValue(_dualStore) is LiteDB.ILiteCollection<RawEpisode> episodes)
                {
                    var recent = episodes.Query().OrderByDescending(e => e.Timestamp).Limit(20).ToList();
                    foreach (var e in recent)
                    {
                        var summary = e.Query.Length > 60 ? e.Query[..57] + "..." : e.Query;
                        items.Add(new MemoryItem(
                            e.Timestamp,
                            summary,
                            e.Confidence,
                            e.WasSuccessful ? "episode" : "failed",
                            e.Domain
                        ));
                    }
                }
            }
            catch { }
        }

        if (_synapticMemory != null)
        {
            try
            {
                var recent = _synapticMemory.GetRecentUntrained(20);
                foreach (var exp in recent)
                {
                    var summary = exp.Query.Length > 60 ? exp.Query[..57] + "..." : exp.Query;
                    items.Add(new MemoryItem(
                        exp.CreatedAt,
                        summary,
                        exp.Confidence,
                        string.IsNullOrWhiteSpace(exp.Label) ? "synapse" : exp.Label,
                        exp.Type.ToString()
                    ));
                }
            }
            catch { }
        }

        if (_memoryFiles != null)
        {
            try
            {
                foreach (var kv in _memoryFiles.All.Take(10))
                {
                    var mf = kv.Value;
                    var ts = mf.Evolution.UpdatedAt;
                    var summary = mf.Name.Length > 60 ? mf.Name[..57] + "..." : mf.Name;
                    items.Add(new MemoryItem(
                        ts,
                        summary,
                        (float)mf.Confidence,
                        "memfile",
                        mf.Domain
                    ));
                }
            }
            catch { }
        }

        return items.OrderByDescending(i => i.Timestamp).Take(30).ToList();
    }

    private IRenderable BuildTimeline(List<MemoryItem> items)
    {
        var tree = new Tree("[bold cyan]Memory Timeline[/]");
        var now = DateTime.UtcNow;

        foreach (var item in items)
        {
            var age = now - item.Timestamp;
            var ageStr = age.TotalHours >= 24 ? $"{age.Days}d ago" :
                         age.TotalMinutes >= 60 ? $"{age.Hours}h ago" :
                         age.TotalSeconds >= 60 ? $"{age.Minutes}m ago" :
                         $"{Math.Max(1, (int)age.TotalSeconds)}s ago";

            var barLen = (int)(item.Confidence * 20);
            var barColor = item.Confidence >= 0.8f ? "green" :
                          item.Confidence >= 0.5f ? "yellow" : "red";
            var bar = new string('\u2588', barLen) + new string('\u2591', 20 - barLen);

            var icon = item.Kind switch
            {
                "episode" => "\U0001F4DD",
                "failed" => "\u274C",
                "synapse" => "\u26A1",
                "memfile" => "\U0001F4BE",
                _ => "\U0001F4BE"
            };

            var node = tree.AddNode($"{icon} [grey]{ageStr}[/] [{barColor}]{bar}[/] [white]{Markup.Escape(item.Summary)}[/]");
            if (!string.IsNullOrWhiteSpace(item.Domain))
                node.AddNode($"[dim]Domain: {Markup.Escape(item.Domain)} | Confidence: {item.Confidence:F2}[/]");
        }

        return tree;
    }
}

internal record MemoryItem(DateTime Timestamp, string Summary, float Confidence, string Kind, string Domain);

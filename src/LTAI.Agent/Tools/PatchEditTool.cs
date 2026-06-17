using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using LTAI.AI;
using LTAI.Core;
using LTAI.Core.Configuration;

namespace LTAI.Agent.Tools;

[ToolDomain("filesystem")]
public sealed class PatchEditTool
{
    private readonly string _ws;

    // Optional: injected by AgentBuilder or DI for --dry-run impact analysis
    public Func<string, Task<string>>? ImpactAnalyzer { get; set; }

    public PatchEditTool(string ws)
    {
        _ws = ws;
    }

    [Description("基于最小 diff 修改文件。适用于小幅修改（替换、插入、删除行），比完整重写更省 token。\n"
        + "格式: 每一行用 ~old~text~ 表示替换，+text 表示插入，-text 表示删除。\n"
        + "参数: path — 文件路径；patches — 要应用的修改列表\n"
        + "可选 dryRun=true 时只预览不写入，同时附带影响分析。")]
    public async Task<string> ApplyPatches(
        string path,
        [Description("修改列表。每条格式：~old~new~ 替换 | +text 插入 | -text 删除")]
        List<string> patches,
        [Description("可选 dry-run 模式：true=只预览不写入，同时显示影响范围")]
        bool dryRun = false)
    {
        try
        {
            var (fp, denied) = PathUtils.TryResolveWithPermission(_ws, path);
            if (denied != null)
                return $"Path '{denied}' is outside workspace.";

            if (dryRun)
                return await DryRunAsync(fp, path, patches).ConfigureAwait(false);

            if (!File.Exists(fp))
                return $"File '{fp}' not found.";

            var lines = await File.ReadAllLinesAsync(fp).ConfigureAwait(false);
            var result = new List<string>(lines);
            var applied = 0;
            var failed = 0;

            foreach (var patch in patches)
            {
                var trimmed = patch.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                if (trimmed.StartsWith('~'))
                {
                    var match = Regex.Match(trimmed, @"^~(.*?)~(.*)$");
                    if (match.Success)
                    {
                        var old = match.Groups[1].Value;
                        var replacement = match.Groups[2].Value;
                        var replaced = false;
                        for (int i = 0; i < result.Count; i++)
                        {
                            if (result[i].Contains(old))
                            {
                                result[i] = result[i].Replace(old, replacement);
                                replaced = true;
                                break;
                            }
                        }
                        if (replaced) applied++; else failed++;
                    }
                    else failed++;
                }
                else if (trimmed.StartsWith('+'))
                {
                    result.Add(trimmed[1..]);
                    applied++;
                }
                else if (trimmed.StartsWith('-'))
                {
                    var target = trimmed[1..];
                    var removed = result.RemoveAll(l => l.Contains(target));
                    if (removed > 0) applied++; else failed++;
                }
                else failed++;
            }

            await File.WriteAllLinesAsync(fp, result).ConfigureAwait(false);

            // Token savings: diff output vs full file rewrite
            var naiveTokens = lines.Length * 10; // ~10 tokens per naive full-file rewrite
            var actualTokens = applied * 8 + failed * 4; // concise patch format
            TokenSavingsTracker.RecordLookup(naiveTokens, actualTokens);

            var summary = $"Patches: {applied} applied, {failed} failed";
            return summary;
        }
        catch (Exception ex)
        {
            return $"PatchEdit error: {ex.Message}";
        }
    }

    private async Task<string> DryRunAsync(string fp, string originalPath, List<string> patches)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 预览修改 (dry-run)\n");

        // Show patches to be applied
        sb.AppendLine("### 待应用的修改");
        foreach (var patch in patches)
        {
            var trimmed = patch.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            sb.AppendLine($"  {trimmed}");
        }

        // Impact analysis
        if (ImpactAnalyzer != null)
        {
            try
            {
                var symbol = Path.GetFileNameWithoutExtension(originalPath);
                var impact = await ImpactAnalyzer(symbol).ConfigureAwait(false);
                if (!impact.Contains("not found") && !impact.Contains("No impact"))
                {
                    sb.AppendLine();
                    sb.AppendLine("### 影响分析");
                    sb.AppendLine(impact);
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"\n(impact analysis failed: {ex.Message})");
            }
        }

        // File stats
        if (File.Exists(fp))
        {
            var content = await File.ReadAllTextAsync(fp).ConfigureAwait(false);
            var lines = content.Split('\n').Length;
            var size = content.Length;
            sb.AppendLine($"\n### 文件信息");
            sb.AppendLine($"  路径: {originalPath}");
            sb.AppendLine($"  行数: {lines:N0}");
            sb.AppendLine($"  大小: {size:N0} bytes");
        }

        sb.AppendLine($"\n💡 确认执行请去掉 dryRun=true 参数");
        return sb.ToString();
    }
}

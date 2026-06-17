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

    public PatchEditTool(string ws)
    {
        _ws = ws;
    }

    [Description("基于最小 diff 修改文件。适用于小幅修改（替换、插入、删除行），比完整重写更省 token。\n"
        + "格式: 每一行用 ~old~text~ 表示替换，+text 表示插入，-text 表示删除。\n"
        + "参数: path — 文件路径；patches — 要应用的修改列表")]
    public async Task<string> ApplyPatches(
        string path,
        [Description("修改列表。每条格式：~old~new~ 替换 | +text 插入 | -text 删除")]
        List<string> patches)
    {
        try
        {
            var (fp, denied) = PathUtils.TryResolveWithPermission(_ws, path);
            if (denied != null)
                return $"Path '{denied}' is outside workspace.";

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
            var summary = $"Patches: {applied} applied, {failed} failed";
            return summary;
        }
        catch (Exception ex)
        {
            return $"PatchEdit error: {ex.Message}";
        }
    }
}

#pragma warning disable MAAI001

using LTAI.AI;
using Microsoft.Agents.AI;

namespace LTAI.Agent.Tools;

[ToolDomain("skills")]
public sealed class AgentUrlSkillsSource : AgentSkillsSource
{
    private readonly string[] _urls;
    private readonly HttpClient _http;

    public AgentUrlSkillsSource(IEnumerable<string> urls, HttpClient? http = null)
    {
        _urls = urls.ToArray();
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    public override async Task<IList<AgentSkill>> GetSkillsAsync(CancellationToken ct = default)
    {
        var skills = new List<AgentSkill>();
        foreach (var url in _urls)
        {
            try
            {
                var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("skills", out var arr))
                {
                    foreach (var item in arr.EnumerateArray())
                    {
                        var name = item.GetProperty("name").GetString() ?? "unknown";
                        var desc = item.TryGetProperty("description", out var d) ? d.GetString() : null;
                        var skillUrl = item.TryGetProperty("url", out var u) ? u.GetString() : null;
                        if (skillUrl == null) continue;

                        var content = await _http.GetStringAsync(skillUrl, ct).ConfigureAwait(false);
                        skills.Add(new AgentInlineSkill(name, desc ?? "", content));
                    }
                }
            }
            catch { /* skip failed URLs */ }
        }
        return skills.AsReadOnly();
    }
}
#pragma warning restore MAAI001

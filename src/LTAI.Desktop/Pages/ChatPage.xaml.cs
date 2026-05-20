using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LTAI.Desktop.Pages;

public partial class ChatPage : ContentPage
{
    private readonly LTAIService _svc;
    private readonly StringBuilder _html = new();
    private readonly List<(string role, string text, DateTime time)> _history = new();
    private int _totalTurns;
    private int _totalTokens;
    private string? _originalContent;
    private string? _loadedFilePath;
    private CancellationTokenSource? _streamCts;
    private bool _streaming;

    private static readonly string[] Templates = new[]
    {
        "Code Review: Review this code for bugs and improvements:",
        "Refactor: Refactor this code to improve readability:",
        "Generate Tests: Write unit tests for this code:",
        "Document: Write documentation for this function:",
        "Explain Code: Explain what this code does in detail:",
        "Find Bugs: Analyze this code for bugs and security issues:",
        "Optimize Performance: Identify performance bottlenecks:",
        "Add Error Handling: Add proper error handling to this code:"
    };

    public ChatPage(LTAIService svc)
    {
        InitializeComponent();
        _svc = svc;
        ChatWebView.Navigated += (_, _) => { };
        ChatWebView.Source = new HtmlWebViewSource { Html = BaseHtml() };
    }

    private async void OnSend(object? sender, EventArgs e)
    {
        var query = InputEditor.Text?.Trim();
        if (string.IsNullOrWhiteSpace(query) || _streaming) return;
        InputEditor.Text = "";
        _streaming = true;
        _totalTurns++;
        UpdateStats();

        AppendMessage("You", EscapeHtml(query));

        await ResolveFileAsync(query);
        query = ResolveQuery(query);

        _history.Add(("You", query, DateTime.Now));
        var responseId = $"msg_{_totalTurns}";
        AppendStreamBlock(responseId);

        _streamCts = new CancellationTokenSource();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri("http://localhost:8080") };
            var json = JsonSerializer.Serialize(new { query });
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/maf/stream") { Content = content };

            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _streamCts.Token);
            using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);

            var fullResponse = new StringBuilder();
            while (true)
            {
                var line = await reader.ReadLineAsync();
                if (line == null || line == "data: [DONE]") break;
                if (!line.StartsWith("data: ")) continue;

                var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(line[6..]);
                if (data != null && data.TryGetValue("text", out var t))
                {
                    var token = t.GetString() ?? "";
                    fullResponse.Append(token);
                    _totalTokens++;
                    UpdateStreamBlock(responseId, RenderMarkdownToHtml(fullResponse.ToString()));
                    UpdateStats();
                }
            }

            var final = fullResponse.ToString();
            _history.Add(("LTAI", final, DateTime.Now));
            FinalizeStreamBlock(responseId, RenderMarkdownToHtml(final));

            if (_originalContent != null)
            {
                var codeBlock = ExtractCodeBlock(final);
                if (!string.IsNullOrWhiteSpace(codeBlock))
                {
                    var diff = ComputeDiff(_originalContent, codeBlock);
                    AppendMessage("Diff", diff);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppendMessage("Error", ex.Message); }
        finally
        {
            _streaming = false;
            _streamCts?.Dispose();
            _streamCts = null;
        }
    }

    private string ResolveQuery(string raw)
    {
        if (raw.StartsWith("@@"))
        {
            var dir = raw[2..].Trim();
            if (!Directory.Exists(dir)) dir = Path.Combine(Environment.CurrentDirectory, dir);
            if (Directory.Exists(dir))
            {
                var files = Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories).Take(30);
                return $"[Folder: {Path.GetFileName(dir)}]\n" + string.Join("\n", files.Select(f => Path.GetRelativePath(dir, f)));
            }
        }
        else if (raw.StartsWith("@"))
        {
            var f = raw[1..].Trim();
            if (!File.Exists(f)) f = Path.Combine(Environment.CurrentDirectory, f);
            if (File.Exists(f))
            {
                _loadedFilePath = f;
                _originalContent = File.ReadAllText(f);
                var content = _originalContent[..Math.Min(_originalContent.Length, 5000)];
                return $"[File: {Path.GetFileName(f)}]\n```{Path.GetExtension(f).TrimStart('.')}\n{content}\n```\n\nAnalyze this file.";
            }
        }
        return raw;
    }

    private async Task ResolveFileAsync(string raw)
    {
        if (raw.StartsWith("@") && !raw.StartsWith("@@"))
        {
            var f = raw[1..].Trim();
            if (!File.Exists(f)) f = Path.Combine(Environment.CurrentDirectory, f);
            if (File.Exists(f))
            {
                _loadedFilePath = f;
                _originalContent = File.ReadAllText(f);
                AppendMessage("System", $"📎 Loaded: {Path.GetFileName(f)} ({_originalContent.Length / 1024}KB)");
            }
        }
        await Task.CompletedTask;
    }

    private void AppendMessage(string role, string text)
    {
        var color = role switch { "You" => "#1a2332", "LTAI" => "#1a2e1a", "Error" => "#2e1a1a", "Diff" => "#1a1a2e", "System" => "#21262d", _ => "#161b22" };
        var prefix = role switch { "You" => "You", "LTAI" => "LTAI", "Diff" => "Diff", "System" => "System", _ => role };
        var escaped = EscapeHtml(text).Replace("\n", "<br>");

        InvokeOnMainThreadAsync(() =>
        {
            ChatWebView.EvaluateJavaScriptAsync(
                $"addMessage('{prefix}', `{escaped}`, '{color}');");
        });
    }

    private void AppendStreamBlock(string id)
    {
        InvokeOnMainThreadAsync(() =>
        {
            ChatWebView.EvaluateJavaScriptAsync(
                $"addStreamBlock('{id}');");
        });
    }

    private void UpdateStreamBlock(string id, string html)
    {
        var escaped = html.Replace("`", "\\`").Replace("$", "\\$");
        InvokeOnMainThreadAsync(() =>
        {
            ChatWebView.EvaluateJavaScriptAsync(
                $"updateStreamBlock('{id}', `{escaped}`);");
        });
    }

    private void FinalizeStreamBlock(string id, string html)
    {
        var escaped = html.Replace("`", "\\`").Replace("$", "\\$");
        InvokeOnMainThreadAsync(() =>
        {
            ChatWebView.EvaluateJavaScriptAsync(
                $"finalizeStreamBlock('{id}', `{escaped}`);");
        });
    }

    private void UpdateStats()
    {
        SessionLabel.Text = $"Turns:{_totalTurns} Tokens:{_totalTokens}";
    }

    private async void OnTemplates(object? sender, EventArgs e)
    {
        var selected = await DisplayActionSheet("Prompt Templates", "Cancel", null, Templates);
        if (selected != null && selected != "Cancel")
        {
            InputEditor.Text = selected;
        }
    }

    private async void OnAttach(object? sender, EventArgs e)
    {
        try
        {
            var result = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "Select file to attach",
                FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    { DevicePlatform.WinUI, new[] { ".cs", ".py", ".js", ".ts", ".go", ".rs", ".java", ".md", ".json", ".xml", ".txt", ".csproj", ".sln" } }
                })
            });
            if (result != null)
            {
                InputEditor.Text = $"@{result.FullPath}";
            }
        }
        catch { /* non-fatal */ }
    }

    private async void OnBranch(object? sender, EventArgs e)
    {
        var providers = _svc.DNA?.Evolution.CurrentGenome.Genes.Keys.Take(3).ToList();
        if (providers == null || providers.Count < 2)
        {
            await DisplayAlert("Branch", "Need at least 2 models available", "OK");
            return;
        }
        var query = _history.LastOrDefault().role == "You" ? _history.Last().text : null;
        if (query == null) return;

        foreach (var p in providers)
        {
            AppendMessage("System", $"🔀 Branching with {p}...");
            try
            {
                await foreach (var token in _svc.LTS.StreamWithModelAsync(query, p))
                {
                    // simplified: non-streaming for branch
                }
                var response = await _svc.LTS.ChatAsync(query);
                AppendMessage(p, response[..Math.Min(response.Length, 2000)] + "...");
            }
            catch (Exception ex) { AppendMessage("Error", $"{p}: {ex.Message}"); }
        }
    }

    private async void OnExport(object? sender, EventArgs e)
    {
        var md = string.Join("\n\n---\n\n", _history.Select(m => $"## {m.role}\n\n{m.text}"));
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LTAI");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"session-{DateTime.Now:yyyyMMdd-HHmmss}.md");
        await File.WriteAllTextAsync(file, md);
        await DisplayAlert("Exported", file, "OK");
    }

    private void OnClear(object? sender, EventArgs e)
    {
        _history.Clear();
        _html.Clear();
        _totalTurns = 0;
        _totalTokens = 0;
        UpdateStats();
        ChatWebView.Source = new HtmlWebViewSource { Html = BaseHtml() };
    }

    private static string RenderMarkdownToHtml(string text)
    {
        text = EscapeHtml(text);
        text = Regex.Replace(text, @"```(\w+)?\n([\s\S]*?)```", m =>
        {
            var lang = m.Groups[1].Value;
            var code = m.Groups[2].Value.TrimEnd();
            return $"<pre class='code'><code class='{lang}'>{code}</code></pre>";
        });
        text = Regex.Replace(text, @"\*\*(.+?)\*\*", "<strong>$1</strong>");
        text = Regex.Replace(text, @"\*([^*]+)\*", "<em>$1</em>");
        text = Regex.Replace(text, @"`([^`]+)`", "<code>$1</code>");
        text = Regex.Replace(text, @"^### (.+)$", "<h4>$1</h4>", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^## (.+)$", "<h3>$1</h3>", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^# (.+)$", "<h2>$1</h2>", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^- (.+)$", "<li>$1</li>", RegexOptions.Multiline);
        text = text.Replace("\n\n", "<br><br>").Replace("\n", "<br>");
        return text;
    }

    private static string EscapeHtml(string text) => text
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("\"", "&quot;").Replace("'", "&#39;");

    private static string ExtractCodeBlock(string response)
    {
        var m = Regex.Match(response, @"```(?:\w+)?\n([\s\S]*?)```");
        return m.Success ? m.Groups[1].Value : "";
    }

    private static string ComputeDiff(string original, string modified)
    {
        var origLines = original.Split('\n');
        var modLines = modified.Split('\n');
        var sb = new StringBuilder();
        sb.AppendLine("<pre class='diff'>");
        var max = Math.Max(origLines.Length, modLines.Length);
        var changes = 0;
        for (var i = 0; i < max; i++)
        {
            var o = i < origLines.Length ? origLines[i] : null;
            var m = i < modLines.Length ? modLines[i] : null;
            if (o == m) { sb.AppendLine($"  {EscapeHtml(o ?? "")}"); }
            else
            {
                if (o != null) sb.AppendLine($"<span class='removed'>- {EscapeHtml(o)}</span>");
                if (m != null) sb.AppendLine($"<span class='added'>+ {EscapeHtml(m)}</span>");
                changes++;
            }
        }
        sb.AppendLine("</pre>");
        sb.Insert(0, $"<div class='diff-header'>Diff: {changes} changes ({origLines.Length}→{modLines.Length} lines)</div>");
        return sb.ToString();
    }

    private static string BaseHtml() => """
<!DOCTYPE html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'>
<style>
*{margin:0;padding:0;box-sizing:border-box}
body{background:#0d1117;color:#c9d1d9;font:13px/1.6 'Segoe UI',system-ui,monospace;padding:16px;overflow-y:auto}
.msg{margin:8px 0;padding:12px;border-radius:8px;border:1px solid #30363d;max-width:90%}
.msg.you{margin-left:auto;background:#1a2332;border-color:#58a6ff33}
.msg.ltai{background:#1a2e1a;border-color:#3fb95033}
.msg.system{background:#21262d;border-color:#30363d;font-size:11px;color:#8b949e}
.msg.error{background:#2e1a1a;border-color:#f8514933;color:#f85149}
.msg.diff{background:#1a1a2e;border-color:#58a6ff33}
.msg-header{font-size:11px;color:#8b949e;margin-bottom:6px;font-weight:700;text-transform:uppercase;letter-spacing:0.5px}
.msg-body{white-space:pre-wrap;word-break:break-word}
.streaming{border-left:2px solid #58a6ff;animation:pulse 1.5s infinite}
@keyframes pulse{0%,100%{border-color:#58a6ff}50%{border-color:#58a6ff33}}
pre.code{background:#161b22;border:1px solid #30363d;border-radius:6px;padding:12px;margin:8px 0;overflow-x:auto;font:12px/1.5 'Cascadia Code','Fira Code',monospace}
pre.code code{color:#c9d1d9}
pre.diff{font:12px/1.5 monospace;margin:4px 0;padding:8px;background:#0d1117;border-radius:4px}
.diff-header{font-size:11px;color:#58a6ff;margin-bottom:4px}
.removed{color:#f85149;display:block;background:#f8514915}.added{color:#3fb950;display:block;background:#3fb95015}
h2{color:#f0f6fc;font-size:16px;margin:12px 0 4px;border-bottom:1px solid #30363d;padding-bottom:4px}
h3{color:#c9d1d9;font-size:14px;margin:8px 0 4px}
h4{color:#8b949e;font-size:13px;margin:6px 0 2px}
code{background:#21262d;padding:1px 5px;border-radius:3px;font-size:12px;color:#d2a8ff}
li{margin:2px 0 2px 16px;color:#c9d1d9}
strong{color:#f0f6fc}em{color:#8b949e}
</style></head><body><div id='chat'></div>
<script>
function addMessage(role,text,color){
var c=document.getElementById('chat');
var d=document.createElement('div');
d.className='msg '+role.toLowerCase();
d.style.background=color;
d.innerHTML='<div class=msg-header>'+role+'</div><div class=msg-body>'+text+'</div>';
c.appendChild(d);
window.scrollTo(0,document.body.scrollHeight);
}
function addStreamBlock(id){
var c=document.getElementById('chat');
var d=document.createElement('div');
d.id=id;d.className='msg ltai streaming';
d.innerHTML='<div class=msg-header>LTAI</div><div class=msg-body></div>';
c.appendChild(d);
window.scrollTo(0,document.body.scrollHeight);
}
function updateStreamBlock(id,html){
var d=document.getElementById(id);
if(d){d.querySelector('.msg-body').innerHTML=html;window.scrollTo(0,document.body.scrollHeight);}
}
function finalizeStreamBlock(id,html){
var d=document.getElementById(id);
if(d){d.querySelector('.msg-body').innerHTML=html;d.classList.remove('streaming');window.scrollTo(0,document.body.scrollHeight);}
}
</script></body></html>
""";
}

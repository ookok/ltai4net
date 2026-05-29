using System.Text;
using System.Text.RegularExpressions;
using LTAI.Knowledge.Core;
using LTAI.Knowledge.Core.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.MAF;

/// <summary>
/// Learns document writing style from existing knowledge base documents,
/// then injects the learned style into LLM generation prompts.
/// No hardcoded templates — style is derived from examples in the KB.
/// </summary>
public sealed class DocumentStyleLearner
{
    private readonly AgenticRAG _rag;
    private readonly ILogger<DocumentStyleLearner> _logger;

    public DocumentStyleLearner(AgenticRAG rag, ILogger<DocumentStyleLearner>? logger = null)
    {
        _rag = rag;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<DocumentStyleLearner>.Instance;
    }

    /// <summary>
    /// Search KB for documents similar to the requested type, extract style patterns,
    /// and build a system prompt that guides the LLM to match that style.
    /// </summary>
    public async Task<string> BuildStylePrompt(string topic, string? docType = null)
    {
        var query = docType != null
            ? $"{docType} {topic} 文档 报告 模板 格式 写作"
            : $"{topic} 文档 报告 模板";

        var similarDocs = await _rag.SearchAsync(query, mode: RAGMode.Iterative);
        if (similarDocs == null || similarDocs.Count == 0)
        {
            _logger.LogInformation("No style examples found in KB for {Topic}, using generic prompt", topic);
            return "";
        }

        var structurePatterns = new List<string>();
        var styleNotes = new List<string>();
        var termGlossary = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var doc in similarDocs.Take(3))
        {
            var content = doc.Content ?? "";
            if (string.IsNullOrWhiteSpace(content)) continue;

            var headings = Regex.Matches(content, @"^(#{1,3})\s+(.+)$", RegexOptions.Multiline)
                .Select(m => new { Level = m.Groups[1].Value.Length, Text = m.Groups[2].Value.Trim() })
                .ToList();

            if (headings.Count > 0)
            {
                var structure = string.Join("\n", headings.Select(h => $"{new string('#', h.Level)} {h.Text}"));
                structurePatterns.Add($"--- Example structure ---\n{structure}");
            }

            var sentences = content.Split(new[] { '.', '。', '!', '！', '?', '？' }, StringSplitOptions.RemoveEmptyEntries);
            if (sentences.Length > 0)
            {
                var avgLen = sentences.Average(s => s.Trim().Length);
                styleNotes.Add($"Avg sentence length: {avgLen:F0} chars ({(avgLen > 60 ? "formal" : "concise")})");
            }

            var terms = Regex.Matches(content, @"[A-Z][a-z]+(?:[A-Z][a-z]+)*|[\u4e00-\u9fff]{2,}(?:方法|技术|系统|模型|标准|指标|分析|评估|报告|方案|措施|影响|风险)")
                .Select(m => m.Value).Where(t => t.Length > 1);
            foreach (var term in terms) termGlossary.Add(term);
        }

        var sb = new StringBuilder();
        sb.AppendLine("## Document Style Reference");
        sb.AppendLine("Derived from similar documents in the knowledge base. Match this style.\n");

        if (structurePatterns.Count > 0)
        {
            sb.AppendLine("### Reference Structure");
            foreach (var s in structurePatterns.Take(2))
                sb.AppendLine(s);
        }

        if (styleNotes.Count > 0)
        {
            sb.AppendLine("\n### Style");
            foreach (var note in styleNotes.Distinct())
                sb.AppendLine($"- {note}");
        }

        if (termGlossary.Count > 0)
        {
            sb.AppendLine("\n### Preferred Terms");
            sb.AppendLine(string.Join(", ", termGlossary.OrderBy(t => t).Take(30)));
        }

        sb.AppendLine("\n---\nGenerate the document matching the above style. Output: Markdown.");
        return sb.ToString();
    }
}

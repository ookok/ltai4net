using System.Text.RegularExpressions;

namespace LTAI.Knowledge.Core;

public enum ChunkingStrategyType
{
    Fix,
    Recursive,
    Vector,
    Paragraph
}

public static class ChunkingStrategies
{
    private const int CharsPerToken = 4;
    private static readonly Regex SentenceBoundaryRegex = new(
        @"(?<=[.!?。！？\u3002\uff01\uff1f])\s+", RegexOptions.Compiled);

    public static List<string> FixChunk(string text, int maxTokens = 512)
    {
        if (string.IsNullOrEmpty(text)) return new List<string>();

        var chunks = new List<string>();
        var maxChars = maxTokens * CharsPerToken;

        for (int i = 0; i < text.Length; i += maxChars)
        {
            var length = Math.Min(maxChars, text.Length - i);
            chunks.Add(text.Substring(i, length));
        }

        return chunks;
    }

    public static List<string> RecursiveChunk(string text, int maxTokens = 512)
    {
        if (string.IsNullOrEmpty(text)) return new List<string>();

        var maxChars = maxTokens * CharsPerToken;
        return RecursiveSplit(text, maxChars);
    }

    private static List<string> RecursiveSplit(string text, int maxChars)
    {
        if (text.Length <= maxChars)
            return new List<string> { text };

        var chunks = new List<string>();

        var paragraphs = text.Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var para in paragraphs)
        {
            var trimmed = para.Trim();
            if (trimmed.Length == 0) continue;

            if (trimmed.Length <= maxChars)
            {
                chunks.Add(trimmed);
                continue;
            }

            var lines = trimmed.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var lineTrimmed = line.Trim();
                if (lineTrimmed.Length == 0) continue;

                if (lineTrimmed.Length <= maxChars)
                {
                    chunks.Add(lineTrimmed);
                    continue;
                }

                var sentences = SentenceBoundaryRegex.Split(lineTrimmed);
                foreach (var sentence in sentences)
                {
                    var s = sentence.Trim();
                    if (s.Length == 0) continue;

                    if (s.Length <= maxChars)
                    {
                        chunks.Add(s);
                        continue;
                    }

                    for (int i = 0; i < s.Length; i += maxChars)
                    {
                        var length = Math.Min(maxChars, s.Length - i);
                        chunks.Add(s.Substring(i, length));
                    }
                }
            }
        }

        return chunks;
    }

    public static List<string> VectorChunk(string text, int maxTokens = 512)
    {
        if (string.IsNullOrEmpty(text)) return new List<string>();

        var maxChars = maxTokens * CharsPerToken;
        var sentences = SentenceBoundaryRegex.Split(text)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

        if (sentences.Count == 0) return new List<string>();

        var chunks = new List<string>();
        int i = 0;

        while (i < sentences.Count)
        {
            var chunkParts = new List<string> { sentences[i] };
            var currentLength = sentences[i].Length;

            int j = i + 1;
            while (j < sentences.Count && currentLength + 1 + sentences[j].Length <= maxChars)
            {
                chunkParts.Add(sentences[j]);
                currentLength += 1 + sentences[j].Length;
                j++;
            }

            chunks.Add(string.Join(" ", chunkParts));

            if (j >= sentences.Count) break;

            var overlapChars = (int)(maxChars * 0.2);
            var overlapSentences = 0;
            var accumulated = 0;
            for (int k = chunkParts.Count - 1; k >= 0; k--)
            {
                accumulated += chunkParts[k].Length + 1;
                overlapSentences++;
                if (accumulated >= overlapChars) break;
            }

            i = Math.Max(i + 1, j - overlapSentences);
        }

        return chunks;
    }

    public static List<string> ParagraphChunk(string text, int maxTokens = 512)
    {
        if (string.IsNullOrEmpty(text)) return new List<string>();

        var maxChars = maxTokens * CharsPerToken;
        var paragraphs = new List<string>();
        var current = "";
        var currentLength = 0;

        var rawParagraphs = text.Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var para in rawParagraphs)
        {
            var trimmed = para.Trim();
            if (trimmed.Length == 0) continue;

            if (currentLength == 0)
            {
                current = trimmed;
                currentLength = trimmed.Length;
            }
            else if (currentLength + 2 + trimmed.Length <= maxChars)
            {
                current += "\n\n" + trimmed;
                currentLength += 2 + trimmed.Length;
            }
            else
            {
                paragraphs.Add(current);
                current = trimmed;
                currentLength = trimmed.Length;
            }
        }

        if (currentLength > 0)
            paragraphs.Add(current);

        return paragraphs;
    }
}

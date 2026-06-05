namespace LTAI.Agent.Indexing;

internal static class SemanticChunker
{
    public static List<string> Chunk(string text, int maxChars = 6000, int minChars = 1000)
    {
        if (string.IsNullOrEmpty(text)) return [];
        if (text.Length <= maxChars) return [text];

        var chunks = new List<string>();
        var span = text.AsSpan();
        int start = 0;

        while (start < span.Length)
        {
            var end = FindBoundary(span, start, maxChars, minChars);
            chunks.Add(span[start..end].ToString());
            start = end;
        }

        return chunks;
    }

    private static int FindBoundary(ReadOnlySpan<char> text, int start, int maxChars, int minChars)
    {
        var end = Math.Min(start + maxChars, text.Length);
        if (end >= text.Length) return end;

        var bestBreak = -1;

        // Try section boundary first
        for (int i = end; i > start; i--)
        {
            if (i >= 4 && text[i - 1] == '\n' && text[i - 2] == '\n'
                && text[i - 3] == '#' && text[i - 4] == '\n')
            { bestBreak = i - 1; break; }
        }
        if (bestBreak > start + minChars) return bestBreak;

        // Try paragraph boundary
        for (int i = end; i > start; i--)
        {
            if (i >= 2 && text[i - 1] == '\n' && text[i - 2] == '\n')
            { bestBreak = i - 1; break; }
        }
        if (bestBreak > start + minChars) return bestBreak;

        // Try sentence boundary
        for (int i = end; i > start; i--)
        {
            if (i >= 2 && (text[i - 1] == '.' || text[i - 1] == '。'
                || text[i - 1] == '!' || text[i - 1] == '！'
                || text[i - 1] == '?' || text[i - 1] == '？'))
            { bestBreak = i; break; }
        }
        if (bestBreak > start + minChars) return bestBreak;

        // Line boundary
        for (int i = end; i > start; i--)
        {
            if (text[i - 1] == '\n')
            { bestBreak = i - 1; break; }
        }
        if (bestBreak > start) return bestBreak;

        return end;
    }
}

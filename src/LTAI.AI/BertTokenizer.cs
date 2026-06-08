namespace LTAI.AI;

internal static class BertTokenizer
{
    public const int MaxLength = 512;
    public const int ClsTokenId = 101;
    public const int SepTokenId = 102;
    public const int PadTokenId = 0;
    public const int UnkTokenId = 100;

    private static readonly System.Text.RegularExpressions.Regex WhitespaceRegex =
        new(@"\s+", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static Token[] TokenizeToIds(string text, Dictionary<string, int> vocab)
    {
        var normalized = NormalizeText(text);
        var words = SplitWords(normalized);
        var pieces = new List<string>(MaxLength) { "[CLS]" };
        foreach (var word in words)
        {
            pieces.AddRange(WordPiece(word, vocab));
            if (pieces.Count >= MaxLength - 1) break;
        }
        pieces.Add("[SEP]");
        if (pieces.Count > MaxLength)
        {
            pieces = pieces.Take(MaxLength - 1).ToList();
            pieces.Add("[SEP]");
        }
        var tokens = new Token[MaxLength];
        for (int i = 0; i < pieces.Count; i++)
            tokens[i] = new Token(vocab.GetValueOrDefault(pieces[i], UnkTokenId), 1);
        for (int i = pieces.Count; i < MaxLength; i++)
            tokens[i] = new Token(PadTokenId, 0);
        return tokens;
    }

    public static List<Token> BuildTokens(List<string> allPieces, int start, int end, Dictionary<string, int> vocab)
    {
        var tokens = new List<Token>(MaxLength);
        tokens.Add(new Token(vocab.GetValueOrDefault("[CLS]", ClsTokenId), 1));
        for (int i = start; i < end; i++)
        {
            var id = vocab.GetValueOrDefault(allPieces[i], UnkTokenId);
            tokens.Add(new Token(id, 1));
        }
        tokens.Add(new Token(vocab.GetValueOrDefault("[SEP]", SepTokenId), 1));
        while (tokens.Count < MaxLength)
            tokens.Add(new Token(PadTokenId, 0));
        return tokens;
    }

    public static List<Token> Tokenize(string text, Dictionary<string, int> vocab)
    {
        var normalized = NormalizeText(text);
        var words = SplitWords(normalized);
        var pieces = new List<string> { "[CLS]" };
        foreach (var word in words)
        {
            var wordPieces = WordPiece(word, vocab);
            pieces.AddRange(wordPieces);
            if (pieces.Count >= MaxLength - 1) break;
        }
        pieces.Add("[SEP]");
        if (pieces.Count > MaxLength)
        {
            pieces = pieces.Take(MaxLength - 1).ToList();
            pieces.Add("[SEP]");
        }
        var tokens = new List<Token>();
        foreach (var piece in pieces)
        {
            var id = vocab.GetValueOrDefault(piece, UnkTokenId);
            tokens.Add(new Token(id, 1));
        }
        while (tokens.Count < MaxLength)
            tokens.Add(new Token(PadTokenId, 0));
        return tokens;
    }

    public static string NormalizeText(string text)
    {
        text = text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        text = WhitespaceRegex.Replace(text, " ");
        return text.Trim();
    }

    public static List<string> SplitWords(string text)
    {
        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (char c in text)
        {
            if (c == ' ')
            {
                if (current.Length > 0) { words.Add(current.ToString()); current.Clear(); }
                continue;
            }
            if (IsCjk(c))
            {
                if (current.Length > 0) { words.Add(current.ToString()); current.Clear(); }
                words.Add(c.ToString());
            }
            else
            {
                if (char.IsPunctuation(c) && current.Length > 0) { words.Add(current.ToString()); current.Clear(); }
                current.Append(c);
            }
        }
        if (current.Length > 0) words.Add(current.ToString());
        return words;
    }

    public static List<string> WordPiece(string word, Dictionary<string, int> vocab)
    {
        if (vocab.ContainsKey(word)) return [word];
        var pieces = new List<string>();
        var chars = word.ToCharArray();
        int start = 0;
        while (start < chars.Length)
        {
            int end = chars.Length;
            string? found = null;
            while (end > start)
            {
                var sub = start == 0 ? new string(chars[start..end]) : "##" + new string(chars[start..end]);
                if (vocab.ContainsKey(sub)) { found = sub; break; }
                end--;
            }
            if (found != null)
            {
                pieces.Add(found);
                start += found.StartsWith("##") ? found.Length - 2 : found.Length;
            }
            else
            {
                pieces.Add("[UNK]");
                start++;
            }
        }
        return pieces;
    }

    public static bool IsCjk(char c) =>
        (c >= 0x4E00 && c <= 0x9FFF) ||
        (c >= 0x3400 && c <= 0x4DBF) ||
        (c >= 0x2E80 && c <= 0x2EFF) ||
        (c >= 0x3000 && c <= 0x303F) ||
        (c >= 0xFF00 && c <= 0xFFEF);
}

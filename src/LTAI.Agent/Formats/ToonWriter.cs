using System.Text;

namespace LTAI.Agent.Formats;

/// <summary>
/// Lightweight TOON (Token-Oriented Object Notation) serializer.
/// Produces compact structured output suitable for LLM context injection.
/// Follows the Toonify spec: tabular arrays, key-value pairs, minimal quoting.
/// </summary>
public sealed class ToonWriter
{
    private readonly StringBuilder _sb = new();
    private int _indent;

    public override string ToString() => _sb.ToString();
    public void Clear() { _sb.Clear(); _indent = 0; }

    /// <summary>Simple key-value:  `key: value`</summary>
    public ToonWriter KeyValue(string key, string value)
    {
        Indent();
        _sb.Append(key);
        _sb.Append(": ");
        _sb.AppendLine(Quote(value));
        return this;
    }

    /// <summary>Numeric value:  `key: 42`</summary>
    public ToonWriter KeyValue(string key, double value)
    {
        Indent();
        _sb.Append(key);
        _sb.Append(": ");
        _sb.AppendLine(value.ToString("F3"));
        return this;
    }

    /// <summary>Boolean value:  `key: true`</summary>
    public ToonWriter KeyValue(string key, bool value)
    {
        Indent();
        _sb.Append(key);
        _sb.Append(": ");
        _sb.AppendLine(value ? "true" : "false");
        return this;
    }

    /// <summary>Integer value:  `key: 3`</summary>
    public ToonWriter KeyValue(string key, int value)
    {
        Indent();
        _sb.Append(key);
        _sb.Append(": ");
        _sb.AppendLine(value.ToString());
        return this;
    }

    /// <summary>
    /// Tabular array: header + rows.
    /// `name[N]{col1,col2,...}:\n  val1,val2,...`
    /// </summary>
    public ToonWriter Table(string name, IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        if (rows.Count == 0) return this;

        Indent();
        _sb.Append(name);
        _sb.Append('[');
        _sb.Append(rows.Count);
        _sb.Append("]{");
        _sb.Append(string.Join(",", columns.Select(QuoteColumn)));
        _sb.AppendLine("}:");

            using var _ = BeginIndentScope();
        foreach (var row in rows)
        {
            Indent();
            for (int i = 0; i < row.Count; i++)
            {
                if (i > 0) _sb.Append(',');
                _sb.Append(Quote(row[i]));
            }
            _sb.AppendLine();
        }
        return this;
    }

    /// <summary>Start a nested object section.</summary>
    public ToonWriter BeginObject(string name)
    {
        Indent();
        _sb.AppendLine($"{name}:");
        _indent++;
        return this;
    }

    /// <summary>End a nested object section.</summary>
    public ToonWriter EndObject()
    {
        if (_indent > 0) _indent--;
        return this;
    }

    /// <summary>Comment line (prefixed with #).</summary>
    public ToonWriter Comment(string text)
    {
        Indent();
        _sb.Append("# ");
        _sb.AppendLine(text);
        return this;
    }

    /// <summary>Blank line.</summary>
    public ToonWriter Blank()
    {
        _sb.AppendLine();
        return this;
    }

    // ── helpers ──

    private void Indent()
    {
        if (_indent > 0) _sb.Append(' ', _indent * 2);
    }

    private IndentScope BeginIndentScope() => new(this);

    private sealed class IndentScope(ToonWriter writer) : IDisposable
    {
        public void Dispose() => writer._indent--;
    }

    /// <summary>
    /// Quote a value only when necessary (contains comma, colon, quote, newline,
    /// leading/trailing space, or is empty / a literal).
    /// </summary>
    internal static string Quote(string value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        if (value[0] == ' ' || value[^1] == ' ')
            return EscapeAndQuote(value);
        if (value.Any(c => c is ',' or ':' or '"' or '\n' or '\r'))
            return EscapeAndQuote(value);
        if (value is "true" or "false" or "null" or "yes" or "no")
            return EscapeAndQuote(value);
        if (value.Length > 0 && char.IsDigit(value[0]) && value.Any(c => c is ',' or '.'))
        {
            // Could be a number — only quote if it has leading zeros or other issues
        }
        return value;
    }

    private static string EscapeAndQuote(string value)
    {
        var escaped = value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r");
        return $"\"{escaped}\"";
    }

    private static string QuoteColumn(string col)
    {
        // Column names in header rarely need quoting
        if (col.Any(c => c is ',' or ':' or '"' or ' ' or '\n')) return EscapeAndQuote(col);
        return col;
    }
}

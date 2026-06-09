using LTAI.Mm.Core;
using LTAI.Mm.Ir;
using LTAI.Mm.Tree;

namespace LTAI.Mm.Jsonc;

public static class JsoncEmitter
{
    public static string ToJsonc(INode node)
    {
        return NodeToJsonc(node, 0);
    }

    private static string NodeToJsonc(INode node, int indent)
    {
        string pad = new string(' ', indent * 2);
        string innerPad = new string(' ', (indent + 1) * 2);

        switch (node)
        {
            case NodeScalar scalar:
                return scalar.Kind switch
                {
                    MmValueType.Str or MmValueType.Email or MmValueType.Url or MmValueType.Ip or
                    MmValueType.Uuid or MmValueType.Bytes or MmValueType.Enums or MmValueType.Media or
                    MmValueType.DateTime or MmValueType.Date or MmValueType.Time =>
                        $"\"{EscapeJson(scalar.Text)}\"",
                    MmValueType.Bool => scalar.Text.ToLowerInvariant(),
                    MmValueType.Unknown when scalar.Data == null => "null",
                    _ => scalar.Text,
                };

            case MmArray arr:
                {
                    if (arr.Children.Count == 0) return "[]";
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("[");
                    for (int i = 0; i < arr.Children.Count; i++)
                    {
                        if (arr.Tag != null) AppendTagComment(sb, innerPad, arr.Tag);
                        sb.Append(innerPad);
                        sb.Append(NodeToJsonc(arr.Children[i], indent + 1));
                        if (i < arr.Children.Count - 1) sb.Append(',');
                        sb.AppendLine();
                    }
                    sb.Append($"{pad}]");
                    return sb.ToString();
                }

            case MmMap map:
                {
                    if (map.Entries.Count == 0) return "{}";
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("{");
                    for (int i = 0; i < map.Entries.Count; i++)
                    {
                        var entry = map.Entries[i];
                        if (entry.Value.Tag != null)
                            AppendTagComment(sb, innerPad, entry.Value.Tag);
                        sb.Append($"{innerPad}\"{EscapeJson(entry.Key.Text)}\": ");
                        sb.Append(NodeToJsonc(entry.Value, indent + 1));
                        if (i < map.Entries.Count - 1) sb.Append(',');
                        sb.AppendLine();
                    }
                    sb.Append($"{pad}}}");
                    return sb.ToString();
                }

            case MmDoc doc:
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var field in doc.Fields)
                    {
                        if (field.Value.Tag != null)
                            AppendTagComment(sb, pad, field.Value.Tag);
                        sb.Append($"{pad}\"{EscapeJson(field.Key.Text)}\": ");
                        sb.AppendLine(NodeToJsonc(field.Value, indent));
                    }
                    return sb.ToString();
                }

            default:
                return "null";
        }
    }

    private static void AppendTagComment(System.Text.StringBuilder sb, string pad, Tag tag)
    {
        string tagStr = tag.ToTagString();
        if (!string.IsNullOrEmpty(tagStr))
        {
            sb.Append(pad);
            sb.Append("// mm: ");
            sb.AppendLine(tagStr);
        }
    }

    private static string EscapeJson(string s)
    {
        return s.Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t");
    }
}

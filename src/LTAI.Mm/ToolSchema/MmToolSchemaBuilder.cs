using System.Reflection;
using System.Text.Json;
using LTAI.Mm.Ir;

namespace LTAI.Mm.ToolSchema;

public static class MmToolSchemaBuilder
{
    public static JsonElement? EnhanceParameterSchema(ParameterInfo param, JsonElement? originalSchema)
    {
        var mmAttr = param.GetCustomAttribute<MMAttribute>(false);
        if (mmAttr == null || mmAttr.IsExcluded) return originalSchema;

        var tag = mmAttr.Parsed;
        var dict = new Dictionary<string, JsonElement?>();

        if (originalSchema.HasValue && originalSchema.Value.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in originalSchema.Value.EnumerateObject())
                dict[prop.Name] = prop.Value.Clone();
        }

        if (tag.Desc != null)
            dict["description"] = JsonDocument.Parse($"\"{EscapeJson(tag.Desc)}\"").RootElement;

        if (tag.Min != null)
            dict["minimum"] = JsonDocument.Parse(tag.Min).RootElement;

        if (tag.Max != null)
            dict["maximum"] = JsonDocument.Parse(tag.Max).RootElement;

        if (tag.Pattern != null)
            dict["pattern"] = JsonDocument.Parse($"\"{EscapeJson(tag.Pattern)}\"").RootElement;

        if (tag.Enums != null)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append('[');
            var items = tag.Enums.Split('|');
            for (int i = 0; i < items.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(EscapeJson(items[i])).Append('"');
            }
            sb.Append(']');
            using var arrDoc = System.Text.Json.JsonDocument.Parse(sb.ToString());
            dict["enum"] = arrDoc.RootElement.Clone();
        }

        if (tag.Nullable)
            dict["nullable"] = JsonDocument.Parse("true").RootElement;

        return BuildObject(dict);
    }

    public static JsonElement? EnhanceFunctionSchema(MethodInfo method, JsonElement? originalSchema)
    {
        if (!originalSchema.HasValue || originalSchema.Value.ValueKind != JsonValueKind.Object)
            return originalSchema;

        var dict = new Dictionary<string, JsonElement?>();

        foreach (var prop in originalSchema.Value.EnumerateObject())
        {
            if (prop.Name == "properties" && prop.Value.ValueKind == JsonValueKind.Object)
            {
                var enhancedProps = new Dictionary<string, JsonElement?>();
                foreach (var paramProp in prop.Value.EnumerateObject())
                {
                    var paramInfo = method.GetParameters()
                        .FirstOrDefault(p => p.Name == paramProp.Name);
                    if (paramInfo != null)
                    {
                        var enhanced = EnhanceParameterSchema(paramInfo, paramProp.Value.Clone());
                        enhancedProps[paramProp.Name] = enhanced;
                    }
                    else
                    {
                        enhancedProps[paramProp.Name] = paramProp.Value.Clone();
                    }
                }
                dict["properties"] = BuildObject(enhancedProps);
            }
            else
            {
                dict[prop.Name] = prop.Value.Clone();
            }
        }

        return BuildObject(dict);
    }

    public static Dictionary<string, object?> GetMmConstraints(ParameterInfo param)
    {
        var mmAttr = param.GetCustomAttribute<MMAttribute>(false);
        if (mmAttr == null || mmAttr.IsExcluded) return [];

        var tag = mmAttr.Parsed;
        var result = new Dictionary<string, object?>();

        if (tag.Min != null) result["minimum"] = tag.Min;
        if (tag.Max != null) result["maximum"] = tag.Max;
        if (tag.Pattern != null) result["pattern"] = tag.Pattern;
        if (tag.Enums != null) result["enum"] = tag.Enums.Split('|');
        if (tag.Nullable) result["nullable"] = true;
        if (tag.AllowEmpty) result["allowEmpty"] = true;

        return result;
    }

    private static JsonElement? BuildObject(Dictionary<string, JsonElement?> entries)
    {
        var sb = new System.Text.StringBuilder("{");
        bool first = true;
        foreach (var (key, value) in entries)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append($"\"{EscapeJson(key)}\":");
            sb.Append(value?.GetRawText() ?? "null");
        }
        sb.Append('}');
        return JsonDocument.Parse(sb.ToString()).RootElement;
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

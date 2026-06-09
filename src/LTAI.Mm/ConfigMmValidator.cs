using System.Reflection;
using LTAI.Core.Configuration;
using LTAI.Mm.Core;
using LTAI.Mm.Ir;

namespace LTAI.Mm;

public static class ConfigMmValidator
{
    private static readonly Dictionary<string, string> BuiltInRules = new()
    {
        ["LTAI.AI.MaxTokens"] = "type=i; desc=Max output tokens per request; min=1; max=128000",
        ["LTAI.AI.Temperature"] = "type=f64; desc=LLM sampling temperature; min=0; max=2",
        ["LTAI.AI.ContextWindowSize"] = "type=i; desc=Context window size for token budget; min=1024",
        ["LTAI.AI.GlobalTokenBudget"] = "type=i; desc=Global token budget across all users; min=10000",
        ["LTAI.AI.PerUserTokenBudget"] = "type=i; desc=Per-user daily token budget; min=1000",
        ["LTAI.AI.ResponseCacheSize"] = "type=i; desc=Response cache entries per provider; min=0; max=10000",
        ["LTAI.AI.Mode"] = "type=str; desc=LLM operational mode; enums=balanced|fast|precise|creative",
        ["LTAI.Web.Port"] = "type=i; desc=HTTP server listen port; min=80; max=65535",
        ["LTAI.Session.MaxSessions"] = "type=i; desc=Max concurrent session files; min=10; max=10000",
        ["LTAI.Session.KeyRotationMonths"] = "type=i; desc=AES key rotation interval; min=0; max=120",
        ["LTAI.Unused"] = "",
    };

    public static List<string> ValidateOptions(LTAIOptions options)
    {
        var errors = new List<string>();
        var seen = new HashSet<string>();

        WalkObject(errors, options, "LTAI", seen);
        return errors;
    }

    public static void ThrowIfInvalid(LTAIOptions options)
    {
        var errors = ValidateOptions(options);
        if (errors.Count > 0)
            throw new InvalidOperationException(
                $"Configuration validation failed:\n{string.Join("\n", errors)}");
    }

    private static void WalkObject(List<string> errors, object obj, string path, HashSet<string> seen)
    {
        if (obj == null || !seen.Add(path)) return;
        var type = obj.GetType();

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead) continue;
            var value = prop.GetValue(obj);
            var fullPath = $"{path}.{prop.Name}";

            if (BuiltInRules.TryGetValue(fullPath, out var rule) && !string.IsNullOrEmpty(rule))
            {
                var tag = Tag.Parse(rule);
                if (value != null)
                {
                    var result = Validator.Validate(value, tag);
                    if (!result.IsValid)
                        errors.Add($"{fullPath}: {result.Error}");
                }
            }

            if (value != null && IsConfigClass(prop.PropertyType))
                WalkObject(errors, value, fullPath, seen);

            if (value is System.Collections.IEnumerable enumerable && prop.PropertyType != typeof(string))
            {
                int idx = 0;
                foreach (var item in enumerable)
                {
                    if (item != null && IsConfigClass(item.GetType()))
                        WalkObject(errors, item, $"{fullPath}[{idx}]", seen);
                    idx++;
                }
            }
        }
    }

    private static bool IsConfigClass(Type type)
    {
        if (type.IsPrimitive || type == typeof(string) || type.IsEnum) return false;
        if (type.IsGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            if (def == typeof(List<>) || def == typeof(Dictionary<,>) ||
                def == typeof(IReadOnlyList<>) || def == typeof(IReadOnlyDictionary<,>) ||
                def == typeof(IEnumerable<>))
                return false;
        }
        if (type.IsArray) return false;
        return type.Namespace?.StartsWith("LTAI.Core.Configuration") == true;
    }
}

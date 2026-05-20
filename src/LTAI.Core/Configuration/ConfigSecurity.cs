namespace LTAI.Core.Configuration;

public static class ConfigSecurity
{
    private static readonly HashSet<string> DeniedKeys = new()
    {
        "api_key", "base_url", "provider",
        "deepseek_api_key", "api_base", "mcp_config_path"
    };

    private static readonly Dictionary<string, HashSet<string>> DeniedValues = new()
    {
        ["approval_policy"] = new() { "auto", "yolo" },
        ["sandbox_mode"] = new() { "danger-full-access", "danger_full_access" }
    };

    public static Dictionary<string, object> Sanitize(Dictionary<string, object> config, string source = "project")
    {
        var sanitized = new Dictionary<string, object>();
        var removed = new List<string>();

        foreach (var (key, value) in config)
        {
            if (DeniedKeys.Contains(key))
            {
                removed.Add(key);
                continue;
            }

            if (DeniedValues.TryGetValue(key, out var bannedVals) &&
                value is string sv && bannedVals.Contains(sv))
            {
                removed.Add($"{key}={sv}");
                continue;
            }

            if (value is Dictionary<string, object> subDict)
                sanitized[key] = Sanitize(subDict, $"{source}.{key}");
            else
                sanitized[key] = value;
        }

        return sanitized;
    }

    public static bool Validate(Dictionary<string, object> config)
    {
        foreach (var key in DeniedKeys)
            if (config.ContainsKey(key))
                return false;

        foreach (var (key, bannedVals) in DeniedValues)
            if (config.TryGetValue(key, out var val) && val is string sv && bannedVals.Contains(sv))
                return false;

        return true;
    }

    public static bool IsSafeConfigKey(string key, object? value = null)
    {
        if (DeniedKeys.Contains(key)) return false;
        if (value is string sv && DeniedValues.TryGetValue(key, out var banned) && banned.Contains(sv))
            return false;
        return true;
    }
}

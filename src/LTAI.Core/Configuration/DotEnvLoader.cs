namespace LTAI.Core.Configuration;

/// <summary>
/// Minimal .env file loader. Reads KEY=VALUE lines and sets them as process-level env vars.
/// Search order: 1) current directory, 2) AppContext.BaseDirectory.
/// After loading, SecretManager caches are invalidated for the loaded keys.
/// </summary>
public static class DotEnvLoader
{
    /// <summary>Load .env from default locations. Returns count of keys loaded.</summary>
    public static int Load(string? specificPath = null)
    {
        string? path = specificPath;
        if (path == null || !File.Exists(path))
        {
            // Search order: cwd → base dir
            var candidates = new[]
            {
                Path.Combine(Environment.CurrentDirectory, ".env"),
                Path.Combine(AppContext.BaseDirectory, ".env"),
                Path.Combine(AppContext.BaseDirectory, ".env.example"),
            };
            foreach (var c in candidates)
            {
                if (File.Exists(c)) { path = c; break; }
            }
        }
        if (path == null || !File.Exists(path)) return 0;

        var count = 0;
        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

            var eq = trimmed.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0) continue;

            var key = trimmed[..eq].Trim();
            var val = trimmed[(eq + 1)..].Trim();

            // Handle quoted values
            if (val.Length >= 2 && val[0] == '"' && val[^1] == '"')
                val = val[1..^1];
            else if (val.Length >= 2 && val[0] == '\'' && val[^1] == '\'')
                val = val[1..^1];

            // Strip inline comments (space+#)
            var commentIdx = val.IndexOf(" #", StringComparison.Ordinal);
            if (commentIdx > 0) val = val[..commentIdx].Trim();
            commentIdx = val.IndexOf("\t#", StringComparison.Ordinal);
            if (commentIdx > 0) val = val[..commentIdx].Trim();

            if (string.IsNullOrEmpty(key)) continue;

            // Only set if not already defined (user env takes precedence)
            var existing = Environment.GetEnvironmentVariable(key, EnvironmentVariableTarget.Process);
            if (existing != null) continue;

            Environment.SetEnvironmentVariable(key, val, EnvironmentVariableTarget.Process);
            SecretManager.Invalidate(key);
            count++;
        }
        return count;
    }
}

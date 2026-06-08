using LTAI.Core.Configuration;

namespace LTAI.TUI;

public static class ProviderHelpers
{
    public sealed record ProviderInfo(string EnvVar, string Endpoint, string Model);

    public static readonly Dictionary<string, ProviderInfo> KnownProviders = BuildKnownProviders();

    private static Dictionary<string, ProviderInfo> BuildKnownProviders()
    {
        var d = KnownKeys.All
            .Where(k => k.Endpoint != null && k.Model != null)
            .ToDictionary(k => k.Service, k => new ProviderInfo(k.EnvVar, k.Endpoint!, k.Model!));
        d["Ollama"]   = new("", "http://localhost:11434/v1", "");
        d["LMStudio"] = new("", "http://localhost:1234/v1",  "");
        d["vLLM"]     = new("", "http://localhost:8000/v1",  "");
        return d;
    }

    public static string LongestCommonPrefix(List<string> strings)
    {
        if (strings.Count == 0) return "";
        if (strings.Count == 1) return strings[0];

        var first = strings[0];
        for (int i = 0; i < first.Length; i++)
        {
            for (int j = 1; j < strings.Count; j++)
            {
                if (i >= strings[j].Length || strings[j][i] != first[i])
                    return first[..i];
            }
        }
        return first;
    }
}

using System.Text.RegularExpressions;

namespace LTAI.Agent.Utils;

internal static class GlobUtils
{
    public static Regex ToRegex(string glob)
        => RegexCache.GetOrAddGlob(glob);

    public static bool IsMatch(string name, string glob)
        => ToRegex(glob).IsMatch(name);
}

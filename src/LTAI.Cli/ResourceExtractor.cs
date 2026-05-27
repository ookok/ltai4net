using System.Reflection;

namespace LTAI.Cli;

public static class ResourceExtractor
{
    private static readonly string[] RequiredDirs = { "skills", "tools", "prompts", "rules" };

    public static void EnsureExtracted(string installPath)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceNames = assembly.GetManifestResourceNames();

        foreach (var dir in RequiredDirs)
        {
            var targetDir = Path.Combine(installPath, dir);
            Directory.CreateDirectory(targetDir);

            var prefix = $"LTAI.Cli.{dir}.";
            foreach (var name in resourceNames)
            {
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

                var relativePath = name[prefix.Length..];
                var targetPath = Path.Combine(targetDir, relativePath.Replace('.', Path.DirectorySeparatorChar));

                // Fix extension: embedded resources with dots in path get flattened
                if (!targetPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                {
                    var dotIdx = targetPath.LastIndexOf('.');
                    if (dotIdx > 0 && dotIdx > targetPath.LastIndexOf(Path.DirectorySeparatorChar))
                        targetPath = targetPath[..dotIdx] + ".md";
                }

                var targetParent = Path.GetDirectoryName(targetPath);
                if (targetParent != null) Directory.CreateDirectory(targetParent);

                if (!File.Exists(targetPath))
                {
                    using var stream = assembly.GetManifestResourceStream(name);
                    if (stream != null)
                    {
                        using var fs = File.Create(targetPath);
                        stream.CopyTo(fs);
                    }
                }
            }
        }

        // Copy config if needed
        var configDir = Path.Combine(installPath, "config");
        if (!Directory.Exists(configDir) || !Directory.GetFiles(configDir, "*.json").Any())
        {
            // Config is handled by InteractiveSetupWizard, not extracted
            Directory.CreateDirectory(configDir);
        }
    }
}

namespace LTAI.Agent.Tools.Review;

/// <summary>
/// Deterministic file grouping following OCR's smart-bundling approach.
/// Groups related files so each group can be reviewed as a unit.
/// </summary>
public sealed class DiffGroupingAnalyzer
{
    private static readonly string[] CodeExtensions = [".cs", ".cshtml", ".razor", ".xaml", ".js", ".ts", ".css", ".scss", ".html", ".mbt", ".mojo", ".cj"];
    private static readonly string[] ConfigExtensions = [".json", ".xml", ".yaml", ".yml", ".config", ".props", ".targets"];

    /// <summary>Analyze diff files and group related files together.</summary>
    public List<FileGroup> Analyze(List<DiffFileInfo> files)
    {
        var groups = new List<FileGroup>();
        var remaining = new HashSet<string>(files.Select(f => f.FilePath));
        var byDir = files.GroupBy(f => Path.GetDirectoryName(f.FilePath) ?? "")
                         .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var dir in byDir)
        {
            var dirFiles = dir.Value;

            // 1. Code-behind: .xaml + .cs, .razor + .cs
            foreach (var pair in FindCodeBehindPairs(dirFiles))
            {
                if (!remaining.Contains(pair.front) && !remaining.Contains(pair.back))
                    continue;

                var groupFiles = dirFiles.Where(f => f.FilePath == pair.front || f.FilePath == pair.back).ToList();
                if (groupFiles.Count == 0) continue;

                groups.Add(new FileGroup(
                    GroupId: $"cb-{groups.Count}",
                    GroupName: Path.GetFileNameWithoutExtension(pair.front),
                    GroupType: "code-behind",
                    Files: groupFiles));

                foreach (var f in groupFiles)
                    remaining.Remove(f.FilePath);
            }

            // 2. Interface + Implementation: IFoo.cs + Foo.cs
            foreach (var pair in FindInterfaceImplPairs(dirFiles))
            {
                if (!remaining.Contains(pair.iface) && !remaining.Contains(pair.impl))
                    continue;

                var groupFiles = dirFiles.Where(f => f.FilePath == pair.iface || f.FilePath == pair.impl).ToList();
                if (groupFiles.Count == 0) continue;

                groups.Add(new FileGroup(
                    GroupId: $"ii-{groups.Count}",
                    GroupName: Path.GetFileNameWithoutExtension(pair.impl),
                    GroupType: "interface-impl",
                    Files: groupFiles));

                foreach (var f in groupFiles)
                    remaining.Remove(f.FilePath);
            }

            // 3. Test + Source: FooTests.cs + Foo.cs, FooTest.cs + Foo.cs
            foreach (var pair in FindTestSourcePairs(dirFiles))
            {
                if (!remaining.Contains(pair.test) && !remaining.Contains(pair.source))
                    continue;

                var groupFiles = dirFiles.Where(f => f.FilePath == pair.test || f.FilePath == pair.source).ToList();
                if (groupFiles.Count == 0) continue;

                groups.Add(new FileGroup(
                    GroupId: $"ts-{groups.Count}",
                    GroupName: Path.GetFileNameWithoutExtension(pair.source),
                    GroupType: "test-source",
                    Files: groupFiles));

                foreach (var f in groupFiles)
                    remaining.Remove(f.FilePath);
            }

            // 4. Locale/Resource: Foo.resx + Foo.zh-CN.resx, etc.
            foreach (var group in FindLocaleResourceGroups(dirFiles))
            {
                var active = group.Where(f => remaining.Contains(f.FilePath)).ToList();
                if (active.Count < 2) continue;

                groups.Add(new FileGroup(
                    GroupId: $"loc-{groups.Count}",
                    GroupName: Path.GetFileNameWithoutExtension(active[0].FilePath),
                    GroupType: "locale-resource",
                    Files: active));

                foreach (var f in active)
                    remaining.Remove(f.FilePath);
            }

            // 5. Shared-prefix files in same directory
            foreach (var group in FindSharedPrefixGroups(dirFiles))
            {
                var active = group.Where(f => remaining.Contains(f.FilePath)).ToList();
                if (active.Count < 2) continue;

                groups.Add(new FileGroup(
                    GroupId: $"rel-{groups.Count}",
                    GroupName: Path.GetFileNameWithoutExtension(active[0].FilePath).Split('.').FirstOrDefault() ?? "related",
                    GroupType: "related",
                    Files: active));

                foreach (var f in active)
                    remaining.Remove(f.FilePath);
            }
        }

        // 6. Remaining standalone files
        var standalone = files.Where(f => remaining.Contains(f.FilePath)).ToList();
        foreach (var file in standalone)
        {
            groups.Add(new FileGroup(
                GroupId: $"st-{groups.Count}",
                GroupName: Path.GetFileName(file.FilePath),
                GroupType: "standalone",
                Files: [file]));
        }

        return groups;
    }

    // ── pairing helpers ──

    private static List<(string front, string back)> FindCodeBehindPairs(List<DiffFileInfo> files)
    {
        var pairs = new List<(string, string)>();
        var byBase = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var f in files)
        {
            var ext = Path.GetExtension(f.FilePath);
            if (ext == ".xaml" || ext == ".razor" || ext == ".cshtml")
            {
                var baseName = Path.GetFileNameWithoutExtension(f.FilePath);
                if (!byBase.ContainsKey(baseName))
                    byBase[baseName] = [];
                byBase[baseName].Add(f.FilePath);
            }
        }

        foreach (var (baseName, fronts) in byBase)
        {
            var codeBehind = files.FirstOrDefault(f =>
                Path.GetExtension(f.FilePath) == ".cs" &&
                Path.GetFileNameWithoutExtension(f.FilePath).Equals(baseName + ".xaml", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileNameWithoutExtension(f.FilePath).Equals(baseName, StringComparison.OrdinalIgnoreCase));

            if (codeBehind != null)
            {
                foreach (var front in fronts)
                    pairs.Add((front, codeBehind.FilePath));
            }
        }

        return pairs;
    }

    private static List<(string iface, string impl)> FindInterfaceImplPairs(List<DiffFileInfo> files)
    {
        var pairs = new List<(string, string)>();
        var fileMap = files.ToDictionary(f => Path.GetFileNameWithoutExtension(f.FilePath), f => f.FilePath, StringComparer.OrdinalIgnoreCase);

        foreach (var f in files)
        {
            var name = Path.GetFileNameWithoutExtension(f.FilePath);
            if (name.StartsWith('I') && name.Length > 1 && char.IsUpper(name[1]))
            {
                var implName = name[1..]; // IFoo → Foo
                if (fileMap.TryGetValue(implName, out var implPath))
                {
                    pairs.Add((f.FilePath, implPath));
                }
            }
        }

        return pairs;
    }

    private static List<(string test, string source)> FindTestSourcePairs(List<DiffFileInfo> files)
    {
        var pairs = new List<(string, string)>();
        var fileMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var f in files)
            fileMap[Path.GetFileNameWithoutExtension(f.FilePath)] = f.FilePath;

        foreach (var f in files)
        {
            var name = Path.GetFileNameWithoutExtension(f.FilePath);
            var sourceName = name switch
            {
                _ when name.EndsWith("Tests", StringComparison.OrdinalIgnoreCase) && name.Length > 5 => name[..^5],
                _ when name.EndsWith("Test", StringComparison.OrdinalIgnoreCase) && name.Length > 4 => name[..^4],
                _ when name.StartsWith("Test", StringComparison.OrdinalIgnoreCase) && name.Length > 4 => name[4..],
                _ => null
            };

            if (sourceName != null && fileMap.TryGetValue(sourceName, out var sourcePath) && sourcePath != f.FilePath)
            {
                pairs.Add((f.FilePath, sourcePath));
            }
        }

        return pairs;
    }

    private static List<List<DiffFileInfo>> FindLocaleResourceGroups(List<DiffFileInfo> files)
    {
        var groups = new Dictionary<string, List<DiffFileInfo>>(StringComparer.OrdinalIgnoreCase);

        foreach (var f in files)
        {
            var name = Path.GetFileNameWithoutExtension(f.FilePath);
            // Foo.zh-CN → Foo
            var baseName = name;
            var parts = name.Split('.');
            if (parts.Length >= 2 && parts[^1].Length <= 6 && parts[^1].Contains('-'))
                baseName = string.Join(".", parts[..^1]);

            if (!groups.ContainsKey(baseName))
                groups[baseName] = [];
            groups[baseName].Add(f);
        }

        return groups.Values.Where(g => g.Count >= 2).ToList();
    }

    private static List<List<DiffFileInfo>> FindSharedPrefixGroups(List<DiffFileInfo> files)
    {
        var groups = new List<List<DiffFileInfo>>();
        var byPrefix = new Dictionary<string, List<DiffFileInfo>>(StringComparer.OrdinalIgnoreCase);

        foreach (var f in files)
        {
            var name = Path.GetFileNameWithoutExtension(f.FilePath);
            var ext = Path.GetExtension(f.FilePath);

            if (!CodeExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase) &&
                !ConfigExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                continue;

            // Use first segment before '.' or first 4 chars
            var prefix = name.Contains('.') ? name.Split('.')[0] :
                         name.Length >= 4 ? name[..4] : name;

            if (!byPrefix.ContainsKey(prefix))
                byPrefix[prefix] = [];
            byPrefix[prefix].Add(f);
        }

        foreach (var kvp in byPrefix)
        {
            if (kvp.Value.Count >= 2)
                groups.Add(kvp.Value);
        }

        return groups;
    }
}

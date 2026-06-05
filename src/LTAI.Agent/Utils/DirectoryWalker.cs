using System.Collections.Concurrent;

namespace LTAI.Agent.Utils;

internal static class DirectoryWalker
{
    private static readonly HashSet<string> DefaultSkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", "packages", ".vs"
    };

    public static IEnumerable<string> Walk(string root, HashSet<string>? skipDirNames = null)
    {
        skipDirNames ??= DefaultSkipDirs;
        var queue = new ConcurrentQueue<string>();
        queue.Enqueue(root);

        while (queue.TryDequeue(out var dir))
        {
            IEnumerable<string>? subDirs = null;
            try { subDirs = Directory.EnumerateDirectories(dir); }
            catch { continue; }

            foreach (var sub in subDirs)
            {
                var name = Path.GetFileName(sub);
                if (skipDirNames.Contains(name)) continue;
                queue.Enqueue(sub);
            }

            IEnumerable<string>? files = null;
            try { files = Directory.EnumerateFiles(dir); }
            catch { continue; }

            foreach (var file in files)
                yield return file;
        }
    }

    public static string[] WalkToArray(
        string root,
        HashSet<string>? allowedExtensions = null,
        HashSet<string>? skipDirNames = null)
    {
        skipDirNames ??= DefaultSkipDirs;
        var results = new List<string>();

        var queue = new Queue<string>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var dir = queue.Dequeue();

            string[] subDirs;
            try { subDirs = Directory.GetDirectories(dir); }
            catch { continue; }

            foreach (var sub in subDirs)
            {
                var name = Path.GetFileName(sub);
                if (skipDirNames.Contains(name)) continue;
                queue.Enqueue(sub);
            }

            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { continue; }

            foreach (var file in files)
            {
                if (allowedExtensions != null)
                {
                    var ext = Path.GetExtension(file);
                    if (!allowedExtensions.Contains(ext)) continue;
                }
                results.Add(file);
            }
        }

        return results.ToArray();
    }
}

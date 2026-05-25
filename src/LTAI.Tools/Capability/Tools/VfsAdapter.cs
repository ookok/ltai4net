using LTAI.Core.System;

namespace LTAI.Tools.Tools;

public sealed class VfsAdapter
{
    private static readonly Lazy<VfsAdapter> _instance = new(() => new VfsAdapter());
    public static VfsAdapter Instance => _instance.Value;

    private readonly ResourceTree _vfs;
    private bool _registered;

    private VfsAdapter()
    {
        _vfs = ResourceTree.Instance;
    }

    public void Register()
    {
        if (_registered) return;
        _registered = true;

        var registry = UnifiedRegistry.Instance;
        registry.RegisterTool(new RegistryTool { Name = "vfs:read", Description = "Read a file from the virtual filesystem", Category = "vfs", Source = "vfs" });
        registry.RegisterTool(new RegistryTool { Name = "vfs:write", Description = "Write content to a file in the virtual filesystem", Category = "vfs", Source = "vfs" });
        registry.RegisterTool(new RegistryTool { Name = "vfs:delete", Description = "Delete a file from the virtual filesystem", Category = "vfs", Source = "vfs" });
        registry.RegisterTool(new RegistryTool { Name = "vfs:list", Description = "List contents of a virtual filesystem directory", Category = "vfs", Source = "vfs" });
        registry.RegisterTool(new RegistryTool { Name = "vfs:move", Description = "Move/rename a file in the virtual filesystem", Category = "vfs", Source = "vfs" });
        registry.RegisterTool(new RegistryTool { Name = "vfs:exists", Description = "Check if a file exists in the virtual filesystem", Category = "vfs", Source = "vfs" });

        _vfs.Mount("/ram/", "RAM", writeFn: async (path, content) =>
        {
            RamStorage.Store(path, content);
            return true;
        });
    }

    public async Task<string?> ReadAsync(string path)
    {
        var result = await _vfs.Read(path).ConfigureAwait(false);
        return result.Content;
    }

    public async Task<bool> WriteAsync(string path, string content)
    {
        var result = await _vfs.Write(path, content).ConfigureAwait(false);
        return result.Error == null;
    }

    public bool Delete(string path) => _vfs.Unmount(path);

    public async Task<List<object>> ListAsync(string path)
    {
        var result = await _vfs.List(path).ConfigureAwait(false);
        return result.Items ?? new List<object>();
    }

    public async Task<List<object>> SearchAsync(string path, string query, int limit = 20)
    {
        var result = await _vfs.Search(path, query, limit).ConfigureAwait(false);
        return result.Items ?? new List<object>();
    }

    public async Task<bool> MoveAsync(string source, string dest)
    {
        var readResult = await _vfs.Read(source).ConfigureAwait(false);
        if (readResult.Content == null) return false;
        var writeResult = await _vfs.Write(dest, readResult.Content).ConfigureAwait(false);
        if (writeResult.Error != null) return false;
        _vfs.Unmount(source);
        return true;
    }

    public async Task<bool> ExistsAsync(string path)
    {
        var result = await _vfs.Read(path).ConfigureAwait(false);
        return result.Content != null;
    }
}

internal static class RamStorage
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _store = new();
    private static readonly System.Collections.Concurrent.ConcurrentQueue<string> _insertionOrder = new();

    public static void Store(string path, string content)
    {
        if (_store.TryAdd(path, content))
            _insertionOrder.Enqueue(path);
        else
            _store[path] = content;

        while (_store.Count > 1000 && _insertionOrder.TryDequeue(out var oldest))
            _store.TryRemove(oldest, out _);
    }
    public static string? Get(string path) => _store.TryGetValue(path, out var v) ? v : null;
    public static List<object> List(string prefix) =>
        _store.Keys.Where(k => k.StartsWith(prefix)).Select(k => (object)new { path = k, size = _store[k].Length }).ToList();
    public static bool Remove(string path) => _store.TryRemove(path, out _);
}

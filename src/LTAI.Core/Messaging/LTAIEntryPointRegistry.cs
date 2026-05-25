using System.Collections.Concurrent;
using LTAI.Core.Interfaces;

namespace LTAI.Core.Messaging;

public static class LTAIEntryPointRegistry
{
    private static readonly ConcurrentDictionary<string, ILTAIEntryPoint> _entries = new();

    public static void Register(string mode, ILTAIEntryPoint entry)
    {
        _entries[mode] = entry;
    }

    public static ILTAIEntryPoint? Get(string mode)
    {
        _entries.TryGetValue(mode, out var entry);
        return entry;
    }

    public static IEnumerable<string> RegisteredModes => _entries.Keys;
}

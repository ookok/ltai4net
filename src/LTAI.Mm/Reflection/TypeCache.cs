#pragma warning disable IL2070 // NativeAOT IL warnings — reflection-based MM codec

using System.Collections.Concurrent;
using System.Reflection;
using LTAI.Mm.Core;
using LTAI.Mm.Ir;

namespace LTAI.Mm.Reflection;

internal sealed class CachedProperty
{
    public string Name { get; }
    public bool CanRead { get; }
    public bool CanWrite { get; }
    public Func<object, object?> Getter { get; }
    public Action<object, object?>? Setter { get; }
    public Tag? MmTag { get; }
    public bool Excluded { get; }
    public MmValueType InferredType { get; }

    public CachedProperty(PropertyInfo prop)
    {
        Name = prop.Name;
        CanRead = prop.CanRead;
        CanWrite = prop.CanWrite;

        if (prop.CanRead)
        {
            var getMethod = prop.GetMethod!;
            Getter = obj => getMethod.Invoke(obj, null);
        }
        else
        {
            Getter = _ => null;
        }

        if (prop.CanWrite)
        {
            var setMethod = prop.SetMethod!;
            Setter = (obj, val) => setMethod.Invoke(obj, [val]);
        }

        var mmAttr = prop.GetCustomAttribute<MMAttribute>(false);
        if (mmAttr != null)
        {
            Excluded = mmAttr.IsExcluded;
            if (!Excluded)
                MmTag = mmAttr.Parsed;
        }

        InferredType = MmTag?.Type != MmValueType.Unknown && MmTag != null
            ? MmTag.Type
            : TypeInfer.Infer(prop.PropertyType);
    }
}

internal sealed class CachedType
{
    public Type Type { get; }
    public IReadOnlyList<CachedProperty> Properties { get; }

    public CachedType(Type type)
    {
        Type = type;
        Properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead)
            .Select(p => new CachedProperty(p))
            .Where(p => !p.Excluded)
            .ToList().AsReadOnly();
    }
}

internal static class TypeMetadataCache
{
    private static readonly ConcurrentDictionary<Type, CachedType> _cache = new();

    public static CachedType Get(Type type) =>
        _cache.GetOrAdd(type, static t => new CachedType(t));
}

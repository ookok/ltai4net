using LTAI.Mm.Core;
using LTAI.Mm.Ir;

namespace LTAI.Mm.Tree;

public sealed class MmMap : INode
{
    public Tag? Tag { get; set; }
    public MmValueType Kind => MmValueType.Obj;

    public List<MmMapEntry> Entries { get; } = [];

    public T? As<T>() where T : class => this as T;
}

public sealed class MmMapEntry
{
    public NodeScalar Key { get; set; }
    public INode Value { get; set; }

    public MmMapEntry(NodeScalar key, INode value)
    {
        Key = key;
        Value = value;
    }
}

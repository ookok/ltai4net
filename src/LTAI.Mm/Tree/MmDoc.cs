using LTAI.Mm.Core;
using LTAI.Mm.Ir;

namespace LTAI.Mm.Tree;

public sealed class MmDoc : INode
{
    public Tag? Tag { get; set; }
    public MmValueType Kind => MmValueType.Doc;
    public List<MmMapEntry> Fields { get; } = [];

    public T? As<T>() where T : class => this as T;
}

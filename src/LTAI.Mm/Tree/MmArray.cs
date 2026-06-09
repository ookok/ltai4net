using LTAI.Mm.Core;
using LTAI.Mm.Ir;

namespace LTAI.Mm.Tree;

public sealed class MmArray : INode
{
    public Tag? Tag { get; set; }
    public MmValueType Kind => MmValueType.Vec;
    public List<INode> Children { get; } = [];

    public T? As<T>() where T : class => this as T;
}

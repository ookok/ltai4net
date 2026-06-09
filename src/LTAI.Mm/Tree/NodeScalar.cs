using LTAI.Mm.Core;
using LTAI.Mm.Ir;

namespace LTAI.Mm.Tree;

public sealed class NodeScalar : INode
{
    public Tag? Tag { get; set; }
    public MmValueType Kind { get; }
    public object? Data { get; }
    public string Text { get; }

    public NodeScalar(object? data, MmValueType kind, string text, Tag? tag = null)
    {
        Data = data;
        Kind = kind;
        Text = text;
        Tag = tag;
    }

    public T? As<T>() where T : class => this as T;
}

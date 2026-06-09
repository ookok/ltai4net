using LTAI.Mm.Core;
using LTAI.Mm.Ir;

namespace LTAI.Mm.Tree;

public interface INode
{
    Tag? Tag { get; set; }
    MmValueType Kind { get; }
    T? As<T>() where T : class;
}

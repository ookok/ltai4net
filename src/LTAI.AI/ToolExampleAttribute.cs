// 工具示例查询属性
// 标记该工具在用户对话中可能出现的问法示例，
// ToolRegistry 在构建 embedding 时会将其注入向量化文本，
// 显著提升语义召回的准确率。

namespace LTAI.AI;

/// <summary>
/// 标记工具的用户问法示例。
/// 每个示例是一条用户可能说的自然语言 query。
/// 可重复使用（每个工具可标注多条）。
///
/// 示例：
/// <code>
/// [Description("查询国内航班信息")]
/// [ToolExample("帮我查下明天北京飞上海的机票")]
/// [ToolExample("周末去成都最便宜的航班是哪班")]
/// public async Task&lt;string&gt; SearchFlights(...)
/// </code>
///
/// ToolRegistry.InitializeAsync 会自动收集此属性，
/// 将示例注入 embedding 文本以拉近与用户 query 的向量距离。
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class ToolExampleAttribute : Attribute
{
    /// <summary>用户可能说的自然语言问法。</summary>
    public string Query { get; }

    /// <param name="query">一条用户可能说的自然语言问法。</param>
    public ToolExampleAttribute(string query)
    {
        Query = query;
    }
}

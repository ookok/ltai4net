using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace LTAI.Agent.Context;

public sealed class MemoryAuthorityProvider : AIContextProvider
{
    private static readonly string AuthorityText = """
        <memory-authority>
        ═══ 记忆权威层级 ═══
        以下是硬性规则，必须严格遵守:

        1. 上下文中注入的记忆片段（来自 PalaceStore / Knowledge Graph / 会话历史）是**权威**的。
        2. **禁止**对这些已注入的记忆调用任何搜索/查询工具去验证、重新查找或确认。
        3. 如果记忆片段之间出现矛盾，以时间戳更近的为准。
        4. 如果你发现记忆片段明显过时或与当前对话矛盾，在回复中说明分歧即可，不要额外调用工具。
        5. 本规则的优先级高于任何"信息不足请搜索"的通用指令。
        ════════════════════
        </memory-authority>
        """;

    public MemoryAuthorityProvider() : base(null, null, null) { }

    public override IReadOnlyList<string> StateKeys => ["MemoryAuthority"];

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken ct = default)
    {
        var ctx = new AIContext
        {
            Messages = [new ChatMessage(ChatRole.System, AuthorityText)]
        };
        return ValueTask.FromResult(ctx);
    }
}

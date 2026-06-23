using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace LTAI.Agent.Context;

public sealed class MemoryAuthorityProvider : AIContextProvider
{
    private static readonly string AuthorityText = """
        <memory-authority>
        ═══ 记忆权威与治理规则 ═══
        以下是硬性规则，必须严格遵守:

        1. **权威性**: 上下文中注入的记忆片段（来自 PalaceStore / Knowledge Graph / 会话历史）是**权威**的。
        2. **禁止重复查询**: **禁止**对已注入的记忆调用任何搜索/查询工具去验证、重新查找或确认。
        3. **矛盾处理**: 如果记忆片段之间出现矛盾，以时间戳更近的为准。
        4. **过时说明**: 如果发现记忆片段明显过时或与当前对话矛盾，在回复中说明分歧即可，不要额外调用工具。
        5. **优先级**: 本规则的优先级高于任何"信息不足请搜索"的通用指令。

        ═══ 访问控制规则（GateMem 记忆治理）═══
        每个记忆条目有 scope 标签控制可见性:
        - `scope=private`: **只能由创建该记忆的用户（principal）访问**。其他用户不可见、不可查询、不可引用。
        - `scope=shared`: 所有用户可见、可查询。
        - `scope=role:*`: 特定角色可见（如 `role:admin`）。

        遗忘规则:
        - 用户可以删除自己的记忆（按 ID 或 room）。
        - 已删除的记忆不可再被查询或引用。
        - 带 `expires_at` 的记忆到期后自动清除。
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

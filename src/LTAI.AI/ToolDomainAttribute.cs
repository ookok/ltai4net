// 工具领域标注属性
// 标记工具所属的功能领域，Tool RAG 按领域分层召回，
// 减少跨领域误召，提升工具选择精准度。

namespace LTAI.AI;

/// <summary>
/// 标记工具所属的功能领域。
/// ToolRegistry 在构建 embedding 文本时会注入 domain 信息，
/// ToolRetrievalProvider 支持按 domain 过滤召回结果。
///
/// 预定义领域：
///   core      — 核心文件操作（读/写/编/搜/目录）
///   file      — 文件管理（复制/移动/删除/下载/信息）
///   git       — Git 版本控制
///   code      — 代码分析（符号/查找）
///   web       — 网络操作（搜索/抓取）
///   eia       — 环境评价（大气/噪声/水质/标准）
///   office    — Office 文档（Excel/Word）
///   flowchart — 图表生成（流程图/时序图/类图）
///   system    — 系统信息（进程/网络/环境变量/时间）
///   workflow  — 工作流编排
///   subagent  — 子 Agent 调度
///   container — Docker 容器
///   memory    — 记忆管理
///   plan      — 计划审批
///   choice    — 用户选择
///   task      — 待办事项
///   tool      — 工具自身管理
///   shell     — Shell 命令执行
///   sandbox   — 沙箱执行
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ToolDomainAttribute : Attribute
{
    /// <summary>领域名称，如 "core"、"git"、"web"。</summary>
    public string Domain { get; }

    /// <param name="domain">领域名称。</param>
    public ToolDomainAttribute(string domain)
    {
        Domain = domain;
    }
}

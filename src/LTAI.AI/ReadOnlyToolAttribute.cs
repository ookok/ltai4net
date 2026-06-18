// Marks a tool method as read-only, usable by read-only subagents.
// Replaces fragile prefix-based matching in SubagentTools.FilterTools.

namespace LTAI.AI;

/// <summary>
/// Marks a tool method as read-only. Only tools with this attribute
/// are exposed to explore/review/security_review subagents.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ReadOnlyToolAttribute : Attribute;

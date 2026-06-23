namespace LTAI.AI;

/// <summary>
/// Declares the permission requirements for a tool or tool method.
/// Used by agent builders to filter tools based on agent permissions,
/// and by SubagentTools to enforce read-only restrictions.
/// Optional — tools without this attribute default to ToolPermission.All.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ToolPermissionAttribute : Attribute
{
    /// <summary>Required permissions.</summary>
    public ToolPermission Required { get; }

    /// <param name="required">One or more ToolPermission flags.</param>
    public ToolPermissionAttribute(ToolPermission required)
    {
        Required = required;
    }
}

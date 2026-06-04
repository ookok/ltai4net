namespace LTAI.Core.Session;

public sealed record SessionInfo(string Name, string DisplayName, string? ParentId = null);

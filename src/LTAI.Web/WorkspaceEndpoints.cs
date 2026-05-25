using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LTAI.Web;

public sealed class WorkspaceInfo
{
    public string WorkspaceId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Owner { get; set; } = "";
    public string OwnerName { get; set; } = "";
    public string Role { get; set; } = "";
    public int MemberCount { get; set; }
    public int ProjectCount { get; set; }
    public double CreatedAt { get; set; }
}

public sealed class CreateWorkspaceRequest
{
    public string Name { get; set; } = "";
}

public sealed class InviteMemberRequest
{
    public string UserId { get; set; } = "";
    public string Role { get; set; } = "editor";
}

public static class WorkspaceEndpoints
{
    private static readonly ConcurrentDictionary<string, WorkspaceRecord> _workspaces = new();
    private static readonly string[] ValidRoles = { "owner", "editor", "viewer" };
    private static readonly Timer _workspaceCleanupTimer;

    static WorkspaceEndpoints()
    {
        _workspaceCleanupTimer = new Timer(CleanupOldWorkspaces, null, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));
    }

    private static void CleanupOldWorkspaces(object? state)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddHours(-24);
            foreach (var (key, ws) in _workspaces)
            {
                if (ws.LastAccessedAt < cutoff)
                    _workspaces.TryRemove(key, out _);
            }
        }
        catch { /* timer callback must not throw */ }
    }

    public static void MapWorkspaceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/workspaces", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync().ConfigureAwait(false);
            var req = JsonSerializer.Deserialize<CreateWorkspaceRequest>(body);

            if (req == null || string.IsNullOrWhiteSpace(req.Name))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new { error = "Workspace name is required" });
                return;
            }

            var userId = context.Request.Headers["X-User-Id"].FirstOrDefault() ?? "anonymous";
            var wsId = $"ws_{Guid.NewGuid():N}"[..20];
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var ws = new WorkspaceRecord
            {
                WorkspaceId = wsId,
                Name = req.Name.Trim(),
                Owner = userId,
                CreatedAt = now,
                Members = new ConcurrentDictionary<string, MemberRecord>
                {
                    [userId] = new MemberRecord { Role = "owner", JoinedAt = now }
                }
            };

            if (_workspaces.Count >= 500)
            {
                var oldest = _workspaces.Values.MinBy(w => w.CreatedAt);
                if (oldest != null)
                    _workspaces.TryRemove(oldest.WorkspaceId, out _);
            }
            _workspaces[wsId] = ws;

            await context.Response.WriteAsJsonAsync(new WorkspaceInfo
            {
                WorkspaceId = wsId,
                Name = ws.Name,
                Owner = userId,
                Role = "owner",
                MemberCount = 1,
                ProjectCount = 0,
                CreatedAt = now
            });
        });

        endpoints.MapGet("/api/workspaces", async (HttpContext context) =>
        {
            var userId = context.Request.Headers["X-User-Id"].FirstOrDefault() ?? "anonymous";

            var result = _workspaces.Values
                .Where(w => w.Members.ContainsKey(userId))
                .Select(w => new WorkspaceInfo
                {
                    WorkspaceId = w.WorkspaceId,
                    Name = w.Name,
                    Owner = w.Owner,
                    Role = w.Members.TryGetValue(userId, out var m) ? m.Role : "viewer",
                    MemberCount = w.Members.Count,
                    ProjectCount = w.Projects.Count,
                    CreatedAt = w.CreatedAt
                })
                .OrderByDescending(w => w.CreatedAt)
                .ToList();

            await context.Response.WriteAsJsonAsync(result).ConfigureAwait(false);
        });

        endpoints.MapGet("/api/workspaces/{workspaceId}", async (string workspaceId, HttpContext context) =>
        {
            var userId = context.Request.Headers["X-User-Id"].FirstOrDefault() ?? "anonymous";

            if (!_workspaces.TryGetValue(workspaceId, out var ws))
            {
                context.Response.StatusCode = 404;
                await context.Response.WriteAsJsonAsync(new { error = "Workspace not found" });
                return;
            }

            if (!ws.Members.TryGetValue(userId, out var member))
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsJsonAsync(new { error = "Not a workspace member" });
                return;
            }

            ws.LastAccessedAt = DateTime.UtcNow;

            await context.Response.WriteAsJsonAsync(new WorkspaceInfo
            {
                WorkspaceId = ws.WorkspaceId,
                Name = ws.Name,
                Owner = ws.Owner,
                Role = member.Role,
                MemberCount = ws.Members.Count,
                ProjectCount = ws.Projects.Count,
                CreatedAt = ws.CreatedAt
            }).ConfigureAwait(false);
        });

        endpoints.MapDelete("/api/workspaces/{workspaceId}", async (string workspaceId, HttpContext context) =>
        {
            var userId = context.Request.Headers["X-User-Id"].FirstOrDefault() ?? "anonymous";

            if (!_workspaces.TryGetValue(workspaceId, out var ws))
            {
                context.Response.StatusCode = 404;
                return;
            }

            if (ws.Owner != userId)
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsJsonAsync(new { error = "Only workspace owner can delete" });
                return;
            }

            _workspaces.TryRemove(workspaceId, out _);
            await context.Response.WriteAsJsonAsync(new { ok = true, workspace_id = workspaceId }).ConfigureAwait(false);
        });

        endpoints.MapPost("/api/workspaces/{workspaceId}/members", async (string workspaceId, HttpContext context) =>
        {
            var userId = context.Request.Headers["X-User-Id"].FirstOrDefault() ?? "anonymous";
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync().ConfigureAwait(false);
            var req = JsonSerializer.Deserialize<InviteMemberRequest>(body);

            if (req == null || string.IsNullOrWhiteSpace(req.UserId))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new { error = "User ID is required" });
                return;
            }

            if (!ValidRoles.Contains(req.Role))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new { error = $"Role must be one of: {string.Join(", ", ValidRoles)}" });
                return;
            }

            if (!_workspaces.TryGetValue(workspaceId, out var ws))
            {
                context.Response.StatusCode = 404;
                return;
            }

            if (ws.Owner != userId)
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsJsonAsync(new { error = "Only workspace owner can invite members" });
                return;
            }

            ws.Members[req.UserId] = new MemberRecord
            {
                Role = req.Role,
                JoinedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };

            await context.Response.WriteAsJsonAsync(new { ok = true, user_id = req.UserId, role = req.Role }).ConfigureAwait(false);
        });

        endpoints.MapDelete("/api/workspaces/{workspaceId}/members/{targetUserId}", async (string workspaceId, string targetUserId, HttpContext context) =>
        {
            var userId = context.Request.Headers["X-User-Id"].FirstOrDefault() ?? "anonymous";

            if (!_workspaces.TryGetValue(workspaceId, out var ws))
            {
                context.Response.StatusCode = 404;
                return;
            }

            if (userId != targetUserId && ws.Owner != userId)
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsJsonAsync(new { error = "Only workspace owner can remove others" });
                return;
            }

            if (ws.Members.TryGetValue(targetUserId, out var member) && member.Role == "owner")
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsJsonAsync(new { error = "Cannot remove workspace owner" });
                return;
            }

            ws.Members.TryRemove(targetUserId, out _);
            await context.Response.WriteAsJsonAsync(new { ok = true, removed_user = targetUserId }).ConfigureAwait(false);
        });

        endpoints.MapGet("/api/workspaces/{workspaceId}/members", async (string workspaceId, HttpContext context) =>
        {
            if (!_workspaces.TryGetValue(workspaceId, out var ws))
            {
                context.Response.StatusCode = 404;
                return;
            }

            var members = ws.Members.Select(kvp => new
            {
                user_id = kvp.Key,
                role = kvp.Value.Role,
                joined_at = kvp.Value.JoinedAt
            }).ToList();

            await context.Response.WriteAsJsonAsync(members).ConfigureAwait(false);
        });
    }
}

internal sealed class WorkspaceRecord
{
    public string WorkspaceId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Owner { get; set; } = "";
    public double CreatedAt { get; set; }
    public DateTime LastAccessedAt { get; set; } = DateTime.UtcNow;
    public ConcurrentDictionary<string, MemberRecord> Members { get; set; } = new();
    public ConcurrentBag<object> Projects { get; set; } = new();
}

internal sealed class MemberRecord
{
    public string Role { get; set; } = "";
    public double JoinedAt { get; set; }
}

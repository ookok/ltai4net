using LTAI.Core.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace LTAI.MAF;

public static class MCPEndpoints
{
    public static void MapMCPEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/mcp", async (
            HttpContext context,
            AIToolRegistry registry,
            CancellationToken ct) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync(ct);
                var request = JsonSerializer.Deserialize<JsonElement>(body);

                var method = request.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";
                string? id = null;
                if (request.TryGetProperty("id", out var iElem))
                    id = iElem.GetRawText();

                object responseObj = method switch
                {
                    "initialize" => new
                    {
                        protocolVersion = "2024-11-05",
                        capabilities = new { tools = new { }, resources = new { } },
                        serverInfo = new { name = "LTAI", version = "5.5.0" }
                    },
                    "tools/list" => new
                    {
                        tools = registry.ListTools().Select(t =>
                        {
                            var func = registry.GetTool(t);
                            object? inputSchema = new { type = "object" };
                            if (func is Microsoft.Extensions.AI.AIFunction aiFunc)
                            {
                                try { inputSchema = JsonSerializer.Deserialize<object>(aiFunc.JsonSchema.ToString()); } catch { }
                            }
                            return new
                            {
                                name = t,
                                description = func?.GetType().GetProperty("Description")?.GetValue(func)?.ToString() ?? "",
                                inputSchema
                            };
                        })
                    },
                    "tools/call" => new
                    {
                        content = new[]
                        {
                            new
                            {
                                type = "text",
                                text = JsonSerializer.Serialize(
                                    await registry.InvokeAsync(
                                        request.TryGetProperty("params", out var p) && p.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                                        request.TryGetProperty("params", out var p2) && p2.TryGetProperty("arguments", out var a)
                                            ? JsonSerializer.Deserialize<Dictionary<string, object?>>(a.GetRawText()) ?? new()
                                            : new(),
                                        ct) ?? new { error = "Tool not found" })
                            }
                        }
                    },
                    _ => new { error = new { code = -32601, message = $"Method not found: {method}" } }
                };

                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = id,
                    result = responseObj is Dictionary<string, object?> d ? (object)d : responseObj,
                    error = responseObj is Dictionary<string, object?> de && de.ContainsKey("error") ? de["error"] : null
                }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = 500;
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", error = new { code = -32603, message = ex.Message } }));
            }
        });
    }
}

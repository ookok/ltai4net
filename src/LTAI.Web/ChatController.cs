using System.Runtime.CompilerServices;
using System.Text.Json;
using LTAI.Agent;
using Microsoft.AspNetCore.Mvc;

namespace LTAI.Web;

/// <summary>
/// Chat API for LTAI. Supports non-streaming and SSE streaming modes.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class ChatController : ControllerBase
{
    private readonly ChatAgent _agent;
    private readonly ILogger<ChatController> _logger;

    public ChatController(ChatAgent agent, ILogger<ChatController> logger)
    {
        _agent = agent;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/chat — Non-streaming chat.
    /// Body: {"message": "hello", "userId": "optional"}
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Post([FromBody] ChatRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
            return BadRequest(new { error = "message is required" });

        try
        {
            var reply = await _agent.ChatAsync(request.Message, request.UserId ?? "default");
            return Ok(new ChatResponse(reply));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chat failed");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// GET /api/chat/stream?message=hello — SSE streaming chat.
    /// </summary>
    [HttpGet("stream")]
    public async Task Stream([FromQuery] string message, [FromQuery] string? userId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            Response.StatusCode = 400;
            await Response.WriteAsync("{\"error\":\"message is required\"}", ct);
            return;
        }

        Response.Headers["Content-Type"] = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["Connection"] = "keep-alive";

        try
        {
            await foreach (var update in _agent.ChatStreamingAsync(message, ct))
            {
                if (update.Text == null) continue;
                var payload = JsonSerializer.Serialize(new { text = update.Text });
                await Response.WriteAsync($"data: {payload}\n\n", ct);
                await Response.Body.FlushAsync(ct);
            }
            await Response.WriteAsync("data: [DONE]\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stream chat failed");
            var err = JsonSerializer.Serialize(new { error = ex.Message });
            await Response.WriteAsync($"data: {err}\n\n", ct);
        }
    }

    // ═══════════════════════════════════════════
    //  DTOs
    // ═══════════════════════════════════════════

    public sealed record ChatRequest(string Message, string? UserId = null);
    public sealed record ChatResponse(string Reply);
}

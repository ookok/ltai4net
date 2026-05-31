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

    private static readonly TimeSpan ChatTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StreamTimeout = TimeSpan.FromSeconds(300);

    public ChatController(ChatAgent agent, ILogger<ChatController> logger)
    {
        _agent = agent;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/chat — Non-streaming chat with 60s timeout.
    /// Body: {"message": "hello", "userId": "optional"}
    /// Headers: X-API-Key (optional, if configured)
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Post([FromBody] ChatRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
            return BadRequest(new { error = "message is required", type = "validation_error" });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            HttpContext.RequestAborted);
        cts.CancelAfter(ChatTimeout);

        try
        {
            var reply = await _agent.ChatAsync(
                request.Message, request.UserId ?? RequestTraceId(), cts.Token);
            return Ok(new ChatResponse(reply));
        }
        catch (OperationCanceledException) when (!HttpContext.RequestAborted.IsCancellationRequested)
        {
            return StatusCode(408, new { error = "Request timed out", type = "timeout" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chat failed for user={UserId}", request.UserId);
            return StatusCode(500, new { error = "Internal error", type = "internal_error" });
        }
    }

    /// <summary>
    /// GET /api/chat/stream?message=hello — SSE streaming with 300s timeout.
    /// </summary>
    [HttpGet("stream")]
    public async Task Stream([FromQuery] string message, [FromQuery] string? userId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            Response.StatusCode = 400;
            await Response.WriteAsync(
                "{\"error\":\"message is required\",\"type\":\"validation_error\"}", ct);
            return;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        timeoutCts.CancelAfter(StreamTimeout);

        Response.Headers["Content-Type"] = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["Connection"] = "keep-alive";
        Response.Headers["X-Accel-Buffering"] = "no";  // Disable nginx buffering

        try
        {
            await foreach (var update in _agent.ChatStreamingAsync(message, timeoutCts.Token)
                .ConfigureAwait(false))
            {
                if (update.Text == null) continue;
                var payload = JsonSerializer.Serialize(new { text = update.Text });
                await Response.WriteAsync($"data: {payload}\n\n", ct);
                await Response.Body.FlushAsync(ct);
            }
            await Response.WriteAsync("data: [DONE]\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var err = JsonSerializer.Serialize(new { error = "Stream timed out", type = "timeout" });
            await Response.WriteAsync($"data: {err}\n\n", ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stream chat failed");
            var err = JsonSerializer.Serialize(new { error = "Internal error", type = "internal_error" });
            await Response.WriteAsync($"data: {err}\n\n", ct);
        }
    }

    /// <summary>
    /// Use trace identifier as fallback userId for request-level isolation.
    /// </summary>
    private string RequestTraceId() =>
        HttpContext.TraceIdentifier;
}

// ═══════════════════════════════════════════
//  DTOs
// ═══════════════════════════════════════════

public sealed record ChatRequest(string Message, string? UserId = null);
public sealed record ChatResponse(string Reply);

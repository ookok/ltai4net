using System.Runtime.CompilerServices;
using System.Text.Json;
using LTAI.Agent;
using LTAI.Web.Rendering;
using Microsoft.AspNetCore.Mvc;

namespace LTAI.Web;

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

    [HttpPost]
    public async Task<IActionResult> Post([FromBody] ChatRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
            return BadRequest(new { error = "message is required", type = "validation_error" });
        if (request.Message.Length > 50000)
            return BadRequest(new { error = "message exceeds 50000 character limit", type = "validation_error" });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            HttpContext.RequestAborted);
        cts.CancelAfter(ChatTimeout);

        try
        {
            var reply = await _agent.ChatAsync(
                request.Message, userId: request.UserId ?? RequestTraceId(), ct: cts.Token).ConfigureAwait(false);
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

    [HttpGet("stream")]
    public async Task Stream([FromQuery] string message, [FromQuery] string? userId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            Response.StatusCode = 400;
            await Response.WriteAsync(
                "{\"error\":\"message is required\",\"type\":\"validation_error\"}", ct).ConfigureAwait(false);
            return;
        }
        if (message.Length > 50000)
        {
            Response.StatusCode = 400;
            await Response.WriteAsync(
                "{\"error\":\"message exceeds 50000 character limit\",\"type\":\"validation_error\"}", ct).ConfigureAwait(false);
            return;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        timeoutCts.CancelAfter(StreamTimeout);

        Response.Headers["Content-Type"] = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["Connection"] = "keep-alive";
        Response.Headers["X-Accel-Buffering"] = "no";

        var renderer = new WebChatRenderer(Response);

        try
        {
            await foreach (var update in _agent.ChatStreamingAsync(message, ct: timeoutCts.Token)
                .ConfigureAwait(false))
            {
                if (update.Text == null) continue;
                renderer.OnTextDelta(update.Text);
            }
            await renderer.WriteDoneAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!HttpContext.RequestAborted.IsCancellationRequested)
        {
            await renderer.WriteErrorAsync("Stream timed out").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stream chat failed");
            await renderer.WriteErrorAsync("Internal error").ConfigureAwait(false);
        }
    }

    private string RequestTraceId() =>
        HttpContext.TraceIdentifier;
}

public sealed record ChatRequest(string Message, string? UserId = null);
public sealed record ChatResponse(string Reply);

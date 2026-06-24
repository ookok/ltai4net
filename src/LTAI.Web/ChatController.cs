using System.Runtime.CompilerServices;
using System.Text.Json;
using LTAI.Agent;
using LTAI.Core;
using LTAI.Core.Configuration;
using LTAI.Web.Rendering;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace LTAI.Web;

[ApiController]
[Route("api/[controller]")]
public class ChatController : ControllerBase
{
    private readonly ChatAgent _agent;
    private readonly ILogger<ChatController> _logger;
    private readonly TimeSpan _chatTimeout;
    private readonly TimeSpan _streamTimeout;
    private readonly int _maxMessageLength;
    private readonly ChatScope _scope;

    public ChatController(ChatAgent agent, ILogger<ChatController> logger,
        ChatScope scope, IOptions<LTAIOptions>? options = null)
    {
        _agent = agent;
        _logger = logger;
        _scope = scope;
        var webConfig = options?.Value.Web;
        _chatTimeout = TimeSpan.FromSeconds(webConfig?.ChatTimeoutSeconds ?? 60);
        _streamTimeout = TimeSpan.FromSeconds(webConfig?.StreamTimeoutSeconds ?? 300);
        _maxMessageLength = webConfig?.MaxMessageLength ?? 50000;
    }

    [HttpPost]
    public async Task<IActionResult> Post([FromBody] ChatRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
            return BadRequest(new { error = "message is required", type = "validation_error" });
        if (request.Message.Length > _maxMessageLength)
            return BadRequest(new { error = $"message exceeds {_maxMessageLength} character limit", type = "validation_error" });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            HttpContext.RequestAborted);
        cts.CancelAfter(_chatTimeout);

        try
        {
            var reply = await _agent.ChatAsync(
                request.Message, userId: request.UserId ?? _scope.TraceId, ct: cts.Token).ConfigureAwait(false);
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
        if (message.Length > _maxMessageLength)
        {
            Response.StatusCode = 400;
            await Response.WriteAsync(
                $"{{\"error\":\"message exceeds {_maxMessageLength} character limit\",\"type\":\"validation_error\"}}", ct).ConfigureAwait(false);
            return;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        timeoutCts.CancelAfter(_streamTimeout);

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
                await renderer.OnTextDelta(update.Text).ConfigureAwait(false);
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

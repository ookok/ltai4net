using System.Net;
using System.Text.Json;

namespace LTAI.Web.Middleware;

/// <summary>
/// Global exception handling middleware.
/// Catches all unhandled exceptions, logs them, and returns a consistent JSON error response.
/// Never exposes stack traces to clients.
/// </summary>
public sealed class ExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionMiddleware> _logger;

    public ExceptionMiddleware(RequestDelegate next, ILogger<ExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Client disconnected — not an error
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception processing {Method} {Path}",
                context.Request.Method, context.Request.Path);

            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            context.Response.ContentType = "application/json";

            var error = new
            {
                error = "Internal server error",
                requestId = context.TraceIdentifier,
                type = "internal_error"
            };

            await context.Response.WriteAsync(JsonSerializer.Serialize(error));
        }
    }
}

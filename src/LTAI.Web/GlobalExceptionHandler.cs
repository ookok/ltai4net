using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace LTAI.Web;

public static class GlobalExceptionHandler
{
    public static IApplicationBuilder UseLTAIExceptionHandler(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            try
            {
                await next();
            }
            catch (Exception ex)
            {
                var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("GlobalExceptionHandler");
                logger.LogError(ex, "Unhandled exception: {Path} {Method}", context.Request.Path, context.Request.Method);

                context.Response.StatusCode = 500;
                context.Response.ContentType = "application/json";
                var response = new { error = "Internal server error", traceId = context.TraceIdentifier };
                await context.Response.WriteAsJsonAsync(response);
            }
        });
    }
}

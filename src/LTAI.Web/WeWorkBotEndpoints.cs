using System.Text;
using LTAI.Tools.Integration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace LTAI.Web;

public static class WeWorkBotEndpoints
{
    public static void MapWeWorkBotEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/bot/wework", async (HttpContext context) =>
        {
            var msgSignature = context.Request.Query["msg_signature"].FirstOrDefault() ?? "";
            var timestamp = context.Request.Query["timestamp"].FirstOrDefault() ?? "";
            var nonce = context.Request.Query["nonce"].FirstOrDefault() ?? "";
            var echostr = context.Request.Query["echostr"].FirstOrDefault() ?? "";

            if (string.IsNullOrEmpty(msgSignature) || string.IsNullOrEmpty(echostr))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("missing parameters");
                return;
            }

            var crypt = endpoints.ServiceProvider.GetRequiredService<WXBizMsgCrypt>();
            var result = crypt.DecryptMsg(msgSignature, timestamp, nonce, echostr);

            if (string.IsNullOrEmpty(result))
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsync("verification failed");
                return;
            }

            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync(result).ConfigureAwait(false);
        });

        endpoints.MapPost("/api/bot/wework", async (HttpContext context) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
                var xmlBody = await reader.ReadToEndAsync().ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(xmlBody))
                {
                    context.Response.ContentType = "text/plain";
                    await context.Response.WriteAsync("success");
                    return;
                }

                var bot = endpoints.ServiceProvider.GetRequiredService<WeWorkBot>();
                var reply = await bot.HandleMessageAsync(xmlBody).ConfigureAwait(false);

                if (string.IsNullOrEmpty(reply))
                {
                    context.Response.ContentType = "text/plain";
                    await context.Response.WriteAsync("success");
                }
                else
                {
                    context.Response.ContentType = "application/xml";
                    await context.Response.WriteAsync(reply).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = 500;
                context.Response.ContentType = "text/plain";
                await context.Response.WriteAsync($"error: {ex.Message}");
            }
        });
    }
}

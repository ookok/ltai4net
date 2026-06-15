using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using LTAI.Web.Middleware;

namespace LTAI.Web.Tests;

public sealed class MiddlewareTests
{
    // ═══════════════════════════════════════════════
    //  ExceptionMiddleware
    // ═══════════════════════════════════════════════

    [Fact]
    public async Task ExceptionMiddleware_UnhandledException_Returns500()
    {
        var builder = new WebHostBuilder()
            .ConfigureServices(s => s.AddRouting())
            .Configure(app =>
            {
                app.UseMiddleware<ExceptionMiddleware>();
                app.Run(_ => throw new InvalidOperationException("test error"));
            });
        using var server = new TestServer(builder);
        var client = server.CreateClient();
        var resp = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
    }

    [Fact]
    public async Task ExceptionMiddleware_CancelledRequest_ReturnsNoContent()
    {
        var builder = new WebHostBuilder()
            .ConfigureServices(s => s.AddRouting())
            .Configure(app =>
            {
                app.UseMiddleware<ExceptionMiddleware>();
                app.Run(ctx =>
                {
                    var cts = new CancellationTokenSource();
                    cts.Cancel();
                    return Task.FromCanceled(cts.Token);
                });
            });
        using var server = new TestServer(builder);
        var client = server.CreateClient();
        var resp = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact]
    public async Task ExceptionMiddleware_NormalRequest_PassesThrough()
    {
        var builder = new WebHostBuilder()
            .ConfigureServices(s => s.AddRouting())
            .Configure(app =>
            {
                app.UseMiddleware<ExceptionMiddleware>();
                app.Run(async ctx =>
                {
                    ctx.Response.StatusCode = 200;
                    await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("ok"));
                });
            });
        using var server = new TestServer(builder);
        var client = server.CreateClient();
        var resp = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("ok", await resp.Content.ReadAsStringAsync());
    }

    // ═══════════════════════════════════════════════
    //  ApiKeyMiddleware
    // ═══════════════════════════════════════════════

    [Fact]
    public async Task ApiKeyMiddleware_HealthEndpoint_Skipped()
    {
        var builder = new WebHostBuilder()
            .ConfigureServices(s => s.AddRouting())
            .Configure(app =>
            {
                app.UseMiddleware<ApiKeyMiddleware>();
                app.Run(async ctx =>
                {
                    ctx.Response.StatusCode = 200;
                    await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("healthy"));
                });
            });
        using var server = new TestServer(builder);
        var client = server.CreateClient();
        var resp = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task ApiKeyMiddleware_ReadyEndpoint_Skipped()
    {
        var builder = new WebHostBuilder()
            .ConfigureServices(s => s.AddRouting())
            .Configure(app =>
            {
                app.UseMiddleware<ApiKeyMiddleware>();
                app.Run(async ctx =>
                {
                    ctx.Response.StatusCode = 200;
                    await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("ready"));
                });
            });
        using var server = new TestServer(builder);
        var client = server.CreateClient();
        var resp = await client.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task ApiKeyMiddleware_NoKeyConfigured_DevModeDisabled_Returns401()
    {
        var builder = new WebHostBuilder()
            .ConfigureServices(s => s.AddRouting())
            .Configure(app =>
            {
                app.UseMiddleware<ApiKeyMiddleware>();
                app.Run(async ctx =>
                {
                    ctx.Response.StatusCode = 200;
                    await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("ok"));
                });
            });
        using var server = new TestServer(builder);
        var client = server.CreateClient();
        var resp = await client.GetAsync("/api/test");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task ApiKeyMiddleware_ValidApiKey_Passes()
    {
        var apiKey = "sk-test-key-" + Guid.NewGuid().ToString("N")[..16];
        Environment.SetEnvironmentVariable("LTAI_API_KEY", apiKey);
        try
        {
            var builder = new WebHostBuilder()
                .ConfigureServices(s => s.AddRouting())
                .Configure(app =>
                {
                    app.UseMiddleware<ApiKeyMiddleware>();
                    app.Run(async ctx =>
                    {
                        ctx.Response.StatusCode = 200;
                        await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("ok"));
                    });
                });
            using var server = new TestServer(builder);
            var client = server.CreateClient();
            var req = new HttpRequestMessage(HttpMethod.Get, "/api/test");
            req.Headers.Add("X-API-Key", apiKey);
            var resp = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LTAI_API_KEY", null);
        }
    }

    [Fact]
    public async Task ApiKeyMiddleware_BearerToken_Passes()
    {
        var apiKey = "sk-bearer-" + Guid.NewGuid().ToString("N")[..16];
        Environment.SetEnvironmentVariable("LTAI_API_KEY", apiKey);
        try
        {
            var builder = new WebHostBuilder()
                .ConfigureServices(s => s.AddRouting())
                .Configure(app =>
                {
                    app.UseMiddleware<ApiKeyMiddleware>();
                    app.Run(async ctx =>
                    {
                        ctx.Response.StatusCode = 200;
                        await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("ok"));
                    });
                });
            using var server = new TestServer(builder);
            var client = server.CreateClient();
            var req = new HttpRequestMessage(HttpMethod.Get, "/api/test");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            var resp = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LTAI_API_KEY", null);
        }
    }

    [Fact]
    public async Task ApiKeyMiddleware_WrongKey_Returns401()
    {
        Environment.SetEnvironmentVariable("LTAI_API_KEY", "correct-key");
        try
        {
            var builder = new WebHostBuilder()
                .ConfigureServices(s => s.AddRouting())
                .Configure(app =>
                {
                    app.UseMiddleware<ApiKeyMiddleware>();
                    app.Run(async ctx =>
                    {
                        ctx.Response.StatusCode = 200;
                        await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("ok"));
                    });
                });
            using var server = new TestServer(builder);
            var client = server.CreateClient();
            var req = new HttpRequestMessage(HttpMethod.Get, "/api/test");
            req.Headers.Add("X-API-Key", "wrong-key");
            var resp = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LTAI_API_KEY", null);
        }
    }

    [Fact]
    public async Task ApiKeyMiddleware_HmacSignature_Passes()
    {
        var apiKey = "sk-hmac-" + Guid.NewGuid().ToString("N")[..16];
        Environment.SetEnvironmentVariable("LTAI_API_KEY", apiKey);
        try
        {
            var builder = new WebHostBuilder()
                .ConfigureServices(s => s.AddRouting())
                .Configure(app =>
                {
                    app.UseMiddleware<ApiKeyMiddleware>();
                    app.Run(async ctx =>
                    {
                        ctx.Response.StatusCode = 200;
                        await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("ok"));
                    });
                });
            using var server = new TestServer(builder);
            var client = server.CreateClient();
            var path = "/api/test";
            var signature = Convert.ToBase64String(HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(apiKey), Encoding.UTF8.GetBytes(path)));
            var req = new HttpRequestMessage(HttpMethod.Get, path);
            req.Headers.Add("X-Signature", signature);
            var resp = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LTAI_API_KEY", null);
        }
    }

    // ═══════════════════════════════════════════════
    //  RateLimitMiddleware
    // ═══════════════════════════════════════════════

    [Fact]
    public async Task RateLimitMiddleware_HealthEndpoint_Skipped()
    {
        var builder = new WebHostBuilder()
            .ConfigureServices(s => s.AddRouting())
            .Configure(app =>
            {
                app.UseMiddleware<RateLimitMiddleware>();
                app.Run(async ctx =>
                {
                    ctx.Response.StatusCode = 200;
                    await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("healthy"));
                });
            });
        using var server = new TestServer(builder);
        var client = server.CreateClient();
        var resp = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task RateLimitMiddleware_NormalRequest_SetsHeaders()
    {
        var builder = new WebHostBuilder()
            .ConfigureServices(s => s.AddRouting())
            .Configure(app =>
            {
                app.UseMiddleware<RateLimitMiddleware>();
                app.Run(async ctx =>
                {
                    ctx.Response.StatusCode = 200;
                    await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("ok"));
                });
            });
        using var server = new TestServer(builder);
        var client = server.CreateClient();
        var resp = await client.GetAsync("/api/test");
        Assert.True(resp.StatusCode == HttpStatusCode.OK || resp.StatusCode == HttpStatusCode.TooManyRequests);
        Assert.True(resp.Headers.Contains("X-RateLimit-Limit"));
    }
}

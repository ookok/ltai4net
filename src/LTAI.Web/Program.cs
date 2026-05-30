using LTAI.Agent;
using LTAI.Agent.Vector;
using LTAI.AI;
using LTAI.Core;
using LTAI.Core.Configuration;
using LTAI.Web.Middleware;
using Microsoft.Data.Sqlite;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.WithProperty("Application", "LTAI.Web")
    .WriteTo.Console()
    .WriteTo.File("logs/ltai-web-.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog();

    // ── LTAI Services ──
    builder.Services.AddLTAICore();
    builder.Services.AddLTAIAI();
    builder.Services.AddLTAIAgent();

    // ── Controllers ──
    builder.Services.AddControllers();

    // ── CORS ──
    var corsOrigins = builder.Configuration
        .GetSection("LTAI:Web:CorsOrigins")
        .Get<string[]>() ?? ["http://localhost:5173", "http://localhost:3000"];

    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy => policy
            .WithOrigins(corsOrigins)
            .WithHeaders("Content-Type", "Authorization", "X-API-Key")
            .WithMethods("GET", "POST", "OPTIONS"));
    });

    // ── Swagger ──
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();

    var app = builder.Build();

    // ── Middleware pipeline (order matters) ──
    app.UseMiddleware<ExceptionMiddleware>();    // 1. Catch all exceptions
    app.UseSerilogRequestLogging();              // 2. Log every request
    app.UseMiddleware<ApiKeyMiddleware>();       // 3. Auth
    app.UseMiddleware<RateLimitMiddleware>();    // 4. Rate limit
    app.UseCors();                               // 5. CORS

    app.UseSwagger();
    app.UseSwaggerUI();

    // ── Health Check (detailed) ──
    app.MapGet("/health", async (IServiceProvider sp) =>
    {
        var checks = new List<object>();

        // Check KgStore (SQLite)
        try
        {
            var kgStore = sp.GetRequiredService<KgStore>();
            using var conn = new SqliteConnection($"Data Source={kgStore.DbPath};Mode=ReadOnly;");
            await conn.OpenAsync();
            checks.Add(new { name = "kgstore", status = "healthy" });
        }
        catch (Exception ex)
        {
            checks.Add(new { name = "kgstore", status = "unhealthy", error = ex.Message });
        }

        // Check LLM providers
        try
        {
            var router = sp.GetRequiredService<MultiProviderChatClient>();
            var providers = router.RegisteredProviders.ToList();
            checks.Add(new { name = "llm_providers", status = "healthy", count = providers.Count, providers });
        }
        catch (Exception ex)
        {
            checks.Add(new { name = "llm_providers", status = "unhealthy", error = ex.Message });
        }

        var allHealthy = checks.All(c => c.GetType().GetProperty("status")?.GetValue(c)?.ToString() == "healthy");

        return Results.Json(new
        {
            status = allHealthy ? "healthy" : "degraded",
            timestamp = DateTime.UtcNow,
            version = "1.0.0",
            checks
        });
    });

    app.MapControllers();

    var port = builder.Configuration.GetValue<int?>("LTAI:Web:Port") ?? 5100;
    app.Run($"http://0.0.0.0:{port}");
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

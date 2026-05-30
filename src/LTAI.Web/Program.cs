using LTAI.Agent;
using LTAI.AI;
using LTAI.Core;
using LTAI.Core.Configuration;
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
            .AllowAnyHeader()
            .AllowAnyMethod());
    });

    // ── Swagger ──
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();

    var app = builder.Build();

    app.UseCors();
    app.UseSwagger();
    app.UseSwaggerUI();

    // ── Health Check ──
    app.MapGet("/health", () => Results.Ok(new
    {
        status = "healthy",
        timestamp = DateTime.UtcNow,
        version = "1.0.0"
    }));

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

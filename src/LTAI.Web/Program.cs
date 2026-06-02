using LTAI.Agent;
using LTAI.Agent.Vector;
using LTAI.AI;
using LTAI.Core;
using LTAI.Core.Configuration;
using LTAI.Web.Middleware;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.DevUI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.Data.Sqlite;
using OpenTelemetry.Trace;
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
    builder.Services.AddLTAIAgent(out var agentNames);

    // ── P6: MAF Protocol Endpoints ──
    // OpenAI Responses + ChatCompletions + Conversations (in-memory)
    builder.Services.AddOpenAIResponses();
    builder.Services.AddOpenAIChatCompletions();
    builder.Services.AddOpenAIConversations();
    // AGUI (Agent-UI) protocol
    builder.Services.AddAGUI();
    // Claims-based session isolation (prevents one user's contextId from resuming another's thread
    // when persistent session stores are registered — required for multi-user A2A / AGUI hosts).
    builder.Services.AddHttpContextAccessor();
    builder.Services.UseClaimsBasedSessionIsolation();

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

    // ── OpenAPI metadata ──
    // Swashbuckle 9.0.6 has TypeLoadException on .NET 10 preview; use Minimal API OpenAPI metadata only.
    builder.Services.AddOpenApi();

    // ── P7.1: MAF DevUI (development-only) ──
    // Auto-discovers all 18 agents registered via MAF keyed services.
    // Exposes /devui for browser-based agent playground (chat, history, tool inspection).
    // Loopback-only by default (DevUIAuthFilter rejects non-127.0.0.1 callers).
    // See D14: not exposed in production — system-prompt disclosure risk.
    if (builder.Environment.IsDevelopment())
    {
        builder.AddDevUI();
    }

    // ── P7.2: OTel exporters ──
    // LTAI.Core already wires tracing sources (LTAI.* + Microsoft.Agents.AI.*) and
    // HttpClient/ASP.NET Core instrumentation. We just add exporters here:
    //   - ConsoleExporter: human-readable spans to stdout (always on in Development)
    //   - OtlpExporter:    OTLP/gRPC to localhost:4317 if LTAI:Telemetry:OtlpEndpoint configured
    // See D15: console is the default (no external deps); OTLP is opt-in via config.
    builder.Services.Configure<OpenTelemetry.Trace.TracerProviderBuilder>(tracing =>
    {
        if (builder.Environment.IsDevelopment())
        {
            tracing.AddConsoleExporter();
        }
        var otlpEndpoint = builder.Configuration["LTAI:Telemetry:OtlpEndpoint"];
        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            tracing.AddOtlpExporter(opt => opt.Endpoint = new Uri(otlpEndpoint));
        }
    });

    // ── P6: A2A server registration uses deferred factory (no eager agent resolution) ──
    foreach (var name in agentNames)
    {
        builder.Services.AddA2AServer(name);
    }
    Log.Information("P6 Protocol endpoints: registered {Count} agents for protocol exposure: {Names}",
        agentNames.Count, string.Join(", ", agentNames));

    var app = builder.Build();

    // ── Middleware pipeline (order matters) ──
    app.UseMiddleware<ExceptionMiddleware>();    // 1. Catch all exceptions
    app.UseSerilogRequestLogging();              // 2. Log every request
    app.UseCors();                               // 3. CORS (before auth — OPTIONS preflight needs no auth)
    app.UseMiddleware<ApiKeyMiddleware>();       // 4. Auth
    app.UseMiddleware<RateLimitMiddleware>();    // 5. Rate limit

    app.MapOpenApi();

    // ── P9.0: LTAIDevUIService shared REST surface ──
    // Backed by the same service used by LTAI.TUI (/dashboard) and
    // LTAI.Desktop (WebView2 / browser-launched DevUI). Exposes:
    //   GET /ltai/v1/entities                  -> LTAIAgentCard[]  (10 agents)
    //   GET /ltai/v1/entities/{name}/card      -> LTAIAgentCard    (single)
    // These complement MAF's /v1/entities (DevUI auto-discovery) by adding
    // LTAI-specific fields (model, temperature, tools, permissions).
    app.MapGet("/ltai/v1/entities", (LTAI.Agent.DevUI.LTAIDevUIService devUi) =>
        devUi.ListAgentCards());
    app.MapGet("/ltai/v1/entities/{name}/card", (LTAI.Agent.DevUI.LTAIDevUIService devUi, string name) =>
    {
        var card = devUi.GetAgentCard(name);
        return card is null ? Results.NotFound() : Results.Ok(card);
    });

    // ── P15.9: Workflow hot-reload REST surface ──
    // Companion to TUI /workflow and Desktop WorkflowsView. Backs the
    // browser DevUI page that lists/inspects/reloads hot-editable
    // .livingtree/workflows/*.yaml|*.json files. See D72/D73.
    app.MapGet("/ltai/v1/workflows", (LTAI.Agent.Workflows.YAMLWorkflowRegistry? reg) =>
    {
        if (reg == null) return Results.NotFound(new { error = "YAMLWorkflowRegistry not registered" });
        return Results.Ok(new
        {
            watchDir = reg.WatchDirectory,
            workflows = reg.List(),
        });
    });
    app.MapGet("/ltai/v1/workflows/{name}", async (LTAI.Agent.Workflows.YAMLWorkflowRegistry? reg, string name) =>
    {
        if (reg == null) return Results.NotFound(new { error = "YAMLWorkflowRegistry not registered" });
        var match = reg.List().FirstOrDefault(w =>
            string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));
        if (match == default) return Results.NotFound(new { error = $"Workflow '{name}' not found" });
        try
        {
            var content = await System.IO.File.ReadAllTextAsync(match.FilePath);
            return Results.Ok(new
            {
                name = match.Name,
                type = match.Type,
                version = match.Version,
                filePath = match.FilePath,
                loadedAtUtc = match.LoadedAtUtc,
                sizeBytes = match.SizeBytes,
                content,
            });
        }
        catch (Exception ex)
        {
            return Results.Problem($"Read failed: {ex.Message}");
        }
    });
    app.MapPost("/ltai/v1/workflows/reload", async (LTAI.Agent.Workflows.YAMLWorkflowRegistry? reg) =>
    {
        if (reg == null) return Results.NotFound(new { error = "YAMLWorkflowRegistry not registered" });
        await reg.ReloadAllAsync();
        return Results.Ok(new { reloaded = reg.List().Count, reloadedAtUtc = DateTime.UtcNow });
    });
    app.MapPost("/ltai/v1/workflows/{name}/reload", async (LTAI.Agent.Workflows.YAMLWorkflowRegistry? reg, string name) =>
    {
        if (reg == null) return Results.NotFound(new { error = "YAMLWorkflowRegistry not registered" });
        var match = reg.List().FirstOrDefault(w =>
            string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));
        if (match == default) return Results.NotFound(new { error = $"Workflow '{name}' not found" });
        try
        {
            await reg.ReloadFileAsync(match.FilePath);
            return Results.Ok(new { reloaded = name, reloadedAtUtc = DateTime.UtcNow });
        }
        catch (Exception ex)
        {
            // D68: failed reload keeps the old snapshot alive; surface the
            // error to the client so it can show a meaningful toast.
            return Results.Problem($"Reload failed: {ex.Message}", statusCode: 422);
        }
    });

    // ── P14.15: Background job REST surface ──
    // Backed by the same BackgroundJobService that the agent tools call.
    // Use cases: TUI/Desktop jobs panel (P14.14) polls these, CI/cron
    // scripts can read job status, and external tools can cancel misbehaving
    // jobs. Snapshot semantics: jobs auto-evict 60s after completion
    // (BackgroundJobService.StartJob line 50), so callers should treat 404
    // as "completed and gone" rather than an error.
    app.MapGet("/ltai/v1/jobs", (LTAI.Agent.Tools.BackgroundJobService jobs) =>
    {
        var list = jobs.SnapshotJobs()
            .OrderBy(kv => int.TryParse(kv.Key, out var n) ? n : 0)
            .Select(kv =>
            {
                var j = kv.Value;
                return new
                {
                    id = kv.Key,
                    status = j.Completed ? (j.ExitCode == 0 ? "completed" :
                                            j.Error == "Cancelled" ? "cancelled" :
                                            "failed") : "running",
                    exitCode = j.ExitCode,
                    command = j.Command,
                    startedAtUtc = j.StartedAtUtc,
                    completed = j.Completed,
                    stdoutBytes = j.Output?.Length ?? 0,
                    stderrBytes = j.Error?.Length ?? 0,
                };
            });
        return Results.Ok(new { count = list.Count(), jobs = list });
    });
    app.MapGet("/ltai/v1/jobs/{id}", (LTAI.Agent.Tools.BackgroundJobService jobs, string id) =>
    {
        var j = jobs.GetJobEntry(id);
        if (j is null) return Results.NotFound(new { error = $"Job '{id}' not found (or already evicted)" });
        return Results.Ok(new
        {
            id,
            command = j.Command,
            startedAtUtc = j.StartedAtUtc,
            completed = j.Completed,
            exitCode = j.ExitCode,
            stdout = j.Output ?? "",
            stderr = j.Error ?? "",
        });
    });
    app.MapPost("/ltai/v1/jobs/{id}/cancel", (LTAI.Agent.Tools.BackgroundJobService jobs, string id) =>
    {
        var j = jobs.GetJobEntry(id);
        if (j is null) return Results.NotFound(new { error = $"Job '{id}' not found (or already evicted)" });
        if (j.Completed) return Results.Conflict(new { error = $"Job '{id}' already completed" });
        j.Completed = true;
        j.Error = "Cancelled";
        return Results.Ok(new { id, cancelled = true, cancelledAtUtc = DateTime.UtcNow });
    });

    // ── P7.1: Map DevUI endpoint (Development-only) ──
    if (app.Environment.IsDevelopment())
    {
        app.MapDevUI();
    }

    // ── Health Check (detailed) ──
    app.MapGet("/health", async (IServiceProvider sp) =>
    {
        var checks = new List<object>();

        // Check KgStore (SQLite)
        try
        {
            var kgStore = sp.GetRequiredService<KgStore>();
            using var conn = new SqliteConnection($"Data Source={kgStore.DbPath};Mode=ReadOnly;");
            await conn.OpenAsync().ConfigureAwait(false);
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

    // ── P6: MAF Protocol Endpoint Mapping ──
    foreach (var name in agentNames)
    {
        var agent = app.Services.GetRequiredKeyedService<AIAgent>(name);
        app.MapOpenAIResponses(agent, $"/v1/agents/{name}/responses")
            .WithTags("OpenAI", name);
        app.MapOpenAIChatCompletions(agent, $"/v1/agents/{name}/chat/completions")
            .WithTags("OpenAI", name);
        app.MapAGUI(name, $"/agui/{name}")
            .WithTags("AGUI", name);
        app.MapA2AHttpJson(name, $"/a2a/{name}")
            .WithTags("A2A", name);
    }

    // OpenAI Conversations API (global, no agent-specific)
    app.MapOpenAIConversations().WithTags("OpenAI");

    // Note: global /v1/responses and /v1/chat/completions are NOT registered here to avoid
    // endpoint-name collisions with the per-agent routes above. Clients can use
    // /v1/agents/LTAI-Chat/responses as the canonical default-agent endpoint.

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

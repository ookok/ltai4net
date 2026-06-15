using LTAI.Agent;
using LTAI.Agent.Caching;
using LTAI.Agent.Memory;
using LTAI.Agent.Vector;
using LTAI.AI;
using LTAI.Core;
using LTAI.Core.Configuration;
using LTAI.Core.Session;
using LTAI.Web.Middleware;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.DevUI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.Data.Sqlite;
using OpenTelemetry.Trace;
using System.Threading.Channels;
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

    // Session persistence: SessionManager for cross-restart session resume (A2A/AGUI)
    builder.Services.AddSingleton<SessionManager>();
    builder.Services.AddSingleton<ISessionSerializer, JsonSessionSerializer>();

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
            tracing.SetSampler(new OpenTelemetry.Trace.AlwaysOnSampler());
            tracing.AddConsoleExporter();
        }
        var otlpEndpoint = builder.Configuration["LTAI:Telemetry:OtlpEndpoint"];
        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            tracing.AddOtlpExporter(opt => opt.Endpoint = new Uri(otlpEndpoint));
        }
    });

    // ── P6: A2A server registration with persistent session store ──
    // Register LTAI's encrypted session store for each agent so A2A/AGUI
    // conversations survive process restarts. Must register BEFORE AddA2AServer.
    foreach (var name in agentNames)
    {
        builder.Services.AddKeyedSingleton<Microsoft.Agents.AI.Hosting.AgentSessionStore>(name,
            (sp, key) =>
            {
                var sessionMgr = sp.GetRequiredService<SessionManager>();
                Microsoft.Agents.AI.Hosting.AgentSessionStore store = new LTAI.Web.Session.LTAIAgentSessionStore(
                    sessionMgr,
                    sp.GetRequiredService<ILogger<LTAI.Web.Session.LTAIAgentSessionStore>>());

                // Wrap with claims-based isolation for multi-user safety
                var isolationProvider = sp.GetService<Microsoft.Agents.AI.Hosting.SessionIsolationKeyProvider>();
                if (isolationProvider != null)
                    store = new Microsoft.Agents.AI.Hosting.IsolationKeyScopedAgentSessionStore(
                        store, isolationProvider, new() { Strict = true });

                return store;
            });
    }

    foreach (var name in agentNames)
    {
        builder.Services.AddA2AServer(name);
    }
    Log.Information("P6 Protocol endpoints: registered {Count} agents for protocol exposure: {Names}",
        agentNames.Count, string.Join(", ", agentNames));

    var app = builder.Build();

    // ── Middleware pipeline (order matters) ──
    app.UseMiddleware<ExceptionMiddleware>();    // 1. Catch all exceptions
    // 2. Security headers (before everything except exception handler)
    app.Use(async (ctx, next) =>
    {
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers["X-Frame-Options"] = "DENY";
        ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'";
        ctx.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        ctx.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        ctx.Response.Headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
        await next(ctx).ConfigureAwait(false);
    });
    app.UseSerilogRequestLogging();              // 3. Log every request
    app.UseCors();                               // 4. CORS (before auth — OPTIONS preflight needs no auth)
    app.UseMiddleware<ApiKeyMiddleware>();       // 5. Auth
    app.UseMiddleware<RateLimitMiddleware>();    // 6. Rate limit

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

    // ── Todo / Mode REST surface (AgentModeObserver bridge) ──
    app.MapGet("/ltai/v1/todos", () =>
    {
        var remaining = LTAI.Agent.Tooling.AgentModeObserver.RemainingTodos;
        var total = LTAI.Agent.Tooling.AgentModeObserver.TotalTodos;
        var summary = LTAI.Agent.Tooling.AgentModeObserver.TodoSummary;
        return Results.Ok(new
        {
            remaining,
            total,
            summary,
        });
    });
    app.MapGet("/ltai/v1/mode", () =>
    {
        var mode = LTAI.Agent.Tooling.AgentModeObserver.CurrentMode;
        var icon = LTAI.Agent.Tooling.AgentModeObserver.ModeIcon;
        return Results.Ok(new
        {
            mode,
            icon,
        });
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
            workflows = reg.List().Select(w => new { w.Name, w.Type, w.Version, w.LoadedAtUtc, w.SizeBytes }),
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
                loadedAtUtc = match.LoadedAtUtc,
                sizeBytes = match.SizeBytes,
                content,
            });
        }
        catch (Exception)
        {
            return Results.Problem("Read failed");
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
            return Results.Problem($"Reload failed: {ex.Message}", statusCode: 422);
        }
    });

    // ── P16.3: SSE event stream for workflow hot-reload notifications ──
    // Subscribes to WorkflowHotReloadNotifier and pushes real-time
    // reload/failed events as Server-Sent Events. Use cases:
    //   - Browser DevUI live toast on reload
    //   - CI/webhook triggered by workflow changes
    //   - TUI/Desktop long-polling alternative (though they use OTel spans)
    app.MapGet("/ltai/v1/workflows/events", async (
        HttpContext ctx,
        LTAI.Agent.Workflows.WorkflowHotReloadNotifier notifier,
        ILoggerFactory loggerFactory) =>
    {
        var logger = loggerFactory.CreateLogger("WorkflowSSE");
        ctx.Response.Headers.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers.Connection = "keep-alive";
        ctx.Response.Headers["X-Accel-Buffering"] = "no"; // nginx proxy support

        // Channel to bridge the push-based IWorkflowSubscriber with async SSE writes.
        var channel = System.Threading.Channels.Channel.CreateBounded<string>(new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        var subscriber = new SseWorkflowSubscriber(channel.Writer);
        var token = notifier.Subscribe(subscriber);

        // Keepalive timer: every 30s send a comment to keep the connection open
        // on proxies / load balancers that idle-timeout long-lived connections.
        using var keepaliveCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        var keepalive = Task.Run(async () =>
        {
            try
            {
                while (!keepaliveCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(30_000, keepaliveCts.Token).ConfigureAwait(false);
                    await channel.Writer.WriteAsync(": keepalive\n\n", keepaliveCts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { logger.LogWarning(ex, "SSE keepalive error"); }
        }, keepaliveCts.Token);

        try
        {
            // Stream events until the client disconnects.
            await foreach (var sse in channel.Reader.ReadAllAsync(ctx.RequestAborted).ConfigureAwait(false))
            {
                await ctx.Response.WriteAsync(sse, ctx.RequestAborted).ConfigureAwait(false);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* client disconnected */ }
        finally
        {
            keepaliveCts.Cancel();
            notifier.Unsubscribe(token);
            channel.Writer.TryComplete();
            // Graceful: wait a moment for keepalive to exit.
            try { await keepalive.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
        }
    });

    // ── P16.1: Pipeline REST surface (sequential/concurrent) ──
    app.MapGet("/ltai/v1/pipelines", (LTAI.Agent.Workflows.YAMLWorkflowRegistry? reg) =>
    {
        if (reg == null) return Results.NotFound(new { error = "YAMLWorkflowRegistry not registered" });
        var all = reg.List();
        var pipes = all.Where(w => w.Type is "sequential" or "concurrent").ToList();
        return Results.Ok(new
        {
            watchDir = reg.WatchDirectory,
            pipelines = pipes.Select(p =>
            {
                var cfg = reg.TryGetPipelineConfig(p.Name);
                return new
                {
                    name = p.Name,
                    type = p.Type,
                    version = p.Version,
                    agents = cfg?.Agents ?? (IReadOnlyList<string>)[],
                    defaultTask = cfg?.DefaultTask,
                    filePath = p.FilePath,
                    loadedAtUtc = p.LoadedAtUtc,
                };
            }),
        });
    });
    app.MapGet("/ltai/v1/pipelines/{name}", (LTAI.Agent.Workflows.YAMLWorkflowRegistry? reg, string name) =>
    {
        if (reg == null) return Results.NotFound(new { error = "YAMLWorkflowRegistry not registered" });
        var cfg = reg.TryGetPipelineConfig(name);
        if (cfg == null) return Results.NotFound(new { error = $"Pipeline '{name}' not found" });
        return Results.Ok(new
        {
            name,
            type = cfg.Type,
            version = cfg.Version,
            agents = cfg.Agents,
            defaultTask = cfg.DefaultTask,
        });
    });
    app.MapPost("/ltai/v1/pipelines/{name}/run", async (
        LTAI.Agent.Workflows.YAMLWorkflowRegistry? reg,
        LTAI.Agent.Workflows.AgentWorkflows? pipes,
        string name,
        HttpContext ctx) =>
    {
        if (reg == null || pipes == null)
            return Results.NotFound(new { error = "AgentWorkflows or YAMLWorkflowRegistry not registered" });
        var cfg = reg.TryGetPipelineConfig(name);
        if (cfg == null) return Results.NotFound(new { error = $"Pipeline '{name}' not found" });
        var result = cfg.Type == "concurrent"
            ? await pipes.RunConcurrentAsync([name], cfg.DefaultTask ?? "Execute pipeline", ct: ctx.RequestAborted)
            : await pipes.RunSequentialAsync([name], cfg.DefaultTask ?? "Execute pipeline", ct: ctx.RequestAborted);
        return Results.Ok(new { pipeline = name, type = cfg.Type, result });
    });

    // ── P14.15: Background job REST surface ──
    // Backed by the same BackgroundJobService that the agent tools call.
    // Use cases: TUI/Desktop jobs panel (P14.14) polls these, CI/cron
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

    // ── Audit finding REST surface (PalaceStore backed) ──
    app.MapGet("/ltai/v1/audit", async (
        PalaceStore store,
        string? statusFilter, string? severity, string? fileFilter,
        string? category, string? fromDate, string? toDate,
        int limit = 500, bool includeFixed = false) =>
    {
        var max = Math.Clamp(limit > 0 ? limit : 500, 1, 2000);
        var drawers = await store.SearchByWingAsync("audit", maxCount: max);
        if (drawers.Count == 0) return Results.Ok(new { total = 0, findings = Array.Empty<object>() });

        var statusSet = !string.IsNullOrEmpty(statusFilter)
            ? statusFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet()
            : null;

        var filtered = new List<object>();
        foreach (var d in drawers)
        {
            var st = "open"; string? sev = null, file = null, line = null, cat = null;
            if (d.Metadata != null)
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(d.Metadata);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("status", out var s)) st = s.GetString() ?? "open";
                    if (root.TryGetProperty("severity", out var sv)) sev = sv.GetString();
                    if (root.TryGetProperty("file", out var f)) file = f.GetString();
                    if (root.TryGetProperty("line", out var l)) line = l.GetString();
                    if (root.TryGetProperty("category", out var c)) cat = c.GetString();
                }
                catch { }
            }
            if (statusSet != null && !statusSet.Contains(st)) continue;
            if (!string.IsNullOrEmpty(severity) && !string.Equals(sev, severity, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrEmpty(category) && !string.Equals(cat, category, StringComparison.OrdinalIgnoreCase)) continue;
            if (!includeFixed && st is not "open") continue;

            filtered.Add(new
            {
                id = d.DrawerId[..8],
                fullId = d.DrawerId,
                status = st, severity = sev, file, line, category = cat,
                content = d.Content, room = d.Room,
                importance = d.Importance,
                createdAt = DateTimeOffset.FromUnixTimeMilliseconds(d.CreatedAt).ToString("o"),
            });
        }
        return Results.Ok(new { total = filtered.Count, findings = filtered });
    });

    app.MapGet("/ltai/v1/audit/{id}", async (PalaceStore store, string id) =>
    {
        var drawers = await store.SearchByWingAsync("audit", maxCount: 2000);
        var match = drawers.FirstOrDefault(d =>
            d.DrawerId.StartsWith(id, StringComparison.OrdinalIgnoreCase));
        if (match == null) return Results.NotFound(new { error = $"Finding '{id}' not found" });

        string? status = null, sev = null, file = null, line = null, cat = null,
                resolvedAt = null, verifiedAt = null, closedAt = null,
                fixDesc = null, closeSummary = null;
        List<object>? trail = null;
        if (match.Metadata != null)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(match.Metadata);
                var root = doc.RootElement;
                if (root.TryGetProperty("status", out var s)) status = s.GetString();
                if (root.TryGetProperty("severity", out var sv)) sev = sv.GetString();
                if (root.TryGetProperty("file", out var f)) file = f.GetString();
                if (root.TryGetProperty("line", out var l)) line = l.GetString();
                if (root.TryGetProperty("category", out var c)) cat = c.GetString();
                if (root.TryGetProperty("resolved_at", out var ra)) resolvedAt = ra.GetString();
                if (root.TryGetProperty("verified_at", out var va)) verifiedAt = va.GetString();
                if (root.TryGetProperty("closed_at", out var ca)) closedAt = ca.GetString();
                if (root.TryGetProperty("fix_description", out var fd)) fixDesc = fd.GetString();
                if (root.TryGetProperty("close_summary", out var cs)) closeSummary = cs.GetString();
                if (root.TryGetProperty("_audit_trail", out var tr))
                {
                    var trailJson = tr.GetString();
                    if (trailJson != null)
                        trail = System.Text.Json.JsonSerializer.Deserialize<List<object>>(trailJson);
                }
            }
            catch { }
        }

        return Results.Ok(new
        {
            id = match.DrawerId[..8],
            fullId = match.DrawerId,
            wing = match.Wing, room = match.Room,
            status = status ?? "open", severity = sev, file, line, category = cat,
            content = match.Content,
            importance = match.Importance,
            createdAt = DateTimeOffset.FromUnixTimeMilliseconds(match.CreatedAt).ToString("o"),
            expiresAt = match.ExpiresAt.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(match.ExpiresAt.Value).ToString("o") : null,
            resolvedAt, verifiedAt, closedAt,
            fixDescription = fixDesc, closeSummary,
            auditTrail = trail,
        });
    });

    app.MapGet("/ltai/v1/audit/export", async (
        PalaceStore store, string format = "json",
        string? statusFilter = null, string? severity = null, string? category = null,
        bool includeAll = false) =>
    {
        var drawers = await store.SearchByWingAsync("audit", maxCount: 2000);
        var statusSet = !string.IsNullOrEmpty(statusFilter)
            ? statusFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet()
            : null;

        var records = new List<Dictionary<string, string?>>();
        foreach (var d in drawers)
        {
            var st = "open"; string? sev = null, file = null, line = null, cat = null;
            if (d.Metadata != null)
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(d.Metadata);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("status", out var s)) st = s.GetString() ?? "open";
                    if (root.TryGetProperty("severity", out var sv)) sev = sv.GetString();
                    if (root.TryGetProperty("file", out var f)) file = f.GetString();
                    if (root.TryGetProperty("line", out var l)) line = l.GetString();
                    if (root.TryGetProperty("category", out var c)) cat = c.GetString();
                }
                catch { }
            }
            if (statusSet != null && !statusSet.Contains(st)) continue;
            if (!string.IsNullOrEmpty(severity) && !string.Equals(sev, severity, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrEmpty(category) && !string.Equals(cat, category, StringComparison.OrdinalIgnoreCase)) continue;
            if (!includeAll && st is not "open") continue;

            records.Add(new Dictionary<string, string?>
            {
                ["id"] = d.DrawerId[..8], ["status"] = st, ["severity"] = sev,
                ["file"] = file, ["line"] = line, ["category"] = cat,
                ["content"] = d.Content, ["room"] = d.Room,
            });
        }

        if (format.Equals("csv", StringComparison.OrdinalIgnoreCase))
        {
            static string CsvEscape(string? v)
            {
                var s = v ?? "";
                if (s.Length > 0 && (s[0] == '=' || s[0] == '+' || s[0] == '-' || s[0] == '@'))
                    s = "'" + s;
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            }
            var csv = "Id,Status,Severity,Category,File,Line,Content\n" +
                string.Join("\n", records.Select(r =>
                    $"{CsvEscape(r["id"])},{CsvEscape(r["status"])},{CsvEscape(r["severity"])},{CsvEscape(r["category"])},{CsvEscape(r["file"])},{r["line"]},{CsvEscape(r["content"])}"));
            return Results.Text(csv, "text/csv");
        }
        if (format.Equals("markdown", StringComparison.OrdinalIgnoreCase))
        {
            static string MdEscape(string? v)
            {
                var s = v ?? "";
                return s.Replace("|", "\\|").Replace("\n", " ");
            }
            var md = $"# Audit Findings ({records.Count})\n\n| ID | Status | Severity | Category | File:Line | Content |\n|----|--------|----------|----------|-----------|--------|\n" +
                string.Join("\n", records.Select(r =>
                    $"| {r["id"]} | {r["status"]} | {r["severity"]} | {r["category"]} | {r["file"]}:{r["line"]} | {MdEscape(((r["content"] ?? "").Length > 80 ? (r["content"] ?? "")[..80] + "..." : r["content"]))} |"));
            return Results.Text(md, "text/markdown");
        }
        return Results.Ok(new { total = records.Count, findings = records });
    });

    app.MapGet("/ltai/v1/audit/stats", async (PalaceStore store,
        string? severity = null, string? category = null) =>
    {
        var drawers = await store.SearchByWingAsync("audit", maxCount: 2000);
        var statusCounts = new Dictionary<string, int>();
        var sevCounts = new Dictionary<string, int>();
        var catCounts = new Dictionary<string, int>();
        var fileCounts = new Dictionary<string, int>();

        foreach (var d in drawers)
        {
            var st = "open"; string sev = "?"; string cat = "?"; string file = "?";
            if (d.Metadata != null)
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(d.Metadata);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("status", out var s)) st = s.GetString() ?? "open";
                    if (root.TryGetProperty("severity", out var sv)) sev = sv.GetString() ?? "?";
                    if (root.TryGetProperty("category", out var c)) cat = c.GetString() ?? "?";
                    if (root.TryGetProperty("file", out var f)) file = f.GetString() ?? "?";
                }
                catch { }
            }
            if (!string.IsNullOrEmpty(severity) && !string.Equals(sev, severity, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrEmpty(category) && !string.Equals(cat, category, StringComparison.OrdinalIgnoreCase)) continue;

            statusCounts[st] = statusCounts.GetValueOrDefault(st) + 1;
            sevCounts[sev] = sevCounts.GetValueOrDefault(sev) + 1;
            catCounts[cat] = catCounts.GetValueOrDefault(cat) + 1;
            fileCounts[file] = fileCounts.GetValueOrDefault(file) + 1;
        }

        return Results.Ok(new
        {
            total = drawers.Count,
            byStatus = statusCounts,
            bySeverity = sevCounts,
            byCategory = catCounts,
            topFiles = fileCounts.OrderByDescending(kv => kv.Value).Take(10)
                .ToDictionary(kv => kv.Key, kv => kv.Value),
        });
    });

    app.MapPost("/ltai/v1/audit/save", async (
        PalaceStore store,
        HttpRequest request,
        CancellationToken ct) =>
    {
        try
        {
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync(ct);
            var findings = System.Text.Json.JsonSerializer.Deserialize<List<LTAI.Agent.Tools.Review.ReviewTools.AuditFinding>>(body);
            if (findings == null || findings.Count == 0)
                return Results.BadRequest(new { error = "Empty or invalid findings array" });

            var roomName = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
            var cnt = 0;
            foreach (var f in findings)
            {
                await store.StoreAsync(
                    wing: "audit", room: roomName,
                    content: System.Text.Json.JsonSerializer.Serialize(new
                    {
                        f.Severity, f.File, f.Line,
                        Category = f.Category ?? "general",
                        f.Description,
                        PersistedAt = DateTimeOffset.UtcNow.ToString("o"),
                    }),
                    role: "audit",
                    importance: f.Severity switch { "P0" => 0.9, "P1" => 0.7, _ => 0.5 },
                    agentId: "review_tool",
                    metadata: new Dictionary<string, object>
                    {
                        ["severity"] = f.Severity ?? "P2",
                        ["file"] = f.File ?? "",
                        ["line"] = f.Line ?? "",
                        ["category"] = f.Category ?? "general",
                        ["status"] = "open",
                    },
                    ttlMs: null);
                cnt++;
            }
            return Results.Ok(new { persisted = cnt, room = roomName });
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message);
        }
    });

    // ── P7.1: Map DevUI endpoint (Development-only) ──
    if (app.Environment.IsDevelopment())
    {
        app.MapDevUI();
    }

    // ── Readiness Probe (lightweight — just checks KG SQLite) ──
    app.MapGet("/ready", async (IServiceProvider sp) =>
    {
        try
        {
            var kgStore = sp.GetRequiredService<KgStore>();
            var csb = new SqliteConnectionStringBuilder { DataSource = kgStore.DbPath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly };
            using var conn = new SqliteConnection(csb.ConnectionString);
            await conn.OpenAsync().ConfigureAwait(false);
            return Results.Json(new { status = "ready", timestamp = DateTime.UtcNow });
        }
        catch (Exception)
        {
            return Results.Json(new { status = "not_ready", error = "Database unavailable" }, statusCode: 503);
        }
    });

    // ── Health Check (detailed) ──
    app.MapGet("/health", async (IServiceProvider sp) =>
    {
        var checks = new List<object>();

        // Check KgStore (SQLite)
        try
        {
            var kgStore = sp.GetRequiredService<KgStore>();
            var csb = new SqliteConnectionStringBuilder { DataSource = kgStore.DbPath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly };
            using var conn = new SqliteConnection(csb.ConnectionString);
            await conn.OpenAsync().ConfigureAwait(false);
            checks.Add(new { name = "kgstore", status = "healthy" });
        }
        catch (Exception)
        {
            checks.Add(new { name = "kgstore", status = "unhealthy", error = "Database unavailable" });
        }

        // Check session persistence
        try
        {
            var sessionMgr = sp.GetService<SessionManager>();
            if (sessionMgr == null)
            {
                checks.Add(new { name = "session_store", status = "degraded", error = "SessionManager not registered" });
            }
            else
            {
                var baseDir = Path.GetDirectoryName(SessionKeyInfo.KeyPath);
                var diskFree = string.Empty;
                try
                {
                    var drive = new DriveInfo(Path.GetPathRoot(SessionKeyInfo.KeyPath) ?? Path.GetPathRoot(Directory.GetCurrentDirectory()) ?? "C:\\");
                    diskFree = $"{drive.AvailableFreeSpace / 1024 / 1024} MB";
                }
                catch { }
                checks.Add(new
                {
                    name = "session_store",
                    status = "healthy",
                    keyExists = SessionKeyInfo.KeyExists,
                    diskFree,
                });
            }
        }
        catch (Exception)
        {
            checks.Add(new { name = "session_store", status = "unhealthy", error = "Session persistence unavailable" });
        }

        // Check LLM providers
        try
        {
            var router = sp.GetRequiredService<MultiProviderChatClient>();
            var providers = router.RegisteredProviders.ToList();
            checks.Add(new { name = "llm_providers", status = "healthy", count = providers.Count, providers });
        }
        catch (Exception)
        {
            checks.Add(new { name = "llm_providers", status = "unhealthy", error = "LLM providers unavailable" });
        }

        var allHealthy = checks.All(c =>
        {
            var status = c.GetType().GetProperty("status")?.GetValue(c)?.ToString();
            return status == "healthy";
        });

        return Results.Json(new
        {
            status = allHealthy ? "healthy" : "degraded",
            timestamp = DateTime.UtcNow,
            version = "1.0.0",
            checks
        });
    });

    app.MapControllers();

    // ── P19: Full-chain intent classification diagnostic endpoint ──
    if (app.Environment.IsDevelopment())
    {
    app.MapGet("/ltai/v1/classify", (string q, IServiceProvider sp) =>
    {
        if (string.IsNullOrWhiteSpace(q))
            return Results.BadRequest(new { error = "Missing 'q' query parameter" });

        var result = new Dictionary<string, object>();

        // Layer 1: Safety (rule-based pre-check)
        var safeByRules = LTAI.Core.Safety.SafetyRules.IsSafeByRules(q);
        result["safety"] = new { safeByRules };

        // Layer 2: Greeting detection
        var queryClassifier = sp.GetService<QueryClassifier>();
        var isGreeting = queryClassifier?.IsGreetingOnly(q) ?? QueryClassifier.IsGreetingOnlyStatic(q);
        result["greeting"] = new { isGreeting };

        // Layer 3: Knowledge query gate
        var isKnowledgeQuery = KbGraph.IsKnowledgeQuery(q);
        result["knowledgeQuery"] = new { isKnowledgeQuery };

        // Layer 4: Query intent classification (with confidence)
        var (intent, confidence) = queryClassifier?.ClassifyIntentWithScore(q)
            ?? (QueryIntent.What, 0f);
        result["intent"] = new { intent = intent.ToString(), confidence };

        // Layer 5: Full classification summary
        if (queryClassifier != null)
        {
            var full = queryClassifier.Classify(q);
            result["summary"] = new { full.IsGreeting, intent = full.Intent.ToString(), full.Confidence, full.IsSubstantive, full.IsConfident };
        }

        return Results.Ok(new
        {
            query = q,
            timestamp = DateTime.UtcNow,
            chain = result
        });
    });
    }

    // ── P6: MAF Protocol Endpoint Mapping (per-agent, isolated) ──
    // Each agent is mapped independently so that a single agent DI failure
    // does not prevent the rest of the web app from starting.
    foreach (var name in agentNames)
    {
        try
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
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to map protocol endpoints for agent {AgentName}; skipping", name);
        }
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

// Helper: IWorkflowSubscriber that writes JSON SSE events to a Channel<string>.
sealed class SseWorkflowSubscriber(System.Threading.Channels.ChannelWriter<string> writer) : LTAI.Agent.Workflows.IWorkflowSubscriber
{
    public void OnReloaded(LTAI.Agent.Workflows.WorkflowReloadEvent evt)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new { evt.Name, evt.Type, evt.Version, evt.FilePath, reloadedAtUtc = evt.ReloadedAtUtc });
        writer.TryWrite($"event: reloaded\ndata: {json}\n\n");
    }

    public void OnLoadFailed(LTAI.Agent.Workflows.WorkflowLoadFailedEvent evt)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new { evt.Name, evt.Type, evt.FilePath, evt.Reason, failedAtUtc = evt.FailedAtUtc });
        writer.TryWrite($"event: load_failed\ndata: {json}\n\n");
    }
}

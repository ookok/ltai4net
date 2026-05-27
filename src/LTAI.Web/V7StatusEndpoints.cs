using LTAI.Agent.Routing;
using LTAI.Core.Governors;
using LTAI.DNA;
using LTAI.DNA.Regulation;
using LTAI.DNA.Safety;
using LTAI.Tools.Evolution;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace LTAI.Web.V7;

public static class V7StatusEndpoints
{
    public static void MapV7StatusEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v7/status", async (HttpContext ctx) =>
        {
            var sp = ctx.RequestServices;
            var dna = sp.GetService<DNAOrchestrator>();
            var safety = sp.GetRequiredService<UnifiedSafetyGate>();
            var router = sp.GetService<ShadowRouter>();
            var regulation = sp.GetRequiredService<IRegulationProvider>();
            var evolution = sp.GetService<ToolEvolutionLoop>();

            var safetyStats = safety.GetStats();
            var routerStats = router?.GetStats();

            var status = new
            {
                version = "1.0.0",
                name = "LTAI Agent OS",
                uptime = DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime(),
                dna = dna == null ? null : new
                {
                    consciousness = dna.Consciousness.State.Level.ToString(),
                    awareness = dna.Consciousness.State.AwarenessScore,
                    safety = dna.Safety.Posture.ToString(),
                    generation = dna.GetStatus().Generation,
                    fitness = dna.GetStatus().FitnessScore
                },
                safety_gate = new
                {
                    active_sessions = safetyStats.GetValueOrDefault("active_sessions", 0),
                    frozen_sessions = safetyStats.GetValueOrDefault("frozen_sessions", 0)
                },
                router = routerStats == null ? null : new
                {
                    shadow_mode = true,
                    accuracy = routerStats.Value.accuracy,
                    total_routes = routerStats.Value.total,
                    agreed_routes = routerStats.Value.agreed
                },
                regulation = new
                {
                    standards_count = (await regulation.SearchAsync("GB", ctx.RequestAborted)).Count
                },
                evolution = evolution == null ? null : evolution.GetStats()
            };

            // Compute CPS, Scheduler, Pareto, Kernel stats
            object? cpsStats = null;
            object? schedulerStats = null;
            object? paretoStats = null;
            object? kernelStats = null;

            var cpsService = sp.GetService<CPSProcessingService>();
            if (cpsService != null)
            {
                var s = cpsService.GetPerformanceStats();
                cpsStats = new { total_processed = s.TotalProcessed, avg_latency_ms = s.AvgLatencyMs, est_tokens = s.EstimatedTotalTokens, routes = s.RouteDistribution };
            }

            var sch = sp.GetService<CoordinationScheduler>();
            if (sch != null)
            {
                schedulerStats = new { running = sch.IsRunning, queue_depth = sch.QueueDepth, events_processed = sch.EventsProcessed, rules_triggered = sch.RulesTriggered };
            }

            var pr = sp.GetService<ParetoRouter>();
            if (pr != null)
            {
                paretoStats = new { frontier_size = pr.FrontierSize, total_decisions = pr.TotalDecisions, shadow_rate = pr.ShadowRate };
            }

            var mk = sp.GetService<IMicroKernel>();
            if (mk != null)
            {
                var v = mk.GetAggregatedVitals();
                kernelStats = new { healthy = mk.IsHealthy, p50_ms = v.P50LatencyMs, p99_ms = v.P99LatencyMs };
            }

            var fullStatus = new
            {
                version = "1.0.0",
                name = "LTAI Agent OS",
                uptime = DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime(),
                dna = status.dna,
                safety_gate = status.safety_gate,
                router = status.router,
                regulation = status.regulation,
                evolution = status.evolution,
                cps = cpsStats,
                scheduler = schedulerStats,
                pareto = paretoStats,
                kernel = kernelStats
            };

            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsJsonAsync(fullStatus, ctx.RequestAborted).ConfigureAwait(false);
        });

        app.MapGet("/api/v7/health", (HttpContext ctx) =>
        {
            ctx.Response.StatusCode = 200;
            return Results.Ok(new { status = "healthy", version = "1.0.0" });
        });
    }
}

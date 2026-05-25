using LTAI.Agent.Routing;
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
                version = "0.51.0",
                name = "LTAI Sentient Mesh",
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

            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsJsonAsync(status, ctx.RequestAborted).ConfigureAwait(false);
        });

        app.MapGet("/api/v7/health", (HttpContext ctx) =>
        {
            ctx.Response.StatusCode = 200;
            return Results.Ok(new { status = "healthy", version = "0.51.0" });
        });
    }
}

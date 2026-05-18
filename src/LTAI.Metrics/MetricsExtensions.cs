using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;

namespace LTAI.Metrics;

public static class MetricsExtensions
{
    public static IServiceCollection AddLTAIMetrics(this IServiceCollection services)
    {
        services.AddSingleton<LTAIMetricsCollector>();
        return services;
    }

    public static WebApplication UseLTAIMetrics(this WebApplication app)
    {
        app.MapGet("/metrics", (LTAIMetricsCollector collector) =>
        {
            var s = collector.GetSnapshot();
            return Results.Text(
                "# HELP ltai_requests_total Total request count\n" +
                "# TYPE ltai_requests_total counter\n" +
                "ltai_requests_total " + s.TotalRequests + "\n" +
                "# HELP ltai_tokens_total Total tokens processed\n" +
                "# TYPE ltai_tokens_total counter\n" +
                "ltai_tokens_total " + s.TotalTokens + "\n" +
                "# HELP ltai_avg_latency_ms Average latency\n" +
                "# TYPE ltai_avg_latency_ms gauge\n" +
                "ltai_avg_latency_ms " + s.AvgLatencyMs.ToString("F1") + "\n" +
                "# HELP ltai_dna_awareness DNA awareness score\n" +
                "# TYPE ltai_dna_awareness gauge\n" +
                "ltai_dna_awareness " + s.Awareness.ToString("F3") + "\n" +
                "# HELP ltai_dna_fitness DNA fitness score\n" +
                "# TYPE ltai_dna_fitness gauge\n" +
                "ltai_dna_fitness " + s.Fitness.ToString("F3") + "\n" +
                "# HELP ltai_active_tasks Active tasks\n" +
                "# TYPE ltai_active_tasks gauge\n" +
                "ltai_active_tasks " + s.ActiveTasks + "\n" +
                "# HELP ltai_memory_mb Process memory\n" +
                "# TYPE ltai_memory_mb gauge\n" +
                "ltai_memory_mb " + s.MemoryMb + "\n",
                "text/plain; version=0.0.4");
        });

        app.MapGet("/api/metrics", (LTAIMetricsCollector collector) =>
            Results.Json(collector.GetSnapshot()));

        app.MapGet("/api/metrics/dashboard", () =>
            Results.Text(GrafanaDashboard.GenerateJson(), "application/json"));

        return app;
    }
}

public static class GrafanaDashboard
{
    public static string GenerateJson(string appName = "LTAI")
    {
        return @"{""title"":""" + appName + @""",""uid"":""ltai-main"",""panels"":[" +
            @"{""title"":""Request Rate"",""targets"":[{""expr"":""ltai_requests_total""}],""gridPos"":{""x"":0,""y"":0,""w"":8,""h"":6}}," +
            @"{""title"":""Avg Latency"",""targets"":[{""expr"":""ltai_avg_latency_ms""}],""gridPos"":{""x"":8,""y"":0,""w"":8,""h"":6}}," +
            @"{""title"":""Tokens"",""targets"":[{""expr"":""ltai_tokens_total""}],""gridPos"":{""x"":16,""y"":0,""w"":8,""h"":6}}," +
            @"{""title"":""DNA Awareness"",""targets"":[{""expr"":""ltai_dna_awareness""}],""gridPos"":{""x"":0,""y"":6,""w"":8,""h"":6}}," +
            @"{""title"":""DNA Fitness"",""targets"":[{""expr"":""ltai_dna_fitness""}],""gridPos"":{""x"":8,""y"":6,""w"":8,""h"":6}}," +
            @"{""title"":""Memory"",""targets"":[{""expr"":""ltai_memory_mb""}],""gridPos"":{""x"":16,""y"":6,""w"":8,""h"":6}}," +
            @"{""title"":""Active Tasks"",""targets"":[{""expr"":""ltai_active_tasks""}],""gridPos"":{""x"":0,""y"":12,""w"":8,""h"":6}}" +
            @"]}";
    }
}

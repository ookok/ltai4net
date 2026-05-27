using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Core.Governors;

public sealed class ParetoRouterSeeder
{
    private readonly ParetoRouter _router;
    private readonly ILogger<ParetoRouterSeeder> _logger;

    private static readonly Dictionary<string, (float Q, float S, float C)> DomainProfiles = new()
    {
        ["code"] = (0.95f, 0.4f, 0.6f),
        ["math"] = (0.95f, 0.3f, 0.7f),
        ["chat"] = (0.60f, 0.9f, 0.05f),
        ["reasoning"] = (0.90f, 0.2f, 0.8f),
        ["eia"] = (0.85f, 0.5f, 0.4f),
        ["general"] = (0.75f, 0.5f, 0.15f),
        ["translation"] = (0.80f, 0.7f, 0.10f),
        ["summarization"] = (0.70f, 0.8f, 0.10f),
        ["reflex"] = (0.30f, 1.0f, 0.0f),
        ["command"] = (0.40f, 1.0f, 0.0f),
    };

    public ParetoRouterSeeder(ParetoRouter router, ILogger<ParetoRouterSeeder>? logger = null)
    {
        _router = router;
        _logger = logger ?? NullLogger<ParetoRouterSeeder>.Instance;
    }

    public void SeedFromDomainProfiles()
    {
        foreach (var (domain, (q, s, c)) in DomainProfiles)
        {
            var route = domain switch
            {
                "code" or "math" or "reasoning" => "L2",
                "chat" or "translation" or "summarization" => "L1",
                "reflex" or "command" => "reflex",
                "eia" => "L1",
                _ => "local"
            };

            var point = new ParetoPoint
            {
                Label = route,
                Quality = q,
                Speed = s,
                Cost = c,
                Embedding = DomainHash(domain, 3)
            };
            _router.AddFrontierPoint(point);
        }

        _router.PruneDominated();
        _logger.LogInformation("Seeded ParetoRouter with {Count} domain profiles, frontier size={Size}",
            DomainProfiles.Count, _router.FrontierSize);
    }

    public void SeedFromRouterBehaviors(
        Func<string, string>? moERouter = null,
        Func<string, bool>? l1L2Delegator = null)
    {
        var testQueries = new[]
        {
            "write a quick sort in C#",          // code
            "calculate the integral of x^2",     // math
            "how are you doing today",           // chat
            "explain the implications of quantum entanglement on cryptography", // reasoning
            "what is the environmental impact of this project",  // eia
            "list files in current directory",   // reflex
            "translate 'hello world' to Chinese", // translation
            "summarize this article about AI",   // summarization
        };

        foreach (var query in testQueries)
        {
            string? route = null;

            if (moERouter != null)
            {
                var expert = moERouter(query);
                route = expert switch
                {
                    "code" or "math" => "L2",
                    "chat" or "eia" => "L1",
                    "reasoning" => "L2",
                    _ => "local"
                };
            }

            if (l1L2Delegator != null && route == null)
            {
                route = l1L2Delegator(query) ? "L2" : "L1";
            }

            if (route != null)
            {
                var emb = HashEmbed(query, 3);
                var (q, s, c) = route switch
                {
                    "L2" => (0.90f, 0.2f, 0.8f),
                    "L1" => (0.75f, 0.5f, 0.15f),
                    "reflex" => (0.30f, 1.0f, 0.0f),
                    _ => (0.55f, 0.8f, 0.05f)
                };

                _router.AddFrontierPoint(new ParetoPoint
                {
                    Label = route,
                    Quality = q,
                    Speed = s,
                    Cost = c,
                    Embedding = emb
                });
            }
        }

        _router.PruneDominated();
        _logger.LogInformation("Seed from router behaviors: frontier size={Size}", _router.FrontierSize);
    }

    private static float[] DomainHash(string domain, int dim)
    {
        var emb = new float[dim];
        var bytes = global::System.Text.Encoding.UTF8.GetBytes(domain);
        for (var i = 0; i < Math.Min(bytes.Length, dim); i++)
            emb[i] = bytes[i] / 255f;
        return emb;
    }

    private static float[] HashEmbed(string text, int dim)
    {
        var emb = new float[dim];
        var bytes = global::System.Text.Encoding.UTF8.GetBytes(text);
        for (var i = 0; i < Math.Min(bytes.Length, dim); i++)
            emb[i] = bytes[i] / 255f;
        return emb;
    }
}

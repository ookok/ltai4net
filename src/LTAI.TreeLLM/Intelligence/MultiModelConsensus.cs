using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.TreeLLM.Intelligence;

public sealed record MultiConsensusResult
{
    public string Answer { get; init; } = "";
    public Dictionary<string, string> ModelVotes { get; init; } = new();
    public double AgreementScore { get; init; }
    public List<string> ModelsUsed { get; init; } = new();
    public string Method { get; init; } = "majority";
    public int ModelsAgreeing { get; init; }
    public double HighestConfidence { get; init; }
}

public sealed class MultiModelConsensus
{
    private const double ConsensusThreshold = 0.6;
    private readonly ILogger<MultiModelConsensus>? _logger;

    public MultiModelConsensus(ILogger<MultiModelConsensus>? logger = null)
    {
        _logger = logger;
    }

    public async Task<MultiConsensusResult> GatherConsensusAsync(
        string prompt,
        IReadOnlyList<IChatClient> clients,
        IReadOnlyList<string> modelNames,
        CancellationToken cancellationToken = default)
    {
        if (clients.Count == 0)
            throw new ArgumentException("At least one client is required", nameof(clients));

        if (clients.Count != modelNames.Count)
            throw new ArgumentException("Clients and model names must have the same length");

        var message = new ChatMessage(ChatRole.User, prompt);
        var messages = new List<ChatMessage> { message };

        var tasks = new List<Task<(string Model, string Response, bool Success)>>();
        for (var i = 0; i < clients.Count; i++)
        {
            var idx = i;
            tasks.Add(CallClientAsync(clients[idx], modelNames[idx], messages, cancellationToken));
        }

        var results = await Task.WhenAll(tasks);
        var responses = results.Where(r => r.Success).ToList();

        if (responses.Count == 0)
        {
            return new MultiConsensusResult
            {
                Answer = "",
                AgreementScore = 0,
                ModelsUsed = modelNames.ToList(),
                Method = "none",
                ModelsAgreeing = 0,
                HighestConfidence = 0
            };
        }

        if (responses.Count == 1)
        {
            return new MultiConsensusResult
            {
                Answer = responses[0].Response,
                ModelVotes = new Dictionary<string, string> { [responses[0].Model] = responses[0].Response },
                AgreementScore = 1.0,
                ModelsUsed = modelNames.ToList(),
                Method = "single",
                ModelsAgreeing = 1,
                HighestConfidence = 1.0
            };
        }

        var pairs = ComputePairwiseJaccard(responses.Select(r => r.Response).ToList());
        var agreementGroups = FindAgreementGroups(responses, pairs);

        string answer;
        Dictionary<string, string> votes;
        string method;
        double agreementScore;
        int agreeing;

        var majorityGroup = agreementGroups.OrderByDescending(g => g.Count).First();
        if (majorityGroup.Count >= 2)
        {
            var bestInGroup = majorityGroup
                .OrderByDescending(m => ComputeAverageJaccard(m.Model, pairs))
                .First();
            answer = bestInGroup.Response;
            votes = majorityGroup.ToDictionary(m => m.Model, m => m.Response);
            method = "majority";
            agreementScore = majorityGroup.Count > 1
                ? ComputeAverageJaccard(majorityGroup[0].Model, pairs)
                : 1.0;
            agreeing = majorityGroup.Count;
        }
        else
        {
            var best = responses.OrderByDescending(r =>
                responses.Sum(other => pairs.TryGetValue((r.Model, other.Model), out var j) ? j :
                    pairs.TryGetValue((other.Model, r.Model), out var j2) ? j2 : 0)
            ).First();
            answer = best.Response;
            votes = responses.ToDictionary(r => r.Model, r => r.Response);
            method = "highest_confidence";
            agreementScore = pairs.Values.DefaultIfEmpty(0).Max();
            agreeing = 1;
        }

        _logger?.LogDebug("MultiModelConsensus: {Method} with {Count}/{Total} models agreeing (score={Score:F2})",
            method, agreeing, responses.Count, agreementScore);

        return new MultiConsensusResult
        {
            Answer = answer,
            ModelVotes = votes,
            AgreementScore = agreementScore,
            ModelsUsed = modelNames.ToList(),
            Method = method,
            ModelsAgreeing = agreeing,
            HighestConfidence = agreementScore
        };
    }

    private static async Task<(string Model, string Response, bool Success)> CallClientAsync(
        IChatClient client, string modelName, List<ChatMessage> messages, CancellationToken ct)
    {
        try
        {
            var response = await client.GetResponseAsync(messages, null, ct);
            var text = response.Text ?? "";
            return (modelName, text, true);
        }
        catch (Exception)
        {
            return (modelName, "", false);
        }
    }

    private static Dictionary<(string, string), double> ComputePairwiseJaccard(List<string> responses)
    {
        var tokenSets = responses.Select(Tokenize).ToList();
        var pairs = new Dictionary<(string, string), double>();

        for (var i = 0; i < responses.Count; i++)
        {
            for (var j = i + 1; j < responses.Count; j++)
            {
                var a = tokenSets[i];
                var b = tokenSets[j];
                var intersection = a.Count(w => b.Contains(w));
                var union = a.Count + b.Count - intersection;
                var jaccard = union > 0 ? (double)intersection / union : 0;
                pairs[($"model_{i}", $"model_{j}")] = jaccard;
            }
        }

        return pairs;
    }

    private static List<List<(string Model, string Response)>> FindAgreementGroups(
        List<(string Model, string Response, bool)> responses,
        Dictionary<(string, string), double> pairs)
    {
        var n = responses.Count;
        var adj = new bool[n, n];

        for (var i = 0; i < n; i++)
        {
            for (var j = i + 1; j < n; j++)
            {
                var key = ($"model_{i}", $"model_{j}");
                var jaccard = pairs.TryGetValue(key, out var jv) ? jv : 0;
                adj[i, j] = jaccard > ConsensusThreshold;
                adj[j, i] = adj[i, j];
            }
        }

        var visited = new bool[n];
        var groups = new List<List<(string, string)>>();

        for (var i = 0; i < n; i++)
        {
            if (visited[i]) continue;

            var group = new List<(string, string)>();
            var queue = new Queue<int>();
            queue.Enqueue(i);
            visited[i] = true;

            while (queue.Count > 0)
            {
                var u = queue.Dequeue();
                group.Add((responses[u].Model, responses[u].Response));

                for (var v = 0; v < n; v++)
                {
                    if (!visited[v] && adj[u, v])
                    {
                        visited[v] = true;
                        queue.Enqueue(v);
                    }
                }
            }

            groups.Add(group);
        }

        return groups;
    }

    private static double ComputeAverageJaccard(string modelName, Dictionary<(string, string), double> pairs)
    {
        return pairs.Where(kv => kv.Key.Item1 == modelName || kv.Key.Item2 == modelName)
            .Select(kv => kv.Value)
            .DefaultIfEmpty(0)
            .Average();
    }

    internal static HashSet<string> Tokenize(string text)
    {
        return text.ToLower()
            .Split(new[] { ' ', '\n', '\r', '\t', '.', ',', ';', ':', '(', ')', '[', ']', '{', '}', '!', '?' },
                StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2)
            .ToHashSet();
    }
}

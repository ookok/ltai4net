using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LTAI.Core.System;

public sealed class MetamorphicRelation
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public Func<string, string> TransformInput { get; set; } = s => s;
    public Func<string, string, (bool passed, string reason)> CheckOutput { get; set; } = (a, b) => (true, "");
}

public sealed class GoldenTrace
{
    public string TraceId { get; set; } = "";
    public string InputQuery { get; set; } = "";
    public string ExpectedOutput { get; set; } = "";
    public List<string> ReasoningChain { get; set; } = new();
    public List<Dictionary<string, object>> ToolCalls { get; set; } = new();
    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
    public Dictionary<string, object> Metadata { get; set; } = new();
}

public sealed class HitlRequest
{
    public string RequestId { get; set; } = "";
    public string TaskId { get; set; } = "";
    public string Question { get; set; } = "";
    public string Context { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string Status { get; set; } = "pending";
    public string Response { get; set; } = "";
}

public sealed class AgentQA
{
    private static readonly Lazy<AgentQA> _instance = new(() => new AgentQA());
    public static AgentQA Instance => _instance.Value;

    private readonly List<MetamorphicRelation> _relations;
    private readonly List<Dictionary<string, object>> _results = new();
    private readonly Dictionary<string, GoldenTrace> _traces = new();
    private readonly Dictionary<string, HitlRequest> _pendingHitl = new();
    private readonly string _qaDir;
    private readonly object _lock = new();

    private AgentQA(string qaDir = ".livingtree/qa")
    {
        _qaDir = qaDir;
        Directory.CreateDirectory(qaDir);
        Directory.CreateDirectory(Path.Combine(qaDir, "golden_traces"));

        _relations = new List<MetamorphicRelation>
        {
            new()
            {
                Name = "length_monotonic",
                Description = "More detailed input should produce more output",
                TransformInput = inp => inp + " Please provide a more detailed analysis with background and data.",
                CheckOutput = (orig, trans) => (
                    trans.Length >= orig.Length * 0.5,
                    $"Original:{orig.Length} chars -> New:{trans.Length} chars")
            },
            new()
            {
                Name = "role_symmetry",
                Description = "Swapping roles should preserve core facts",
                TransformInput = inp => $"Assume you are the user. Ask questions about '{inp}'.",
                CheckOutput = (orig, trans) => (trans.Length > 10, $"Output length: {trans.Length}")
            },
            new()
            {
                Name = "format_preservation",
                Description = "Markdown format request should produce Markdown output",
                TransformInput = inp => $"Please answer in Markdown format:\n{inp}",
                CheckOutput = (orig, trans) =>
                    (trans.Contains('#') || trans.Contains('-') || trans.Contains('*'),
                     $"Contains Markdown: {trans.Contains('#') || trans.Contains('-') || trans.Contains('*')}")
            }
        };
    }

    public async Task<Dictionary<string, object>> RunTestAsync(
        Func<string, Task<string>> agentFn, string testInput)
    {
        var originalOutput = await agentFn(testInput);
        var results = new List<Dictionary<string, object>>();

        foreach (var rel in _relations)
        {
            try
            {
                var transformedInput = rel.TransformInput(testInput);
                var transformedOutput = await agentFn(transformedInput);
                var (checkOk, reason) = rel.CheckOutput(originalOutput, transformedOutput);
                results.Add(new Dictionary<string, object>
                {
                    ["relation"] = rel.Name,
                    ["passed"] = checkOk,
                    ["reason"] = reason,
                    ["original_len"] = originalOutput.Length,
                    ["transformed_len"] = transformedOutput.Length
                });
            }
            catch (Exception ex)
            {
                results.Add(new Dictionary<string, object>
                {
                    ["relation"] = rel.Name,
                    ["passed"] = false,
                    ["reason"] = ex.Message
                });
            }
        }

        var passed = results.Count(r => (bool)r["passed"]);
        var entry = new Dictionary<string, object>
        {
            ["input"] = testInput.Length > 200 ? testInput[..200] : testInput,
            ["relations_total"] = results.Count,
            ["relations_passed"] = passed,
            ["pass_rate"] = results.Count > 0 ? $"{(double)passed / results.Count * 100:F0}%" : "0%",
            ["results"] = results,
            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        lock (_lock)
        {
            _results.Add(entry);
        }

        var metaFile = Path.Combine(_qaDir, "metamorphic_results.jsonl");
        File.AppendAllText(metaFile, JsonSerializer.Serialize(entry) + "\n");

        return entry;
    }

    public Dictionary<string, object> GetMetamorphicSummary()
    {
        if (_results.Count == 0)
            return new Dictionary<string, object> { ["message"] = "No tests run yet" };

        int totalPassed = 0, totalRelations = 0;
        foreach (var r in _results)
        {
            totalPassed += Convert.ToInt32(r["relations_passed"]);
            totalRelations += Convert.ToInt32(r["relations_total"]);
        }

        return new Dictionary<string, object>
        {
            ["tests_run"] = _results.Count,
            ["total_relations"] = totalRelations,
            ["passed"] = totalPassed,
            ["overall_pass_rate"] = totalRelations > 0 ? $"{(double)totalPassed / totalRelations * 100:F0}%" : "0%"
        };
    }

    public string RecordGoldenTrace(string inputQuery, string output,
        List<string>? reasoning = null, List<Dictionary<string, object>>? tools = null)
    {
        var raw = $"{inputQuery}{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        var tid = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..16];

        var trace = new GoldenTrace
        {
            TraceId = tid,
            InputQuery = inputQuery,
            ExpectedOutput = output,
            ReasoningChain = reasoning ?? new List<string>(),
            ToolCalls = tools ?? new List<Dictionary<string, object>>()
        };

        lock (_lock)
        {
            _traces[tid] = trace;
        }

        var traceFile = Path.Combine(_qaDir, "golden_traces", $"{tid}.json");
        File.WriteAllText(traceFile, JsonSerializer.Serialize(trace));

        return tid;
    }

    public Dictionary<string, object> CompareTrace(string traceId, string currentOutput)
    {
        GoldenTrace? trace;
        lock (_lock)
        {
            if (!_traces.TryGetValue(traceId, out trace))
                return new Dictionary<string, object> { ["error"] = "Trace not found" };
        }

        if (trace.ExpectedOutput == currentOutput)
            return new Dictionary<string, object> { ["status"] = "exact_match", ["diff"] = "" };

        var goldenWords = new HashSet<string>(trace.ExpectedOutput.ToLower().Split(' '));
        var currentWords = new HashSet<string>(currentOutput.ToLower().Split(' '));
        var union = new HashSet<string>(goldenWords);
        union.UnionWith(currentWords);
        var overlap = union.Count > 0 ? (double)goldenWords.Intersect(currentWords).Count() / union.Count : 0;

        var status = overlap > 0.9 ? "exact_match" : overlap > 0.5 ? "minor_diff" : "significant_drift";

        return new Dictionary<string, object>
        {
            ["status"] = status,
            ["semantic_overlap"] = Math.Round(overlap, 3),
            ["length_ratio"] = Math.Round((double)currentOutput.Length / Math.Max(trace.ExpectedOutput.Length, 1), 2),
            ["diff"] = overlap > 0.5 ? "acceptable variation" : "significant drift detected"
        };
    }

    public string RequestApproval(string taskId, string question, string context = "")
    {
        var rid = $"hitl_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(question)))[..6]}";

        var req = new HitlRequest
        {
            RequestId = rid,
            TaskId = taskId,
            Question = question,
            Context = context
        };

        lock (_lock)
        {
            _pendingHitl[rid] = req;
        }

        var hitlFile = Path.Combine(_qaDir, "hitl_queue.jsonl");
        File.AppendAllText(hitlFile, JsonSerializer.Serialize(req) + "\n");

        return rid;
    }

    public bool Approve(string requestId, string response = "")
    {
        HitlRequest? req;
        lock (_lock)
        {
            if (!_pendingHitl.TryGetValue(requestId, out req))
                return false;
            req.Status = "approved";
            req.Response = response;
        }
        return true;
    }

    public bool Reject(string requestId, string reason = "")
    {
        HitlRequest? req;
        lock (_lock)
        {
            if (!_pendingHitl.TryGetValue(requestId, out req))
                return false;
            req.Status = "rejected";
            req.Response = reason;
        }
        return true;
    }

    public List<Dictionary<string, object>> GetPendingHitl()
    {
        lock (_lock)
        {
            return _pendingHitl.Values
                .Where(r => r.Status == "pending")
                .Select(r => new Dictionary<string, object>
                {
                    ["id"] = r.RequestId,
                    ["task"] = r.TaskId,
                    ["question"] = r.Question,
                    ["context"] = r.Context,
                    ["created"] = new DateTimeOffset(r.CreatedAt).ToUnixTimeSeconds()
                })
                .ToList();
        }
    }
}

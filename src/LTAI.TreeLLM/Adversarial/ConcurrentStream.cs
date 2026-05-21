using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using LTAI.TreeLLM.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.TreeLLM.Adversarial;

public sealed class ConcurrentStream
{
    private const int FLASH_FIRST_TOKEN_TARGET_MS = 500;
    private const int PRO_WEAVE_DELAY_MS = 2000;
    private const int MAX_PRO_INSIGHTS = 3;
    private const int EARLY_DISPATCH_TOKENS = 3;

    private static readonly Lazy<ConcurrentStream> _instance = new(() => new ConcurrentStream());
    public static ConcurrentStream Instance => _instance.Value;

    private Func<string, string, Task<string>>? _streamChatFn;
    private bool _connected;
    private readonly Lock _connectLock = new();

    private ILogger<ConcurrentStream>? _logger;

    private ConcurrentStream() { }

    public void SetLogger(ILogger<ConcurrentStream> logger) => _logger = logger;

    public void SetStreamChatFn(Func<string, string, Task<string>> fn)
    {
        lock (_connectLock)
        {
            _streamChatFn = fn;
            _connected = true;
        }
    }

    public bool AutoConnect()
    {
        lock (_connectLock)
        {
            _connected = _streamChatFn != null;
            return _connected;
        }
    }

    public async IAsyncEnumerable<StreamEvent> Stream(
        string query,
        string flashModel,
        string proModel,
        string systemPrompt,
        string taskType,
        Func<string, string, Task<string>> flashFn,
        Func<string, string, Task<string>> proFn,
        Func<string, Task<string>>? deepProbeFn = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var seq = 0;

        var flashTokens = new List<string>();
        var proTokens = new List<string>();
        string? flashFull = null;
        string? proFull = null;
        Exception? flashError = null;
        Exception? proError = null;

        var flashTcs = new TaskCompletionSource<bool>();
        var proTcs = new TaskCompletionSource<bool>();

        var flashTask = Task.Run(async () =>
        {
            try
            {
                flashFull = await flashFn(query, systemPrompt);
                flashTcs.SetResult(true);
            }
            catch (Exception ex)
            {
                flashError = ex;
                flashTcs.SetResult(false);
            }
        }, cancellationToken);

        var proTask = Task.Run(async () =>
        {
            try
            {
                proFull = await proFn(query, systemPrompt);
                proTcs.SetResult(true);
            }
            catch (Exception ex)
            {
                proError = ex;
                proTcs.SetResult(false);
            }
        }, cancellationToken);

        if (deepProbeFn != null)
        {
            try
            {
                await deepProbeFn(query);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Deep probe failed, continuing");
            }
        }

        var weaveCount = 0;
        var earlyDispatched = false;

        while (!flashTcs.Task.IsCompleted || !proTcs.Task.IsCompleted)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (flashTcs.Task.IsCompleted && flashFull != null)
            {
                var words = flashFull.Split(' ');
                var currentCount = flashTokens.Count;

                for (var i = currentCount; i < words.Length; i++)
                {
                    flashTokens.Add(words[i]);

                    yield return new StreamEvent
                    {
                        Kind = StreamEventKind.FlashToken,
                        Text = words[i],
                        Provider = flashModel,
                        Sequence = ++seq,
                        Timestamp = DateTime.UtcNow
                    };

                    var tokenCount = i + 1;

                    if (!earlyDispatched && tokenCount >= EARLY_DISPATCH_TOKENS)
                    {
                        earlyDispatched = true;
                        yield return new StreamEvent
                        {
                            Kind = StreamEventKind.EarlyDispatch,
                            Text = string.Join(" ", flashTokens),
                            Provider = flashModel,
                            Sequence = ++seq,
                            Timestamp = DateTime.UtcNow,
                            Metadata = new Dictionary<string, object>
                            {
                                ["tokens_received"] = tokenCount,
                                ["elapsed_ms"] = sw.ElapsedMilliseconds
                            }
                        };
                    }

                    if (proTcs.Task.IsCompleted && proFull != null && weaveCount < MAX_PRO_INSIGHTS)
                    {
                        if (_IsWeavePoint(flashTokens, weaveCount))
                        {
                            var proInsights = _ExtractInsights(proFull, MAX_PRO_INSIGHTS);
                            if (weaveCount < proInsights.Count)
                            {
                                var insight = proInsights[weaveCount];
                                weaveCount++;

                                yield return new StreamEvent
                                {
                                    Kind = StreamEventKind.ProInsight,
                                    Text = insight,
                                    Provider = proModel,
                                    Sequence = ++seq,
                                    Timestamp = DateTime.UtcNow,
                                    Metadata = new Dictionary<string, object>
                                    {
                                        ["weave_index"] = weaveCount,
                                        ["insight_source"] = "pro_model"
                                    }
                                };
                            }
                        }
                    }
                }

                break;
            }

            await Task.Delay(50, cancellationToken);
        }

        if (flashError != null && flashFull == null)
        {
            yield return new StreamEvent
            {
                Kind = StreamEventKind.Error,
                Text = $"Flash stream failed: {flashError.Message}",
                Provider = flashModel,
                Sequence = ++seq,
                Timestamp = DateTime.UtcNow
            };
        }

        yield return new StreamEvent
        {
            Kind = StreamEventKind.FlashComplete,
            Text = flashFull ?? "",
            Provider = flashModel,
            Sequence = ++seq,
            Timestamp = DateTime.UtcNow,
            Metadata = new Dictionary<string, object>
            {
                ["latency_ms"] = sw.ElapsedMilliseconds,
                ["tokens"] = flashFull?.Length / 4 ?? 0
            }
        };

        if (proError != null && proFull == null)
        {
            yield return new StreamEvent
            {
                Kind = StreamEventKind.Error,
                Text = $"Pro stream failed: {proError.Message}",
                Provider = proModel,
                Sequence = ++seq,
                Timestamp = DateTime.UtcNow
            };
        }

        yield return new StreamEvent
        {
            Kind = StreamEventKind.ProComplete,
            Text = proFull ?? "",
            Provider = proModel,
            Sequence = ++seq,
            Timestamp = DateTime.UtcNow,
            Metadata = new Dictionary<string, object>
            {
                ["latency_ms"] = sw.ElapsedMilliseconds,
                ["tokens"] = proFull?.Length / 4 ?? 0
            }
        };

        var fused = !string.IsNullOrWhiteSpace(flashFull)
            ? (flashFull! + (weaveCount > 0 ? "\n\n--- Key Insights ---\n" : ""))
            : proFull ?? "";

        yield return new StreamEvent
        {
            Kind = StreamEventKind.Meta,
            Text = fused,
            Provider = "concurrent",
            Sequence = ++seq,
            Timestamp = DateTime.UtcNow,
            Metadata = new Dictionary<string, object>
            {
                ["flash_model"] = flashModel,
                ["pro_model"] = proModel,
                ["task_type"] = taskType,
                ["weave_count"] = weaveCount,
                ["total_latency_ms"] = sw.ElapsedMilliseconds
            }
        };
    }

    public async Task<ConcurrentResult> Collect(
        string query,
        string flashModel,
        string proModel,
        string systemPrompt,
        string taskType,
        Func<string, string, Task<string>> chatFn)
    {
        var sw = Stopwatch.StartNew();
        var events = new List<StreamEvent>();

        var flashSw = Stopwatch.StartNew();
        string flashOutput;

        try
        {
            flashOutput = await chatFn($"[Flash] {query}", systemPrompt);
        }
        catch (Exception ex)
        {
            flashOutput = "";
            events.Add(new StreamEvent
            {
                Kind = StreamEventKind.Error,
                Text = ex.Message,
                Provider = flashModel,
                Sequence = events.Count
            });
        }

        var flashLatency = flashSw.ElapsedMilliseconds;

        var proSw = Stopwatch.StartNew();
        string proOutput;

        try
        {
            proOutput = await chatFn($"[Pro] {query}", systemPrompt);
        }
        catch (Exception ex)
        {
            proOutput = "";
            events.Add(new StreamEvent
            {
                Kind = StreamEventKind.Error,
                Text = ex.Message,
                Provider = proModel,
                Sequence = events.Count
            });
        }

        var proLatency = proSw.ElapsedMilliseconds;

        var insights = _ExtractInsights(proOutput, MAX_PRO_INSIGHTS);

        var fusedOutput = flashOutput;
        if (insights.Count > 0)
        {
            fusedOutput += "\n\n--- Key Insights ---\n" +
                string.Join("\n", insights.Select(s => $"- {s}"));
        }

        return new ConcurrentResult
        {
            FlashOutput = flashOutput,
            ProOutput = proOutput,
            FusedOutput = fusedOutput,
            Events = events,
            FlashLatencyMs = flashLatency,
            ProLatencyMs = proLatency,
            FlashTokens = flashOutput.Length / 4,
            ProTokens = proOutput.Length / 4,
            WeaveCount = insights.Count
        };
    }

    private static List<string> _ExtractInsights(string text, int max = 3)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<string>();

        var insightKeywords = new[]
        {
            "key", "critical", "important", "crucial", "essential",
            "notably", "significantly", "fundamental", "vital", "major"
        };

        var sentences = Regex.Split(text, @"(?<=[.!?])\s+")
            .Where(s => s.Trim().Length >= 30)
            .ToList();

        var insights = new List<string>();

        foreach (var sentence in sentences)
        {
            if (insights.Count >= max)
                break;

            var lower = sentence.ToLowerInvariant();
            var score = insightKeywords.Count(k => lower.Contains(k));

            if (score >= 1)
                insights.Add(sentence.Trim());
        }

        return insights;
    }

    private static bool _IsWeavePoint(List<string> flashTokens, int weaveCount)
    {
        if (flashTokens.Count < 5)
            return false;

        var lastFive = flashTokens.TakeLast(5).ToList();

        var pauseIndicators = new[] { ".", "。", "\n\n" };
        var combined = string.Join("", lastFive);

        if (pauseIndicators.Any(p => combined.Contains(p)))
            return true;

        var summaryMarkers = new[] { "综上", "总之", "综上所述", "in summary", "to summarize" };
        if (summaryMarkers.Any(m => combined.Contains(m, StringComparison.OrdinalIgnoreCase)))
            return true;

        return false;
    }
}

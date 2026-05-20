namespace LTAI.TreeLLM.Session;

public sealed record SegmentWriteConfig
{
    public WriteMode WriteMode { get; init; } = WriteMode.Segment;
    public int TokenWiseWindowSize { get; init; } = 64;
    public int TokenWiseStride { get; init; } = 32;
    public int MultiSegmentMinSize { get; init; } = 128;
    public int MultiSegmentOverlap { get; init; } = 32;
    public int MaxSegmentsPerWrite { get; init; } = 8;
    public double WriteThreshold { get; init; } = 0.3;
    public bool SkipLowInformation { get; init; } = true;
    public int MaxHistorySegments { get; init; } = 100;
}

public sealed record WriteResult
{
    public WriteMode ModeUsed { get; init; }
    public int SegmentsWritten { get; init; }
    public int TokensWritten { get; init; }
    public double InfoDensity { get; init; }
    public bool Skipped { get; init; }
    public long ElapsedMs { get; init; }
}

public sealed class SegmentWriteStrategies
{
    private readonly OnlineMemoryState _memoryState;
    private readonly SegmentWriteConfig _config;
    private readonly List<SegmentRecord> _history = new();
    private readonly List<StrategyEffectiveness> _effectiveness = new();
    private readonly object _lock = new();

    public SegmentWriteStrategies(
        OnlineMemoryState? memoryState = null,
        SegmentWriteConfig? config = null)
    {
        _memoryState = memoryState ?? new OnlineMemoryState();
        _config = config ?? new SegmentWriteConfig();
    }

    public WriteResult WriteTokenWise(string text)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tokens = Tokenize(text);

        if (tokens.Count < _config.TokenWiseWindowSize)
        {
            var result = WriteSingleSegment(text, WriteMode.TokenWise);
            sw.Stop();
            return result with { ElapsedMs = sw.ElapsedMilliseconds };
        }

        int written = 0;
        for (int i = 0; i < tokens.Count; i += _config.TokenWiseStride)
        {
            var window = tokens.Skip(i).Take(_config.TokenWiseWindowSize).ToList();
            if (window.Count < _config.WriteThreshold * _config.TokenWiseWindowSize)
                break;

            var segment = string.Join(" ", window.Select(t => t.Text));
            var info = ComputeInfoDensity(segment);

            if (_config.SkipLowInformation && info < _config.WriteThreshold)
                continue;

            _memoryState.Write(segment, WriteMode.TokenWise);
            written++;
        }

        RecordHistory(text, WriteMode.TokenWise, written);
        sw.Stop();

        return new WriteResult
        {
            ModeUsed = WriteMode.TokenWise,
            SegmentsWritten = written,
            TokensWritten = tokens.Count,
            InfoDensity = written > 0 ? ComputeInfoDensity(text) : 0,
            ElapsedMs = sw.ElapsedMilliseconds
        };
    }

    public WriteResult WriteSegmentWise(string text)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var info = ComputeInfoDensity(text);

        if (_config.SkipLowInformation && info < _config.WriteThreshold)
        {
            sw.Stop();
            return new WriteResult
            {
                ModeUsed = WriteMode.Segment,
                SegmentsWritten = 0,
                TokensWritten = text.Length / 4,
                InfoDensity = info,
                Skipped = true,
                ElapsedMs = sw.ElapsedMilliseconds
            };
        }

        _memoryState.Write(text, WriteMode.Segment);
        RecordHistory(text, WriteMode.Segment, 1);
        sw.Stop();

        return new WriteResult
        {
            ModeUsed = WriteMode.Segment,
            SegmentsWritten = 1,
            TokensWritten = text.Length / 4,
            InfoDensity = info,
            ElapsedMs = sw.ElapsedMilliseconds
        };
    }

    public WriteResult WriteMultiSegment(string text)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tokens = Tokenize(text);

        if (tokens.Count <= _config.MultiSegmentMinSize)
        {
            var result = WriteSegmentWise(text);
            sw.Stop();
            return result with { ElapsedMs = sw.ElapsedMilliseconds };
        }

        var segments = SlidingWindowSegments(tokens);
        int written = 0;

        foreach (var segment in segments.Take(_config.MaxSegmentsPerWrite))
        {
            var segmentText = string.Join(" ", segment.Select(t => t.Text));
            var info = ComputeInfoDensity(segmentText);

            if (_config.SkipLowInformation && info < _config.WriteThreshold)
                continue;

            _memoryState.Write(segmentText, WriteMode.MultiSegment);
            written++;
        }

        RecordHistory(text, WriteMode.MultiSegment, written);
        sw.Stop();

        return new WriteResult
        {
            ModeUsed = WriteMode.MultiSegment,
            SegmentsWritten = written,
            TokensWritten = tokens.Count,
            InfoDensity = written > 0 ? ComputeInfoDensity(text) : 0,
            ElapsedMs = sw.ElapsedMilliseconds
        };
    }

    public WriteResult WriteAuto(string text)
    {
        var tokens = text.Length / 4;
        var info = ComputeInfoDensity(text);

        if (info > 0.6 && tokens > _config.MultiSegmentMinSize)
            return WriteMultiSegment(text);
        if (tokens > 256)
            return WriteTokenWise(text);
        return WriteSegmentWise(text);
    }

    public WriteResult WriteAccordingToConfig(string text)
    {
        return _config.WriteMode switch
        {
            WriteMode.TokenWise => WriteTokenWise(text),
            WriteMode.MultiSegment => WriteMultiSegment(text),
            _ => WriteSegmentWise(text)
        };
    }

    public string BuildMemoryEnhancedPrompt(string prompt, int maxMemoryTokens = 200)
    {
        var queryVec = ComputeQueryVector(prompt);
        var memoryContext = _memoryState.BuildMemoryContext(queryVec, maxMemoryTokens);

        if (string.IsNullOrEmpty(memoryContext))
            return prompt;

        return $"{memoryContext}\n\n---\n\n{prompt}";
    }

    public void UpdateStrategyEffectiveness(WriteMode mode, bool improved)
    {
        lock (_lock)
        {
            var existing = _effectiveness.FirstOrDefault(e => e.Mode == mode);
            if (existing == null)
            {
                _effectiveness.Add(new StrategyEffectiveness(mode, improved ? 0.7 : 0.3, 1));
            }
            else
            {
                existing.Score = existing.Score * 0.9 + (improved ? 0.2 : 0.05);
                existing.Trials++;
            }

            while (_effectiveness.Count > 20)
                _effectiveness.RemoveAt(0);
        }
    }

    public Dictionary<string, object> GetStats()
    {
        lock (_lock)
        {
            return new()
            {
                ["history_count"] = _history.Count,
                ["total_tokens_written"] = _history.Sum(h => h.Tokens),
                ["total_segments"] = _history.Sum(h => h.Segments),
                ["avg_info_density"] = Math.Round(_history.Count > 0 ? _history.Average(h => h.InfoDensity) : 0, 3),
                ["strategy_effectiveness"] = _effectiveness
                    .OrderByDescending(e => e.Score)
                    .Select(e => new { mode = e.Mode.ToString(), score = Math.Round(e.Score, 3), e.Trials })
                    .ToList(),
                ["best_strategy"] = _effectiveness.Count > 0
                    ? _effectiveness.MaxBy(e => e.Score)?.Mode.ToString() ?? "Segment"
                    : "Segment"
            };
        }
    }

    private List<List<(string Text, int Pos)>> SlidingWindowSegments(
        List<(string Text, int Pos)> tokens)
    {
        var segments = new List<List<(string Text, int Pos)>>();
        for (int i = 0; i < tokens.Count; i += _config.MultiSegmentMinSize - _config.MultiSegmentOverlap)
        {
            var segment = tokens.Skip(i).Take(_config.MultiSegmentMinSize).ToList();
            if (segment.Count < _config.MultiSegmentMinSize / 2) break;
            segments.Add(segment);
        }
        return segments;
    }

    private WriteResult WriteSingleSegment(string text, WriteMode mode)
    {
        _memoryState.Write(text, mode);
        RecordHistory(text, mode, 1);
        return new WriteResult
        {
            ModeUsed = mode,
            SegmentsWritten = 1,
            TokensWritten = text.Length / 4,
            InfoDensity = ComputeInfoDensity(text),
        };
    }

    private void RecordHistory(string text, WriteMode mode, int segments)
    {
        lock (_lock)
        {
            _history.Add(new SegmentRecord(
                Guid.NewGuid().ToString("N")[..8],
                mode,
                text.Length / 4,
                segments,
                ComputeInfoDensity(text),
                DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

            while (_history.Count > _config.MaxHistorySegments)
                _history.RemoveAt(0);
        }
    }

    private static List<(string Text, int Pos)> Tokenize(string text)
    {
        var words = text.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        return words.Select((w, i) => (w, i)).ToList();
    }

    private static double ComputeInfoDensity(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;

        var words = text.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 3) return 0.2;

        var uniqueRatio = (double)new HashSet<string>(words).Count / words.Length;

        var highValue = new[] { "决定", "选择", "发现", "证据", "结论", "关键", "重要",
            "decided", "found", "evidence", "conclusion", "key", "important", "result" };
        var highValueCount = words.Count(w =>
            highValue.Any(hv => w.Contains(hv, StringComparison.OrdinalIgnoreCase)));

        var valueRatio = (double)highValueCount / Math.Max(1, words.Length);

        var punctuation = text.Count(c => c is '。' or '.' or '!' or '？' or '?' or '\n');
        var structureRatio = Math.Min(1.0, punctuation / (double)Math.Max(1, words.Length / 10));

        return Math.Min(1.0, uniqueRatio * 0.4 + valueRatio * 0.35 + structureRatio * 0.25);
    }

    private static float[] ComputeQueryVector(string text, int dim = 384)
    {
        var vec = new float[dim];
        var hash = (uint)text.GetHashCode();
        var rng = new Random((int)hash);
        for (int i = 0; i < dim; i++)
            vec[i] = ((float)rng.NextDouble() - 0.5f) * 2.0f;
        float norm = 0;
        for (int i = 0; i < dim; i++) norm += vec[i] * vec[i];
        norm = MathF.Sqrt(norm);
        if (norm > 1e-8f)
            for (int i = 0; i < dim; i++) vec[i] /= norm;
        return vec;
    }

    private sealed record SegmentRecord(
        string Id, WriteMode Mode, int Tokens, int Segments,
        double InfoDensity, double Timestamp);

    private sealed class StrategyEffectiveness(WriteMode mode, double score, int trials)
    {
        public WriteMode Mode { get; } = mode;
        public double Score { get; set; } = score;
        public int Trials { get; set; } = trials;
    }
}

using System.Collections.Concurrent;
using System.Numerics;
using System.Text.Json;

namespace LTAI.Agent.Session;

public sealed record OnlineMemoryStats
{
    public int StateDim { get; init; }
    public int ReadRank { get; init; }
    public int WriteCount { get; init; }
    public int ReadCount { get; init; }
    public double AvgWriteNorm { get; init; }
    public double AvgReadNorm { get; init; }
    public double LearningRate { get; init; }
    public double[]? TopSingularValues { get; init; }
}

public sealed class OnlineMemoryState
{
    private readonly int _stateDim;
    private readonly int _readRank;
    private readonly float _learningRate;
    private readonly float _decayRate;
    private readonly object _lock = new();

    private float[] _stateKey;    // StateDim × StateDim key matrix (flattened row-major)
    private float[] _stateValue;  // StateDim × StateDim value matrix (flattened row-major)
    private float[] _writeProj; // StateDim × EmbedDim projection for writing
    private float[] _readKeyProj;  // ReadRank × StateDim for query projection
    private float[] _readValueProj; // StateDim × ReadRank for value projection

    private int _writeCount;
    private int _readCount;
    private double _cumulativeWriteNorm;
    private double _cumulativeReadNorm;

    private const int EmbedDim = 384;
    private const float Epsilon = 1e-8f;
    private const int MaxHistoryTokens = 8192;

    private readonly ConcurrentQueue<string> _segmentHistory = new();
    private int _totalHistoryTokens;

    public OnlineMemoryState(int stateDim = 16, int readRank = 4, float learningRate = 0.01f, float decayRate = 0.999f)
    {
        _stateDim = stateDim;
        _readRank = readRank;
        _learningRate = learningRate;
        _decayRate = decayRate;

        _stateKey = new float[_stateDim * _stateDim];
        _stateValue = new float[_stateDim * _stateDim];
        _writeProj = InitProjection(_stateDim * EmbedDim);
        _readKeyProj = InitProjection(_readRank * _stateDim);
        _readValueProj = InitProjection(_stateDim * _readRank);
    }

    private static float[] InitProjection(int size)
    {
        var arr = new float[size];
        var rng = new Random(size.GetHashCode());
        float scale = 1.0f / MathF.Sqrt(size);
        for (int i = 0; i < size; i++)
            arr[i] = ((float)rng.NextDouble() - 0.5f) * 2.0f * scale;
        return arr;
    }

    public void Write(string content, WriteMode mode = WriteMode.Segment)
    {
        var tokenVecs = TokenizeToVectors(content);
        if (tokenVecs.Count == 0) return;

        float[] segmentVec;
        switch (mode)
        {
            case WriteMode.TokenWise:
                foreach (var tv in tokenVecs)
                    WriteSingle(tv);
                return;
            case WriteMode.MultiSegment:
                var chunks = SplitIntoChunks(tokenVecs, 128);
                foreach (var chunk in chunks)
                {
                    segmentVec = AverageVectors(chunk);
                    WriteSingle(segmentVec);
                }
                return;
            default:
                segmentVec = AverageVectors(tokenVecs);
                WriteSingle(segmentVec);
                break;
        }

        _segmentHistory.Enqueue(content);
        _totalHistoryTokens += content.Length / 4;
        while (_totalHistoryTokens > MaxHistoryTokens && _segmentHistory.TryDequeue(out var old))
            _totalHistoryTokens -= old.Length / 4;
    }

    private void WriteSingle(float[] inputVec)
    {
        lock (_lock)
        {
            var projected = new float[_stateDim];
            for (int i = 0; i < _stateDim; i++)
            {
                float sum = 0;
                for (int j = 0; j < Math.Min(inputVec.Length, EmbedDim); j++)
                    sum += _writeProj[i * EmbedDim + j] * inputVec[j];
                projected[i] = sum;
            }

            Normalize(projected);

            for (int i = 0; i < _stateDim; i++)
            {
                for (int j = 0; j < _stateDim; j++)
                {
                    int idx = i * _stateDim + j;
                    float keyOuter = projected[i] * projected[j];
                    float valueOuter = projected[i] * projected[j];

                    _stateKey[idx] = _stateKey[idx] * _decayRate + _learningRate * keyOuter;
                    _stateValue[idx] = _stateValue[idx] * _decayRate + _learningRate * valueOuter;
                }
            }

            _writeCount++;
            float writeNorm = Norm(projected);
            _cumulativeWriteNorm += writeNorm;
        }
    }

    public float[] Read(float[] queryVec, int? topK = null)
    {
        int k = topK ?? _readRank;

        lock (_lock)
        {
            var queryProj = new float[k];
            for (int i = 0; i < k; i++)
            {
                float sum = 0;
                for (int j = 0; j < Math.Min(queryVec.Length, _stateDim); j++)
                    sum += _readKeyProj[i * _stateDim + j] * queryVec[(j * EmbedDim / _stateDim) % Math.Min(queryVec.Length, EmbedDim)];
                queryProj[i] = sum;
            }

            var readoutVec = new float[_stateDim];
            for (int i = 0; i < _stateDim; i++)
            {
                float sum = 0;
                for (int j = 0; j < k; j++)
                {
                    int idx = j * _stateDim + i;
                    float keyVal = QueryKeyElement(j, queryVec);
                    sum += _readValueProj[i * k + j] * keyVal;
                }
                readoutVec[i] = sum;
            }

            Normalize(readoutVec);

            _readCount++;
            _cumulativeReadNorm += Norm(readoutVec);

            return readoutVec;
        }
    }

    public float[] ReadWithAttentionCorrection(float[] queryVec)
    {
        var memoryReadout = Read(queryVec);

        var correction = new float[_stateDim];
        lock (_lock)
        {
            for (int i = 0; i < _stateDim; i++)
            {
                float sum = 0;
                for (int j = 0; j < _stateDim; j++)
                {
                    int idx = i * _stateDim + j;
                    sum += _stateValue[idx] * memoryReadout[j];
                }
                correction[i] = sum * _learningRate;
            }
        }

        Normalize(correction);
        return correction;
    }

    public string BuildMemoryContext(float[] queryVec, int maxTokens = 200)
    {
        var readout = Read(queryVec);

        var topIndices = Enumerable.Range(0, Math.Min(6, readout.Length))
            .Select(i => (idx: i, val: readout[i]))
            .OrderByDescending(x => Math.Abs(x.val))
            .Take(3)
            .Select(x => x.idx)
            .ToList();

        var parts = new List<string> { $"[OnlineMemory: dim={_stateDim} writes={_writeCount} decay={_decayRate:F4}]" };

        parts.AddRange(topIndices.Select(i => $"- channel_{i}: {readout[i]:F4}"));

        var recent = _segmentHistory.TakeLast(2).ToList();
        if (recent.Count > 0)
        {
            var latest = recent.Last();
            var preview = latest.Length > 80 ? latest[..80] + "..." : latest;
            parts.Add($"- recent: {preview}");
        }

        return string.Join("\n", parts);
    }

    public float ComputeMemorySurprise(float[] currentVec)
    {
        var readout = Read(currentVec);

        lock (_lock)
        {
            float reconstructionError = 0;
            for (int i = 0; i < Math.Min(currentVec.Length, _stateDim); i++)
            {
                float diff = currentVec[i % currentVec.Length] - readout[i % readout.Length];
                reconstructionError += diff * diff;
            }

            return MathF.Sqrt(reconstructionError / Math.Min(currentVec.Length, _stateDim));
        }
    }

    public OnlineMemoryStats GetStats()
    {
        lock (_lock)
        {
            var singularValues = EstimateTopSingulars();

            return new OnlineMemoryStats
            {
                StateDim = _stateDim,
                ReadRank = _readRank,
                WriteCount = _writeCount,
                ReadCount = _readCount,
                AvgWriteNorm = Math.Round(_writeCount > 0 ? _cumulativeWriteNorm / _writeCount : 0, 4),
                AvgReadNorm = Math.Round(_readCount > 0 ? _cumulativeReadNorm / _readCount : 0, 4),
                LearningRate = Math.Round(_learningRate, 4),
                TopSingularValues = singularValues?.Select(v => Math.Round(v, 4)).ToArray()
            };
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            Array.Clear(_stateKey, 0, _stateKey.Length);
            Array.Clear(_stateValue, 0, _stateValue.Length);
            _writeCount = 0;
            _readCount = 0;
            _cumulativeWriteNorm = 0;
            _cumulativeReadNorm = 0;
            _segmentHistory.Clear();
            _totalHistoryTokens = 0;
        }
    }

    public void Save(string path)
    {
        lock (_lock)
        {
            var data = new
            {
                state_dim = _stateDim,
                read_rank = _readRank,
                learning_rate = _learningRate,
                decay_rate = _decayRate,
                write_count = _writeCount,
                read_count = _readCount,
                state_key = Convert.ToBase64String(FloatsToBytes(_stateKey)),
                state_value = Convert.ToBase64String(FloatsToBytes(_stateValue)),
                write_proj = Convert.ToBase64String(FloatsToBytes(_writeProj)),
                read_key_proj = Convert.ToBase64String(FloatsToBytes(_readKeyProj)),
                read_value_proj = Convert.ToBase64String(FloatsToBytes(_readValueProj)),
                history = _segmentHistory.ToArray()
            };
            File.WriteAllText(path, JsonSerializer.Serialize(data));
        }
    }

    public static OnlineMemoryState Load(string path, int stateDim = 16, int readRank = 4)
    {
        var json = File.ReadAllText(path);
        var doc = JsonDocument.Parse(json).RootElement;

        var lr = doc.TryGetProperty("learning_rate", out var v) ? v.GetSingle() : 0.01f;
        var dr = doc.TryGetProperty("decay_rate", out v) ? v.GetSingle() : 0.999f;
        var result = new OnlineMemoryState(stateDim, readRank, lr, dr);

        lock (result._lock)
        {
            if (doc.TryGetProperty("state_key", out var sk))
                result._stateKey = BytesToFloats(Convert.FromBase64String(sk.GetString() ?? throw new InvalidOperationException("state_key is null in memory state file")));
            if (doc.TryGetProperty("state_value", out var sv))
                result._stateValue = BytesToFloats(Convert.FromBase64String(sv.GetString() ?? throw new InvalidOperationException("state_value is null in memory state file")));
            if (doc.TryGetProperty("write_proj", out var wp))
                result._writeProj = BytesToFloats(Convert.FromBase64String(wp.GetString() ?? throw new InvalidOperationException("write_proj is null in memory state file")));
            if (doc.TryGetProperty("read_key_proj", out var rkp))
                result._readKeyProj = BytesToFloats(Convert.FromBase64String(rkp.GetString() ?? throw new InvalidOperationException("read_key_proj is null in memory state file")));
            if (doc.TryGetProperty("read_value_proj", out var rvp))
                result._readValueProj = BytesToFloats(Convert.FromBase64String(rvp.GetString() ?? throw new InvalidOperationException("read_value_proj is null in memory state file")));
            if (doc.TryGetProperty("write_count", out var wc))
                result._writeCount = wc.GetInt32();
            if (doc.TryGetProperty("read_count", out var rc))
                result._readCount = rc.GetInt32();
            if (doc.TryGetProperty("history", out var hist))
            {
                foreach (var h in hist.EnumerateArray())
                {
                    var hStr = h.GetString() ?? throw new InvalidOperationException("history entry is null in memory state file");
                    result._segmentHistory.Enqueue(hStr);
                    result._totalHistoryTokens += hStr.Length / 4;
                }
            }
        }

        return result;
    }

    private List<float[]> TokenizeToVectors(string text)
    {
        var vectors = new List<float[]>();
        var words = text.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < words.Length; i++)
        {
            if (words[i].Length < 2) continue;
            var vec = HashToVector(words[i], EmbedDim);
            if (i > 0)
            {
                var bigram = (words[i - 1] + "_" + words[i]).ToLowerInvariant();
                var bigramVec = HashToVector(bigram, EmbedDim);
                for (int j = 0; j < EmbedDim; j++)
                    vec[j] = vec[j] * 0.7f + bigramVec[j] * 0.3f;
            }
            vectors.Add(vec);
        }

        if (vectors.Count == 0)
        {
            var fallback = HashToVector(text, EmbedDim);
            vectors.Add(fallback);
        }

        return vectors;
    }

    private static List<List<float[]>> SplitIntoChunks(List<float[]> tokens, int chunkSize)
    {
        var chunks = new List<List<float[]>>();
        for (int i = 0; i < tokens.Count; i += chunkSize)
        {
            chunks.Add(tokens.Skip(i).Take(chunkSize).ToList());
        }
        return chunks;
    }

    private static float[] AverageVectors(List<float[]> vectors)
    {
        var avg = new float[EmbedDim];
        foreach (var vec in vectors)
        {
            for (int i = 0; i < Math.Min(vec.Length, EmbedDim); i++)
                avg[i] += vec[i];
        }
        for (int i = 0; i < EmbedDim; i++)
            avg[i] /= vectors.Count;
        Normalize(avg);
        return avg;
    }

    private float QueryKeyElement(int rankIdx, float[] queryVec)
    {
        float sum = 0;
        lock (_lock)
        {
            for (int j = 0; j < _stateDim; j++)
            {
                int stateIdx = rankIdx * _stateDim + j;
                if (stateIdx < _stateKey.Length)
                    sum += _stateKey[stateIdx] * queryVec[Math.Min(j, queryVec.Length - 1)];
            }
        }
        return sum;
    }

    private double[] EstimateTopSingulars()
    {
        var vals = new List<double>();
        for (int r = 0; r < Math.Min(3, _readRank); r++)
        {
            double norm = 0;
            for (int i = 0; i < _stateDim; i++)
            {
                int idx = r * _stateDim + i;
                if (idx < _stateKey.Length)
                    norm += _stateKey[idx] * _stateKey[idx];
            }
            vals.Add(Math.Sqrt(norm));
        }
        return vals.ToArray();
    }

    private static float[] HashToVector(string text, int dim)
    {
        var vec = new float[dim];
        var hash = (uint)text.GetHashCode();
        var rng = new Random((int)hash);
        for (int i = 0; i < dim; i++)
            vec[i] = ((float)rng.NextDouble() - 0.5f) * 2.0f;
        Normalize(vec);
        return vec;
    }

    private static void Normalize(float[] vec)
    {
        float norm = 0;
        for (int i = 0; i < vec.Length; i++)
            norm += vec[i] * vec[i];
        norm = MathF.Sqrt(norm);
        if (norm > Epsilon)
        {
            for (int i = 0; i < vec.Length; i++)
                vec[i] /= norm;
        }
    }

    private static float Norm(float[] vec)
    {
        float sum = 0;
        for (int i = 0; i < vec.Length; i++)
            sum += vec[i] * vec[i];
        return MathF.Sqrt(sum);
    }

    private static byte[] FloatsToBytes(float[] floats)
    {
        var bytes = new byte[floats.Length * 4];
        Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] BytesToFloats(byte[] bytes)
    {
        var floats = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }
}

public enum WriteMode { TokenWise, Segment, MultiSegment }

using System.Runtime.CompilerServices;

namespace LTAI.AI.Governors;

public sealed class CorrectionState
{
    public string Domain { get; init; } = "";
    public float[] Matrix { get; init; } = [];
    public int DimK { get; init; }
    public int DimV { get; init; }
    public float[] DecayAccumulator { get; init; } = [];
}

public sealed class CorrectionRecord
{
    public string Domain { get; init; } = "";
    public string Query { get; init; } = "";
    public string WrongOutput { get; init; } = "";
    public string CorrectOutput { get; init; } = "";
    public float EraseStrength { get; init; }
    public float WriteStrength { get; init; }
    public float CorrectionQuality { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public sealed class Gdn2CorrectionGate
{
    private readonly Dictionary<string, CorrectionState> _domains = new();
    private readonly List<CorrectionRecord> _history = [];
    private readonly int _dimK;
    private readonly int _dimV;
    private readonly int _maxStates;
    private const float DefaultEraseStrength = 0.85f;
    private const float DefaultWriteStrength = 0.90f;

    public int HistoryCount => _history.Count;

    public Gdn2CorrectionGate(int dimK = 128, int dimV = 128, int maxStates = 32)
    {
        _dimK = dimK;
        _dimV = dimV;
        _maxStates = maxStates;
    }

    public CorrectionState GetOrCreate(string domain)
    {
        if (!_domains.TryGetValue(domain, out var state))
        {
            state = new CorrectionState
            {
                Domain = domain,
                Matrix = new float[_dimK * _dimV],
                DimK = _dimK,
                DimV = _dimV,
                DecayAccumulator = new float[_dimK]
            };
            if (_domains.Count >= _maxStates)
            {
                var oldest = _domains.Keys.First();
                _domains.Remove(oldest);
            }
            _domains[domain] = state;
        }
        return state;
    }

    public void Erase(string domain, float[] key, float? eraseStrength = null)
    {
        var state = GetOrCreate(domain);
        var strength = eraseStrength ?? DefaultEraseStrength;
        var dk = state.DimK;
        var dv = state.DimV;

        for (var i = 0; i < dk; i++)
        {
            var bi = Math.Clamp(key[i] * strength, 0f, 1f);
            for (var j = 0; j < dv; j++)
            {
                var idx = i * dv + j;
                state.Matrix[idx] *= (1f - bi * key[i]);
            }
        }

        for (var i = 0; i < dk; i++)
            state.DecayAccumulator[i] = Math.Max(state.DecayAccumulator[i], Math.Abs(key[i]));
    }

    public void Write(string domain, float[] key, float[] value, float? writeStrength = null)
    {
        var state = GetOrCreate(domain);
        var strength = writeStrength ?? DefaultWriteStrength;
        var dk = state.DimK;
        var dv = state.DimV;

        for (var i = 0; i < dk; i++)
        {
            var wi = Math.Clamp(key[i] * strength, 0f, 1f);
            var gated = wi * value[Math.Min(i, value.Length - 1)];
            for (var j = 0; j < dv; j++)
            {
                var jj = Math.Min(j, value.Length - 1);
                state.Matrix[i * dv + j] += key[i] * gated * wi;
            }
        }
    }

    public void ApplyCorrection(string domain, float[] queryKey, float[] wrongValue, float[] correctValue)
    {
        Erase(domain, queryKey, DefaultEraseStrength);
        Write(domain, queryKey, correctValue, DefaultWriteStrength);

        _history.Add(new CorrectionRecord
        {
            Domain = domain,
            EraseStrength = DefaultEraseStrength,
            WriteStrength = DefaultWriteStrength,
            CorrectionQuality = ComputeQuality(wrongValue, correctValue),
            Timestamp = DateTime.UtcNow
        });
    }

    public void ApplyCorrection(string domain, float[] queryKey, float[] wrongValue, float[] correctValue,
        string query, string wrongOutput, string correctOutput)
    {
        Erase(domain, queryKey, DefaultEraseStrength);
        Write(domain, queryKey, correctValue, DefaultWriteStrength);

        _history.Add(new CorrectionRecord
        {
            Domain = domain,
            Query = query,
            WrongOutput = wrongOutput,
            CorrectOutput = correctOutput,
            EraseStrength = DefaultEraseStrength,
            WriteStrength = DefaultWriteStrength,
            CorrectionQuality = ComputeQuality(wrongValue, correctValue),
            Timestamp = DateTime.UtcNow
        });
    }

    public float Retrieve(string domain, float[] queryKey, float[] outputBuffer)
    {
        if (!_domains.TryGetValue(domain, out var state)) return 0f;

        var dv = state.DimV;
        float maxScore = 0f;

        Array.Clear(outputBuffer, 0, outputBuffer.Length);

        for (var i = 0; i < state.DimK; i++)
        {
            var score = queryKey[i] * queryKey[i];
            if (score > 0.01f)
            {
                for (var j = 0; j < dv; j++)
                {
                    outputBuffer[j] += state.Matrix[i * dv + j] * queryKey[i];
                }
                if (score > maxScore) maxScore = score;
            }
        }

        return Math.Min(maxScore * dv, 1f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ComputeQuality(float[] a, float[] b)
    {
        var len = Math.Min(a.Length, b.Length);
        if (len == 0) return 0.5f;
        float dot = 0, na = 0, nb = 0;
        for (var i = 0; i < len; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        var cos = dot / (float)(Math.Sqrt(na) * Math.Sqrt(nb) + 1e-8f);
        return Math.Clamp((1f - Math.Abs(cos)) * 0.5f + 0.5f, 0f, 1f);
    }

    public static float[] SimpleEmbed(string text, int dim)
    {
        var emb = new float[dim];
        for (var i = 0; i < text.Length && i < dim; i++)
        {
            var val = (float)text[i] / 65535f;
            emb[i % dim] += val;
        }
        var norm = 0f;
        for (var i = 0; i < dim; i++) norm += emb[i] * emb[i];
        norm = (float)Math.Sqrt(norm) + 1e-8f;
        for (var i = 0; i < dim; i++) emb[i] /= norm;
        return emb;
    }

    public IReadOnlyList<CorrectionRecord> GetRecentCorrections(int count = 20)
    {
        return _history.TakeLast(count).ToList();
    }

    public Dictionary<string, object> GetStats()
    {
        return new()
        {
            ["domains"] = _domains.Count,
            ["history"] = _history.Count,
            ["avg_quality"] = _history.Count > 0
                ? _history.Average(r => r.CorrectionQuality)
                : 0f,
            ["by_domain"] = _domains.Keys.ToDictionary(k => k, k => (object)_history.Count(r => r.Domain == k))
        };
    }
}

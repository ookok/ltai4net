namespace LTAI.AI.Governors;

/// Low-Rank Adaptation (LoRA) layer.
/// Freezes base weight W, trains only A*B with rank r << min(d_in, d_out).
/// Supports merge/unmerge for hot-swap without overhead.
public sealed class LoraLayer
{
    private readonly float[,] _A;  // (dOut, r) — only trained params
    private readonly float[,] _B;  // (r, dIn) — only trained params
    private readonly float[,] _W;  // (dOut, dIn) — frozen base weights
    private float[,]? _merged;     // lazy: W + (A @ B) * scale

    public int InputDim { get; }
    public int OutputDim { get; }
    public int Rank { get; }
    public float Scale { get; set; }
    public bool IsMerged => _merged != null;

    public LoraLayer(float[,] baseWeights, int rank, float scale = 1.0f, Random? rng = null)
    {
        OutputDim = baseWeights.GetLength(0);
        InputDim = baseWeights.GetLength(1);
        Rank = rank;
        Scale = scale;
        _W = baseWeights;

        rng ??= Random.Shared;
        _A = new float[OutputDim, Rank];
        _B = new float[Rank, InputDim];

        // Kaiming-uniform init for A, zeros for B
        var std = MathF.Sqrt(2f / (InputDim * Rank));
        for (int i = 0; i < OutputDim; i++)
        for (int j = 0; j < Rank; j++)
            _A[i, j] = (float)(rng.NextDouble() * 2 - 1) * std;

        // B initialized to zero so initially ΔW = 0
    }

    /// Forward pass: y = (W + scale * A @ B) * x
    public float[] Forward(float[] input)
    {
        var y = new float[OutputDim];

        // Base path: W * x
        for (int i = 0; i < OutputDim; i++)
        {
            float sum = 0;
            for (int j = 0; j < InputDim; j++)
                sum += _W[i, j] * input[j];
            y[i] = sum;
        }

        if (Scale == 0) return y;

        // LoRA path: scale * A @ B @ x
        var bOut = new float[Rank]; // B @ x
        for (int r = 0; r < Rank; r++)
        {
            float sum = 0;
            for (int j = 0; j < InputDim; j++)
                sum += _B[r, j] * input[j];
            bOut[r] = sum;
        }

        for (int i = 0; i < OutputDim; i++)
        {
            float loraSum = 0;
            for (int r = 0; r < Rank; r++)
                loraSum += _A[i, r] * bOut[r];
            y[i] += Scale * loraSum;
        }

        return y;
    }

    /// Compute gradients for A and B given upstream gradients dL/dy
    public (float[,] dA, float[,] dB) Backward(float[] input, float[] upstreamGrad)
    {
        // B @ x (reuse from forward)
        var bx = new float[Rank];
        for (int r = 0; r < Rank; r++)
        {
            float sum = 0;
            for (int j = 0; j < InputDim; j++)
                sum += _B[r, j] * input[j];
            bx[r] = sum;
        }

        // dA = upstreamGrad @ bx^T  scaled
        var dA = new float[OutputDim, Rank];
        for (int i = 0; i < OutputDim; i++)
        for (int r = 0; r < Rank; r++)
            dA[i, r] = Scale * upstreamGrad[i] * bx[r];

        // dB = A^T @ upstreamGrad @ x^T  scaled
        var atUp = new float[Rank]; // A^T @ upstreamGrad
        for (int r = 0; r < Rank; r++)
        {
            float sum = 0;
            for (int i = 0; i < OutputDim; i++)
                sum += _A[i, r] * upstreamGrad[i];
            atUp[r] = sum;
        }

        var dB = new float[Rank, InputDim];
        for (int r = 0; r < Rank; r++)
        for (int j = 0; j < InputDim; j++)
            dB[r, j] = Scale * atUp[r] * input[j];

        return (dA, dB);
    }

    /// Apply gradient step to A and B
    public void ApplyGradient(float[,] dA, float[,] dB, float lr)
    {
        for (int i = 0; i < OutputDim; i++)
        for (int r = 0; r < Rank; r++)
            _A[i, r] -= lr * dA[i, r];

        for (int r = 0; r < Rank; r++)
        for (int j = 0; j < InputDim; j++)
            _B[r, j] -= lr * dB[r, j];
    }

    /// Merge LoRA into base: W_merged = W + scale * A @ B
    public float[,] Merge()
    {
        if (_merged != null) return _merged;

        _merged = new float[OutputDim, InputDim];
        for (int i = 0; i < OutputDim; i++)
        for (int j = 0; j < InputDim; j++)
        {
            float lora = 0;
            for (int r = 0; r < Rank; r++)
                lora += _A[i, r] * _B[r, j];
            _merged[i, j] = _W[i, j] + Scale * lora;
        }

        return _merged;
    }

    /// Revert to unmerged state (for next training cycle)
    public void Unmerge()
    {
        _merged = null;
    }

    /// Export LoRA-only parameters (A, B, rank, scale) for checkpointing
    public LoraCheckpoint ExportCheckpoint()
    {
        return new LoraCheckpoint
        {
            InputDim = InputDim, OutputDim = OutputDim, Rank = Rank, Scale = Scale,
            A = _A, B = _B
        };
    }

    /// Import LoRA from checkpoint (restoring a previously saved state)
    public void ImportCheckpoint(LoraCheckpoint ckpt)
    {
        if (ckpt.InputDim != InputDim || ckpt.OutputDim != OutputDim || ckpt.Rank != Rank)
            throw new ArgumentException("Checkpoint dimensions mismatch");

        Array.Copy(ckpt.A, _A, Math.Min(ckpt.A.Length, _A.Length));
        Array.Copy(ckpt.B, _B, Math.Min(ckpt.B.Length, _B.Length));
        Scale = ckpt.Scale;
        _merged = null;
    }
}

public sealed record LoraCheckpoint
{
    public int InputDim { get; init; }
    public int OutputDim { get; init; }
    public int Rank { get; init; }
    public float Scale { get; init; }
    public float[,] A { get; init; } = new float[0, 0];
    public float[,] B { get; init; } = new float[0, 0];

    public static LoraCheckpoint FromJson(string json)
    {
        var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new()
        {
            InputDim = root.GetProperty("input_dim").GetInt32(),
            OutputDim = root.GetProperty("output_dim").GetInt32(),
            Rank = root.GetProperty("rank").GetInt32(),
            Scale = root.GetProperty("scale").GetSingle(),
            A = DeserializeMatrix(root.GetProperty("a"), root.GetProperty("output_dim").GetInt32(), root.GetProperty("rank").GetInt32()),
            B = DeserializeMatrix(root.GetProperty("b"), root.GetProperty("rank").GetInt32(), root.GetProperty("input_dim").GetInt32())
        };
    }

    public string ToJson()
    {
        using var stream = new global::System.IO.MemoryStream();
        using var writer = new System.Text.Json.Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteNumber("input_dim", InputDim);
        writer.WriteNumber("output_dim", OutputDim);
        writer.WriteNumber("rank", Rank);
        writer.WriteNumber("scale", Scale);
        SerializeMatrix(writer, "a", A);
        SerializeMatrix(writer, "b", B);
        writer.WriteEndObject();
        writer.Flush();
        return global::System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void SerializeMatrix(System.Text.Json.Utf8JsonWriter w, string name, float[,] mat)
    {
        w.WriteStartArray(name);
        for (int i = 0; i < mat.GetLength(0); i++)
        {
            w.WriteStartArray();
            for (int j = 0; j < mat.GetLength(1); j++)
                w.WriteNumberValue(mat[i, j]);
            w.WriteEndArray();
        }
        w.WriteEndArray();
    }

    private static float[,] DeserializeMatrix(System.Text.Json.JsonElement elem, int rows, int cols)
    {
        var mat = new float[rows, cols];
        int ri = 0;
        foreach (var row in elem.EnumerateArray())
        {
            if (ri >= rows) break;
            int ci = 0;
            foreach (var val in row.EnumerateArray())
            {
                if (ci >= cols) break;
                mat[ri, ci] = val.GetSingle();
                ci++;
            }
            ri++;
        }
        return mat;
    }
}

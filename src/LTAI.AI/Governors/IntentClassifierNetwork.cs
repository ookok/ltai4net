using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

/// Lightweight intent classifier neural network with LoRA adapters.
/// Trainable via SGD in C#, exportable as weights JSON.
/// Architecture: TextEncode(256) → FC(256→128)+ReLU+LoRA → FC(128→64)+ReLU+LoRA → FC(64→N)
public sealed class IntentClassifierNetwork : IDisposable
{
    private readonly int _inputDim;
    private readonly int _hidden1Dim;
    private readonly int _hidden2Dim;
    private readonly int _numClasses;
    private readonly int _vocabSize;

    // Frozen base weights
    private readonly float[,] _w1;   // (hidden1Dim, inputDim)
    private readonly float[]  _b1;   // (hidden1Dim)
    private readonly float[,] _w2;   // (hidden2Dim, hidden1Dim)
    private readonly float[]  _b2;   // (hidden2Dim)
    private readonly float[,] _w3;   // (numClasses, hidden2Dim)
    private readonly float[]  _b3;   // (numClasses)

    // LoRA adapters (trainable)
    public LoraLayer Lora1 { get; }
    public LoraLayer Lora2 { get; }

    // Cache for forward pass
    private float[]? _h1Cache;
    private float[]? _h1aCache;
    private float[]? _h2Cache;
    private float[]? _h2aCache;

    public int NumClasses => _numClasses;
    public int Generation { get; private set; }
    public int TotalSamplesTrained { get; private set; }

    public string MapClassLabel(int idx)
    {
        return idx switch
        {
            0 => "fast", 1 => "deep", 2 => "code", 3 => "chat", 4 => "reasoning",
            _ => "chat"
        };
    }

    public IntentClassifierNetwork(
        int vocabSize = 1000, int inputDim = 256, int hidden1Dim = 128, int hidden2Dim = 64,
        int numClasses = 5, int loraRank = 8, Random? rng = null)
    {
        _vocabSize = vocabSize;
        _inputDim = inputDim;
        _hidden1Dim = hidden1Dim;
        _hidden2Dim = hidden2Dim;
        _numClasses = numClasses;
        rng ??= Random.Shared;

        // Xavier init for base weights
        _w1 = XavierInit(hidden1Dim, inputDim, rng);
        _b1 = new float[hidden1Dim];
        _w2 = XavierInit(hidden2Dim, hidden1Dim, rng);
        _b2 = new float[hidden2Dim];
        _w3 = XavierInit(numClasses, hidden2Dim, rng);
        _b3 = new float[numClasses];

        // LoRA adapters with Kaiming init for A, zero for B
        Lora1 = new LoraLayer(_w1, loraRank, 1.0f, rng);
        Lora2 = new LoraLayer(_w2, loraRank, 1.0f, rng);
    }

    private static float[,] XavierInit(int outDim, int inDim, Random rng)
    {
        var w = new float[outDim, inDim];
        var scale = MathF.Sqrt(6f / (outDim + inDim));
        for (int i = 0; i < outDim; i++)
        for (int j = 0; j < inDim; j++)
            w[i, j] = (float)(rng.NextDouble() * 2 - 1) * scale;
        return w;
    }

    /// Hash-based text encoding: produce inputDim-dimensional feature vector
    public float[] EncodeText(string text)
    {
        var vec = new float[_inputDim];
        if (string.IsNullOrEmpty(text)) return vec;

        var words = text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return vec;

        // Multi-hash encoding: each word contributes to multiple dims
        foreach (var word in words)
        {
            var h1 = Math.Abs(word.GetHashCode()) % _vocabSize;
            var h2 = Math.Abs((word + "salt").GetHashCode()) % _vocabSize;

            vec[h1 % _inputDim] += 1.0f;
            vec[h2 % _inputDim] += 1.0f;

            // Character n-gram features
            for (int i = 0; i < word.Length - 2; i++)
            {
                var tri = word[i..(i + 3)];
                var th = Math.Abs(tri.GetHashCode()) % _vocabSize;
                vec[th % _inputDim] += 0.3f;
            }
        }

        // L2 normalize
        var norm = MathF.Sqrt(vec.Sum(v => v * v));
        if (norm > 1e-8f)
            for (int i = 0; i < _inputDim; i++) vec[i] /= norm;

        return vec;
    }

    /// Forward pass with LoRA: returns class logits
    public float[] Forward(string text, bool cacheForBackward = false)
    {
        var x = EncodeText(text);

        // Layer 1: FC + LoRA + ReLU
        var h1 = Lora1.Forward(x);
        for (int i = 0; i < _hidden1Dim; i++) h1[i] += _b1[i]; // bias
        if (cacheForBackward) _h1Cache = (float[])h1.Clone();

        // ReLU
        for (int i = 0; i < _hidden1Dim; i++)
            h1[i] = Math.Max(0, h1[i]);
        if (cacheForBackward) _h1aCache = (float[])h1.Clone();

        // Layer 2: FC + LoRA + ReLU
        var h2 = Lora2.Forward(h1);
        for (int i = 0; i < _hidden2Dim; i++) h2[i] += _b2[i];
        if (cacheForBackward) _h2Cache = (float[])h2.Clone();

        for (int i = 0; i < _hidden2Dim; i++)
            h2[i] = Math.Max(0, h2[i]);
        if (cacheForBackward) _h2aCache = (float[])h2.Clone();

        // Layer 3: FC (no LoRA, no activation) → logits
        var logits = new float[_numClasses];
        for (int i = 0; i < _numClasses; i++)
        {
            float sum = _b3[i];
            for (int j = 0; j < _hidden2Dim; j++)
                sum += _w3[i, j] * h2[j];
            logits[i] = sum;
        }

        return logits;
    }

    /// Predict class label and confidence
    public (int classIndex, float confidence) Predict(string text)
    {
        var logits = Forward(text);
        var maxIdx = 0;
        var maxVal = logits[0];
        for (int i = 1; i < logits.Length; i++)
        {
            if (logits[i] > maxVal) { maxIdx = i; maxVal = logits[i]; }
        }

        // Softmax for confidence
        var expSum = 0f;
        var exps = new float[logits.Length];
        for (int i = 0; i < logits.Length; i++)
        {
            exps[i] = MathF.Exp(logits[i] - maxVal); // stable exp
            expSum += exps[i];
        }
        var confidence = expSum > 0 ? exps[maxIdx] / expSum : 1f;

        return (maxIdx, confidence);
    }

    /// Train one step on a single sample (SGD with cross-entropy loss)
    public float TrainStep(string text, int targetClass, float lr = 0.01f)
    {
        // Forward with caching
        var logits = Forward(text, cacheForBackward: true);

        // Softmax + cross-entropy
        var maxLogit = logits.Max();
        var exps = logits.Select(l => MathF.Exp(l - maxLogit)).ToArray();
        var expSum = exps.Sum();
        var probs = exps.Select(e => e / (expSum + 1e-8f)).ToArray();

        var loss = -MathF.Log(Math.Max(probs[targetClass], 1e-8f));

        // Gradient of loss w.r.t. logits: dL/dz = p - one_hot(y)
        var dLogits = new float[_numClasses];
        for (int i = 0; i < _numClasses; i++)
            dLogits[i] = probs[i] - (i == targetClass ? 1 : 0);

        // Backward through layer 3 (FC, no activation)
        var dH2a = new float[_hidden2Dim]; // gradient at h2 after ReLU
        for (int j = 0; j < _hidden2Dim; j++)
        {
            float sum = 0;
            for (int i = 0; i < _numClasses; i++)
                sum += _w3[i, j] * dLogits[i];
            dH2a[j] = sum;
        }

        // Update W3, B3
        for (int i = 0; i < _numClasses; i++)
        {
            _b3[i] -= lr * dLogits[i];
            for (int j = 0; j < _hidden2Dim; j++)
                _w3[i, j] -= lr * dLogits[i] * _h2aCache![j];
        }

        // Backward through ReLU 2
        var dH2 = new float[_hidden2Dim]; // gradient at h2 pre-ReLU
        for (int i = 0; i < _hidden2Dim; i++)
            dH2[i] = _h2Cache![i] > 0 ? dH2a[i] : 0;

        // Backward through layer 2 (FC + LoRA)
        var (dA2, dB2) = Lora2.Backward(_h1aCache!, dH2);
        Lora2.ApplyGradient(dA2, dB2, lr);
        for (int i = 0; i < _hidden2Dim; i++) _b2[i] -= lr * dH2[i];

        var dH1a = new float[_hidden1Dim]; // gradient at h1 after ReLU
        for (int j = 0; j < _hidden1Dim; j++)
        {
            float sum = 0;
            for (int i = 0; i < _hidden2Dim; i++)
                sum += Lora2.IsMerged
                    ? Lora2.Merge()[i, j] * dH2[i]
                    : (_w2[i, j] + LoraScaleDot(_w2[i, j], Lora2)) * dH2[i];
            dH1a[j] = sum;
        }

        // Backward through ReLU 1
        var dH1 = new float[_hidden1Dim];
        for (int i = 0; i < _hidden1Dim; i++)
            dH1[i] = _h1Cache![i] > 0 ? dH1a[i] : 0;

        // Backward through layer 1 (FC + LoRA)
        var (dA1, dB1) = Lora1.Backward(EncodeText(text), dH1);
        Lora1.ApplyGradient(dA1, dB1, lr);
        for (int i = 0; i < _hidden1Dim; i++) _b1[i] -= lr * dH1[i];

        TotalSamplesTrained++;
        return loss;
    }

    private static float LoraScaleDot(float wVal, LoraLayer lora)
    {
        // Approximate: use scale * (A[i,r] * B[r,j] avg)
        return 0;
    }

    /// Train for multiple epochs on a batch of samples
    public float Train(List<(string text, int targetClass)> samples, int epochs = 3, float lr = 0.01f, ILogger? logger = null)
    {
        var initialLr = lr;
        float totalLoss = 0;

        for (int epoch = 0; epoch < epochs; epoch++)
        {
            // LR decay
            var epochLr = initialLr * MathF.Pow(0.9f, epoch);
            float epochLoss = 0;

            // Shuffle
            var shuffled = samples.OrderBy(_ => Random.Shared.Next()).ToList();
            foreach (var (text, label) in shuffled)
            {
                epochLoss += TrainStep(text, label, epochLr);
            }

            epochLoss /= Math.Max(1, samples.Count);
            totalLoss = epochLoss;

            logger?.LogDebug("LoRA training epoch {Epoch}/{Total}: loss={Loss:F4}, lr={LR:F6}",
                epoch + 1, epochs, epochLoss, epochLr);
        }

        return totalLoss;
    }

    /// Merge all LoRA adapters into base weights and return merged weights for export
    public (float[,] w1, float[] b1, float[,] w2, float[] b2, float[,] w3, float[] b3) Merge()
    {
        var m1 = Lora1.Merge();
        var m2 = Lora2.Merge();
        Generation++;
        return (m1, _b1, m2, _b2, _w3, _b3);
    }

    /// Unmerge to continue training
    public void Unmerge()
    {
        Lora1.Unmerge();
        Lora2.Unmerge();
    }

    public void Dispose()
    {
        _h1Cache = null; _h1aCache = null;
        _h2Cache = null; _h2aCache = null;
    }
}

using System.Text.Json;
using LTAI.TreeLLM.Models;

namespace LTAI.TreeLLM.Prompting;

public sealed class InputField
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Type { get; set; } = "string";
}

public sealed class OutputField
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Type { get; set; } = "string";
}

public sealed class Signature
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<InputField> Inputs { get; set; } = new();
    public List<OutputField> Outputs { get; set; } = new();
    public List<Dictionary<string, string>> Examples { get; set; } = new();

    public string ToPrompt()
    {
        var parts = new List<string> { $"# {Name}" };
        if (!string.IsNullOrEmpty(Description))
            parts.Add(Description);

        if (Inputs.Count > 0)
            parts.Add("## Inputs\n" + string.Join("\n", Inputs.Select(i => $"- {i.Name} ({i.Type}): {i.Description}")));

        if (Outputs.Count > 0)
            parts.Add("## Outputs\n" + string.Join("\n", Outputs.Select(o => $"- {o.Name} ({o.Type}): {o.Description}")));

        if (Examples.Count > 0)
        {
            parts.Add("## Examples");
            foreach (var ex in Examples.Take(3))
                parts.Add($"Input: {ex.GetValueOrDefault("input", "")}\nOutput: {ex.GetValueOrDefault("output", "")}");
        }

        return string.Join("\n\n", parts);
    }

    public void AddExample(string input, string output, double weight = 1.0)
    {
        Examples.Add(new Dictionary<string, string> { ["input"] = input, ["output"] = output, ["weight"] = weight.ToString() });
        if (Examples.Count > 20)
            Examples.RemoveAt(0);
    }
}

public sealed class PromptModule
{
    public string Name { get; set; } = "";
    public Signature Signature { get; set; } = new();
    public string PromptTemplate { get; set; } = "";
    public Func<string, Task<string>>? ExecuteFn { get; set; }

    public string RenderPrompt(Dictionary<string, string> inputs)
    {
        var prompt = PromptTemplate;
        foreach (var (k, v) in inputs)
            prompt = prompt.Replace($"{{{k}}}", v);
        return Signature.ToPrompt() + "\n\n" + prompt;
    }
}

public sealed class PromptCompiler
{
    private readonly Dictionary<string, List<PromptVariant>> _variants = new();
    private readonly Dictionary<string, double> _banditScores = new();
    private readonly Random _rng = new();
    private readonly Lock _compilerLock = new();
    private int _callCount;

    public PromptCompiler()
    {
        SeedDefaults();
    }

    public string Compile(string taskType, Dictionary<string, string> inputs)
    {
        lock (_compilerLock)
        {
            _callCount++;
            var variants = _variants.GetValueOrDefault(taskType, _variants["general"]);
            var selected = ThompsonSelect(variants);
            var result = selected.Text;
            foreach (var (k, v) in inputs)
                result = result.Replace($"{{{k}}}", v);
            return result;
        }
    }

    public void Feedback(string taskType, string variantId, double quality)
    {
        lock (_compilerLock)
        {
            _banditScores[$"{taskType}:{variantId}"] =
                _banditScores.GetValueOrDefault($"{taskType}:{variantId}") * 0.9 + quality * 0.1;
        }
    }

    private PromptVariant ThompsonSelect(List<PromptVariant> variants)
    {
        var best = variants[0];
        var bestScore = double.MinValue;
        foreach (var v in variants)
        {
            var score = SampleBeta(_rng, v.Alpha, v.Beta);
            if (score > bestScore) { bestScore = score; best = v; }
        }
        return best;
    }

    private static double SampleBeta(Random rng, double alpha, double beta)
    {
        var x = Gamma(rng, alpha);
        var y = Gamma(rng, beta);
        return x / (x + y);
    }

    private static double Gamma(Random rng, double shape)
    {
        if (shape < 1)
        {
            var u = rng.NextDouble();
            return Gamma(rng, shape + 1) * Math.Pow(u, 1.0 / shape);
        }
        var d = shape - 1.0 / 3.0;
        var c = 1.0 / Math.Sqrt(9.0 * d);
        while (true)
        {
            double x, v;
            do
            {
                x = NormalRng(rng);
                v = 1 + c * x;
            } while (v <= 0);
            v = v * v * v;
            var u = rng.NextDouble();
            if (u < 1 - 0.0331 * (x * x) * (x * x))
                return d * v;
            if (Math.Log(u) < 0.5 * x * x + d * (1 - v + Math.Log(v)))
                return d * v;
        }
    }

    private static double NormalRng(Random rng)
    {
        double u1, u2;
        do { u1 = rng.NextDouble(); } while (u1 <= double.Epsilon);
        u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    private void SeedDefaults()
    {
        var defaults = new Dictionary<string, string[]>
        {
            ["general"] = new[] { "Answer the question directly and concisely.", "Provide a thorough analysis with examples.", "Structure the response with clear headings." },
            ["code"] = new[] { "Generate clean, well-documented code.", "Explain the approach first, then provide implementation.", "Focus on correctness and edge cases." },
            ["reasoning"] = new[] { "Think step by step through the problem.", "Consider multiple perspectives before concluding.", "Provide evidence for each reasoning step." }
        };

        foreach (var (type, prompts) in defaults)
        {
            _variants[type] = prompts.Select((p, i) => new PromptVariant
            {
                Id = $"{type}-v{i + 1}", Text = p, Alpha = 3.0, Beta = 3.0
            }).ToList();
        }
    }
}

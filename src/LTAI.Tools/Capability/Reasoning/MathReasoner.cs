using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace LTAI.Tools.Reasoning;

public sealed class MathReasoner
{
    private readonly ILogger<MathReasoner> _logger;

    public MathReasoner(ILogger<MathReasoner> logger)
    {
        _logger = logger;
    }

    public async Task<MathResult> SolveAsync(string problem, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Math: {Problem}", problem[..Math.Min(problem.Length, 100)]);

        var result = new MathResult { OriginalProblem = problem };

        try
        {
            if (TrySolveLinearEquation(problem, out var eqResult))
            { result.Solution = eqResult; result.Method = "linear_equation"; }
            else if (TrySolveQuadratic(problem, out var quadResult))
            { result.Solution = quadResult; result.Method = "quadratic"; }
            else if (TryStatistics(problem, out var statsResult))
            { result.Solution = statsResult; result.Method = "statistics"; }
            else if (TryEvaluate(problem, out var exprResult))
            { result.Solution = exprResult; result.Method = "expression"; }
            else
            { result.Solution = "Unable to parse. Provide a clear equation or expression."; }
        }
        catch (Exception ex)
        {
            result.Solution = $"Error: {ex.Message}";
            result.Method = "error";
        }

        return await Task.FromResult(result).ConfigureAwait(false);
    }

    private static bool TrySolveLinearEquation(string problem, out string result)
    {
        var m = Regex.Match(problem.Replace(" ", ""), @"([\d.]+)x\+?([\d.]+)=([\d.]+)");
        if (!m.Success)
        {
            m = Regex.Match(problem.Replace(" ", ""), @"([\d.]+)\*x\+([\d.]+)=([\d.]+)");
        }
        if (m.Success)
        {
            var a = double.Parse(m.Groups[1].Value);
            var b = double.Parse(m.Groups[2].Value);
            var c = double.Parse(m.Groups[3].Value);
            var x = (c - b) / a;
            result = $"x = {x:F6}\n  {a}x + {b} = {c}\n  {a}x = {c - b}\n  x = ({c} - {b}) / {a} = {x:F6}";
            return true;
        }
        result = "";
        return false;
    }

    private static bool TrySolveQuadratic(string problem, out string result)
    {
        var m = Regex.Match(problem.Replace(" ", ""), @"x\^2\+?([\d.]+)x\+?([\d.]+)=0");
        if (m.Success)
        {
            var b = double.Parse(m.Groups[1].Value);
            var c = double.Parse(m.Groups[2].Value);
            var d = b * b - 4 * c;
            if (d >= 0)
            {
                var sqrtD = Math.Sqrt(d);
                result = $"x1 = {(-b + sqrtD) / 2:F6}, x2 = {(-b - sqrtD) / 2:F6} (D = {d:F4})";
            }
            else
            {
                result = $"No real roots. Complex: x = {-b / 2:F6} ± {Math.Sqrt(-d) / 2:F6}i";
            }
            return true;
        }
        result = "";
        return false;
    }

    private static bool TryStatistics(string problem, out string result)
    {
        var numbers = Regex.Matches(problem, @"\b([\d.]+)\b")
            .Select(m => double.Parse(m.Groups[1].Value))
            .ToArray();

        if (numbers.Length < 3)
        { result = ""; return false; }

        var mean = numbers.Average();
        var variance = numbers.Average(x => (x - mean) * (x - mean));
        var median = numbers.OrderBy(x => x).ElementAt(numbers.Length / 2);
        if (numbers.Length % 2 == 0)
            median = (numbers.OrderBy(x => x).ElementAt(numbers.Length / 2 - 1) + numbers.OrderBy(x => x).ElementAt(numbers.Length / 2)) / 2;

        var sb = new System.Text.StringBuilder();
        if (problem.Contains("mean") || problem.Contains("average") || problem.Contains("均值"))
            sb.AppendLine($"Mean: {mean:F4}, StdDev: {Math.Sqrt(variance):F4}, Median: {median:F4}");
        else if (problem.Contains("median") || problem.Contains("中位数"))
            sb.AppendLine($"Median: {median:F4}");
        else if (problem.Contains("sum") || problem.Contains("总和"))
            sb.AppendLine($"Sum: {numbers.Sum():F4}, Count: {numbers.Length}");
        else
            sb.AppendLine($"Mean: {mean:F4}, StdDev: {Math.Sqrt(variance):F4}, Min: {numbers.Min():F4}, Max: {numbers.Max():F4}");

        result = sb.ToString().Trim();
        return true;
    }

    private static bool TryEvaluate(string problem, out string result)
    {
        var clean = problem.Replace("计算", "").Replace("等于多少", "").Replace("what is", "").Replace(" ", "").Trim();

        var m = Regex.Match(clean, @"([\d.]+)\s*([+\-*/])\s*([\d.]+)");
        if (m.Success)
        {
            var a = double.Parse(m.Groups[1].Value);
            var b = double.Parse(m.Groups[3].Value);
            var op = m.Groups[2].Value;
            var r = op switch { "+" => a + b, "-" => a - b, "*" => a * b, "/" => b != 0 ? a / b : double.NaN, _ => double.NaN };
            result = $"{a} {op} {b} = {r:F6}";
            return !double.IsNaN(r);
        }

        try
        {
            clean = Regex.Replace(clean, @"[^\d+\-*/().^]", "");
            if (!string.IsNullOrEmpty(clean))
            {
                var dt = new System.Data.DataTable();
                clean = clean.Replace("^", "");
                var val = Convert.ToDouble(dt.Compute(clean, null));
                result = $"{clean} = {val:F6}";
                return true;
            }
        }
        catch { /* non-fatal */ }

        result = "";
        return false;
    }
}

public sealed class MathResult
{
    public string OriginalProblem { get; init; } = "";
    public string Solution { get; set; } = "";
    public string Method { get; set; } = "unknown";
}

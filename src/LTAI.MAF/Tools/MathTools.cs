using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LTAI.MAF.Tools;

[Description("Mathematical computation and utility tools")]
public sealed class MathTools
{
    [Description("Evaluate a mathematical expression and return the result. Supports arithmetic, functions like sqrt/pow/sin/cos/log/abs, and constants pi/e.")]
    public static string EvaluateExpression(
        [Description("Math expression to evaluate, e.g. '2 + 3 * 4', 'sqrt(16)', 'pow(2,10)'")] string expression)
    {
        try
        {
            expression = expression
                .Replace("^", "**")
                .Replace("pi", Math.PI.ToString(CultureInfo.InvariantCulture))
                .Replace("e", Math.E.ToString(CultureInfo.InvariantCulture));

            expression = Regex.Replace(expression, @"sqrt\(([^)]+)\)", m => $"Math.Sqrt({m.Groups[1].Value})");
            expression = Regex.Replace(expression, @"pow\(([^,]+),([^)]+)\)", m => $"Math.Pow({m.Groups[1].Value},{m.Groups[2].Value})");
            expression = Regex.Replace(expression, @"sin\(([^)]+)\)", m => $"Math.Sin({m.Groups[1].Value}*Math.PI/180)");
            expression = Regex.Replace(expression, @"cos\(([^)]+)\)", m => $"Math.Cos({m.Groups[1].Value}*Math.PI/180)");
            expression = Regex.Replace(expression, @"log\(([^)]+)\)", m => $"Math.Log({m.Groups[1].Value})");
            expression = Regex.Replace(expression, @"abs\(([^)]+)\)", m => $"Math.Abs({m.Groups[1].Value})");
            expression = Regex.Replace(expression, @"round\(([^,]+),([^)]+)\)", m => $"Math.Round({m.Groups[1].Value},{m.Groups[2].Value})");

            if (!Regex.IsMatch(expression, @"^[\d\s+\-*/().,Math\.\w]+$"))
                return JsonSerializer.Serialize(new { error = "Expression contains invalid characters" });

            var result = new System.Data.DataTable().Compute(expression, null);
            return JsonSerializer.Serialize(new { expression, result = result?.ToString() });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = $"Evaluation failed: {ex.Message}" });
        }
    }

    [Description("Convert a number between different bases (binary, octal, decimal, hexadecimal).")]
    public static string ConvertBase(
        [Description("Number as a string")] string value,
        [Description("Source base: 2, 8, 10, or 16")] int fromBase,
        [Description("Target base: 2, 8, 10, or 16")] int toBase)
    {
        try
        {
            var num = Convert.ToInt64(value, fromBase);
            var result = Convert.ToString(num, toBase)?.ToUpperInvariant();
            return JsonSerializer.Serialize(new { value, fromBase, toBase, result });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    [Description("Perform basic unit conversions (length, weight, temperature, area, volume, speed, time, data).")]
    public static string ConvertUnits(
        [Description("Numeric value to convert")] double value,
        [Description("Source unit, e.g. km, m, cm, mm, mi, ft, inch, kg, g, lb, oz, C, F, K, m2, ft2, L, gal, m3, m/s, km/h, mph, h, min, s, byte, kb, mb, gb")] string fromUnit,
        [Description("Target unit")] string toUnit)
    {
        try
        {
            var result = DoUnitConversion(value, fromUnit.ToLowerInvariant(), toUnit.ToLowerInvariant());
            return JsonSerializer.Serialize(new { value, from = fromUnit, to = toUnit, result });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    private static double DoUnitConversion(double v, string from, string to)
    {
        var factors = new Dictionary<(string, string), double>
        {
            // Length
            { ("km", "m"), 1000 }, { ("m", "cm"), 100 }, { ("cm", "mm"), 10 }, { ("m", "mm"), 1000 },
            { ("mi", "km"), 1.60934 }, { ("mi", "ft"), 5280 }, { ("ft", "inch"), 12 }, { ("inch", "cm"), 2.54 },
            // Weight
            { ("kg", "g"), 1000 }, { ("lb", "kg"), 0.453592 }, { ("lb", "oz"), 16 }, { ("oz", "g"), 28.3495 },
            // Area
            { ("m2", "ft2"), 10.7639 }, { ("km2", "m2"), 1_000_000 },
            // Volume
            { ("l", "ml"), 1000 }, { ("gal", "l"), 3.78541 }, { ("m3", "l"), 1000 },
            // Speed
            { ("km/h", "m/s"), 0.277778 }, { ("mph", "km/h"), 1.60934 },
            // Time
            { ("h", "min"), 60 }, { ("h", "s"), 3600 }, { ("min", "s"), 60 },
            // Data
            { ("kb", "byte"), 1024 }, { ("mb", "kb"), 1024 }, { ("gb", "mb"), 1024 }, { ("tb", "gb"), 1024 }
        };

        if (from == to) return v;

        // Temperature special cases
        if (from == "c" && to == "f") return v * 9 / 5 + 32;
        if (from == "f" && to == "c") return (v - 32) * 5 / 9;
        if (from == "c" && to == "k") return v + 273.15;
        if (from == "k" && to == "c") return v - 273.15;
        if (from == "f" && to == "k") return (v - 32) * 5 / 9 + 273.15;
        if (from == "k" && to == "f") return (v - 273.15) * 9 / 5 + 32;

        if (factors.TryGetValue((from, to), out var factor))
            return v * factor;
        if (factors.TryGetValue((to, from), out var inverse))
            return v / inverse;

        throw new ArgumentException($"Unsupported conversion: {from} → {to}");
    }

    [Description("Generate a random number between min and max (inclusive).")]
    public static string Random(
        [Description("Minimum value (inclusive)")] double min,
        [Description("Maximum value (inclusive)")] double max)
    {
        var rng = System.Random.Shared;
        if (min == (int)min && max == (int)max)
        {
            var result = rng.Next((int)min, (int)max + 1);
            return JsonSerializer.Serialize(new { min, max, result });
        }
        var dResult = min + rng.NextDouble() * (max - min);
        return JsonSerializer.Serialize(new { min, max, result = Math.Round(dResult, 6) });
    }

    [Description("Calculate basic statistics from a list of numbers: count, sum, mean, median, min, max, stddev.")]
    public static string CalculateStatistics(
        [Description("JSON array of numbers, e.g. [1,2,3,4,5]")] string numbersJson)
    {
        try
        {
            var numbers = JsonSerializer.Deserialize<double[]>(numbersJson);
            if (numbers == null || numbers.Length == 0)
                return JsonSerializer.Serialize(new { error = "No numbers provided" });

            Array.Sort(numbers);
            var count = numbers.Length;
            var sum = numbers.Sum();
            var mean = sum / count;
            var median = count % 2 == 0 ? (numbers[count / 2 - 1] + numbers[count / 2]) / 2.0 : numbers[count / 2];
            var min = numbers[0];
            var max = numbers[^1];
            var variance = numbers.Sum(n => Math.Pow(n - mean, 2)) / count;
            var stddev = Math.Sqrt(variance);

            return JsonSerializer.Serialize(new { count, sum, mean = Math.Round(mean, 4), median = Math.Round(median, 4), min, max, stddev = Math.Round(stddev, 4), variance = Math.Round(variance, 4) });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }
}

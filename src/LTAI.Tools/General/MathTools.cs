using System.ComponentModel;

namespace LTAI.Tools.General;

public static class MathTools
{
    [Description("Evaluates a mathematical expression")]
    public static string Evaluate(
        [Description("Mathematical expression to evaluate")] string expression)
    {
        try
        {
            var dt = new System.Data.DataTable();
            var result = dt.Compute(expression, null);
            return $"{expression} = {result}";
        }
        catch (Exception ex)
        {
            return $"Evaluation failed: {ex.Message}";
        }
    }

    [Description("Performs basic arithmetic")]
    public static string BasicMath(
        [Description("First number")] double a,
        [Description("Operation: add, subtract, multiply, divide, power")] string operation,
        [Description("Second number")] double b)
    {
        return operation.ToLowerInvariant() switch
        {
            "add" => $"{a} + {b} = {a + b}",
            "subtract" => $"{a} - {b} = {a - b}",
            "multiply" => $"{a} * {b} = {a * b}",
            "divide" => b != 0 ? $"{a} / {b} = {a / b}" : "Cannot divide by zero",
            "power" => $"{a} ^ {b} = {Math.Pow(a, b)}",
            _ => $"Unknown operation: {operation}"
        };
    }

    [Description("Generates a random number in range")]
    public static double Random(
        [Description("Minimum value (inclusive)")] double min = 0,
        [Description("Maximum value (exclusive)")] double max = 1)
    {
        return System.Random.Shared.NextDouble() * (max - min) + min;
    }

    [Description("Converts between units")]
    public static string Convert(
        [Description("Value to convert")] double value,
        [Description("Source unit (e.g. km, m, cm, mi, ft)")] string fromUnit,
        [Description("Target unit")] string toUnit)
    {
        var meters = fromUnit.ToLowerInvariant() switch
        {
            "km" => value * 1000,
            "m" => value,
            "cm" => value / 100,
            "mm" => value / 1000,
            "mi" => value * 1609.34,
            "ft" => value * 0.3048,
            "in" => value * 0.0254,
            _ => double.NaN
        };

        if (double.IsNaN(meters)) return $"Unknown unit: {fromUnit}";

        var result = toUnit.ToLowerInvariant() switch
        {
            "km" => meters / 1000,
            "m" => meters,
            "cm" => meters * 100,
            "mm" => meters * 1000,
            "mi" => meters / 1609.34,
            "ft" => meters / 0.3048,
            "in" => meters / 0.0254,
            _ => double.NaN
        };

        if (double.IsNaN(result)) return $"Unknown unit: {toUnit}";
        return $"{value} {fromUnit} = {result:F4} {toUnit}";
    }
}

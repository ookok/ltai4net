using Microsoft.Agents.AI;

namespace LTAI.Tools.General;

public static class MathTools
{
    [AIFunction("Evaluates a mathematical expression")]
    public static string Evaluate(
        [AIFunctionParameter("Mathematical expression to evaluate", Required = true)]
        string expression)
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

    [AIFunction("Performs basic arithmetic")]
    public static string BasicMath(
        [AIFunctionParameter("First number")] double a,
        [AIFunctionParameter("Operation: add, subtract, multiply, divide, power")] string operation,
        [AIFunctionParameter("Second number")] double b)
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

    [AIFunction("Generates a random number in range")]
    public static double Random(
        [AIFunctionParameter("Minimum value (inclusive)")] double min = 0,
        [AIFunctionParameter("Maximum value (exclusive)")] double max = 1)
    {
        return System.Random.Shared.NextDouble() * (max - min) + min;
    }

    [AIFunction("Converts between units")]
    public static string Convert(
        [AIFunctionParameter("Value to convert")] double value,
        [AIFunctionParameter("Source unit (e.g. km, m, cm, mi, ft)")] string fromUnit,
        [AIFunctionParameter("Target unit")] string toUnit)
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

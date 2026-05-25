using System.Text.RegularExpressions;

namespace LTAI.Agent.Skills.Runtime;

/// <summary>
/// Expression parser for the Skill DSL.
/// Handles: {{ expr }} interpolation, arithmetic, comparison, string ops, ternary.
/// </summary>
public sealed class SkillExpressionEngine
{
    private readonly SkillVarScope _scope;

    private static readonly Regex Interpolation = new(@"\{\{(.+?)\}\}", RegexOptions.Compiled);
    private static readonly Regex Ternary = new(@"^(.+?)\s*\?\s*(.+?)\s*:\s*(.+)$", RegexOptions.Compiled);
    private static readonly Regex Comparison = new(@"(.+?)\s*(==|!=|>=|<=|>|<|~=)\s*(.+)", RegexOptions.Compiled);
    private static readonly Regex Arithmetic = new(@"(.+?)\s*([+\-*/])\s*(.+)", RegexOptions.Compiled);

    public SkillExpressionEngine(SkillVarScope scope)
    {
        _scope = scope;
    }

    /// <summary>
    /// Evaluate all {{ }} interpolations in a string and replace them.
    /// </summary>
    public string Interpolate(string text)
    {
        if (!text.Contains("{{")) return text;

        return Interpolation.Replace(text, match =>
        {
            var expr = match.Groups[1].Value.Trim();
            var val = Evaluate(expr);
            return val.ToString();
        });
    }

    /// <summary>
    /// Evaluate a single expression to a SkillValue.
    /// </summary>
    public SkillValue Evaluate(string expr)
    {
        expr = expr.Trim();
        if (string.IsNullOrEmpty(expr)) return SkillValue.Nil;

        var ternary = Ternary.Match(expr);
        if (ternary.Success)
        {
            var cond = Evaluate(ternary.Groups[1].Value.Trim());
            return cond.Bool
                ? Evaluate(ternary.Groups[2].Value.Trim())
                : Evaluate(ternary.Groups[3].Value.Trim());
        }

        var comp = Comparison.Match(expr);
        if (comp.Success)
        {
            var left = Evaluate(comp.Groups[1].Value.Trim());
            var op = comp.Groups[2].Value.Trim();
            var right = Evaluate(comp.Groups[3].Value.Trim());

            return op switch
            {
                "==" => SkillValue.FromBool(AreEqual(left, right)),
                "!=" => SkillValue.FromBool(!AreEqual(left, right)),
                ">" => left > right,
                "<" => left < right,
                ">=" => SkillValue.FromBool(!(left < right).Bool),
                "<=" => SkillValue.FromBool(!(left > right).Bool),
                "~=" => SkillValue.FromBool(Regex.IsMatch(left.Text, right.Text)),
                _ => SkillValue.Nil
            };
        }

        var arith = Arithmetic.Match(expr);
        if (arith.Success)
        {
            var left = Evaluate(arith.Groups[1].Value.Trim());
            var op = arith.Groups[2].Value.Trim();
            var right = Evaluate(arith.Groups[3].Value.Trim());

            return op switch
            {
                "+" => left + right,
                "-" => left - right,
                "*" => left * right,
                "/" => left / right,
                _ => SkillValue.Nil
            };
        }

        return _scope.Resolve(expr);
    }

    public string InterpolateWithScope(string text, Dictionary<string, SkillValue> extraVars)
    {
        foreach (var kv in extraVars)
            _scope.Set(kv.Key, kv.Value);
        return Interpolate(text);
    }

    private static bool AreEqual(SkillValue a, SkillValue b)
    {
        if (a.IsNumber && b.IsNumber) return Math.Abs(a.Number - b.Number) < 0.0001;
        return string.Equals(a.Text, b.Text, StringComparison.OrdinalIgnoreCase);
    }
}

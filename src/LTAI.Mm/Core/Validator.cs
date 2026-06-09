using LTAI.Mm.Ir;

namespace LTAI.Mm.Core;

public sealed class ValidationResult
{
    public bool IsValid { get; }
    public string? Error { get; }

    public ValidationResult(bool isValid, string? error = null)
    {
        IsValid = isValid;
        Error = error;
    }
}

public static class Validator
{
    public static ValidationResult Validate(object? value, Tag tag)
    {
        if (value == null)
        {
            if (!tag.Nullable && !tag.AllowEmpty)
                return new ValidationResult(false, "Value is null but not nullable");
            return new ValidationResult(true);
        }

        if (tag.Min != null)
        {
            if (value is string s)
            {
                if (s.Length < int.Parse(tag.Min))
                    return new ValidationResult(false, $"Below minimum length: {s.Length} < {tag.Min}");
            }
            else if (value is IComparable c)
            {
                var minVal = Convert.ChangeType(tag.Min, value.GetType());
                if (c.CompareTo(minVal) < 0)
                    return new ValidationResult(false, $"Below minimum: {value} < {tag.Min}");
            }
        }

        if (tag.Max != null)
        {
            if (value is string s)
            {
                if (s.Length > int.Parse(tag.Max))
                    return new ValidationResult(false, $"Exceeds maximum length: {s.Length} > {tag.Max}");
            }
            else if (value is IComparable c)
            {
                var maxVal = Convert.ChangeType(tag.Max, value.GetType());
                if (c.CompareTo(maxVal) > 0)
                    return new ValidationResult(false, $"Exceeds maximum: {value} > {tag.Max}");
            }
        }

        if (tag.Pattern != null && value is string sp)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(sp, tag.Pattern))
                return new ValidationResult(false, $"Pattern mismatch: '{sp}' does not match '{tag.Pattern}'");
        }

        return new ValidationResult(true);
    }

    public static ValidationResult Validate(object? value, string tagString)
    {
        return Validate(value, Tag.Parse(tagString));
    }
}

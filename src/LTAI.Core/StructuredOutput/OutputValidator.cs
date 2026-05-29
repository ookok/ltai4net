using System.Text.Json;
using LTAI.Core.StructuredOutput;

namespace LTAI.Core.StructuredOutput;

/// <summary>
/// Validates LLM output against a structured schema at runtime.
/// Used after GetResponseAsync when structured output is requested,
/// to catch providers that don't fully honor response_format.
/// Callers: LTAI.Tools.Tools.LTAIToolRegistry, LTAI.AI.Providers.StructuredOutputExtensions.
/// </summary>
public static class OutputValidator
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static ValidationResult Validate(string json, StructuredSchema schema)
    {
        if (string.IsNullOrWhiteSpace(json))
            return ValidationResult.Fail("Output is empty");

        if (schema is TextSchema)
            return ValidationResult.Ok(json);

        if (schema is EnumSchema enumSchema)
            return ValidateEnum(json, enumSchema);

        if (schema is JsonSchema jsonSchema)
            return ValidateJson(json, jsonSchema);

        return ValidationResult.Fail($"Unknown schema type: {schema.GetType().Name}");
    }

    public static ValidationResult ValidateAndParse<T>(string json)
    {
        try
        {
            var result = JsonSerializer.Deserialize<T>(json, _jsonOptions);
            if (result == null)
                return ValidationResult.Fail("Deserialization returned null");
            return ValidationResult.Ok(result);
        }
        catch (JsonException ex)
        {
            return ValidationResult.Fail($"JSON parse error: {ex.Message}");
        }
    }

    private static ValidationResult ValidateEnum(string json, EnumSchema schema)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? actual = null;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("value", out var valProp))
                actual = valProp.GetString();
            else if (root.ValueKind == JsonValueKind.String)
                actual = root.GetString();

            if (actual == null)
                return ValidationResult.Fail("Could not extract enum value from response");
            if (!schema.Values.Contains(actual, StringComparer.OrdinalIgnoreCase))
                return ValidationResult.Fail($"Value '{actual}' is not in allowed set: [{string.Join(", ", schema.Values)}]");
            return ValidationResult.Ok(actual);
        }
        catch (JsonException ex)
        {
            return ValidationResult.Fail($"Invalid JSON: {ex.Message}");
        }
    }

    private static ValidationResult ValidateJson(string json, JsonSchema schema)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return ValidationResult.Fail("Expected a JSON object at root");

            var errors = new List<string>();
            foreach (var (propName, propSchema) in schema.Properties)
            {
                if (!root.TryGetProperty(propName, out var propValue))
                {
                    errors.Add($"Missing required property: '{propName}'");
                    continue;
                }
                var propError = ValidateProperty(propValue, propSchema, propName);
                if (propError != null) errors.Add(propError);
            }
            return errors.Count > 0
                ? ValidationResult.Fail(string.Join("; ", errors))
                : ValidationResult.Ok(root);
        }
        catch (JsonException ex)
        {
            return ValidationResult.Fail($"Invalid JSON: {ex.Message}");
        }
    }

    private static string? ValidateProperty(JsonElement value, FieldProperty prop, string path)
    {
        var kind = value.ValueKind.ToString();

        if (prop is StringProperty)
            return value.ValueKind == JsonValueKind.String ? null : $"'{path}' should be a string, got {kind}";
        if (prop is NumberProperty)
            return value.ValueKind == JsonValueKind.Number ? null : $"'{path}' should be a number, got {kind}";
        if (prop is BooleanProperty)
        {
            var isBool = value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False;
            return isBool ? null : $"'{path}' should be a boolean, got {kind}";
        }
        if (prop is ArrayProperty arrProp)
            return value.ValueKind == JsonValueKind.Array
                ? ValidateArray(value, arrProp.Items, path)
                : $"'{path}' should be an array, got {kind}";
        if (prop is ObjectProperty objProp)
            return value.ValueKind == JsonValueKind.Object
                ? ValidateObject(value, objProp, path)
                : $"'{path}' should be an object, got {kind}";
        if (prop is EnumProperty enumProp)
            return ValidateEnumProperty(value, enumProp, path);

        return null;
    }

    private static string? ValidateArray(JsonElement arr, FieldProperty itemProp, string path)
    {
        var errors = new List<string>();
        var i = 0;
        foreach (var item in arr.EnumerateArray())
        {
            var itemError = ValidateProperty(item, itemProp, $"{path}[{i}]");
            if (itemError != null) errors.Add(itemError);
            i++;
        }
        return errors.Count > 0 ? string.Join("; ", errors) : null;
    }

    private static string? ValidateObject(JsonElement obj, ObjectProperty objProp, string path)
    {
        var errors = new List<string>();
        foreach (var (key, prop) in objProp.Properties)
        {
            if (!obj.TryGetProperty(key, out var val))
            {
                errors.Add($"Missing required property: '{path}.{key}'");
                continue;
            }
            var propError = ValidateProperty(val, prop, $"{path}.{key}");
            if (propError != null) errors.Add(propError);
        }
        return errors.Count > 0 ? string.Join("; ", errors) : null;
    }

    private static string? ValidateEnumProperty(JsonElement value, EnumProperty enumProp, string path)
    {
        if (value.ValueKind != JsonValueKind.String)
            return $"'{path}' should be a string (enum), got {value.ValueKind}";
        var str = value.GetString() ?? "";
        if (!enumProp.Values.Contains(str, StringComparer.OrdinalIgnoreCase))
        {
            var allowed = string.Join(", ", enumProp.Values);
            return $"'{path}' value '{str}' not in [{allowed}]";
        }
        return null;
    }
}

/// <summary>
/// Result of a structured output validation.
/// </summary>
public sealed record ValidationResult
{
    public bool IsValid { get; init; }
    public object? Value { get; init; }
    public string? ErrorMessage { get; init; }
    public static ValidationResult Ok(object? value = null) => new() { IsValid = true, Value = value };
    public static ValidationResult Fail(string error) => new() { IsValid = false, ErrorMessage = error };
}

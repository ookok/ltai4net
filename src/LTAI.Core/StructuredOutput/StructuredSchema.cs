using System.Text.Json;

namespace LTAI.Core.StructuredOutput;

/// <summary>
/// Defines a structured output schema for LLM response formatting.
/// Enables type-safe, validated outputs from chat completions.
/// This is the core of the structured output framework — it allows
/// callers to specify EXACTLY what shape of response they expect,
/// and the framework passes it as response_format to the provider.
///
/// Supported schema types:
///   - JsonSchema: arbitrary JSON object with typed properties
///   - EnumSchema: single string from a fixed set of values
///   - TextSchema: free text (default, no constraint)
///
/// Callers: ChatClientExtensions.WithStructuredOutput&lt;T&gt;(), LTAI.Tools.LTAIToolRegistry.
/// </summary>
public abstract record StructuredSchema
{
    public string Description { get; init; } = "";
    public abstract Dictionary<string, object> ToResponseFormat();
    public string ToResponseFormatJson() => JsonSerializer.Serialize(ToResponseFormat());
}

/// <summary>
/// JSON Schema-based structured output.
/// </summary>
public sealed record JsonSchema : StructuredSchema
{
    public string Name { get; init; }
    public bool AdditionalProperties { get; init; }
    public IReadOnlyDictionary<string, FieldProperty> Properties { get; init; }
        = new Dictionary<string, FieldProperty>();

    public JsonSchema(string name) => Name = name;

    public JsonSchema WithProperty(string name, FieldProperty property)
    {
        var dict = new Dictionary<string, FieldProperty>(Properties) { [name] = property };
        return this with { Properties = dict };
    }

    public override Dictionary<string, object> ToResponseFormat()
    {
        var schema = new Dictionary<string, object>
        {
            ["name"] = Name,
            ["schema"] = BuildJsonSchema(),
            ["strict"] = true
        };
        return new Dictionary<string, object>
        {
            ["type"] = "json_schema",
            ["json_schema"] = schema
        };
    }

    private Dictionary<string, object> BuildJsonSchema()
    {
        var props = new Dictionary<string, object>();
        foreach (var (key, prop) in Properties)
            props[key] = prop.ToJsonSchema();

        return new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = props,
            ["required"] = Properties.Keys.ToList(),
            ["additionalProperties"] = AdditionalProperties
        };
    }
}

/// <summary>
/// Constrain output to one of a fixed set of string values.
/// </summary>
public sealed record EnumSchema : StructuredSchema
{
    public string[] Values { get; init; } = Array.Empty<string>();
    public EnumSchema(params string[] values) => Values = values;

    public override Dictionary<string, object> ToResponseFormat()
    {
        return new Dictionary<string, object>
        {
            ["type"] = "json_schema",
            ["json_schema"] = new Dictionary<string, object>
            {
                ["name"] = "enum_output",
                ["schema"] = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["value"] = new Dictionary<string, object>
                        {
                            ["type"] = "string", ["enum"] = Values
                        }
                    },
                    ["required"] = new[] { "value" },
                    ["additionalProperties"] = false
                },
                ["strict"] = true
            }
        };
    }
}

/// <summary>Free-text output (no constraint).</summary>
public sealed record TextSchema : StructuredSchema
{
    public override Dictionary<string, object> ToResponseFormat()
        => new() { ["type"] = "text" };
}

/// <summary>Base type for a property in a JsonSchema.</summary>
public abstract record FieldProperty
{
    public string Description { get; init; } = "";
    public abstract Dictionary<string, object> ToJsonSchema();
}

/// <summary>A string property.</summary>
public sealed record StringProperty : FieldProperty
{
    public int? MaxLength { get; init; }
    public StringProperty(string description) { Description = description; }
    public override Dictionary<string, object> ToJsonSchema()
    {
        var s = new Dictionary<string, object> { ["type"] = "string" };
        if (!string.IsNullOrEmpty(Description)) s["description"] = Description;
        if (MaxLength.HasValue) s["maxLength"] = MaxLength.Value;
        return s;
    }
}

/// <summary>A numeric property.</summary>
public sealed record NumberProperty : FieldProperty
{
    public double? Minimum { get; init; }
    public double? Maximum { get; init; }
    public NumberProperty(string description) { Description = description; }
    public override Dictionary<string, object> ToJsonSchema()
    {
        var s = new Dictionary<string, object> { ["type"] = "number" };
        if (!string.IsNullOrEmpty(Description)) s["description"] = Description;
        if (Minimum.HasValue) s["minimum"] = Minimum.Value;
        if (Maximum.HasValue) s["maximum"] = Maximum.Value;
        return s;
    }
}

/// <summary>A boolean property.</summary>
public sealed record BooleanProperty : FieldProperty
{
    public BooleanProperty(string description) { Description = description; }
    public override Dictionary<string, object> ToJsonSchema()
    {
        var s = new Dictionary<string, object> { ["type"] = "boolean" };
        if (!string.IsNullOrEmpty(Description)) s["description"] = Description;
        return s;
    }
}

/// <summary>An array property.</summary>
public sealed record ArrayProperty : FieldProperty
{
    public FieldProperty Items { get; init; }
    public ArrayProperty(FieldProperty items) => Items = items;
    public override Dictionary<string, object> ToJsonSchema()
        => new() { ["type"] = "array", ["items"] = Items.ToJsonSchema() };
}

/// <summary>An object property with nested fields.</summary>
public sealed record ObjectProperty : FieldProperty
{
    public IReadOnlyDictionary<string, FieldProperty> Properties { get; init; }
        = new Dictionary<string, FieldProperty>();
    public ObjectProperty(Dictionary<string, FieldProperty> properties) => Properties = properties;
    public override Dictionary<string, object> ToJsonSchema()
    {
        var props = new Dictionary<string, object>();
        foreach (var (key, prop) in Properties)
            props[key] = prop.ToJsonSchema();
        return new Dictionary<string, object>
        {
            ["type"] = "object", ["properties"] = props,
            ["required"] = Properties.Keys.ToList(),
            ["additionalProperties"] = false
        };
    }
}

/// <summary>An enum property.</summary>
public sealed record EnumProperty : FieldProperty
{
    public string[] Values { get; init; } = Array.Empty<string>();
    public EnumProperty(string description, params string[] values) { Description = description; Values = values; }
    public override Dictionary<string, object> ToJsonSchema()
        => new() { ["type"] = "string", ["enum"] = Values, ["description"] = Description };
}

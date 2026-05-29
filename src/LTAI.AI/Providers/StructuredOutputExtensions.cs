using System.Text.Json;
using System.Text.Json.Nodes;
using LTAI.Core.StructuredOutput;
using LTAI.Core.Interfaces;
using Microsoft.Extensions.AI;

namespace LTAI.AI.Providers;

/// <summary>
/// Extension methods for IChatClient that add structured output support.
/// Allows callers to specify a schema for the LLM response, and the
/// framework passes response_format to the provider and validates output.
///
/// Usage:
///   var schema = new JsonSchema("MeetingNote")
///       .WithProperty("title", new StringProperty("Meeting title"))
///       .WithProperty("action_items", new ArrayProperty(
///           new StringProperty("One action item")));
///
///   // Option 1: Inject schema into ChatOptions
///   var options = new ChatOptions();
///   options.WithStructuredOutput(schema);
///
///   // Option 2: Inject schema into LLMChatOptions (cross-assembly safe)
///   var llmOpts = new LLMChatOptions().WithStructuredOutput(schema);
///
///   // Option 3: Use generic typed extension
///   var result = await client.GetStructuredResponseAsync&lt;MeetingNote&gt;(prompt, options);
///
/// Callers: LTAI.Agent.MAF.AgenticLoop, LTAI.Tools.DocEngine, LTAI.Web.
/// </summary>
public static class StructuredOutputExtensions
{
    /// <summary>
    /// Attach a structured output schema to LLMChatOptions (cross-assembly safe).
    /// Schema is serialized to JSON string. The pipeline will deserialize it
    /// when building ChatOptions in ChatClientExtensions.ToChatOptions().
    /// </summary>
    public static LLMChatOptions WithStructuredOutput(this LLMChatOptions options, StructuredSchema schema)
    {
        return options with { StructuredSchemaJson = schema.ToResponseFormatJson() };
    }

    private const string SchemaKey = "structured_schema";

    /// <summary>
    /// Attach a structured output schema to ChatOptions.
    /// The provider will read this in ToOpenAIOptions and set response_format.
    /// </summary>
    public static ChatOptions WithStructuredOutput(this ChatOptions options, StructuredSchema schema)
    {
        var props = options.AdditionalProperties;
        if (props is null)
        {
            // ChatOptions.AdditionalProperties uses a special type; wrap in try-catch
            try { options.AdditionalProperties = []; }
            catch { }
        }
        if (options.AdditionalProperties is not null)
            options.AdditionalProperties[SchemaKey] = JsonSerializer.Serialize(schema.ToResponseFormat());
        return options;
    }

    /// <summary>
    /// Get a structured response from the chat client, parsing the output
    /// into the specified type. Throws if the output doesn't match the schema.
    /// </summary>
    public static async Task<T?> GetStructuredResponseAsync<T>(
        this IChatClient client,
        string prompt,
        ChatOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new ChatOptions();

        // Auto-inject JSON schema for the type if not already set
        if (options.AdditionalProperties?.ContainsKey(SchemaKey) != true)
        {
            var schema = JsonSchemaBuilder.FromType<T>();
            options.WithStructuredOutput(schema);
        }

        var response = await client.GetResponseAsync(prompt, options, ct).ConfigureAwait(false);
        var text = response.Text;

        if (string.IsNullOrWhiteSpace(text))
            return default;

        return ParseStructuredResponse<T>(text);
    }

    /// <summary>
    /// Get a structured streaming response, yielding parsed results
    /// as they arrive. Useful for progressive rendering.
    /// </summary>
    public static async IAsyncEnumerable<T?> GetStructuredStreamingAsync<T>(
        this IChatClient client,
        string prompt,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        options ??= new ChatOptions();

        if (options.AdditionalProperties?.ContainsKey(SchemaKey) != true)
        {
            var schema = JsonSchemaBuilder.FromType<T>();
            options.WithStructuredOutput(schema);
        }

        var buffer = "";
        await foreach (var update in client.GetStreamingResponseAsync(prompt, options, ct).ConfigureAwait(false))
        {
            foreach (var content in update.Contents)
            {
                if (content is TextContent textContent)
                {
                    buffer += textContent.Text;

                    // Try to parse incrementally — if valid JSON, yield it
                    if (TryParsePartial<T>(buffer, out var partial))
                        yield return partial;
                }
            }
        }

        // Final parse attempt on the complete buffer
        if (!string.IsNullOrWhiteSpace(buffer))
        {
            var final = ParseStructuredResponse<T>(buffer);
            if (final != null)
                yield return final;
        }
    }

    /// <summary>
    /// Try to extract the structured schema from ChatOptions (for provider use).
    /// </summary>
    public static StructuredSchema? GetStructuredSchema(this ChatOptions options)
    {
        if (options.AdditionalProperties?.TryGetValue(SchemaKey, out var raw) == true
            && raw is string json && !string.IsNullOrEmpty(json))
        {
            try
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(json);
                if (dict == null) return null;

                var type = dict.GetValueOrDefault("type")?.ToString();
                return type switch
                {
                    "json_schema" => ParseJsonSchema(dict),
                    "text" => new TextSchema(),
                    _ => null
                };
            }
            catch { return null; }
        }
        return null;
    }

    // ========================================================================
    // Internal helpers
    // ========================================================================

    private static T? ParseStructuredResponse<T>(string text)
    {
        // Try to extract JSON array/object from the response (handles markdown fences)
        var json = ExtractJson(text);
        if (string.IsNullOrEmpty(json)) return default;

        try
        {
            return JsonSerializer.Deserialize<T>(json, _jsonOptions);
        }
        catch (JsonException)
        {
            // If T is string, return raw text
            if (typeof(T) == typeof(string))
                return (T)(object)text;
            return default;
        }
    }

    private static bool TryParsePartial<T>(string text, out T? result)
    {
        result = default;
        var json = ExtractJson(text);
        if (string.IsNullOrEmpty(json)) return false;

        try
        {
            result = JsonSerializer.Deserialize<T>(json, _jsonOptions);
            return result != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Extract JSON from a response that may contain markdown fences
    /// or explanatory text around the JSON.
    /// </summary>
    private static string ExtractJson(string text)
    {
        text = text.Trim();

        // Try to extract from ```json ... ``` fence
        var jsonFenceStart = text.IndexOf("```json", StringComparison.Ordinal);
        if (jsonFenceStart >= 0)
        {
            var contentStart = jsonFenceStart + 7;
            var jsonFenceEnd = text.IndexOf("```", contentStart, StringComparison.Ordinal);
            if (jsonFenceEnd >= 0)
                return text[contentStart..jsonFenceEnd].Trim();
        }

        // Try to extract from ``` ... ``` fence (generic)
        var fenceStart = text.IndexOf("\n```\n", StringComparison.Ordinal);
        if (fenceStart >= 0)
        {
            var contentStart = fenceStart + 5;
            var fenceEnd = text.IndexOf("\n```", contentStart, StringComparison.Ordinal);
            if (fenceEnd >= 0)
                return text[contentStart..fenceEnd].Trim();
        }

        // If the text starts with { or [, assume it's already JSON
        if (text.StartsWith('{') || text.StartsWith('['))
            return text;

        // Last resort: find the first { and last }
        var firstBrace = text.IndexOf('{');
        var firstBracket = text.IndexOf('[');
        var start = firstBrace >= 0 && (firstBracket < 0 || firstBrace < firstBracket)
            ? firstBrace : firstBracket;
        if (start < 0) return "";

        // Find matching closing bracket
        var depth = 0;
        var inString = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"' && (i == 0 || text[i - 1] != '\\')) inString = !inString;
            if (inString) continue;

            if (c == '{' || c == '[') depth++;
            else if (c == '}' || c == ']')
            {
                depth--;
                if (depth == 0) return text[start..(i + 1)];
            }
        }

        return "";
    }

    private static JsonSchema? ParseJsonSchema(Dictionary<string, object?> format)
    {
        try
        {
            if (format.TryGetValue("json_schema", out var jsRaw) && jsRaw is JsonElement jsElement)
            {
                var name = jsElement.GetProperty("name").GetString() ?? "output";
                return new JsonSchema(name);
            }
        }
        catch { }
        return null;
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}

/// <summary>
/// Builds a JsonSchema from a .NET type at runtime using reflection.
/// Supports: primitives, strings, enums, nested objects, arrays, List&lt;T&gt;.
/// Used by GetStructuredResponseAsync&lt;T&gt; when no explicit schema is provided.
/// </summary>
internal static class JsonSchemaBuilder
{
    public static JsonSchema FromType<T>()
    {
        var type = typeof(T);
        var schema = new JsonSchema(type.Name);
        return BuildFromType(schema, type);
    }

    private static JsonSchema BuildFromType(JsonSchema schema, Type type)
    {
        foreach (var prop in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!prop.CanRead) continue;

            var fieldProp = GetFieldProperty(prop.PropertyType, prop.Name);
            if (fieldProp != null)
                schema = schema.WithProperty(ToCamelCase(prop.Name), fieldProp);
        }
        return schema;
    }

    private static FieldProperty? GetFieldProperty(Type type, string name)
    {
        if (type == typeof(string) || type == typeof(char))
            return new StringProperty(name);

        if (type == typeof(int) || type == typeof(long) || type == typeof(float)
            || type == typeof(double) || type == typeof(decimal))
            return new NumberProperty(name);

        if (type == typeof(bool))
            return new BooleanProperty(name);

        if (type.IsEnum)
            return new EnumProperty(name, Enum.GetNames(type));

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            var elementType = type.GetGenericArguments()[0];
            var elementProp = GetFieldProperty(elementType, $"{name}_item");
            if (elementProp != null)
                return new ArrayProperty(elementProp);
        }

        // Nested object
        if (type.IsClass && type != typeof(string))
        {
            var nestedProps = new Dictionary<string, FieldProperty>();
            foreach (var nested in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (!nested.CanRead) continue;
                var np = GetFieldProperty(nested.PropertyType, nested.Name);
                if (np != null)
                    nestedProps[ToCamelCase(nested.Name)] = np;
            }
            return new ObjectProperty(nestedProps);
        }

        return null;
    }

    private static string ToCamelCase(string s)
        => string.IsNullOrEmpty(s) || char.IsLower(s[0])
            ? s
            : char.ToLowerInvariant(s[0]) + s[1..];
}

using System.Text.Json;
using Microsoft.Extensions.AI;

namespace LTAI.Agent.Tools;

public sealed class ToolSchemaAdapter
{
    private static readonly Dictionary<string, string> CommonRenames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["filePath"] = "path",
        ["file_path"] = "path",
        ["sourcePath"] = "path",
        ["source_path"] = "path",
        ["destinationPath"] = "dest",
        ["destination_path"] = "dest",
        ["outputPath"] = "output",
        ["output_path"] = "output",
        ["inputText"] = "text",
        ["input_text"] = "text",
        ["searchQuery"] = "query",
        ["search_query"] = "query",
        ["apiKey"] = "key",
        ["api_key"] = "key",
        ["maxTokens"] = "max_tokens",
        ["max_results"] = "limit",
        ["maxResults"] = "limit",
    };

    public AITool AdaptForModel(AITool original, string modelId)
    {
        if (ShouldSkip(modelId)) return original;

        var json = original.GetType().Name switch
        {
            nameof(AITool) => JsonSerializer.Serialize(original),
            _ => JsonSerializer.Serialize(original),
        };

        foreach (var (oldName, newName) in CommonRenames)
        {
            // Use System.Text.Json.Nodes to safely rename properties without corrupting string values
            var node = System.Text.Json.Nodes.JsonNode.Parse(json);
            if (node is System.Text.Json.Nodes.JsonObject obj && obj.TryGetPropertyValue(oldName, out var val))
            {
                obj.Remove(oldName);
                obj[newName] = val?.DeepClone();
                json = obj.ToJsonString();
            }
        }

        var adapted = JsonSerializer.Deserialize<AITool>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        return adapted ?? original;
    }

    private static bool ShouldSkip(string modelId)
    {
        if (string.IsNullOrEmpty(modelId)) return true;
        var lower = modelId.ToLowerInvariant();
        return lower.Contains("pro") || lower.Contains("max") || lower.Contains("large") || lower.Contains("turbo");
    }
}

using LTAI.AI.Utilities;
using LTAI.Core.Configuration;
using LTAI.Core.Interfaces;
using LTAI.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.AI.Governors;

public sealed class InputGovernor : LayerGovernor
{
    private static readonly string[] SpinalCommands = { "/help", "/status", "/pause", "/resume", "/restart" };

    private readonly HybridIntentRouter? _hybridRouter;
    private readonly IOptions<LTAIOptions> _options;

    public InputGovernor(ICognitiveMesh mesh, IChatClient llm, ILogger<InputGovernor> logger,
        IOptions<LTAIOptions> options, HybridIntentRouter? hybridRouter = null)
        : base("input", mesh, llm, logger)
    {
        _options = options;
        _hybridRouter = hybridRouter;
    }

    public override async Task<Handshake> ProcessAsync(Handshake incoming, CancellationToken cancellationToken = default)
    {
        var query = incoming.Payload?.GetValueOrDefault("query")?.ToString() ?? "";

        if (IsSpinalReflex(query, out var command))
        {
            Logger.LogInformation("Spinal reflex: {Command}", command);
            return new Handshake
            {
                From = LayerName,
                Action = "reflex",
                Payload = new Dictionary<string, object?>
                {
                    ["command"] = command,
                    ["original_query"] = query
                }
            };
        }

        float complexity;
        string label;
        string emotion;

        if (_hybridRouter != null)
        {
            var intent = await _hybridRouter.ClassifyAsync(query, cancellationToken);
            label = intent.Label;
            complexity = intent.Complexity;
            Logger.LogInformation("Hybrid intent: label={Label}, confidence={Conf:F2}, source={Source}",
                intent.Label, intent.Confidence, intent.Source);
        }
        else
        {
            (complexity, label) = ClassifyIntent(query);
        }

        emotion = DetectEmotion(query);

        // Entity extraction via L1 flash for factual queries
        string? entity = null;
        string? entityRoot = null;
        if (label != "fast" && label != "reflex")
        {
            try
            {
                var flashModel = _options.Value.AI.L1.Model;
                var prompt = $"从以下查询中提取核心实体名称。规则：1) 如果查询包含特定的人名、公司名、地名、产品名、概念名等实体，返回实体名称；否则返回空。2) 去掉“公司”、“有限”、“集团”、“科技”等通用后缀，只保留核心名称。3) 只返回实体本身，不要解释。\n查询: {query}";
                var response = await LLM.GetResponseAsync(prompt,
                    new ChatOptions { ModelId = flashModel, Temperature = 0.1f, MaxOutputTokens = 100 },
                    cancellationToken);
                var extracted = (response.Text ?? "").Trim();

                if (!string.IsNullOrWhiteSpace(extracted) && extracted.Length <= 50)
                {
                    entity = extracted;
                    entityRoot = extracted;
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Entity extraction failed for: {Query}", query);
            }
        }

        return new Handshake
        {
            From = LayerName,
            Action = "classified",
            Payload = new Dictionary<string, object?>
            {
                ["query"] = query,
                ["complexity"] = complexity,
                ["label"] = label,
                ["emotion"] = emotion,
                ["query_length"] = query.Length,
                ["entity"] = entity,
                ["entity_root"] = entityRoot
            }
        };
    }

    private static bool IsSpinalReflex(string query, out string command)
    {
        foreach (var cmd in SpinalCommands)
        {
            if (query.Trim().StartsWith(cmd, StringComparison.OrdinalIgnoreCase))
            {
                command = cmd;
                return true;
            }
        }
        command = "";
        return false;
    }

    private static (float complexity, string label) ClassifyIntent(string query) =>
        GovernorUtilities.ClassifyIntent(query);

    private static string DetectEmotion(string query) =>
        GovernorUtilities.DetectEmotion(query);
}

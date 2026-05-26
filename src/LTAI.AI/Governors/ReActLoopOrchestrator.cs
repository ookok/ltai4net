using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using LTAI.AI.Providers;
using LTAI.AI.Utilities;
using LTAI.Core.Configuration;
using LTAI.Core.Messaging;
using LTAI.Core.Models;
using LTAI.Core.System;
using LTAI.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.AI.Governors;

public sealed class ReActLoopOrchestrator
{
    private readonly IChatClient _llm;
    private readonly AIToolRegistry _toolRegistry;
    private readonly ToolSelector _toolSelector;
    private readonly ResponseGroundingVerifier _groundingVerifier;
    private readonly MetaCognitiveLayer _metaCognition;
    private readonly PromptTemplateStore _prompts;
    private readonly IOptions<LTAIOptions> _options;
    private readonly ILogger _logger;
    private readonly SynapticMemory? _synapticMemory;
    private readonly ContextGovernor _contextGovernor;
    private readonly BAVTRouter _bavtRouter;
    private readonly ERLLoop _erlLoop;
    private readonly ICrossRunEvolutionStore? _evolutionStore;
    private readonly HarnessEvolution? _harnessEvo;
    private readonly ModelHealthTracker _health;

    private static readonly Regex TextToolCall = new(
        @"【TOOL:(\w[\w_]*)\s+(.*?)】", RegexOptions.Compiled);

    private string DefaultModel => _options.Value.AI.L2.Model;
    private string FlashModel => _options.Value.AI.L1.Model;

    public string? FinalResponse { get; private set; }
    public string ModelUsed { get; private set; } = "";
    public int TotalToolCalls { get; private set; }
    public bool GroundingFailed { get; private set; }
    public bool Layer1HighConfidence { get; private set; }
    public bool PatternMatched { get; private set; }
    public string Label { get; private set; } = "deep";
    public int RetryLevel { get; private set; }

    public ReActLoopOrchestrator(
        IChatClient llm,
        AIToolRegistry toolRegistry,
        ToolSelector toolSelector,
        ResponseGroundingVerifier groundingVerifier,
        MetaCognitiveLayer metaCognition,
        PromptTemplateStore prompts,
        IOptions<LTAIOptions> options,
        ILogger<ReActLoopOrchestrator> logger,
        SynapticMemory? synapticMemory = null,
        ContextGovernor? contextGovernor = null,
        BAVTRouter? bavtRouter = null,
        ERLLoop? erlLoop = null,
        ICrossRunEvolutionStore? evolutionStore = null,
        HarnessEvolution? harnessEvo = null,
        ModelHealthTracker? health = null)
    {
        _llm = llm;
        _toolRegistry = toolRegistry;
        _toolSelector = toolSelector;
        _groundingVerifier = groundingVerifier;
        _metaCognition = metaCognition;
        _prompts = prompts;
        _options = options;
        _logger = logger;
        _synapticMemory = synapticMemory;
        _contextGovernor = contextGovernor!;
        _bavtRouter = bavtRouter ?? new BAVTRouter(100.0);
        _erlLoop = erlLoop ?? new ERLLoop();
        _evolutionStore = evolutionStore;
        _harnessEvo = harnessEvo;
        _health = health ?? new ModelHealthTracker();
    }

    public async IAsyncEnumerable<string> RunReActLoopAsync(
        string query,
        string model,
        string label,
        string dateTag,
        string? layer1Context,
        bool layer1HighConfidence,
        string? autoSearchContext,
        string? layer2Context,
        string? metaContext,
        MetaCognitiveAssessment metaAssessment,
        bool patternMatched,
        int toolCount,
        float budgetRatio,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ModelUsed = model;
        Label = label;
        Layer1HighConfidence = layer1HighConfidence;
        PatternMatched = patternMatched;

        var selectedTools = _toolSelector.SelectTools(query, _toolRegistry.GetTools());

        string? memoryContext = null;
        if (_synapticMemory != null && layer1Context == null)
        {
            var similar = _synapticMemory.FindSimilar(query, maxResults: 2, minReward: 0.7f);
            if (similar.Count > 0)
            {
                var memSb = new StringBuilder();
                memSb.AppendLine("【跨运行记忆】以下是以往相似问题的成功回答，可参考其结构和关键信息：");
                foreach (var exp in similar)
                {
                    var snippet = exp.Response.Length > 500 ? exp.Response[..500] + "..." : exp.Response;
                    memSb.AppendLine($"--- 历史问答 (置信度={exp.Confidence:F2}, 奖励={exp.Reward:F2}) ---");
                    memSb.AppendLine(snippet);
                }
                memoryContext = memSb.ToString();
                _logger.LogInformation("SynapticMemory: injected {Count} similar past experiences", similar.Count);
            }
        }

        var (messages, streamOptions) = BuildSystemMessages(
            model, layer1Context, autoSearchContext, layer2Context,
            metaContext, metaAssessment, label, toolCount, dateTag, query,
            selectedTools);

        if (memoryContext != null)
            messages.Insert(0, new ChatMessage(ChatRole.System, memoryContext));

        if (!layer1HighConfidence)
        {
            var history = _contextGovernor.CompressHistory();
            if (history.Length > 0)
                messages.Insert(0, new ChatMessage(ChatRole.System,
                    $"【此前对话】\n{history}\n\n请基于以上对话历史理解用户当前问题的上下文。"));
        }

        var useStreaming = label != "fast" && label != "reflex";
        var fullResponse = new StringBuilder();
        var totalToolCalls = patternMatched ? 1 : 0;
        var retryLevel = 0;
        var groundingFailed = false;
        const int maxToolRounds = 5;
        for (int round = 0; round < maxToolRounds; round++)
        {
            var toolCalls = new List<FunctionCallContent>();
            var responseText = new StringBuilder();
            var reasoningText = new StringBuilder();

            if (useStreaming)
            {
                IAsyncEnumerable<ChatResponseUpdate>? streamResponse = null;
                try { streamResponse = _llm.GetStreamingResponseAsync(messages, streamOptions, ct); }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Stream init failed for query: {Query}", query[..Math.Min(query.Length, 60)]);
                }

                if (streamResponse == null) { yield return "Error connecting to provider."; yield break; }

                var streamChunks = new List<string>();
                var toolList = new Dictionary<string, ToolInvocationPart>();
                Exception? streamError = null;
                try
                {
                    await foreach (var update in streamResponse)
                    {
                        foreach (var content in update.Contents)
                        {
                            if (content is FunctionCallContent fcc)
                                toolCalls.Add(fcc);
                            else if (content is TextReasoningContent rc && !string.IsNullOrEmpty(rc.Text))
                            {
                                reasoningText.Append(rc.Text);
                                streamChunks.Add($"<thinking>{rc.Text}</thinking>");
                            }
                        }
                        if (!string.IsNullOrEmpty(update.Text))
                        {
                            streamChunks.Add(update.Text);
                            responseText.Append(update.Text);
                        }
                        if (update.AdditionalProperties != null
                            && update.AdditionalProperties.TryGetValue("NormalizedParts", out var partsObj)
                            && partsObj is List<Part> parts)
                        {
                            foreach (var part in parts)
                            {
                                if (part is ToolInvocationPart toolPart && !string.IsNullOrEmpty(toolPart.ToolName))
                                {
                                    var key = toolPart.Id ?? toolPart.ToolName;
                                    if (!toolList.ContainsKey(key))
                                        toolList[key] = toolPart;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    streamError = ex;
                    _logger.LogWarning(ex, "Stream iteration failed, using partial response: {Len} chars",
                        responseText.Length);
                }

                foreach (var chunk in streamChunks)
                    yield return chunk;

                if (streamError != null && responseText.Length == 0)
                {
                    yield return "模型调用失败，请稍后重试。";
                    yield break;
                }
            }
            else
            {
                var response = await _llm.GetResponseAsync(messages, streamOptions, ct).ConfigureAwait(false);
                responseText.Append(response.Text ?? "");
                if (response.Messages != null)
                {
                    foreach (var msg in response.Messages)
                        if (msg.Contents?.OfType<FunctionCallContent>() is { } fccs)
                            toolCalls.AddRange(fccs);
                }
                var text = responseText.ToString();
                if (!string.IsNullOrEmpty(text))
                    yield return text;
            }

            fullResponse.Append(responseText.ToString());

            if (toolCalls.Count == 0)
            {
                var textCalls = ParseTextToolCalls(responseText.ToString());
                if (textCalls.Count > 0)
                {
                    toolCalls.AddRange(textCalls);
                    _logger.LogInformation("TextToolCall: parsed {Count} tool calls from response text", textCalls.Count);
                }
            }

            if (toolCalls.Count == 0)
            {
                if (!layer1HighConfidence)
                {
                    var toolContextForVerification = layer1Context ?? layer2Context ?? autoSearchContext;
                    var verification = _groundingVerifier.Verify(
                        responseText.ToString(), query, toolContextForVerification,
                        totalToolCalls > 0, totalToolCalls, layer1Context != null);

                    if (!verification.IsGrounded)
                    {
                        retryLevel++;
                        var escalation = await EscalateGroundingFailure(
                            query, retryLevel, verification, messages,
                            layer1Context, layer2Context, autoSearchContext,
                            responseText.ToString(), toolContextForVerification, ct).ConfigureAwait(false);

                        switch (escalation.Action)
                        {
                            case EscalationAction.YieldAndBreak:
                                groundingFailed = true;
                                foreach (var chunk in escalation.YieldChunks!)
                                    yield return chunk;
                                goto ExitLoop;
                            case EscalationAction.Break:
                                groundingFailed = true;
                                goto ExitLoop;
                            case EscalationAction.Continue:
                                messages.Add(new ChatMessage(ChatRole.System, escalation.RetryMessage!));
                                continue;
                        }
                    }
                    _logger.LogDebug("Grounding check passed");

                    if (retryLevel == 0 && !string.IsNullOrWhiteSpace(toolContextForVerification)
                        && toolContextForVerification!.Length > 200
                        && budgetRatio > 0.3f)
                    {
                        var llmVerification = await _groundingVerifier.VerifyWithLLMAsync(
                            responseText.ToString(), toolContextForVerification,
                            _llm, FlashModel, ct).ConfigureAwait(false);

                        if (!llmVerification.IsGrounded)
                        {
                            retryLevel = 1;
                            _logger.LogWarning("LLM grounding check failed: {Issue}", llmVerification.Issue);
                            messages.Add(new ChatMessage(ChatRole.System,
                                $"【语义验证失败】{llmVerification.RetryInstruction}"));
                            continue;
                        }
                        _logger.LogDebug("LLM grounding check passed");
                    }
                }
                break;
            }

            totalToolCalls += toolCalls.Count;

            var assistantReply = responseText.ToString();
            var assistantContents = new List<AIContent>(toolCalls);
            if (reasoningText.Length > 0)
                assistantContents.Insert(0, new TextReasoningContent(reasoningText.ToString()));
            messages.Add(new ChatMessage(ChatRole.Assistant, assistantReply) { Contents = assistantContents });

            foreach (var tc in toolCalls)
            {
                yield return "\ud83d\udccb ";

                if (_harnessEvo != null)
                {
                    var argsStr = tc.Arguments != null
                        ? string.Join(" ", tc.Arguments.Select(kv => $"{kv.Key}={kv.Value}"))
                        : "";
                    if (!_harnessEvo.ValidateEnvironmentContract(tc.Name, argsStr, out var violation))
                    {
                        _logger.LogWarning("HarnessEvolution environment contract violated for {Tool}: {Violation}", tc.Name, violation);
                        messages.Add(new ChatMessage(ChatRole.Tool, "") { Contents = new List<AIContent> { new FunctionResultContent(tc.CallId, $"[Blocked by environment contract: {violation}]") } });
                        continue;
                    }
                }

                try
                {
                    var args = new Dictionary<string, object?>();
                    if (tc.Arguments != null)
                    {
                        foreach (var kv in tc.Arguments)
                            args[kv.Key] = kv.Value;
                    }
                    var result = await _toolRegistry.InvokeAsync(tc.Name, args, ct).ConfigureAwait(false);
                    var resultText = ToolCallRepairer.CapToolResult(result?.ToString() ?? "");
                    messages.Add(new ChatMessage(ChatRole.Tool, "") { Contents = new List<AIContent> { new FunctionResultContent(tc.CallId, resultText) } });
                    _logger.LogInformation("ReAct: executed {Tool} (callId={Id})", tc.Name, tc.CallId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ReAct: tool {Tool} failed", tc.Name);
                    messages.Add(new ChatMessage(ChatRole.Tool, "") { Contents = new List<AIContent> { new FunctionResultContent(tc.CallId, $"Error: {ex.Message}") } });
                }
            }

            if (!useStreaming)
            {
                continue;
            }
        }

        ExitLoop:
        FinalResponse = fullResponse.ToString();
        TotalToolCalls = totalToolCalls;
        GroundingFailed = groundingFailed;
        RetryLevel = retryLevel;

        if (!string.IsNullOrEmpty(FinalResponse) && FinalResponse.Length > 20
            && !FinalResponse.Contains("模型调用失败") && !groundingFailed)
            _health.RecordSuccess(model);
        else if (!layer1HighConfidence)
            _health.RecordFailure(model);
    }

    private (List<ChatMessage> Messages, ChatOptions Options) BuildSystemMessages(
        string model,
        string? layer1Context, string? autoSearchContext, string? layer2Context,
        string? metaContext, MetaCognitiveAssessment metaAssessment, string label,
        int toolCount, string dateTag, string query,
        List<AITool> selectedTools)
    {
        var messages = new List<ChatMessage>();
        var options = new ChatOptions
        {
            ModelId = model,
            Temperature = 0.3f,
            MaxOutputTokens = 4096,
            Tools = selectedTools
        };

        if (layer1Context != null)
            messages.Add(new ChatMessage(ChatRole.System, layer1Context));
        if (autoSearchContext != null)
            messages.Add(new ChatMessage(ChatRole.System, autoSearchContext));
        if (layer2Context != null)
            messages.Add(new ChatMessage(ChatRole.System, layer2Context));

        var allLayersEmpty = layer1Context == null && layer2Context == null && autoSearchContext == null;
        if (allLayersEmpty && metaAssessment.ShouldDelegate && label != "fast" && label != "reflex")
        {
            messages.Add(new ChatMessage(ChatRole.System, _prompts.Render("all_layers_empty")));
        }

        var selCount = selectedTools.Count;
        if (selCount > 0)
        {
            var toolNames = string.Join("、", selectedTools.Take(10).Select(t => t.Name));
            if (layer1Context != null)
            {
                messages.Add(new ChatMessage(ChatRole.System, _prompts.Render("layer1_tool_summary", new Dictionary<string, string>
                {
                    ["tool_names"] = toolNames,
                    ["tool_count"] = selCount.ToString()
                })));
            }
            else
            {
                messages.Add(new ChatMessage(ChatRole.System, _prompts.Render("layer_tool_rules", new Dictionary<string, string>
                {
                    ["tool_names"] = toolNames,
                    ["tool_count"] = selCount.ToString()
                })));
            }
        }

        if (metaContext != null)
            messages.Add(new ChatMessage(ChatRole.System, metaContext));

        messages.Add(new ChatMessage(ChatRole.User, $"{dateTag}\n{query}"));
        return (messages, options);
    }

    private static List<FunctionCallContent> ParseTextToolCalls(string text)
    {
        var calls = new List<FunctionCallContent>();
        foreach (Match m in TextToolCall.Matches(text))
        {
            var toolName = m.Groups[1].Value;
            var argsStr = m.Groups[2].Value;
            var args = new Dictionary<string, object?>();

            foreach (Match am in Regex.Matches(argsStr, @"(\w[\w_]*)=(""[^""]*""|[^\s】]+)"))
            {
                var key = am.Groups[1].Value;
                var val = am.Groups[2].Value.Trim('"');
                if (val is "null" or "null!") val = null;
                args[key] = val;
            }

            var callId = $"text_{toolName}_{Guid.NewGuid():N}"[..64];
            calls.Add(new FunctionCallContent(callId, toolName, args));
        }
        return calls;
    }

    private enum EscalationAction { Continue, Break, YieldAndBreak }

    private sealed record EscalationResult(
        EscalationAction Action,
        List<string>? YieldChunks = null,
        string? RetryMessage = null)
    {
        public static EscalationResult ContinueLoop(string msg) => new(EscalationAction.Continue, RetryMessage: msg);
        public static EscalationResult BreakLoop => new(EscalationAction.Break);
        public static EscalationResult YieldAndBreak(List<string> chunks) => new(EscalationAction.YieldAndBreak, YieldChunks: chunks);
    }

    private async Task<EscalationResult> EscalateGroundingFailure(
        string query, int retryLevel, GroundingResult verification,
        List<ChatMessage> messages, string? layer1Context, string? layer2Context,
        string? autoSearchContext, string? responseText,
        string? toolContextForVerification, CancellationToken ct)
    {
        var metaMetrics = _metaCognition.GetMetrics();

        if (retryLevel == 1 && !string.IsNullOrWhiteSpace(responseText)
            && !string.IsNullOrWhiteSpace(toolContextForVerification))
        {
            try
            {
                var ctxSnippet = toolContextForVerification.Length > 1500 ? toolContextForVerification[..1500] : toolContextForVerification;
                var cipoPrompt = $"Tool data:\n{ctxSnippet}\n\nWrong answer:\n{responseText[..Math.Min(responseText.Length, 500)]}\n\nGenerate a brief corrected answer direction (1-2 sentences) based ONLY on the tool data:";
                var cipoResult = await _llm.GetResponseAsync(cipoPrompt, new ChatOptions { ModelId = FlashModel, Temperature = 0.1f, MaxOutputTokens = 200, Tools = new List<AITool>() }, ct).ConfigureAwait(false);
                var correction = cipoResult.Text?.Trim();
                if (!string.IsNullOrWhiteSpace(correction))
                    messages.Add(new ChatMessage(ChatRole.System,
                        $"【CIPO在线纠正 - 仅修正错误部分】问题: {verification.Issue}\n正确方向: {correction}\n保留原有回答中正确的部分，只修正上述问题。"));
            }
            catch (Exception ex) { _logger.LogWarning(ex, "CIPO online correction generation failed"); }
        }
        var avgFamiliarity = metaMetrics.TryGetValue("avg_familiarity", out var af)
            ? Convert.ToSingle(af) : 0.1f;
        var erlSuccessRate = _erlLoop.SuccessRate;

        var blendedConfidence = (float)(avgFamiliarity * 0.6 + erlSuccessRate * 0.4);
        var maxRetries = Math.Clamp((int)(6 - blendedConfidence * 5), 2, 5);

        var budgetRatio = _bavtRouter.BudgetRatio;
        if (budgetRatio < 0.5f) maxRetries = Math.Min(maxRetries, 3);
        if (budgetRatio < 0.2f) maxRetries = Math.Min(maxRetries, 2);

        var forceToolLevel = blendedConfidence < 0.3f ? 1 : 2;

        _logger.LogWarning("Grounding check failed L{Level}/{Max}: {Issue} (type={Type}, fam={Fam:F2}, erl={ERL:F2}, budget={Bud:F2})",
            retryLevel, maxRetries, verification.Issue, verification.CheckType, avgFamiliarity, erlSuccessRate, budgetRatio);

        if (retryLevel >= maxRetries)
        {
            _metaCognition.RecordOutcome(query, false);
            RecordSelfHealingLesson(query, retryLevel, verification.CheckType);

            var allContext = new List<string>();
            if (layer1Context != null) allContext.Add(layer1Context);
            if (layer2Context != null) allContext.Add(layer2Context);
            if (autoSearchContext != null) allContext.Add(autoSearchContext);

            if (allContext.Count > 0)
                return EscalationResult.YieldAndBreak(allContext);

            allContext.Add(_prompts.Render("honest_fallback"));
            return EscalationResult.YieldAndBreak(allContext);
        }

        if (retryLevel >= forceToolLevel)
        {
            var forcedContext = await ForceExecuteForRetryAsync(query, ct).ConfigureAwait(false);
            if (forcedContext != null)
                messages.Add(new ChatMessage(ChatRole.System, _prompts.Render("force_tool_exec", new Dictionary<string, string>
                {
                    ["level"] = retryLevel.ToString(),
                    ["context"] = forcedContext
                })));
        }

        var templateName = retryLevel >= maxRetries - 1 ? "grounding_failed_severe" : "grounding_failed";
        return EscalationResult.ContinueLoop(_prompts.Render(templateName, new Dictionary<string, string>
        {
            ["level"] = retryLevel.ToString(),
            ["check_type"] = verification.CheckType,
            ["retry_instruction"] = verification.RetryInstruction ?? ""
        }));
    }

    private async Task<string?> ForceExecuteForRetryAsync(string query, CancellationToken ct)
    {
        if (_toolRegistry.HasTool("web_search"))
        {
            try
            {
                var result = await _toolRegistry.InvokeAsync("web_search",
                    new Dictionary<string, object?> { ["query"] = query, ["maxResults"] = 3 }, ct);
                var text = result?.ToString();
                if (!string.IsNullOrWhiteSpace(text) && text.Length > 50)
                {
                    var truncated = text.Length > 2000 ? text[..2000] : text;
                    return $"【强制搜索】以下是为确保事实准确性而强制执行的搜索结果：\n{truncated}";
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ForceExecuteForRetry web_search failed");
            }
        }

        if (_toolRegistry.HasTool("shell_exec") && query.Contains("文件"))
        {
            try
            {
                var result = await _toolRegistry.InvokeAsync("shell_exec",
                    new Dictionary<string, object?> { ["command"] = query.Contains("目录") ? "ls -la" : "dir", ["workingDirectory"] = null! }, ct);
                var text = result?.ToString();
                if (!string.IsNullOrWhiteSpace(text) && text.Length > 10)
                    return $"【强制命令执行】以下是为确保准确性而强制执行的命令结果：\n{text[..Math.Min(text.Length, 2000)]}";
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ForceExecuteForRetry shell_exec failed");
            }
        }

        return null;
    }

    private void RecordSelfHealingLesson(string query, int retryLevel, string checkType)
    {
        if (_evolutionStore == null) return;

        try
        {
            _evolutionStore.RecordLesson(new EvolutionLesson
            {
                Category = LessonCategory.QualityRegression.ToString(),
                Severity = Math.Min(0.3f + retryLevel * 0.2f, 1.0f),
                Summary = $"Repeated grounding failures (L{retryLevel} retries, type={checkType}): {query[..Math.Min(query.Length, 80)]}",
                Mitigation = retryLevel >= 3
                    ? "Add Layer1 pattern or pre-emptive tool execution for this query type"
                    : "Enable stricter grounding verification for this domain",
                SourceRun = Guid.NewGuid().ToString("N")[..8],
                SourceStage = "l5_retry_escalation"
            });
            _logger.LogInformation("Self-healing: recorded lesson (severity={Sev}, L{Level})",
                Math.Min(0.3f + retryLevel * 0.2f, 1.0f), retryLevel);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to record self-healing lesson");
        }
    }
}

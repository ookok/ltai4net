using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors.Pipeline;

public sealed record GroundingResult(bool IsGrounded, string Issue, string? RetryInstruction = null);
public enum EscalationAction { Continue, Break, YieldAndBreak }

public sealed record EscalationResult(
    EscalationAction Action,
    string? RetryMessage = null,
    List<string>? YieldChunks = null);

public sealed class GroundingVerificationService
{
    private readonly ResponseGroundingVerifier _verifier;
    private readonly IChatClient _llm;
    private readonly PromptTemplateStore _prompts;
    private readonly ILogger<GroundingVerificationService> _logger;

    public GroundingVerificationService(
        ResponseGroundingVerifier verifier,
        IChatClient llm,
        PromptTemplateStore prompts,
        ILogger<GroundingVerificationService> logger)
    {
        _verifier = verifier;
        _llm = llm;
        _prompts = prompts;
        _logger = logger;
    }

    public GroundingResult Verify(bool layer1HighConfidence, string responseText, string query,
        string? toolContext, bool hasToolCalls, int totalToolCalls, bool hasLayer1Context)
    {
        if (layer1HighConfidence)
            return new GroundingResult(true, "layer1_high_confidence");

        var verification = _verifier.Verify(responseText, query, toolContext,
            hasToolCalls, totalToolCalls, hasLayer1Context);

        if (!verification.IsGrounded)
            _logger.LogWarning("Grounding check failed: {Issue}", verification.Issue);

        return new GroundingResult(verification.IsGrounded, verification.Issue);
    }

    public async Task<GroundingResult> VerifyWithLLMAsync(
        string responseText, string? toolContext, string flashModel, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(toolContext) || toolContext.Length <= 200)
            return new GroundingResult(true, "insufficient_context_for_llm_verify");

        var llmResult = await _verifier.VerifyWithLLMAsync(responseText, toolContext,
            _llm, flashModel, ct).ConfigureAwait(false);

        if (!llmResult.IsGrounded)
            _logger.LogWarning("LLM grounding check failed: {Issue}", llmResult.Issue);

        return new GroundingResult(llmResult.IsGrounded, llmResult.Issue, llmResult.RetryInstruction);
    }

    public EscalationResult Escalate(int retryLevel, string query,
        GroundingResult verification, List<ChatMessage> messages,
        string? layer1Context, string? layer2Context, string? autoSearchContext,
        string responseText, string? toolContext)
    {
        switch (retryLevel)
        {
            case 1:
                var retryMsg = _prompts.Render("grounding_retry", new Dictionary<string, string>
                {
                    ["issue"] = verification.Issue,
                    ["context"] = FormatContexts(layer1Context, layer2Context, autoSearchContext)
                });
                return new EscalationResult(EscalationAction.Continue, RetryMessage: retryMsg);

            case 2:
                var retry2 = _prompts.Render("grounding_retry_l2", new Dictionary<string, string>
                {
                    ["issue"] = verification.Issue,
                    ["response"] = responseText[..Math.Min(responseText.Length, 500)]
                });
                return new EscalationResult(EscalationAction.Continue, RetryMessage: retry2);

            default:
                var fallbackMsg = "\n\n[注意: 模型回答可能不够准确，建议重新描述问题或提供更多信息]";
                try
                {
                    var semanticMsg = _prompts.Render("semantic_verify_failed", new Dictionary<string, string>
                    {
                        ["retry_instruction"] = verification.Issue
                    });
                    if (!string.IsNullOrWhiteSpace(semanticMsg))
                        fallbackMsg = semanticMsg;
                }
                catch { }
                return new EscalationResult(EscalationAction.YieldAndBreak, YieldChunks: new List<string> { fallbackMsg });
        }
    }

    private static string FormatContexts(string? layer1, string? layer2, string? autoSearch)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(layer1)) parts.Add($"Layer1: {layer1[..Math.Min(layer1.Length, 300)]}");
        if (!string.IsNullOrWhiteSpace(layer2)) parts.Add($"Layer2: {layer2[..Math.Min(layer2.Length, 300)]}");
        if (!string.IsNullOrWhiteSpace(autoSearch)) parts.Add($"Search: {autoSearch[..Math.Min(autoSearch.Length, 300)]}");
        return string.Join("\n", parts);
    }
}

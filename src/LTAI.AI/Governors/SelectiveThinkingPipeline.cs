using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

/// <summary>
/// 选择性思考管道
/// 实现 Think-at-Hard (TaH) 理念：仅在困难 Token 上触发额外思考或升级 L2
/// </summary>
public sealed class SelectiveThinkingPipeline
{
    private readonly IL1InferenceEngine _l1Engine;
    private readonly IChatClient? _l2Client;
    private readonly TokenHardnessDecider _decider;
    private readonly ILogger<SelectiveThinkingPipeline> _logger;

    public SelectiveThinkingPipeline(
        IL1InferenceEngine l1Engine,
        IChatClient? l2Client,
        TokenHardnessDecider decider,
        ILogger<SelectiveThinkingPipeline>? logger = null)
    {
        _l1Engine = l1Engine;
        _l2Client = l2Client;
        _decider = decider;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SelectiveThinkingPipeline>.Instance;
    }

    /// <summary>
    /// 执行带选择性思考的生成
    /// </summary>
    public async IAsyncEnumerable<string> GenerateWithSelectiveThinkingAsync(
        string prompt,
        float temperature = 0.7f,
        int maxTokens = 512,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _decider.Reset();
        var fullResponse = new StringBuilder();
        var currentContext = prompt;
        var generatedTokens = 0;
        var lastVerifyToken = 0;
        const int verifyInterval = 64; // 每 64 tokens 验证一次

        while (generatedTokens < maxTokens && !ct.IsCancellationRequested)
        {
            // L1 尝试生成下一个 Token/片段
            var l1Output = await _l1Engine.GenerateAsync(currentContext, temperature, maxTokens: 1, ct).ConfigureAwait(false);
            
            if (string.IsNullOrEmpty(l1Output))
                break;

            // 逐个 Token 评估
            foreach (var token in SplitIntoTokens(l1Output))
            {
                var hardness = _decider.Evaluate(token, context: currentContext);
                
                // 触发验证：达到间隔或刚结束 ReThinking
                if (generatedTokens > 0 && (generatedTokens - lastVerifyToken >= verifyInterval || _decider.CurrentState == ThinkingState.ReThinking))
                {
                    _decider.CurrentState = ThinkingState.Verifying;
                }

                switch (_decider.CurrentState)
                {
                    case ThinkingState.Idle:
                        yield return token;
                        fullResponse.Append(token);
                        currentContext += token;
                        generatedTokens++;
                        break;

                    case ThinkingState.ReThinking:
                        _logger.LogDebug("🧠 Re-thinking token: {Token} (Confidence: {Conf:F2})", token, hardness.Confidence);
                        
                        var refined = await _l1Engine.GenerateAsync(
                            $"{currentContext}\n[Self-Correction: Think carefully before next token]", 
                            temperature: temperature + 0.2f, 
                            maxTokens: 1, 
                            ct);
                        
                        if (!string.IsNullOrEmpty(refined))
                        {
                            yield return refined;
                            fullResponse.Append(refined);
                            currentContext += refined;
                        }
                        else
                        {
                            yield return token;
                            fullResponse.Append(token);
                            currentContext += token;
                        }
                        generatedTokens++;
                        break;

                    case ThinkingState.Delegating:
                        _logger.LogWarning("⚡ Delegating to L2 after {Count} consecutive hard tokens", _decider.CurrentState);
                        
                        if (_l2Client != null)
                        {
                            var l2Response = await _l2Client.GetResponseAsync(
                                new List<ChatMessage> { new(ChatRole.User, currentContext) },
                                new ChatOptions { Temperature = 0.3f, MaxOutputTokens = 128 },
                                ct).ConfigureAwait(false);

                            var l2Text = l2Response.Text ?? "";
                            foreach (var l2Token in SplitIntoTokens(l2Text))
                            {
                                yield return l2Token;
                                fullResponse.Append(l2Token);
                                currentContext += l2Token;
                                generatedTokens++;
                            }

                            _decider.Reset();
                            lastVerifyToken = generatedTokens;
                        }
                        else
                        {
                            yield return token;
                            fullResponse.Append(token);
                            currentContext += token;
                            generatedTokens++;
                        }
                        break;

                    case ThinkingState.Verifying:
                        _logger.LogDebug("🔍 Verifying generated content against prompt...");
                        
                        var verificationResult = await VerifyConsistencyAsync(prompt, fullResponse.ToString(), ct).ConfigureAwait(false);
                        if (!verificationResult.Passed)
                        {
                            _logger.LogWarning("⚠️ Verification failed: {Reason}. Triggering Self-Correction.", verificationResult.Reason);
                            
                            var correctionPrompt = $"{currentContext}\n[Verification Failed: {verificationResult.Reason}. Please correct the output to match the original intent.]";
                            var corrected = await _l1Engine.GenerateAsync(correctionPrompt, temperature: 0.5f, maxTokens: 64, ct).ConfigureAwait(false);
                            
                            if (!string.IsNullOrEmpty(corrected))
                            {
                                yield return $"\n[Corrected]: {corrected}";
                                fullResponse.Append(corrected);
                                currentContext += corrected;
                            }
                        }
                        else
                        {
                            lastVerifyToken = generatedTokens;
                        }
                        
                        _decider.Reset();
                        break;
                }
            }
        }
    }

    private static IEnumerable<string> SplitIntoTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        yield return text;
    }

    /// <summary>
    /// 闭环验证：检查生成内容是否与 prompt 语义一致
    /// 启发式实现：检查关键词覆盖率和长度合理性
    /// </summary>
    private static async Task<(bool Passed, string Reason)> VerifyConsistencyAsync(string prompt, string response, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(response))
            return (false, "Empty response");

        // 1. 提取 prompt 中的关键实体/名词 (简单启发)
        var promptWords = prompt.Split(new[] { ' ', '\n', '\t', ',', '.', '，', '。' }, StringSplitOptions.RemoveEmptyEntries)
                                .Where(w => w.Length > 2)
                                .Select(w => w.ToLowerInvariant())
                                .ToList();
        
        if (promptWords.Count == 0) return (true, "No key terms to verify");

        // 2. 检查关键词在 response 中的覆盖率
        var responseLower = response.ToLowerInvariant();
        var matchedCount = promptWords.Count(w => responseLower.Contains(w));
        var coverage = (float)matchedCount / promptWords.Count;

        if (coverage < 0.3f)
            return (false, $"Low keyword coverage ({coverage:P0})");

        // 3. 长度合理性检查 (避免过短回答)
        if (response.Length < prompt.Length * 0.2f)
            return (false, "Response too short compared to prompt");

        // TODO: 集成 BinaryVector 余弦相似度检查
        // var similarity = BinaryVector.Similarity(promptEmbedding, responseEmbedding);
        // if (similarity < 0.6f) return (false, $"Low semantic similarity ({similarity:P0})");

        return (true, "Verification passed");
    }
}

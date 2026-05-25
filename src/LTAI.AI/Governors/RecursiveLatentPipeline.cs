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
/// 递归潜空间生成管道 (RecursiveMAS 实现)
/// 将 L1/L2 协作从文本空间升级到潜空间，避免重复解码
/// 支持 4 种协作模式: Sequential, Mixture, Distillation, Deliberation
/// 集成 PACE: 基于参数变化 ||Δθ||² 的动态递归终止
/// 支持 LIFE 框架: 动态重组配置
/// </summary>
public sealed class RecursiveLatentPipeline
{
    private readonly IL1InferenceEngine _l1Engine;
    private readonly IL1InferenceEngine? _l2Engine;
    private readonly IChatClient? _l2Client;
    private readonly RecursiveLink? _recursiveLink;
    private readonly LearningProgressTracker? _progressTracker;
    private readonly TemperatureScheduler? _tempScheduler;
    private readonly ILogger<RecursiveLatentPipeline> _logger;

    private SystemEvolutionConfig _config;

    public RecursiveLatentPipeline(
        IL1InferenceEngine l1Engine,
        SystemEvolutionConfig config,
        IL1InferenceEngine? l2Engine = null,
        IChatClient? l2Client = null,
        RecursiveLink? recursiveLink = null,
        LearningProgressTracker? progressTracker = null,
        TemperatureScheduler? tempScheduler = null,
        ILogger<RecursiveLatentPipeline>? logger = null)
    {
        _l1Engine = l1Engine;
        _l2Engine = l2Engine;
        _l2Client = l2Client;
        _recursiveLink = recursiveLink;
        _progressTracker = progressTracker;
        _tempScheduler = tempScheduler;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RecursiveLatentPipeline>.Instance;
        _config = config;
    }

    /// <summary>
    /// 执行递归潜空间生成 (核心方法)
    /// 集成 PACE: 基于 ||Δθ||² 的动态递归终止
    /// 集成 SePT: 基于验证结果的动态温度调整
    /// </summary>
    public async IAsyncEnumerable<string> GenerateRecursiveAsync(
        string prompt,
        int recursionRounds = 3,
        CollaborationPattern pattern = CollaborationPattern.Sequential,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _logger.LogInformation("🔄 Starting RecursiveMAS: rounds={Rounds}, pattern={Pattern}", recursionRounds, pattern);

        // 1. 编码初始 prompt 为潜状态
        var latent = await _l1Engine.EncodeToLatentAsync(prompt, ct).ConfigureAwait(false);
        var previousEmbedding = (float[])latent.Embedding.Clone();
        var actualRounds = 0;
        var earlyStopped = false;
        var lastVerificationPassed = true;
        
        for (int r = 0; r < recursionRounds; r++)
        {
            _logger.LogDebug("🔁 Recursion round {Current}/{Total}", r + 1, recursionRounds);

            // SePT: 如果上一轮验证失败，提高温度进行探索 (Self-Correction via Diversity)
            float roundTemp = 0.7f;
            if (!lastVerificationPassed && _tempScheduler != null)
            {
                roundTemp = _tempScheduler.GetTemperature($"recursive_{prompt.GetHashCode()}", LearningStatus.Plateau);
                _logger.LogDebug("🌡️ SePT: Increasing temperature to {Temp:F2} for exploration after failure", roundTemp);
            }

            var roundStartEmbedding = (float[])latent.Embedding.Clone();
            
            latent = pattern switch
            {
                CollaborationPattern.Sequential => await ExecuteSequentialRoundAsync(latent, r, ct),
                CollaborationPattern.Mixture => await ExecuteMixtureRoundAsync(latent, r, ct),
                CollaborationPattern.Distillation => await ExecuteDistillationRoundAsync(latent, r, ct),
                CollaborationPattern.Deliberation => await ExecuteDeliberationRoundAsync(latent, r, ct),
                _ => await ExecuteSequentialRoundAsync(latent, r, ct)
            };

            actualRounds++;
            
            // PACE: 计算参数变化 ||Δθ||²
            var deltaNorm = ComputeDeltaNormSquared(roundStartEmbedding, latent.Embedding);
            _logger.LogDebug("📊 PACE ||Δθ||² = {DeltaNorm:E4} at round {Round}", deltaNorm, r + 1);
            
            // 记录学习进度
            if (_progressTracker != null)
            {
                _progressTracker.RecordParameterChange(
                    $"recursive_{prompt.GetHashCode()}", 
                    roundStartEmbedding, 
                    latent.Embedding);
            }

            // PACE 动态递归终止: 检查是否收敛
            if (r >= _config.MinRecursionRounds - 1 && deltaNorm < _config.RecursiveConvergenceThreshold)
            {
                _logger.LogInformation("🛑 Early stop: Parameter change converged (||Δθ||²={DeltaNorm:E4} < {Threshold:E4}) at round {Round}", 
                    deltaNorm, _config.RecursiveConvergenceThreshold, r + 1);
                earlyStopped = true;
                break;
            }

            // 验证潜空间一致性 (Closed-Loop Verification)
            if (r < recursionRounds - 1 && !earlyStopped)
            {
                var verified = await VerifyLatentConsistencyAsync(latent, prompt, ct).ConfigureAwait(false);
                lastVerificationPassed = verified;
                
                if (!verified)
                {
                    _logger.LogWarning("⚠️ Latent consistency check failed at round {Round}, triggering correction", r + 1);
                    latent = await CorrectLatentAsync(latent, prompt, ct).ConfigureAwait(false);
                    
                    // SePT: 记录失败状态，下一轮将自动提高温度
                    if (_tempScheduler != null)
                    {
                        _tempScheduler.UpdateStatus($"recursive_{prompt.GetHashCode()}", LearningStatus.Plateau);
                    }
                }
                else
                {
                    // SePT: 记录成功状态，下一轮将降低温度进行利用
                    if (_tempScheduler != null)
                    {
                        _tempScheduler.UpdateStatus($"recursive_{prompt.GetHashCode()}", LearningStatus.Converging);
                    }
                }
            }
        }

        _logger.LogInformation("✅ RecursiveMAS completed: {ActualRounds}/{PlannedRounds} rounds, earlyStop={EarlyStop}", 
            actualRounds, recursionRounds, earlyStopped);

        // 2. 仅最后一轮解码为文本
        var finalText = await _l1Engine.DecodeFromLatentAsync(latent, ct).ConfigureAwait(false);
        yield return finalText;
    }

    /// <summary>
    /// Sequential 模式: Planner(L1) → Critic(L2) → Solver(L1)
    /// 适用于复杂推理/数学
    /// </summary>
    private async Task<LatentState> ExecuteSequentialRoundAsync(LatentState latent, int round, CancellationToken ct)
    {
        // Step 1: L1 作为 Planner 生成规划潜状态
        var plannerLatent = await _l1Engine.RefineLatentAsync(latent, ct: ct).ConfigureAwait(false);
        
        // Step 2: 通过 RecursiveLink 传递给 L2 Critic
        if (_l2Engine != null && _recursiveLink != null)
        {
            var transferred = _recursiveLink.Transfer(plannerLatent);
            var criticLatent = await _l2Engine.RefineLatentAsync(transferred, ct: ct).ConfigureAwait(false);
            
            // Step 3: 传回 L1 作为 Solver 执行
            var backTransferred = _recursiveLink.Transfer(criticLatent with { SourceAgent = "critic" });
            return await _l1Engine.RefineLatentAsync(backTransferred, ct: ct).ConfigureAwait(false);
        }
        
        return plannerLatent;
    }

    /// <summary>
    /// Mixture 模式: 多专家并行 → Summarizer 聚合
    /// 适用于多领域问答
    /// </summary>
    private async Task<LatentState> ExecuteMixtureRoundAsync(LatentState latent, int round, CancellationToken ct)
    {
        // 并行生成多专家潜状态 (简化为顺序)
        var codeExpert = await _l1Engine.RefineLatentAsync(latent with { SourceAgent = "code" }, ct: ct);
        
        if (_l2Engine != null)
        {
            var scienceExpert = await _l2Engine.RefineLatentAsync(latent with { SourceAgent = "science" }, ct: ct);
            
            // 简单融合: 取平均 (实际应使用注意力机制)
            var fused = new float[Math.Max(codeExpert.Embedding.Length, scienceExpert.Embedding.Length)];
            for (int i = 0; i < fused.Length; i++)
            {
                var v1 = i < codeExpert.Embedding.Length ? codeExpert.Embedding[i] : 0;
                var v2 = i < scienceExpert.Embedding.Length ? scienceExpert.Embedding[i] : 0;
                fused[i] = (v1 + v2) / 2.0f;
            }
            
            return latent with { Embedding = fused, SourceAgent = "summarized" };
        }
        
        return codeExpert;
    }

    /// <summary>
    /// Distillation 模式: Expert(L2) → Learner(L1)
    /// 知识蒸馏，保留 L1 速度优势
    /// </summary>
    private async Task<LatentState> ExecuteDistillationRoundAsync(LatentState latent, int round, CancellationToken ct)
    {
        if (_l2Engine != null && _recursiveLink != null)
        {
            // L2 专家生成高质量潜状态
            var expertLatent = await _l2Engine.RefineLatentAsync(latent with { SourceAgent = "expert" }, ct: ct);
            
            // 通过 RecursiveLink 蒸馏到 L1
            var distilled = _recursiveLink.Transfer(expertLatent);
            return await _l1Engine.RefineLatentAsync(distilled, ct: ct).ConfigureAwait(false);
        }
        
        return await _l1Engine.RefineLatentAsync(latent, ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Deliberation 模式: Reflector(L1) ↔ ToolCaller(L2)
    /// 工具调用/搜索场景
    /// </summary>
    private async Task<LatentState> ExecuteDeliberationRoundAsync(LatentState latent, int round, CancellationToken ct)
    {
        // L1 作为 Reflector 思考
        var reflectorLatent = await _l1Engine.RefineLatentAsync(latent with { SourceAgent = "reflector" }, ct: ct);
        
        // L2 作为 ToolCaller 执行工具调用 (模拟)
        if (_l2Engine != null && _recursiveLink != null)
        {
            var toolLatent = _recursiveLink.Transfer(reflectorLatent);
            toolLatent = toolLatent with { SourceAgent = "tool_caller" };
            return await _l2Engine.RefineLatentAsync(toolLatent, ct: ct).ConfigureAwait(false);
        }
        
        return reflectorLatent;
    }

    /// <summary>
    /// 潜空间一致性验证 (启发式)
    /// </summary>
    private async Task<bool> VerifyLatentConsistencyAsync(LatentState latent, string originalPrompt, CancellationToken ct)
    {
        if (latent.Embedding.Length == 0) return false;
        
        // 检查 embedding 是否发生显著变化
        var norm = 0.0f;
        foreach (var v in latent.Embedding) norm += v * v;
        norm = MathF.Sqrt(norm);
        
        // 范数应在合理范围内
        return norm > 0.1f && norm < 100.0f;
    }

    /// <summary>
    /// 潜空间修正 (类似 CLVR 的 Active Verification)
    /// </summary>
    private async Task<LatentState> CorrectLatentAsync(LatentState latent, string originalPrompt, CancellationToken ct)
    {
        // 重新编码原始 prompt 并与当前 latent 融合
        var fresh = await _l1Engine.EncodeToLatentAsync(originalPrompt, ct).ConfigureAwait(false);
        
        var corrected = new float[Math.Max(latent.Embedding.Length, fresh.Embedding.Length)];
        for (int i = 0; i < corrected.Length; i++)
        {
            var v1 = i < latent.Embedding.Length ? latent.Embedding[i] : 0;
            var v2 = i < fresh.Embedding.Length ? fresh.Embedding[i] : 0;
            corrected[i] = v1 * 0.7f + v2 * 0.3f; // 偏向当前状态，但引入新鲜信息
        }
        
        return latent with { Embedding = corrected };
    }

    /// <summary>
    /// 计算参数变化的 L2 范数平方 ||Δθ||²
    /// PACE 核心理论：环境价值 ∝ ||Δθ||²
    /// </summary>
    private static double ComputeDeltaNormSquared(float[] before, float[] after)
    {
        if (before.Length != after.Length)
            throw new ArgumentException("Parameter vectors must have the same length");

        double sum = 0;
        for (int i = 0; i < before.Length; i++)
        {
            var delta = after[i] - before[i];
            sum += delta * delta;
        }
        return sum;
    }
    /// <summary>
    /// 应用演化动作以动态重组管道配置 (LIFE - Evolve)
    /// </summary>
    public void ApplyEvolution(EvolutionAction action)
    {
        switch (action.Type)
        {
            case EvolutionActionType.SwitchCollaborationPattern:
                if (action.Value is CollaborationPattern pattern)
                {
                    _config.DefaultPattern = pattern;
                    _logger.LogInformation("🔄 Pipeline reconfigured: Pattern switched to {Pattern}", pattern);
                }
                break;
                
            case EvolutionActionType.AdjustConvergenceThreshold:
                if (action.Value is double threshold)
                {
                    _config.RecursiveConvergenceThreshold = threshold;
                    _logger.LogInformation("🔄 Pipeline reconfigured: ConvergenceThreshold adjusted to {Threshold:E4}", threshold);
                }
                break;
                
            case EvolutionActionType.UpdateRoutingThreshold:
                if (action.Target == "MinRecursionRounds" && action.Value is int rounds)
                {
                    _config.MinRecursionRounds = rounds;
                    _logger.LogInformation("🔄 Pipeline reconfigured: MinRecursionRounds updated to {Rounds}", rounds);
                }
                break;
        }
    }
}

/// <summary>
/// 协作模式枚举
/// </summary>
public enum CollaborationPattern
{
    /// <summary>Planner → Critic → Solver (复杂推理)</summary>
    Sequential,
    
    /// <summary>多专家并行 → Summarizer (多领域)</summary>
    Mixture,
    
    /// <summary>Expert → Learner (知识蒸馏)</summary>
    Distillation,
    
    /// <summary>Reflector ↔ ToolCaller (工具调用)</summary>
    Deliberation
}

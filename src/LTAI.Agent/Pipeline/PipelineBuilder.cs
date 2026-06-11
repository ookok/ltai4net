// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  PipelineBuilder — fluent pipeline composition (DEPRECATED)
//
//  ⚠ This class is NOT used by the current ChatAgent or ExecutionEngine.
//    It remains for reference only. See ExecutionEngine + IExecutionEngine
//    for the active step execution framework.
// ═══════════════════════════════════════════════════════════════

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Pipeline;

/// <summary>
/// Builds a message processing pipeline by composing IPipelineStep instances.
/// Steps execute in registration order. Each step's output feeds the next.
/// </summary>
public sealed class PipelineBuilder
{
    private readonly List<StepRegistration> _steps = [];
    private ILogger _logger = NullLogger.Instance;

    /// <summary>
    /// Register a pipeline step by type. The step is instantiated via DI
    /// (ActivatorUtilities) when the pipeline is built.
    /// </summary>
    public PipelineBuilder Use<T>() where T : IPipelineStep
    {
        _steps.Add(new StepRegistration(typeof(T), null));
        return this;
    }

    /// <summary>
    /// Register a pipeline step by type with inline configuration.
    /// The step is instantiated via DI with the provided arguments.
    /// </summary>
    public PipelineBuilder Use<T>(params object[] args) where T : IPipelineStep
    {
        _steps.Add(new StepRegistration(typeof(T), args));
        return this;
    }

    /// <summary>
    /// Register a pre-constructed step instance.
    /// </summary>
    public PipelineBuilder Use(IPipelineStep step)
    {
        _steps.Add(new StepRegistration(step.GetType(), step));
        return this;
    }

    /// <summary>
    /// Conditionally add steps. The <paramref name="configure"/> builder
    /// is only executed when <paramref name="condition"/> is true.
    /// </summary>
    public PipelineBuilder When(bool condition, Func<PipelineBuilder, PipelineBuilder> configure)
    {
        if (condition)
            configure(this);
        return this;
    }

    /// <summary>
    /// Set a logger for pipeline diagnostics.
    /// </summary>
    public PipelineBuilder WithLogger(ILogger logger)
    {
        _logger = logger;
        return this;
    }

    /// <summary>
    /// Build the pipeline. Returns an <see cref="IPipeline"/> that
    /// executes all registered steps in order.
    /// </summary>
    public IPipeline Build()
    {
        var steps = new List<IPipelineStep>(_steps.Count);

        // 始终在最前面插入 GrammarCheckStep（如果未显式注册）
        // 确保代码生成后立即进行语法检查，将问题发现前置到「生成时」
        if (!_steps.Any(s => s.Type == typeof(LTAI.Agent.Pipeline.Steps.GrammarCheckStep)))
        {
            _steps.Insert(0, new StepRegistration(typeof(LTAI.Agent.Pipeline.Steps.GrammarCheckStep), null));
        }

        foreach (var reg in _steps)
        {
            if (reg.Instance is IPipelineStep instance)
            {
                steps.Add(instance);
            }
            else
            {
                // TODO: use DI container to resolve
                var step = Activator.CreateInstance(reg.Type) as IPipelineStep;
                if (step == null)
                    throw new InvalidOperationException(
                        $"Cannot instantiate pipeline step '{reg.Type.Name}'. " +
                        "Ensure it has a parameterless constructor or use the instance overload.");
                steps.Add(step);
            }
        }
        return new Pipeline(_logger, steps);
    }

    private sealed record StepRegistration(Type Type, object? Instance);
}

/// <summary>
/// Compiled pipeline. Executes steps in order.
/// Thread-safe after construction (steps are immutable).
/// </summary>
internal sealed class Pipeline : IPipeline
{
    private readonly ILogger _logger;
    private readonly IReadOnlyList<IPipelineStep> _steps;

    public Pipeline(ILogger logger, IReadOnlyList<IPipelineStep> steps)
    {
        _logger = logger;
        _steps = steps;
    }

    public async Task<MessageContext> ProcessAsync(MessageContext context)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        for (int i = 0; i < _steps.Count; i++)
        {
            var step = _steps[i];
            var stepSw = System.Diagnostics.Stopwatch.StartNew();

            if (context.CancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Pipeline cancelled at step {Index} ({Step})", i, step.Name);
                break;
            }

            try
            {
                _logger.LogDebug("Pipeline step {Index}/{Count}: {Step}", i + 1, _steps.Count, step.Name);
                context = await step.ProcessAsync(context).ConfigureAwait(false);

                stepSw.Stop();
                _logger.LogDebug("Pipeline step {Step} completed in {Ms:F1}ms", step.Name, stepSw.Elapsed.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                stepSw.Stop();
                _logger.LogError(ex, "Pipeline step {Step} failed after {Ms:F1}ms", step.Name, stepSw.Elapsed.TotalMilliseconds);
                throw new PipelineStepException(step.Name, ex);
            }
        }

        sw.Stop();
        _logger.LogInformation("Pipeline completed in {Ms:F1}ms ({StepCount} steps)", sw.Elapsed.TotalMilliseconds, _steps.Count);
        return context;
    }

    public IReadOnlyList<IPipelineStep> Steps => _steps;
}

/// <summary>
/// Pipeline interface (public API for built pipelines).
/// </summary>
public interface IPipeline
{
    /// <summary>Process a message context through all registered steps.</summary>
    Task<MessageContext> ProcessAsync(MessageContext context);

    /// <summary>Read-only list of registered steps.</summary>
    IReadOnlyList<IPipelineStep> Steps { get; }
}

/// <summary>
/// Exception thrown when a pipeline step fails.
/// </summary>
public sealed class PipelineStepException : Exception
{
    public string StepName { get; }

    public PipelineStepException(string stepName, Exception inner)
        : base($"Pipeline step '{stepName}' failed: {inner.Message}", inner)
    {
        StepName = stepName;
    }
}

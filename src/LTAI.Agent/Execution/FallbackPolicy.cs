// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  FallbackPolicy — failure strategy for IExecutionEngine
//
//  Phase 2a: defines what happens when a step or the entire plan
//  fails. Supports circuit breaker, graceful degradation, and
//  fallback to simpler routing.
// ═══════════════════════════════════════════════════════════════

namespace LTAI.Agent.Execution;

/// <summary>
/// Failure strategy for step execution. Used by IExecutionEngine
/// to determine behavior after consecutive failures.
/// </summary>
public enum FallbackPolicy
{
    /// <summary>Do not retry; fail fast.</summary>
    FailFast,

    /// <summary>Retry the step up to MaxRetries times.</summary>
    Retry,

    /// <summary>
    /// Circuit breaker: after N consecutive failures in a window,
    /// route all traffic to a fallback handler for a cooldown period.
    /// </summary>
    CircuitBreaker,

    /// <summary>
    /// Simple fallback: use a simpler/default routing path instead
    /// of the preferred one (e.g. fall back to all specialists
    /// instead of top-K confident routing).
    /// </summary>
    SimpleFallback,
}

/// <summary>
/// Circuit breaker state for a single step or agent.
/// Thread-safe via lock.
/// </summary>
public sealed class CircuitBreakerState
{
    private readonly object _lock = new();
    private int _consecutiveFailures;
    private DateTime _lastFailureTime;
    private bool _isOpen;

    /// <summary>Maximum consecutive failures before opening the circuit.</summary>
    public int Threshold { get; }

    /// <summary>Cooldown period before allowing a retry.</summary>
    public TimeSpan Cooldown { get; }

    /// <summary>Number of consecutive failures remaining.</summary>
    public int ConsecutiveFailures
    {
        get { lock (_lock) return _consecutiveFailures; }
    }

    /// <summary>Is the circuit currently open?</summary>
    public bool IsOpen => TryGetIsOpen();

    public CircuitBreakerState(int threshold = 3, TimeSpan? cooldown = null)
    {
        Threshold = threshold;
        Cooldown = cooldown ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Record a success — resets the failure counter.
    /// </summary>
    public void RecordSuccess()
    {
        lock (_lock)
        {
            _consecutiveFailures = 0;
            _isOpen = false;
        }
    }

    /// <summary>
    /// Record a failure — increments the counter and potentially opens
    /// the circuit.
    /// </summary>
    /// <returns>True if the circuit is now open.</returns>
    public bool RecordFailure()
    {
        lock (_lock)
        {
            _consecutiveFailures++;
            _lastFailureTime = DateTime.UtcNow;
            if (_consecutiveFailures >= Threshold)
            {
                _isOpen = true;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Request permission to execute. Returns true if the circuit is
    /// closed (allow execution) or if the cooldown has expired and we
    /// transition to half-open.
    /// </summary>
    public bool TryAcquire()
    {
        lock (_lock)
        {
            if (!_isOpen) return true;

            // Check if cooldown has expired
            if (DateTime.UtcNow - _lastFailureTime >= Cooldown)
            {
                _isOpen = false; // half-open → allow trial
                return true;
            }

            return false;
        }
    }

    private bool TryGetIsOpen()
    {
        lock (_lock)
        {
            if (!_isOpen) return false;
            // Auto-recover after cooldown
            if (DateTime.UtcNow - _lastFailureTime >= Cooldown)
            {
                _isOpen = false;
                return false;
            }
            return true;
        }
    }
}

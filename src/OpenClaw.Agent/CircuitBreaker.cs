using System.Threading;
using Microsoft.Extensions.Logging;

namespace OpenClaw.Agent;

/// <summary>
/// Lightweight circuit breaker for LLM calls (or any external service).
/// No external dependency — custom state machine.
///
/// States:
///   Closed  → requests flow through; consecutive failures tracked
///   Open    → requests short-circuited with error; transitions to HalfOpen after cooldown
///   HalfOpen → one probe request allowed; success closes, failure re-opens
/// </summary>
public sealed class CircuitBreaker
{
    private readonly int _failureThreshold;
    private readonly TimeSpan _cooldown;
    private readonly ILogger? _logger;

    private int _consecutiveFailures;
    private DateTimeOffset _openedAt;
    private int _state = (int)CircuitState.Closed;
    private readonly object _lock = new();

    public CircuitBreaker(int failureThreshold = 5, TimeSpan? cooldown = null, ILogger? logger = null)
    {
        _failureThreshold = Math.Max(1, failureThreshold);
        _cooldown = cooldown ?? TimeSpan.FromSeconds(30);
        _logger = logger;
    }

    public CircuitState State
    {
        get => (CircuitState)Volatile.Read(ref _state);
    }

    /// <summary>
    /// Throws <see cref="CircuitOpenException"/> if the circuit is currently open.
    /// Used for streaming paths where wrapping in ExecuteAsync is impractical.
    /// </summary>
    public void ThrowIfOpen()
    {
        if ((CircuitState)Volatile.Read(ref _state) != CircuitState.Open)
            return;

        lock (_lock)
        {
            if ((CircuitState)_state == CircuitState.Open)
            {
                if (DateTimeOffset.UtcNow - _openedAt >= _cooldown)
                {
                    _state = (int)CircuitState.HalfOpen;
                    _logger?.LogInformation("Circuit breaker transitioning to HalfOpen");
                }
                else
                {
                    throw new CircuitOpenException(
                        "I'm temporarily unavailable. Please try again shortly.",
                        _openedAt + _cooldown - DateTimeOffset.UtcNow);
                }
            }
        }
    }

    /// <summary>
    /// Execute <paramref name="action"/> through the circuit breaker.
    /// Throws <see cref="CircuitOpenException"/> if the circuit is open.
    /// </summary>
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        if ((CircuitState)Volatile.Read(ref _state) != CircuitState.Closed)
        {
            lock (_lock)
            {
                switch ((CircuitState)_state)
                {
                    case CircuitState.Open:
                        if (DateTimeOffset.UtcNow - _openedAt >= _cooldown)
                        {
                            _state = (int)CircuitState.HalfOpen;
                            _logger?.LogInformation("Circuit breaker transitioning to HalfOpen");
                        }
                        else
                        {
                            throw new CircuitOpenException(
                                "I'm temporarily unavailable. Please try again shortly.",
                                _openedAt + _cooldown - DateTimeOffset.UtcNow);
                        }
                        break;

                    case CircuitState.HalfOpen:
                        // Allow the probe request through
                        break;

                    case CircuitState.Closed:
                        // Normal operation
                        break;
                }
            }
        }

        try
        {
            var result = await action(ct);
            OnSuccess();
            return result;
        }
        catch (OperationCanceledException)
        {
            // Cancellation is not a service failure — don't count it
            throw;
        }
        catch
        {
            OnFailure();
            throw;
        }
    }

    private void OnSuccess()
    {
        if ((CircuitState)Volatile.Read(ref _state) == CircuitState.Closed &&
            Volatile.Read(ref _consecutiveFailures) == 0)
        {
            return;
        }

        lock (_lock)
        {
            if ((CircuitState)_state == CircuitState.HalfOpen)
                _logger?.LogInformation("Circuit breaker closing (probe succeeded)");

            _consecutiveFailures = 0;
            _state = (int)CircuitState.Closed;
        }
    }

    private void OnFailure()
    {
        lock (_lock)
        {
            _consecutiveFailures++;

            var state = (CircuitState)_state;
            if (state == CircuitState.HalfOpen ||
                (state == CircuitState.Closed && _consecutiveFailures >= _failureThreshold))
            {
                _state = (int)CircuitState.Open;
                _openedAt = DateTimeOffset.UtcNow;
                _logger?.LogWarning(
                    "Circuit breaker opened after {Failures} consecutive failures. " +
                    "Will retry after {Cooldown}s.",
                    _consecutiveFailures, _cooldown.TotalSeconds);
            }
        }
    }
}

public enum CircuitState
{
    Closed,
    Open,
    HalfOpen
}

/// <summary>
/// Thrown when the circuit breaker is open and the request is short-circuited.
/// </summary>
public sealed class CircuitOpenException : Exception
{
    public TimeSpan RetryAfter { get; }

    public CircuitOpenException(string message, TimeSpan retryAfter)
        : base(message) => RetryAfter = retryAfter;
}

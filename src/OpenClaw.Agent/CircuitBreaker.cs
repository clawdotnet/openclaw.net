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
    private readonly TimeSpan _baseCooldown;
    private readonly ILogger? _logger;
    private static readonly TimeSpan MaxCooldown = TimeSpan.FromMinutes(5);

    private int _consecutiveFailures;
    private int _probeFailures;
    private DateTimeOffset _openedAt;
    private TimeSpan _currentCooldown;
    private int _state = (int)CircuitState.Closed;
    private readonly object _lock = new();

    public CircuitBreaker(int failureThreshold = 5, TimeSpan? cooldown = null, ILogger? logger = null)
    {
        _failureThreshold = Math.Max(1, failureThreshold);
        _baseCooldown = cooldown ?? TimeSpan.FromSeconds(30);
        _currentCooldown = _baseCooldown;
        _logger = logger;
    }

    public CircuitState State
    {
        get => (CircuitState)Volatile.Read(ref _state);
    }

    public void RecordSuccess() => OnSuccess();

    public void RecordFailure() => OnFailure();

    public void Reset()
    {
        lock (_lock)
        {
            _consecutiveFailures = 0;
            _probeFailures = 0;
            _currentCooldown = _baseCooldown;
            _state = (int)CircuitState.Closed;
        }
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
                if (DateTimeOffset.UtcNow - _openedAt >= _currentCooldown)
                {
                    _state = (int)CircuitState.HalfOpen;
                    _logger?.LogInformation("Circuit breaker transitioning to HalfOpen");
                }
                else
                {
                    throw new CircuitOpenException(
                        "I'm temporarily unavailable. Please try again shortly.",
                        _openedAt + _currentCooldown - DateTimeOffset.UtcNow);
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
                        if (DateTimeOffset.UtcNow - _openedAt >= _currentCooldown)
                        {
                            _state = (int)CircuitState.HalfOpen;
                            _logger?.LogInformation("Circuit breaker transitioning to HalfOpen");
                        }
                        else
                        {
                            throw new CircuitOpenException(
                                "I'm temporarily unavailable. Please try again shortly.",
                                _openedAt + _currentCooldown - DateTimeOffset.UtcNow);
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
            _probeFailures = 0;
            _currentCooldown = _baseCooldown;
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
                if (state == CircuitState.HalfOpen)
                {
                    _probeFailures++;
                    _currentCooldown = ComputeBackoffCooldown(_probeFailures);
                }
                else
                {
                    _probeFailures = 0;
                    _currentCooldown = _baseCooldown;
                }

                _state = (int)CircuitState.Open;
                _openedAt = DateTimeOffset.UtcNow;
                _logger?.LogWarning(
                    "Circuit breaker opened after {Failures} consecutive failures. " +
                    "Will retry after {Cooldown}s.",
                    _consecutiveFailures, _currentCooldown.TotalSeconds);
            }
        }
    }

    private TimeSpan ComputeBackoffCooldown(int probeFailures)
    {
        var multiplier = Math.Pow(2, Math.Max(1, probeFailures));
        var ticks = Math.Min(_baseCooldown.Ticks * multiplier, MaxCooldown.Ticks);
        return TimeSpan.FromTicks((long)ticks);
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

using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace UrlShortener.Application.Resilience;

// ════════════════════════════════════════════════════════════════════════════
// REDIS-BACKED DISTRIBUTED CIRCUIT BREAKER
// ════════════════════════════════════════════════════════════════════════════
//
// States:
//   CLOSED    → normal operation; requests pass through; failure counter increments
//   OPEN      → fast-fail; all requests immediately return CircuitOpenException;
//               no requests reach the protected operation
//   HALF-OPEN → one probe request allowed; success → CLOSED; failure → OPEN
//
// Why Redis for state?
//   In-process circuit breakers are per-instance. With 3 API instances:
//   - Instance 1 sees 5 failures → opens ITS circuit
//   - Instances 2 and 3 still route to the failing service
//   - Clients get inconsistent behaviour depending on which instance handles them
//
//   Redis-shared state means all instances share the same view. When the circuit
//   opens, ALL instances stop sending requests within milliseconds.
//
// Redis key pattern:
//   cb:{policyName}:state    — "closed" | "open" | "half-open"
//   cb:{policyName}:failures — failure count (INCR with TTL)
//   cb:{policyName}:opened_at — timestamp of OPEN transition
//
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Distributed circuit breaker backed by Redis.
/// Thread-safe and process-safe — state is shared across all instances.
/// </summary>
public interface ICircuitBreaker
{
    /// <summary>
    /// Executes the operation through the circuit breaker.
    /// Throws CircuitOpenException if the circuit is OPEN.
    /// </summary>
    Task<T> ExecuteAsync<T>(
        string operationName,
        Func<Task<T>> operation,
        Func<T>? fallback = null,
        CancellationToken ct = default);

    Task ExecuteAsync(
        string operationName,
        Func<Task> operation,
        Action? fallback = null,
        CancellationToken ct = default);

    Task<CircuitBreakerState> GetStateAsync(CancellationToken ct = default);
}

/// <summary>
/// Current circuit breaker state with metadata.
/// </summary>
public record CircuitBreakerState
{
    public CircuitStatus Status         { get; init; }
    public int FailureCount             { get; init; }
    public DateTimeOffset? OpenedAt     { get; init; }
    public TimeSpan? TimeUntilHalfOpen  { get; init; }
}

public enum CircuitStatus { Closed, Open, HalfOpen }

public class RedisCircuitBreaker : ICircuitBreaker
{
    private readonly IDatabase _redis;
    private readonly ILogger<RedisCircuitBreaker> _logger;
    private readonly string _policyName;
    private readonly int _failureThreshold;
    private readonly TimeSpan _failureWindow;
    private readonly TimeSpan _cooldownPeriod;
    private readonly TimeSpan _halfOpenTimeout;

    // Redis key patterns
    private string StateKey       => $"cb:{_policyName}:state";
    private string FailureKey     => $"cb:{_policyName}:failures";
    private string OpenedAtKey    => $"cb:{_policyName}:opened_at";
    private string HalfOpenLock   => $"cb:{_policyName}:half_open_lock";

    /// <param name="policyName">Unique name for this circuit (e.g., "postgresql", "payment-processor").</param>
    /// <param name="failureThreshold">Number of failures within failureWindow before opening.</param>
    /// <param name="cooldownSeconds">Seconds to stay OPEN before allowing a probe (HALF-OPEN).</param>
    /// <param name="failureWindowSeconds">Rolling window for counting failures.</param>
    public RedisCircuitBreaker(
        IConnectionMultiplexer redis,
        ILogger<RedisCircuitBreaker> logger,
        string policyName,
        int failureThreshold  = 5,
        int cooldownSeconds   = 30,
        int failureWindowSeconds = 30)
    {
        _redis            = redis.GetDatabase();
        _logger           = logger;
        _policyName       = policyName;
        _failureThreshold = failureThreshold;
        _failureWindow    = TimeSpan.FromSeconds(failureWindowSeconds);
        _cooldownPeriod   = TimeSpan.FromSeconds(cooldownSeconds);
        _halfOpenTimeout  = TimeSpan.FromSeconds(5);
    }

    // ── Core execution ────────────────────────────────────────────────────────

    public async Task<T> ExecuteAsync<T>(
        string operationName,
        Func<Task<T>> operation,
        Func<T>? fallback = null,
        CancellationToken ct = default)
    {
        var state = await GetStateAsync(ct);

        switch (state.Status)
        {
            case CircuitStatus.Open:
                _logger.LogWarning(
                    "Circuit {Policy} is OPEN — fast-failing operation {Operation}",
                    _policyName, operationName);

                if (fallback is not null) return fallback();
                throw new CircuitOpenException(_policyName, state.TimeUntilHalfOpen);

            case CircuitStatus.HalfOpen:
                // Only one probe request at a time — use Redis lock
                var lockAcquired = await _redis.StringSetAsync(
                    HalfOpenLock, "1", _halfOpenTimeout, When.NotExists);

                if (!lockAcquired)
                {
                    // Another instance is probing — fast fail this request
                    if (fallback is not null) return fallback();
                    throw new CircuitOpenException(_policyName, null);
                }

                _logger.LogInformation(
                    "Circuit {Policy} is HALF-OPEN — sending probe request for {Operation}",
                    _policyName, operationName);
                break;

            case CircuitStatus.Closed:
            default:
                break; // proceed normally
        }

        // Execute the operation
        try
        {
            var result = await operation();
            await OnSuccessAsync(state.Status);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw; // cancellation is not a circuit failure
        }
        catch (CircuitOpenException)
        {
            throw; // don't count circuit-open exceptions as failures
        }
        catch (Exception ex)
        {
            await OnFailureAsync(operationName, ex);

            if (fallback is not null)
            {
                _logger.LogDebug("Circuit {Policy} using fallback for {Operation}", _policyName, operationName);
                return fallback();
            }
            throw;
        }
    }

    public async Task ExecuteAsync(
        string operationName,
        Func<Task> operation,
        Action? fallback = null,
        CancellationToken ct = default)
    {
        await ExecuteAsync<bool>(
            operationName,
            async () => { await operation(); return true; },
            fallback is null ? null : () => { fallback(); return true; },
            ct);
    }

    // ── State management ──────────────────────────────────────────────────────

    public async Task<CircuitBreakerState> GetStateAsync(CancellationToken ct = default)
    {
        try
        {
            var stateValue = await _redis.StringGetAsync(StateKey);

            if (!stateValue.HasValue || stateValue == "closed")
            {
                var failures = (int)(await _redis.StringGetAsync(FailureKey)).TryParse(0);
                return new CircuitBreakerState { Status = CircuitStatus.Closed, FailureCount = failures };
            }

            if (stateValue == "open")
            {
                var openedAtValue = await _redis.StringGetAsync(OpenedAtKey);
                DateTimeOffset? openedAt = openedAtValue.HasValue
                    ? DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(openedAtValue!))
                    : null;

                var cooldownRemaining = openedAt.HasValue
                    ? _cooldownPeriod - (DateTimeOffset.UtcNow - openedAt.Value)
                    : TimeSpan.Zero;

                if (cooldownRemaining <= TimeSpan.Zero)
                {
                    // Cooldown elapsed — transition to HALF-OPEN
                    await _redis.StringSetAsync(StateKey, "half-open", TimeSpan.FromMinutes(5));
                    return new CircuitBreakerState { Status = CircuitStatus.HalfOpen };
                }

                return new CircuitBreakerState
                {
                    Status           = CircuitStatus.Open,
                    OpenedAt         = openedAt,
                    TimeUntilHalfOpen = cooldownRemaining > TimeSpan.Zero ? cooldownRemaining : null
                };
            }

            if (stateValue == "half-open")
                return new CircuitBreakerState { Status = CircuitStatus.HalfOpen };

            return new CircuitBreakerState { Status = CircuitStatus.Closed };
        }
        catch (Exception ex)
        {
            // Redis unavailable — treat as CLOSED (fail open on circuit breaker itself)
            _logger.LogWarning(ex, "Cannot read circuit breaker state from Redis — treating as CLOSED");
            return new CircuitBreakerState { Status = CircuitStatus.Closed };
        }
    }

    private async Task OnSuccessAsync(CircuitStatus previousState)
    {
        try
        {
            if (previousState == CircuitStatus.HalfOpen)
            {
                _logger.LogInformation("Circuit {Policy} probe succeeded — transitioning to CLOSED", _policyName);
                await _redis.KeyDeleteAsync(new RedisKey[] { StateKey, FailureKey, OpenedAtKey, HalfOpenLock });
            }
            else
            {
                // Reset failure count on any success in CLOSED state
                // (optional — depends on policy; some implementations only reset on explicit clear)
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record success in circuit breaker — non-fatal");
        }
    }

    private async Task OnFailureAsync(string operationName, Exception ex)
    {
        try
        {
            _logger.LogWarning(ex,
                "Circuit {Policy} recording failure for {Operation}", _policyName, operationName);

            // Increment failure counter with rolling window TTL
            var count = await _redis.StringIncrementAsync(FailureKey);
            if (count == 1)
                await _redis.KeyExpireAsync(FailureKey, _failureWindow);

            if (count >= _failureThreshold)
            {
                // Transition to OPEN
                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
                await _redis.StringSetAsync(StateKey, "open", _cooldownPeriod + TimeSpan.FromMinutes(1));
                await _redis.StringSetAsync(OpenedAtKey, nowMs, _cooldownPeriod + TimeSpan.FromMinutes(1));
                await _redis.KeyDeleteAsync(HalfOpenLock);

                _logger.LogError(
                    "Circuit {Policy} OPENED after {FailureCount} failures in {Window}s. " +
                    "All requests will fast-fail for {Cooldown}s.",
                    _policyName, count, _failureWindow.TotalSeconds, _cooldownPeriod.TotalSeconds);
            }
        }
        catch (Exception redisEx)
        {
            _logger.LogWarning(redisEx, "Failed to record failure in circuit breaker — non-fatal");
        }
    }
}

// ── Helper extension for RedisValue ──────────────────────────────────────────

file static class RedisValueExtensions
{
    public static int TryParse(this RedisValue value, int defaultValue)
        => value.HasValue && int.TryParse(value.ToString(), out var result) ? result : defaultValue;
}

// ── Exceptions ────────────────────────────────────────────────────────────────

/// <summary>
/// Thrown when a request is rejected because the circuit is OPEN.
/// Callers should return 503 Service Unavailable with Retry-After header.
/// </summary>
public class CircuitOpenException : Exception
{
    public string PolicyName { get; }
    public TimeSpan? RetryAfter { get; }

    public CircuitOpenException(string policyName, TimeSpan? retryAfter)
        : base($"Circuit '{policyName}' is open. Service temporarily unavailable.")
    {
        PolicyName = policyName;
        RetryAfter = retryAfter;
    }
}

// ════════════════════════════════════════════════════════════════════════════
// IN-PROCESS CIRCUIT BREAKER (Fallback when Redis unavailable)
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Simple in-process circuit breaker for use when Redis is unavailable.
/// Not distributed — each instance has independent state.
/// Use only as a fallback for the Redis-backed breaker.
/// </summary>
public class InProcessCircuitBreaker : ICircuitBreaker
{
    private volatile CircuitStatus _status = CircuitStatus.Closed;
    private int _failureCount;
    private DateTimeOffset _openedAt;
    private readonly int _failureThreshold;
    private readonly TimeSpan _cooldown;
    private readonly ILogger<InProcessCircuitBreaker> _logger;
    private readonly string _policyName;
    private readonly SemaphoreSlim _halfOpenLock = new(1, 1);

    public InProcessCircuitBreaker(
        string policyName,
        ILogger<InProcessCircuitBreaker> logger,
        int failureThreshold = 5,
        int cooldownSeconds  = 30)
    {
        _policyName       = policyName;
        _logger           = logger;
        _failureThreshold = failureThreshold;
        _cooldown         = TimeSpan.FromSeconds(cooldownSeconds);
    }

    public async Task<T> ExecuteAsync<T>(
        string operationName,
        Func<Task<T>> operation,
        Func<T>? fallback = null,
        CancellationToken ct = default)
    {
        if (_status == CircuitStatus.Open)
        {
            if (DateTimeOffset.UtcNow - _openedAt >= _cooldown)
                _status = CircuitStatus.HalfOpen;
            else
            {
                if (fallback is not null) return fallback();
                throw new CircuitOpenException(_policyName, _cooldown - (DateTimeOffset.UtcNow - _openedAt));
            }
        }

        try
        {
            var result = await operation();
            _failureCount = 0;
            _status       = CircuitStatus.Closed;
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failureCount);
            if (_failureCount >= _failureThreshold)
            {
                _status   = CircuitStatus.Open;
                _openedAt = DateTimeOffset.UtcNow;
                _logger.LogError(ex, "In-process circuit {Policy} OPENED", _policyName);
            }
            if (fallback is not null) return fallback();
            throw;
        }
    }

    public async Task ExecuteAsync(string operationName, Func<Task> operation, Action? fallback = null, CancellationToken ct = default)
    {
        await ExecuteAsync<bool>(operationName, async () => { await operation(); return true; },
            fallback is null ? null : () => { fallback(); return true; }, ct);
    }

    public Task<CircuitBreakerState> GetStateAsync(CancellationToken ct = default)
        => Task.FromResult(new CircuitBreakerState
        {
            Status       = _status,
            FailureCount = _failureCount,
            OpenedAt     = _status == CircuitStatus.Open ? _openedAt : null
        });
}

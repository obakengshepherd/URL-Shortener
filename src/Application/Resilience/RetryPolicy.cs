using Microsoft.Extensions.Logging;

namespace UrlShortener.Application.Resilience;

// ════════════════════════════════════════════════════════════════════════════
// RETRY POLICY — Exponential Backoff with Jitter
// ════════════════════════════════════════════════════════════════════════════
//
// Design principles:
//
// 1. Exponential backoff: 100ms → 200ms → 400ms → 800ms (power of 2).
//    Gives the failing service progressively more time to recover.
//
// 2. Jitter: add 0–100ms random offset to each delay.
//    Prevents "retry storm" — without jitter, 100 instances that all
//    fail at T=0 will all retry at exactly T=100ms, T=200ms, etc.,
//    creating coordinated thundering herds that can re-saturate a
//    recovering service.
//
// 3. Retryable vs non-retryable exceptions:
//    RETRY:    transient network errors, DB connection failures, 5xx responses
//    NO RETRY: 4xx client errors, business logic exceptions (InsufficientFunds,
//              WalletNotFound), idempotency conflicts, validation errors
//
// 4. Max attempts: 3–5 for infrastructure calls; 1 for idempotency-sensitive
//    operations that already have idempotency keys.
//
// Usage:
//   var policy = new RetryPolicy(maxAttempts: 3, logger);
//   var result = await policy.ExecuteAsync(
//       () => _repo.FindByIdAsync(walletId),
//       isRetryable: ex => ex is NpgsqlException { IsTransient: true });
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Configurable retry policy with exponential backoff and random jitter.
/// Thread-safe — safe to share as a singleton.
/// </summary>
public class RetryPolicy
{
    private readonly int _maxAttempts;
    private readonly TimeSpan _initialDelay;
    private readonly TimeSpan _maxDelay;
    private readonly TimeSpan _jitterMax;
    private readonly ILogger? _logger;

    /// <summary>
    /// Default retry policy: 3 attempts, 100ms initial delay, 100ms jitter.
    /// Suitable for transient infrastructure failures.
    /// </summary>
    public static RetryPolicy Default => new(
        maxAttempts:  3,
        initialDelay: TimeSpan.FromMilliseconds(100),
        maxDelay:     TimeSpan.FromSeconds(2),
        jitterMax:    TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// Aggressive retry policy: 5 attempts, 100ms initial delay.
    /// For critical paths (DB writes) where we want more attempts.
    /// </summary>
    public static RetryPolicy Aggressive => new(
        maxAttempts:  5,
        initialDelay: TimeSpan.FromMilliseconds(100),
        maxDelay:     TimeSpan.FromSeconds(5),
        jitterMax:    TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// No retry — for operations that must not be retried (financial writes
    /// without idempotency keys, idempotency-sensitive operations).
    /// </summary>
    public static RetryPolicy NoRetry => new(
        maxAttempts:  1,
        initialDelay: TimeSpan.Zero,
        maxDelay:     TimeSpan.Zero,
        jitterMax:    TimeSpan.Zero);

    public RetryPolicy(
        int maxAttempts          = 3,
        TimeSpan? initialDelay   = null,
        TimeSpan? maxDelay       = null,
        TimeSpan? jitterMax      = null,
        ILogger? logger          = null)
    {
        if (maxAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), "Must be at least 1.");

        _maxAttempts  = maxAttempts;
        _initialDelay = initialDelay ?? TimeSpan.FromMilliseconds(100);
        _maxDelay     = maxDelay     ?? TimeSpan.FromSeconds(2);
        _jitterMax    = jitterMax    ?? TimeSpan.FromMilliseconds(100);
        _logger       = logger;
    }

    // ── Core execution methods ────────────────────────────────────────────────

    /// <summary>
    /// Executes an async operation with retry. Returns the result on success.
    /// </summary>
    /// <param name="operation">The async operation to execute.</param>
    /// <param name="isRetryable">
    ///   Predicate that determines whether an exception should trigger a retry.
    ///   Defaults to retrying all exceptions except OperationCanceledException.
    ///   Override this to protect non-idempotent operations.
    /// </param>
    /// <param name="cancellationToken">Cancellation token — respected between retries.</param>
    public async Task<T> ExecuteAsync<T>(
        Func<Task<T>> operation,
        Func<Exception, bool>? isRetryable = null,
        CancellationToken cancellationToken = default)
    {
        isRetryable ??= DefaultIsRetryable;

        Exception? lastException = null;

        for (int attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await operation();
            }
            catch (OperationCanceledException)
            {
                throw; // never retry cancellations
            }
            catch (Exception ex) when (isRetryable(ex))
            {
                lastException = ex;

                if (attempt == _maxAttempts)
                    break; // exhausted — fall through to throw

                var delay = CalculateDelay(attempt);

                _logger?.LogWarning(ex,
                    "Attempt {Attempt}/{MaxAttempts} failed — retrying in {DelayMs}ms. Error: {Message}",
                    attempt, _maxAttempts, delay.TotalMilliseconds, ex.Message);

                await Task.Delay(delay, cancellationToken);
            }
            catch (Exception ex)
            {
                // Non-retryable exception — rethrow immediately
                _logger?.LogDebug(ex,
                    "Non-retryable exception on attempt {Attempt} — not retrying",
                    attempt);
                throw;
            }
        }

        _logger?.LogError(lastException,
            "All {MaxAttempts} retry attempts exhausted",
            _maxAttempts);

        throw new RetryExhaustedException(
            $"Operation failed after {_maxAttempts} attempts.", lastException!);
    }

    /// <summary>
    /// Executes an async void operation with retry.
    /// </summary>
    public async Task ExecuteAsync(
        Func<Task> operation,
        Func<Exception, bool>? isRetryable = null,
        CancellationToken cancellationToken = default)
    {
        await ExecuteAsync<bool>(
            async () => { await operation(); return true; },
            isRetryable,
            cancellationToken);
    }

    // ── Delay calculation ─────────────────────────────────────────────────────

    /// <summary>
    /// Calculates delay for a given attempt number using exponential backoff + jitter.
    ///
    /// Attempt 1: 100ms + jitter(0–100ms)
    /// Attempt 2: 200ms + jitter(0–100ms)
    /// Attempt 3: 400ms + jitter(0–100ms)
    /// Attempt 4: 800ms + jitter(0–100ms) — capped at maxDelay
    /// </summary>
    private TimeSpan CalculateDelay(int attemptNumber)
    {
        // Exponential: initialDelay × 2^(attempt - 1)
        var exponential = TimeSpan.FromMilliseconds(
            _initialDelay.TotalMilliseconds * Math.Pow(2, attemptNumber - 1));

        // Cap at maxDelay
        var capped = exponential > _maxDelay ? _maxDelay : exponential;

        // Jitter: add 0 to jitterMax milliseconds
        var jitter = TimeSpan.FromMilliseconds(
            Random.Shared.NextDouble() * _jitterMax.TotalMilliseconds);

        return capped + jitter;
    }

    // ── Default retryable predicate ───────────────────────────────────────────

    /// <summary>
    /// Default retryability check. Retry transient infrastructure failures.
    /// Do NOT retry business logic exceptions.
    /// </summary>
    public static bool DefaultIsRetryable(Exception ex) => ex switch
    {
        // Transient PostgreSQL failures (connection lost, server restart, etc.)
        Npgsql.NpgsqlException { IsTransient: true }  => true,

        // Network-level I/O failures
        System.Net.Http.HttpRequestException          => true,
        System.IO.IOException                         => true,
        TimeoutException                              => true,

        // Socket-level failures
        System.Net.Sockets.SocketException            => true,

        // StackExchange.Redis connection failures
        StackExchange.Redis.RedisConnectionException  => true,
        StackExchange.Redis.RedisTimeoutException     => true,

        // Confluent.Kafka transient failures
        Confluent.Kafka.KafkaException k
            when k.Error.IsTransient                  => true,

        // RabbitMQ transient failures
        RabbitMQ.Client.Exceptions.BrokerUnreachableException => true,
        RabbitMQ.Client.Exceptions.AlreadyClosedException     => true,

        // Anything else — do not retry
        _                                             => false
    };

    /// <summary>
    /// Retryable predicate for read-only DB operations — more permissive.
    /// Reads are always safe to retry since they have no side effects.
    /// </summary>
    public static bool ReadOperationIsRetryable(Exception ex) => ex switch
    {
        Npgsql.NpgsqlException        => true,   // all PostgreSQL exceptions — reads are safe
        TimeoutException              => true,
        System.IO.IOException         => true,
        StackExchange.Redis.RedisException => true,
        _                             => false
    };
}

/// <summary>
/// Thrown when all retry attempts are exhausted.
/// Wraps the last exception as InnerException.
/// </summary>
public class RetryExhaustedException(string message, Exception innerException)
    : Exception(message, innerException);

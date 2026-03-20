using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shared.Application.Resilience;
using StackExchange.Redis;

namespace UrlShortener.Application.Resilience;

// ════════════════════════════════════════════════════════════════════════════
// RESILIENCE EXTENSIONS — DI registration helpers
// ════════════════════════════════════════════════════════════════════════════
//
// Add to Program.cs for each system:
//
//   builder.Services.AddResiliencePolicies(builder.Configuration);
//
// This registers:
//   - RetryPolicy.Default       → for transient read failures
//   - RetryPolicy.Aggressive    → for critical write paths
//   - ICircuitBreaker (postgresql) → for DB connection failures
//   - ICircuitBreaker (redis)    → for cache failures (if desired — usually fail-open instead)
//
// Per-system circuit breakers are registered by name via the keyed services
// API (ASP.NET 8): builder.Services.AddKeyedSingleton<ICircuitBreaker>("kafka", ...)
// ════════════════════════════════════════════════════════════════════════════

public static class ResilienceExtensions
{
    /// <summary>
    /// Registers all resilience policies for a system.
    /// Call in Program.cs after Redis is registered.
    /// </summary>
    public static IServiceCollection AddResiliencePolicies(
        this IServiceCollection services,
        IConfiguration configuration,
        ResilienceOptions? options = null)
    {
        options ??= new ResilienceOptions();

        // ── Retry policies ────────────────────────────────────────────────────

        services.AddSingleton(sp => new RetryPolicy(
            maxAttempts:  options.DefaultMaxAttempts,
            initialDelay: options.DefaultInitialDelay,
            maxDelay:     options.DefaultMaxDelay,
            jitterMax:    options.DefaultJitter,
            logger:       sp.GetService<ILogger<RetryPolicy>>()));

        // Named retry policies via keyed services
        services.AddKeyedSingleton("default", (sp, _) => RetryPolicy.Default);
        services.AddKeyedSingleton("aggressive", (sp, _) => RetryPolicy.Aggressive);
        services.AddKeyedSingleton("no-retry", (sp, _) => RetryPolicy.NoRetry);

        // ── PostgreSQL circuit breaker ────────────────────────────────────────

        services.AddKeyedSingleton<ICircuitBreaker>("postgresql", (sp, _) =>
        {
            try
            {
                var redis  = sp.GetRequiredService<IConnectionMultiplexer>();
                var logger = sp.GetRequiredService<ILogger<RedisCircuitBreaker>>();
                return new RedisCircuitBreaker(
                    redis,
                    logger,
                    policyName:       "postgresql",
                    failureThreshold: options.PostgreSqlFailureThreshold,
                    cooldownSeconds:  options.PostgreSqlCooldownSeconds);
            }
            catch
            {
                // Redis unavailable — fall back to in-process circuit breaker
                var logger = sp.GetRequiredService<ILogger<InProcessCircuitBreaker>>();
                return new InProcessCircuitBreaker(
                    "postgresql", logger,
                    failureThreshold: options.PostgreSqlFailureThreshold,
                    cooldownSeconds:  options.PostgreSqlCooldownSeconds);
            }
        });

        // ── Kafka circuit breaker (for Kafka-using systems) ───────────────────

        services.AddKeyedSingleton<ICircuitBreaker>("kafka", (sp, _) =>
        {
            try
            {
                var redis  = sp.GetRequiredService<IConnectionMultiplexer>();
                var logger = sp.GetRequiredService<ILogger<RedisCircuitBreaker>>();
                return new RedisCircuitBreaker(
                    redis,
                    logger,
                    policyName:       "kafka",
                    failureThreshold: options.KafkaFailureThreshold,
                    cooldownSeconds:  options.KafkaCooldownSeconds);
            }
            catch
            {
                var logger = sp.GetRequiredService<ILogger<InProcessCircuitBreaker>>();
                return new InProcessCircuitBreaker("kafka", logger);
            }
        });

        // ── External service circuit breaker (payment processor, etc.) ────────

        services.AddKeyedSingleton<ICircuitBreaker>("external", (sp, _) =>
        {
            try
            {
                var redis  = sp.GetRequiredService<IConnectionMultiplexer>();
                var logger = sp.GetRequiredService<ILogger<RedisCircuitBreaker>>();
                return new RedisCircuitBreaker(
                    redis,
                    logger,
                    policyName:       "external-service",
                    failureThreshold: options.ExternalFailureThreshold,
                    cooldownSeconds:  options.ExternalCooldownSeconds);
            }
            catch
            {
                var logger = sp.GetRequiredService<ILogger<InProcessCircuitBreaker>>();
                return new InProcessCircuitBreaker("external-service", logger);
            }
        });

        return services;
    }

    /// <summary>
    /// Adds the circuit breaker state endpoint to the API.
    /// GET /health/circuit-breakers → returns state of all registered circuit breakers.
    /// </summary>
    public static WebApplication MapCircuitBreakerEndpoints(this WebApplication app)
    {
        app.MapGet("/health/circuit-breakers", async (
            [Microsoft.AspNetCore.Mvc.FromKeyedServices("postgresql")] ICircuitBreaker pgCb,
            [Microsoft.AspNetCore.Mvc.FromKeyedServices("kafka")]      ICircuitBreaker kafkaCb,
            [Microsoft.AspNetCore.Mvc.FromKeyedServices("external")]   ICircuitBreaker externalCb) =>
        {
            var results = new[]
            {
                new { name = "postgresql", state = await pgCb.GetStateAsync() },
                new { name = "kafka",      state = await kafkaCb.GetStateAsync() },
                new { name = "external",   state = await externalCb.GetStateAsync() }
            };

            var anyOpen = results.Any(r => r.state.Status == CircuitStatus.Open);
            return anyOpen
                ? Microsoft.AspNetCore.Http.Results.Json(results, statusCode: 503)
                : Microsoft.AspNetCore.Http.Results.Ok(results);
        }).WithName("CircuitBreakerStatus").AllowAnonymous();

        return app;
    }
}

// ── Configuration ─────────────────────────────────────────────────────────────

/// <summary>
/// Resilience policy configuration. Override defaults per-system as needed.
/// </summary>
public class ResilienceOptions
{
    // Retry defaults
    public int DefaultMaxAttempts        { get; set; } = 3;
    public TimeSpan DefaultInitialDelay  { get; set; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan DefaultMaxDelay      { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan DefaultJitter        { get; set; } = TimeSpan.FromMilliseconds(100);

    // PostgreSQL circuit breaker
    public int PostgreSqlFailureThreshold { get; set; } = 5;
    public int PostgreSqlCooldownSeconds  { get; set; } = 30;

    // Kafka circuit breaker
    public int KafkaFailureThreshold      { get; set; } = 3;
    public int KafkaCooldownSeconds       { get; set; } = 20;

    // External service circuit breaker (payment processor, etc.)
    public int ExternalFailureThreshold   { get; set; } = 5;
    public int ExternalCooldownSeconds    { get; set; } = 60;  // longer cooldown for external
}

// ════════════════════════════════════════════════════════════════════════════
// USAGE EXAMPLES — paste into repository/service classes as needed
// ════════════════════════════════════════════════════════════════════════════

/*

// ── In a repository (constructor injection) ───────────────────────────────────

public class WalletRepository
{
    private readonly RetryPolicy _retryPolicy;
    private readonly ICircuitBreaker _circuitBreaker;

    public WalletRepository(
        RetryPolicy retryPolicy,
        [FromKeyedServices("postgresql")] ICircuitBreaker circuitBreaker,
        IConfiguration configuration)
    {
        _retryPolicy     = retryPolicy;
        _circuitBreaker  = circuitBreaker;
        _connectionString = configuration.GetConnectionString("PostgreSQL")!;
    }

    // Wrap read operations: retry + circuit breaker
    public async Task<Wallet?> FindByIdAsync(string walletId)
    {
        return await _circuitBreaker.ExecuteAsync(
            "FindWalletById",
            () => _retryPolicy.ExecuteAsync(
                async () => {
                    using var conn = CreateConnection();
                    return await conn.QuerySingleOrDefaultAsync<Wallet>(
                        "SELECT * FROM wallets WHERE id = @WalletId",
                        new { WalletId = walletId });
                },
                isRetryable: RetryPolicy.ReadOperationIsRetryable));
    }

    // Wrap write operations: circuit breaker only (not retry — writes have idempotency keys)
    public async Task InsertAsync(Wallet wallet)
    {
        await _circuitBreaker.ExecuteAsync("InsertWallet", async () => {
            using var conn = CreateConnection();
            await conn.ExecuteAsync("INSERT INTO wallets ...", wallet);
        });
    }
}

// ── In an external service call (payment processor) ──────────────────────────

public class AuthorisationService : IAuthorisationService
{
    private readonly ICircuitBreaker _externalCircuitBreaker;
    private readonly RetryPolicy _retryPolicy;

    public async Task<AuthorisationInfo> AuthoriseAsync(...)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        return await _externalCircuitBreaker.ExecuteAsync(
            "ProcessorAuthorise",
            () => _retryPolicy.ExecuteAsync(
                () => CallExternalProcessor(paymentId, amount, currency, timeoutCts.Token),
                isRetryable: ex => ex is TimeoutException or HttpRequestException),
            fallback: () => throw new ProcessorUnavailableException(paymentId));
    }
}

// ── In Program.cs ─────────────────────────────────────────────────────────────

// Digital Wallet:
builder.Services.AddResiliencePolicies(builder.Configuration, new ResilienceOptions
{
    PostgreSqlFailureThreshold = 5,
    PostgreSqlCooldownSeconds  = 30,
    KafkaFailureThreshold      = 3,
    KafkaCooldownSeconds       = 20
});
app.MapCircuitBreakerEndpoints();

// Payment Processing (stricter external service circuit):
builder.Services.AddResiliencePolicies(builder.Configuration, new ResilienceOptions
{
    ExternalFailureThreshold  = 5,
    ExternalCooldownSeconds   = 60  // processor may take longer to recover
});

// URL Shortener (Redis is more critical — lower threshold):
builder.Services.AddResiliencePolicies(builder.Configuration, new ResilienceOptions
{
    PostgreSqlFailureThreshold = 5,
    // Redis handled by fail-open in UrlCacheServiceV2 — no circuit breaker needed
});

*/

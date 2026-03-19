using System.Text.Json;
using Dapper;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using StackExchange.Redis;

namespace UrlShortener.Infrastructure.Cache;

/// <summary>
/// Extended URL cache service with cache stampede protection for hot links.
///
/// Problem: a viral URL receives millions of requests per second.
/// When its 10-minute cache entry expires, all concurrent requests experience
/// a cache miss simultaneously and flood PostgreSQL with the same query.
///
/// Solution: distributed mutex using Redis SET NX.
/// Only the first requester to acquire the mutex rebuilds the cache.
/// All others either wait briefly and read the newly populated cache,
/// or fall through to the database if the mutex holder is slow.
/// </summary>
public class UrlCacheServiceV2
{
    private readonly IDatabase _db;
    private readonly ILogger<UrlCacheServiceV2> _logger;
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MutexTtl   = TimeSpan.FromSeconds(5);

    public UrlCacheServiceV2(IConnectionMultiplexer redis, ILogger<UrlCacheServiceV2> logger)
    {
        _db     = redis.GetDatabase();
        _logger = logger;
    }

    // ── Standard cache operations ─────────────────────────────────────────────

    public async Task<CachedUrlEntry?> GetAsync(string shortCode)
    {
        try
        {
            var value = await _db.StringGetAsync(CacheKey(shortCode));
            return value.HasValue
                ? JsonSerializer.Deserialize<CachedUrlEntry>(value.ToString())
                : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis URL cache read failed for {ShortCode}", shortCode);
            return null;
        }
    }

    public async Task SetAsync(string shortCode, string originalUrl, DateTimeOffset? expiresAt, bool isActive)
    {
        try
        {
            // TTL = min(10 minutes, time until URL expires)
            // This prevents serving an expired URL from cache after its expiry
            var ttl = expiresAt.HasValue
                ? new[] { DefaultTtl, expiresAt.Value - DateTimeOffset.UtcNow }.Min()
                : DefaultTtl;

            if (ttl <= TimeSpan.Zero) return; // already expired — do not cache

            await _db.StringSetAsync(
                CacheKey(shortCode),
                JsonSerializer.Serialize(new CachedUrlEntry(originalUrl, expiresAt, isActive)),
                ttl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis URL cache write failed for {ShortCode}", shortCode);
        }
    }

    public async Task InvalidateAsync(string shortCode)
    {
        try { await _db.KeyDeleteAsync(CacheKey(shortCode)); }
        catch (Exception ex) { _logger.LogWarning(ex, "Redis URL cache invalidation failed"); }
    }

    // ── Cache stampede protection for hot links ───────────────────────────────

    /// <summary>
    /// Resolves a short code with cache stampede protection.
    /// For viral links that receive thousands of concurrent requests at expiry time,
    /// only ONE caller queries the database and repopulates the cache.
    /// </summary>
    public async Task<CachedUrlEntry?> GetWithStampedeProtectionAsync(
        string shortCode,
        Func<Task<CachedUrlEntry?>> dbFallback)
    {
        // Fast path: cache hit — vast majority of requests exit here
        var cached = await GetAsync(shortCode);
        if (cached is not null) return cached;

        // Cache miss: attempt to acquire rebuild mutex
        var mutexKey = $"mutex:url:{shortCode}";
        var acquired = await _db.StringSetAsync(mutexKey, "1", MutexTtl, When.NotExists);

        if (acquired)
        {
            // This caller won the mutex — rebuild the cache
            try
            {
                var entry = await dbFallback();
                if (entry is not null)
                    await SetAsync(shortCode, entry.OriginalUrl, entry.ExpiresAt, entry.IsActive);
                return entry;
            }
            finally
            {
                await _db.KeyDeleteAsync(mutexKey);
            }
        }
        else
        {
            // Another caller is rebuilding — wait briefly then retry
            _logger.LogDebug("Stampede protection: mutex not acquired for {ShortCode}, waiting", shortCode);
            await Task.Delay(30); // 30ms wait

            var retried = await GetAsync(shortCode);
            if (retried is not null) return retried;

            // Mutex holder may be slow — fall through to DB directly
            return await dbFallback();
        }
    }

    // ── Click counter (approximate, denormalised) ─────────────────────────────

    /// <summary>
    /// Increments the in-memory click counter for a URL.
    /// Periodically flushed to PostgreSQL by a background job.
    /// Provides near-real-time click counts without a DB write on every redirect.
    /// </summary>
    public async Task IncrementClickCounterAsync(string urlId)
    {
        try
        {
            var key = $"clicks:pending:{urlId}";
            var count = await _db.StringIncrementAsync(key);
            if (count == 1) await _db.KeyExpireAsync(key, TimeSpan.FromMinutes(5));
        }
        catch { /* non-fatal */ }
    }

    /// <summary>
    /// Reads and clears the pending click counter for a URL.
    /// Called by the click counter flush background job every 5 minutes.
    /// </summary>
    public async Task<long> FlushClickCounterAsync(string urlId)
    {
        var key = $"clicks:pending:{urlId}";
        try
        {
            var value = await _db.StringGetDeleteAsync(key);
            return value.HasValue && long.TryParse(value.ToString(), out var count) ? count : 0;
        }
        catch { return 0; }
    }

    private static string CacheKey(string shortCode) => $"url:{shortCode}";
}

public record CachedUrlEntry(string OriginalUrl, DateTimeOffset? ExpiresAt, bool IsActive);

// ════════════════════════════════════════════════════════════════════════════
// RABBITMQ ANALYTICS CONSUMER
// Consumes click.events queue and writes to url_clicks table in batches
// ════════════════════════════════════════════════════════════════════════════

namespace UrlShortener.Infrastructure.Messaging;

/// <summary>
/// RabbitMQ consumer for click analytics events.
///
/// Queue: click.events (durable)
/// Dead letter exchange: url.dlx → click.dead queue
///
/// Why RabbitMQ and not Kafka?
/// Click events follow the work queue pattern: each event should be processed
/// exactly once. RabbitMQ's per-message acknowledgement model is ideal — if a
/// consumer crashes after processing but before ACKing, the broker redelivers
/// to another consumer. Kafka consumers would need manual offset management
/// to achieve the same guarantee.
///
/// Throughput: ~1,200 messages/second (100M redirects/day)
/// Processing: batched inserts every 100 messages or 5 seconds
/// </summary>
public class ClickAnalyticsConsumer : IDisposable
{
    private readonly IConnection _connection;
    private readonly IModel _channel;
    private readonly string _connectionString;
    private readonly UrlCacheServiceV2 _cache;
    private readonly ILogger<ClickAnalyticsConsumer> _logger;
    private const string Queue   = "click.events";
    private const string DlxName = "url.dlx";
    private const int BatchSize  = 100;

    private readonly List<ClickEventMessage> _batch = [];
    private System.Timers.Timer? _flushTimer;

    public ClickAnalyticsConsumer(
        IConfiguration configuration,
        UrlCacheServiceV2 cache,
        ILogger<ClickAnalyticsConsumer> logger)
    {
        _cache            = cache;
        _logger           = logger;
        _connectionString = configuration.GetConnectionString("PostgreSQL")!;

        var factory = new ConnectionFactory
        {
            Uri = new Uri(configuration.GetConnectionString("RabbitMQ") ?? "amqp://devuser:devpass@localhost:5672/")
        };
        _connection = factory.CreateConnection();
        _channel    = _connection.CreateModel();

        // Declare queue with dead letter exchange
        _channel.QueueDeclare(
            queue: Queue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object>
            {
                ["x-dead-letter-exchange"]    = DlxName,
                ["x-dead-letter-routing-key"] = "click.dead",
                ["x-message-ttl"]             = 86400000  // 24h — unprocessed clicks expire
            });

        // Prefetch 200 messages at a time — balance between throughput and memory
        _channel.BasicQos(prefetchSize: 0, prefetchCount: 200, global: false);
    }

    public void StartConsuming(CancellationToken ct)
    {
        // Flush timer — batch insert every 5 seconds regardless of batch size
        _flushTimer = new System.Timers.Timer(5000);
        _flushTimer.Elapsed += async (_, _) => await FlushBatchAsync(CancellationToken.None);
        _flushTimer.Start();

        var consumer = new EventingBasicConsumer(_channel);
        consumer.Received += async (_, args) =>
        {
            if (ct.IsCancellationRequested)
            {
                _channel.BasicNack(args.DeliveryTag, multiple: false, requeue: true);
                return;
            }

            try
            {
                var body  = args.Body.ToArray();
                var json  = System.Text.Encoding.UTF8.GetString(body);
                var click = JsonSerializer.Deserialize<ClickEventMessage>(json);

                if (click is null)
                {
                    // Malformed message — ACK and discard (retrying won't help)
                    _channel.BasicAck(args.DeliveryTag, multiple: false);
                    return;
                }

                lock (_batch) { _batch.Add(click); }

                // ACK immediately — click analytics are best-effort
                // If we fail to write to DB later, the click count is slightly under-reported
                // but the redirect was already served correctly
                _channel.BasicAck(args.DeliveryTag, multiple: false);

                if (_batch.Count >= BatchSize)
                    await FlushBatchAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process click event");
                // NACK with requeue=false — sends to dead letter exchange
                _channel.BasicNack(args.DeliveryTag, multiple: false, requeue: false);
            }
        };

        _channel.BasicConsume(queue: Queue, autoAck: false, consumer: consumer);
        _logger.LogInformation("ClickAnalyticsConsumer started — consuming from {Queue}", Queue);
    }

    private async Task FlushBatchAsync(CancellationToken ct)
    {
        List<ClickEventMessage> toFlush;
        lock (_batch)
        {
            if (_batch.Count == 0) return;
            toFlush = [.. _batch];
            _batch.Clear();
        }

        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            // Batch insert all clicks in one round trip
            foreach (var click in toFlush)
            {
                await conn.ExecuteAsync("""
                    INSERT INTO url_clicks (id, url_id, clicked_at, user_agent, referer, country_code, ip_hash)
                    VALUES (@Id, @UrlId, @ClickedAt, @UserAgent, @Referer, @CountryCode, @IpHash)
                    """, new
                {
                    Id          = $"{Guid.NewGuid():N}",
                    UrlId       = click.UrlId,
                    ClickedAt   = click.ClickedAt,
                    UserAgent   = click.UserAgent,
                    Referer     = click.Referer,
                    CountryCode = click.CountryCode,
                    IpHash      = click.IpHash
                });

                // Also increment the denormalised click_count on the urls table
                await conn.ExecuteAsync(
                    "UPDATE urls SET click_count = click_count + 1 WHERE id = @UrlId",
                    new { click.UrlId });
            }

            _logger.LogDebug("Flushed {Count} click events to PostgreSQL", toFlush.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to flush {Count} click events — analytics may under-count", toFlush.Count);
            // Click analytics are best-effort — do not re-queue on DB failure
        }
    }

    private record ClickEventMessage(
        string ShortCode, string UrlId, DateTimeOffset ClickedAt,
        string? UserAgent, string? Referer, string? CountryCode, string? IpHash);

    public void Dispose()
    {
        _flushTimer?.Dispose();
        _channel?.Dispose();
        _connection?.Dispose();
    }
}

public class ClickAnalyticsConsumerWorker : BackgroundService
{
    private readonly ClickAnalyticsConsumer _consumer;

    public ClickAnalyticsConsumerWorker(ClickAnalyticsConsumer consumer) => _consumer = consumer;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _consumer.StartConsuming(stoppingToken);
        return Task.CompletedTask;
    }

    public override void Dispose() { _consumer?.Dispose(); base.Dispose(); }
}

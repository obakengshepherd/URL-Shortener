using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using UrlShortener.Infrastructure.Persistence;

namespace UrlShortener.Infrastructure.Messaging;

/// <summary>
/// Batch analytics consumer for click events from RabbitMQ.
/// 
/// NOTE: For MVP, this is a simplified synchronous implementation.
/// Production should use IAsyncChannel-based consumer for true async processing.
/// </summary>
public class ClickAnalyticsConsumer
{
    private const int PrefetchCount = 200;
    private const int BatchSize = 100;
    private const int FlushIntervalMs = 5000;
    private const string QueueName = "click.events";
    private const string DlxExchange = "url.dlx";

    private readonly string _connectionString;
    private readonly ILogger<ClickAnalyticsConsumer> _logger;
    private IConnection? _connection;
    private IModel? _channel;
    private readonly object _lockObject = new();
    private List<ClickEvent> _batch = new(BatchSize + 10);
    private Timer? _flushTimer;
    private bool _running = false;

    public ClickAnalyticsConsumer(
        IConfiguration configuration,
        ILogger<ClickAnalyticsConsumer> logger)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionString 'DefaultConnection' not configured");
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Starts the consumer synchronously. Note: consumer runs in background thread.
    /// </summary>
    public Task StartConsumingAsync(CancellationToken cancellationToken = default)
    {
        if (_running)
        {
            _logger.LogWarning("Consumer already running");
            return Task.CompletedTask;
        }

        try
        {
            var factory = new ConnectionFactory
            {
                Uri = new Uri(Environment.GetEnvironmentVariable("RABBITMQ_URI") 
                    ?? "amqp://devuser:devpass@localhost:5673/"),
                DispatchConsumersAsync = false,  // Use sync consumer
            };

            _connection = factory.CreateConnection();
            _channel = _connection.CreateModel();

            // Declare durable queue with DLX
            _channel.QueueDeclare(
                queue: QueueName,
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: new Dictionary<string, object?>
                {
                    { "x-dead-letter-exchange", DlxExchange },
                    { "x-message-ttl", 86400000 }  // 24 hours
                });

            // Declare DLX
            _channel.ExchangeDeclare(
                exchange: DlxExchange,
                type: ExchangeType.Direct,
                durable: true,
                autoDelete: false);

            // Set quality of service
            _channel.BasicQos(0, PrefetchCount, false);

            // Start consumer (synchronous event handler)
            var consumer = new EventingBasicConsumer(_channel);
            consumer.Received += OnMessageReceived;

            _channel.BasicConsume(
                queue: QueueName,
                autoAck: false,
                consumerTag: "click-analytics",
                consumer: consumer);

            // Start flush timer
            _flushTimer = new Timer(
                OnFlushTimer,
                null,
                TimeSpan.FromMilliseconds(FlushIntervalMs),
                TimeSpan.FromMilliseconds(FlushIntervalMs));

            _running = true;
            _logger.LogInformation("Click analytics consumer started, listening to {QueueName}", QueueName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting click analytics consumer");
            throw;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Gracefully stops the consumer and flushes pending events.
    /// </summary>
    public Task StopAsync()
    {
        _running = false;
        _flushTimer?.Dispose();

        try
        {
            if (_channel != null)
                _channel.Close();
            if (_connection != null)
                _connection.Close();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error closing RabbitMQ connection");
        }

        _logger.LogInformation("Click analytics consumer stopped");
        return Task.CompletedTask;
    }

    // ── Event Handlers ──────────────────────────────────────────────────────

    private void OnMessageReceived(object? model, BasicDeliverEventArgs ea)
    {
        if (!_running)
            return;

        try
        {
            var json = System.Text.Encoding.UTF8.GetString(ea.Body.ToArray());
            var clickEvent = JsonSerializer.Deserialize<ClickEvent>(json)
                ?? throw new InvalidOperationException("Failed to deserialize click event");

            lock (_lockObject)
            {
                _batch.Add(clickEvent);

                if (_batch.Count >= BatchSize)
                {
                    _logger.LogDebug("Batch full ({Count} events), flushing to database", _batch.Count);
                    _ = FlushBatchAsync();  // Fire and forget
                }
            }

            // ACK after receiving (best-effort semantics)
            _channel!.BasicAck(ea.DeliveryTag, multiple: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing click event");
            // NACK to requeue on error
            try
            {
                _channel!.BasicNack(ea.DeliveryTag, multiple: false, requeue: true);
            }
            catch (Exception nackEx)
            {
                _logger.LogWarning(nackEx, "Error sending NACK");
            }
        }
    }

    private void OnFlushTimer(object? state)
    {
        if (!_running)
            return;

        lock (_lockObject)
        {
            if (_batch.Count > 0)
            {
                _logger.LogDebug("Timer flush ({Count} events)", _batch.Count);
                _ = FlushBatchAsync();  // Fire and forget
            }
        }
    }

    private async Task FlushBatchAsync()
    {
        List<ClickEvent> toFlush;

        lock (_lockObject)
        {
            if (_batch.Count == 0)
                return;

            toFlush = _batch;
            _batch = new(BatchSize + 10);
        }

        try
        {
            using var conn = new Npgsql.NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            // Simple batch insert (production would use COPY)
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO url_clicks (url_id, clicked_at, user_agent, referer, country_code, ip_hash)
                VALUES (@url_id, @clicked_at, @user_agent, @referer, @country_code, @ip_hash)";

            foreach (var evt in toFlush)
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@url_id", evt.UrlId);
                cmd.Parameters.AddWithValue("@clicked_at", evt.ClickedAt);
                cmd.Parameters.AddWithValue("@user_agent", evt.UserAgent ?? "");
                cmd.Parameters.AddWithValue("@referer", evt.Referer ?? "");
                cmd.Parameters.AddWithValue("@country_code", evt.CountryCode ?? "");
                cmd.Parameters.AddWithValue("@ip_hash", evt.IpHash ?? "");
                await cmd.ExecuteNonQueryAsync();
            }

            _logger.LogInformation("Flushed {Count} click events to database", toFlush.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error flushing {Count} click events to database", toFlush.Count);
        }
    }
}

/// <summary>
/// Click event from RabbitMQ message.
/// </summary>
public class ClickEvent
{
    public required long UrlId { get; set; }
    public required DateTime ClickedAt { get; set; }
    public required string UserAgent { get; set; }
    public required string Referer { get; set; }
    public required string CountryCode { get; set; }
    public required string IpHash { get; set; }
}


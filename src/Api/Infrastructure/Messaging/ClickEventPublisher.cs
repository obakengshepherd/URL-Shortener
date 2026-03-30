using System.Text.Json;
using RabbitMQ.Client;

namespace UrlShortener.Infrastructure.Messaging;

public class ClickEventPublisher : IDisposable
{
    private readonly IConnection? _connection;
    private readonly IModel? _channel;
    private const string Queue = "click.events";
    private readonly ILogger<ClickEventPublisher> _logger;

    public ClickEventPublisher(IConfiguration configuration, ILogger<ClickEventPublisher> logger)
    {
        _logger = logger;
        try
        {
            var factory = new ConnectionFactory
            {
                Uri = new Uri(configuration.GetConnectionString("RabbitMQ") ?? "amqp://devuser:devpass@localhost:5672/")
            };
            _connection = factory.CreateConnection();
            _channel    = _connection.CreateModel();
            _channel.QueueDeclare(queue: Queue, durable: true, exclusive: false, autoDelete: false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RabbitMQ connection failed — click events will be dropped");
        }
    }

    public void PublishClick(string shortCode, string urlId, string? userAgent, string? referer, string? countryCode, string? ipHash)
    {
        if (_channel is null) return;
        try
        {
            var body = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                ShortCode   = shortCode,
                UrlId       = urlId,
                ClickedAt   = DateTimeOffset.UtcNow,
                UserAgent   = userAgent,
                Referer     = referer,
                CountryCode = countryCode,
                IpHash      = ipHash
            }));
            var props = _channel.CreateBasicProperties();
            props.Persistent = true;
            _channel.BasicPublish(exchange: "", routingKey: Queue, basicProperties: props, body: body);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Click event publish failed — non-fatal"); }
    }

    public void Dispose() { _channel?.Dispose(); _connection?.Dispose(); }
}

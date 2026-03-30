using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace UrlShortener.Infrastructure.Messaging;

/// <summary>
/// Hosted service wrapper for the click analytics consumer.
/// Ensures the consumer starts when the application starts and shuts down gracefully.
/// </summary>
public class ClickAnalyticsConsumerWorker : BackgroundService
{
    private readonly ClickAnalyticsConsumer _consumer;
    private readonly ILogger<ClickAnalyticsConsumerWorker> _logger;

    public ClickAnalyticsConsumerWorker(
        ClickAnalyticsConsumer consumer,
        ILogger<ClickAnalyticsConsumerWorker> logger)
    {
        _consumer = consumer ?? throw new ArgumentNullException(nameof(consumer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Main background service loop. Runs until cancellation.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("Starting click analytics consumer worker");
            await _consumer.StartConsumingAsync(stoppingToken);

            // Keep running until cancellation (the consumer runs async internally)
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Click analytics consumer worker cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in click analytics consumer worker");
            throw;
        }
    }

    /// <summary>
    /// Shut down gracefully.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping click analytics consumer worker");
        await _consumer.StopAsync();
        await base.StopAsync(cancellationToken);
    }
}

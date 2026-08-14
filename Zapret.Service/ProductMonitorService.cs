using Zapret.Core.AutoSelect;

namespace Zapret.Service;

/// <summary>
/// Keeps the product honest without being asked: works out the user's stage at startup, then probes their
/// services on a timer so a real outage becomes a repair rather than something the user has to notice and test
/// for themselves.
/// </summary>
public sealed class ProductMonitorService(ProductOrchestrator orchestrator, ILogger<ProductMonitorService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the engine host finish adopting whatever is already installed before judging the situation.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            orchestrator.Recompute();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not determine the initial product state");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(HealthMonitor.Interval, stoppingToken).ConfigureAwait(false);
                await orchestrator.TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // A failed round is not a reason to stop watching; the next one may well succeed.
                logger.LogWarning(ex, "A monitoring round failed");
            }
        }
    }
}

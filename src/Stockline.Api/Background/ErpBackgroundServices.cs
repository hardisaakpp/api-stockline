using Stockline.Application.Erp;

namespace Stockline.Api.Background;

public sealed class ErpBackgroundOptions
{
    public int MaxAttempts { get; set; } = 5;

    public int RetryIntervalSeconds { get; set; } = 300;
}

/// <summary>
/// Periodically queues the orders that have documents failed for a transient reason. Every instance runs this; the job
/// table keeps one active job per order, so the extra sweeps cost a query and nothing more.
/// </summary>
public sealed class ErpRetryWorker(
    IServiceScopeFactory scopes, Microsoft.Extensions.Options.IOptions<ErpBackgroundOptions> options,
    ILogger<ErpRetryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.RetryIntervalSeconds;
        if (interval <= 0)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(interval));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var retryable = await scope.ServiceProvider.GetRequiredService<IErpDocumentRepository>()
                    .ListRetryableOrderIdsAsync(options.Value.MaxAttempts, stoppingToken);
                var queue = scope.ServiceProvider.GetRequiredService<IErpSubmissionQueue>();
                foreach (var orderId in retryable)
                {
                    await queue.EnqueueAsync(orderId, stoppingToken);
                }

                if (retryable.Count > 0)
                {
                    logger.LogInformation("Re-queued {Count} order(s) with transient ERP failures.", retryable.Count);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "The ERP retry sweep failed.");
            }
        }
    }
}

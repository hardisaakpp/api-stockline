using System.Threading.Channels;
using Stockline.Application.Erp;

namespace Stockline.Api.Background;

public sealed class ErpBackgroundOptions
{
    public int MaxAttempts { get; set; } = 5;

    public int RetryIntervalSeconds { get; set; } = 300;
}

public sealed class ChannelErpSubmissionQueue : IErpSubmissionQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateBounded<Guid>(
        new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });

    public ChannelReader<Guid> Reader => _channel.Reader;

    public ValueTask EnqueueAsync(Guid orderId, CancellationToken ct) => _channel.Writer.WriteAsync(orderId, ct);
}

public sealed class ErpSubmissionWorker(
    ChannelErpSubmissionQueue queue, IServiceScopeFactory scopes, ILogger<ErpSubmissionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var orderId in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var result = await scope.ServiceProvider.GetRequiredService<SubmitOrderToErpHandler>().HandleAsync(orderId, stoppingToken);
                logger.LogInformation("ERP submission for order {OrderId} finished as {Outcome}.", orderId, result.Outcome);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "ERP submission for order {OrderId} crashed.", orderId);
            }
        }
    }
}

public sealed class ErpRetryWorker(
    IErpSubmissionQueue queue, IServiceScopeFactory scopes, Microsoft.Extensions.Options.IOptions<ErpBackgroundOptions> options,
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

public sealed class EmailNotificationWorker(
    Stockline.Infrastructure.Notifications.EmailOrderEvents queue,
    IServiceScopeFactory scopes,
    Microsoft.Extensions.Options.IOptions<Stockline.Infrastructure.Email.EmailOptions> options,
    ILogger<EmailNotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var orderEvent in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<Stockline.Application.Notifications.OrderEmailNotifier>()
                    .NotifyAsync(orderEvent, options.Value.OrderUrlTemplate, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Email notification for order {Order} crashed.", orderEvent.OrderCode);
            }
        }
    }
}

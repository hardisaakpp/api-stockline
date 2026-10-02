using Microsoft.Extensions.Options;
using Stockline.Application.Jobs;
using Stockline.Domain.Jobs;

namespace Stockline.Api.Background;

public sealed class JobOptions
{
    /// <summary>How many jobs one instance runs at the same time.</summary>
    public int WorkerCount { get; set; } = 2;

    /// <summary>How often an idle worker looks for jobs enqueued by other instances.</summary>
    public int PollIntervalMilliseconds { get; set; } = 2000;

    /// <summary>A running job whose worker has not renewed its lease for this long is taken over by another worker.</summary>
    public int LeaseSeconds { get; set; } = 120;

    /// <summary>Finished jobs are deleted after this many days.</summary>
    public int RetentionDays { get; set; } = 14;
}

/// <summary>
/// Runs the jobs in the database. Several instances (and several loops per instance) can run at once: each claims a job
/// atomically with a lease, renews it while working, and releases it with the outcome.
/// </summary>
public sealed class JobWorker(
    IServiceScopeFactory scopes, JobSignal signal, IOptions<JobOptions> options, ILogger<JobWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var loops = Math.Max(1, options.Value.WorkerCount);
        var instance = $"{Environment.MachineName}:{Environment.ProcessId}";
        return Task.WhenAll(Enumerable.Range(1, loops).Select(n => LoopAsync($"{instance}#{n}", stoppingToken)));
    }

    private async Task LoopAsync(string workerId, CancellationToken stoppingToken)
    {
        var poll = TimeSpan.FromMilliseconds(Math.Max(50, options.Value.PollIntervalMilliseconds));
        var lease = TimeSpan.FromSeconds(Math.Max(5, options.Value.LeaseSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await RunNextAsync(workerId, lease, stoppingToken))
                {
                    await signal.WaitAsync(poll, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Worker {Worker} could not read the job table; retrying.", workerId);
                await Task.Delay(poll, stoppingToken);
            }
        }
    }

    /// <returns>False when there was nothing to run.</returns>
    private async Task<bool> RunNextAsync(string workerId, TimeSpan lease, CancellationToken stoppingToken)
    {
        BackgroundJob? job;
        using (var scope = scopes.CreateScope())
        {
            job = await scope.ServiceProvider.GetRequiredService<IJobStore>().ClaimNextAsync(workerId, lease, stoppingToken);
        }

        if (job is null)
        {
            return false;
        }

        using var ownership = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var heartbeat = HeartbeatAsync(job.Id, workerId, lease, ownership);
        JobOutcome outcome;
        try
        {
            outcome = await ExecuteAsync(job, workerId, lease, ownership.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Job {Job} ({Kind}) crashed on attempt {Attempt}.", job.Id, job.Kind, job.Attempts);
            outcome = JobOutcome.Retry($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            await ownership.CancelAsync();
            await heartbeat;
        }

        if (stoppingToken.IsCancellationRequested)
        {
            // Shutting down: leave the job running; its lease expires and another instance finishes it.
            return false;
        }

        using var done = scopes.CreateScope();
        await done.ServiceProvider.GetRequiredService<IJobStore>().CompleteAsync(job.Id, workerId, outcome, CancellationToken.None);
        logger.LogInformation("Job {Job} ({Kind}) finished as {Outcome} on attempt {Attempt}.", job.Id, job.Kind, outcome.Kind, job.Attempts);
        return true;
    }

    private async Task<JobOutcome> ExecuteAsync(BackgroundJob job, string workerId, TimeSpan lease, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var handler = scope.ServiceProvider.GetServices<IJobHandler>().FirstOrDefault(h => h.Kind == job.Kind);
        if (handler is null)
        {
            return JobOutcome.Failed($"No handler is registered for '{job.Kind}' jobs.");
        }

        return await handler.HandleAsync(job, new Progress(scopes, job.Id, workerId, lease), ct);
    }

    /// <summary>Renews the lease while the handler runs; cancels the handler when the job was taken over.</summary>
    private async Task HeartbeatAsync(Guid id, string workerId, TimeSpan lease, CancellationTokenSource ownership)
    {
        using var timer = new PeriodicTimer(lease / 3);
        try
        {
            while (await timer.WaitForNextTickAsync(ownership.Token))
            {
                using var scope = scopes.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<IJobStore>();
                var job = await store.GetAsync(id, ownership.Token);
                if (!await store.ReportProgressAsync(id, workerId, job?.Progress ?? 0, job?.Stage, lease, ownership.Token))
                {
                    logger.LogWarning("Job {Job} is no longer owned by {Worker}; cancelling it.", id, workerId);
                    await ownership.CancelAsync();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The job finished; nothing left to renew.
        }
    }

    private sealed class Progress(IServiceScopeFactory scopes, Guid id, string workerId, TimeSpan lease) : IJobProgress
    {
        public async Task ReportAsync(int percent, string stage, CancellationToken ct)
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IJobStore>().ReportProgressAsync(id, workerId, percent, stage, lease, ct);
        }
    }
}

/// <summary>Deletes finished jobs once they are older than the retention period.</summary>
public sealed class JobCleanupWorker(
    IServiceScopeFactory scopes, IOptions<JobOptions> options, TimeProvider clock, ILogger<JobCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var cutoff = clock.GetUtcNow().AddDays(-Math.Max(1, options.Value.RetentionDays));
                var removed = await scope.ServiceProvider.GetRequiredService<IJobStore>().PurgeFinishedAsync(cutoff, stoppingToken);
                if (removed > 0)
                {
                    logger.LogInformation("Deleted {Count} finished job(s).", removed);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Cleaning up finished jobs failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>Sends the e-mails of an order event. The job payload is the event as JSON.</summary>
public sealed class EmailJobHandler(
    Stockline.Application.Notifications.OrderEmailNotifier notifier,
    IOptions<Stockline.Infrastructure.Email.EmailOptions> options) : IJobHandler
{
    public JobKind Kind => JobKind.EmailNotification;

    public async Task<JobOutcome> HandleAsync(BackgroundJob job, IJobProgress progress, CancellationToken ct)
    {
        var orderEvent = System.Text.Json.JsonSerializer.Deserialize<Stockline.Application.Notifications.OrderEvent>(job.Payload);
        if (orderEvent is null)
        {
            return JobOutcome.Failed("The event payload is empty.");
        }

        var sent = await notifier.NotifyAsync(orderEvent, options.Value.OrderUrlTemplate, ct);
        return JobOutcome.Succeeded(sent ? "sent" : "skipped");
    }
}

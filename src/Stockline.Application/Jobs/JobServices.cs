using Stockline.Application.Erp;
using Stockline.Domain.Jobs;

namespace Stockline.Application.Jobs;

/// <summary>The ERP submission queue backed by the job table: durable, shared by every instance, one active job per order.</summary>
public sealed class JobErpSubmissionQueue(IJobStore jobs) : IErpSubmissionQueue
{
    public async ValueTask EnqueueAsync(Guid orderId, CancellationToken ct) =>
        await jobs.EnqueueAsync(new NewJob(JobKind.ErpSubmission, orderId.ToString(), string.Empty, MaxAttempts: 3), ct);
}

public sealed class JobSignal : IJobSignal
{
    private readonly SemaphoreSlim _signal = new(0);

    public void Notify()
    {
        if (_signal.CurrentCount < 1)
        {
            _signal.Release();
        }
    }

    /// <summary>Waits for a notification or until the timeout (the polling fallback that finds jobs of other instances).</summary>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, ct);
}

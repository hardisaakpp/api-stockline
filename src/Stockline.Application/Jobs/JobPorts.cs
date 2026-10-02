using Stockline.Domain.Jobs;

namespace Stockline.Application.Jobs;

public sealed record NewJob(JobKind Kind, string? Key, string Payload, byte[]? Data = null, string? CreatedBy = null, int MaxAttempts = 3);

/// <summary>What a handler decided about the run: finished, or try again later, or give up.</summary>
public sealed record JobOutcome(JobOutcomeKind Kind, string? Result = null, string? Error = null, TimeSpan? RetryAfter = null)
{
    public static JobOutcome Succeeded(string? result = null) => new(JobOutcomeKind.Succeeded, result);

    public static JobOutcome Retry(string error, TimeSpan? after = null) => new(JobOutcomeKind.Retry, Error: error, RetryAfter: after);

    public static JobOutcome Failed(string error) => new(JobOutcomeKind.Failed, Error: error);
}

public enum JobOutcomeKind
{
    Succeeded,
    Retry,
    Failed,
}

public interface IJobStore
{
    /// <summary>Stores a job; when one with the same kind and key is still pending or running, returns its id instead.</summary>
    Task<Guid> EnqueueAsync(NewJob job, CancellationToken ct);

    /// <summary>
    /// Atomically takes the oldest runnable job (pending, or running with an expired lease) for <paramref name="workerId"/>.
    /// Two workers never get the same job. Returns null when there is nothing to do.
    /// </summary>
    Task<BackgroundJob?> ClaimNextAsync(string workerId, TimeSpan lease, CancellationToken ct);

    /// <summary>Records progress and extends the lease; false when the worker no longer owns the job.</summary>
    Task<bool> ReportProgressAsync(Guid id, string workerId, int progress, string? stage, TimeSpan lease, CancellationToken ct);

    Task CompleteAsync(Guid id, string workerId, JobOutcome outcome, CancellationToken ct);

    Task<BackgroundJob?> GetAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<BackgroundJob>> ListAsync(string? createdBy, JobKind? kind, int limit, CancellationToken ct);

    /// <summary>Deletes finished jobs older than the cutoff; returns how many.</summary>
    Task<int> PurgeFinishedAsync(DateTimeOffset olderThan, CancellationToken ct);
}

/// <summary>Reports how far a running job got; each call also renews the lease so a long job is not taken over.</summary>
public interface IJobProgress
{
    Task ReportAsync(int percent, string stage, CancellationToken ct);
}

public interface IJobHandler
{
    JobKind Kind { get; }

    Task<JobOutcome> HandleAsync(BackgroundJob job, IJobProgress progress, CancellationToken ct);
}

/// <summary>Wakes the workers of this instance right after a job is enqueued (other instances find it by polling).</summary>
public interface IJobSignal
{
    void Notify();
}

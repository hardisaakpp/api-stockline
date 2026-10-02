namespace Stockline.Domain.Jobs;

public enum JobKind
{
    ErpSubmission,
    OrderImport,
    EmailNotification,
}

public enum JobStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
}

/// <summary>
/// A unit of work stored in the database so that any application instance can pick it up (a worker claims it with a lease),
/// it survives restarts, and the caller can ask how it is going.
/// </summary>
public sealed class BackgroundJob
{
    // Parameterless constructor required by EF Core materialization.
    private BackgroundJob()
    {
        Payload = string.Empty;
    }

    public BackgroundJob(
        Guid id, JobKind kind, string? key, string payload, byte[]? data, string? createdBy, int maxAttempts, DateTimeOffset now)
    {
        Id = id;
        Kind = kind;
        Key = key;
        Payload = payload;
        Data = data;
        CreatedBy = createdBy;
        MaxAttempts = maxAttempts;
        CreatedAt = now;
        AvailableAt = now;
        Status = JobStatus.Pending;
    }

    public Guid Id { get; private set; }

    public JobKind Kind { get; private set; }

    /// <summary>
    /// While a job with this key is still waiting, enqueuing the same kind and key again returns that job (it will see the
    /// newest state anyway). A running job does not absorb new requests, since it may already have read its input; the new
    /// job waits until the running one finishes.
    /// </summary>
    public string? Key { get; private set; }

    public string Payload { get; private set; }

    /// <summary>Binary input (an uploaded file); dropped once the job finishes.</summary>
    public byte[]? Data { get; private set; }

    public JobStatus Status { get; private set; }

    public int Attempts { get; private set; }

    public int MaxAttempts { get; private set; }

    public int Progress { get; private set; }

    public string? Stage { get; private set; }

    public string? Error { get; private set; }

    public string? Result { get; private set; }

    public string? CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Not claimable before this instant (retry back-off).</summary>
    public DateTimeOffset AvailableAt { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    public string? ClaimedBy { get; private set; }

    /// <summary>A running job whose lease expired is considered abandoned (its worker died) and can be claimed again.</summary>
    public DateTimeOffset? LeaseUntil { get; private set; }

    public bool IsFinished => Status is JobStatus.Succeeded or JobStatus.Failed;
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stockline.Application.Jobs;
using Stockline.Domain.Jobs;

namespace Stockline.Infrastructure.Persistence;

internal sealed class BackgroundJobConfiguration : IEntityTypeConfiguration<BackgroundJob>
{
    public void Configure(EntityTypeBuilder<BackgroundJob> builder)
    {
        builder.ToTable("background_jobs");
        builder.HasKey(j => j.Id);
        builder.Property(j => j.Id).ValueGeneratedNever();
        builder.Property(j => j.Kind).HasConversion<string>().HasMaxLength(30);
        builder.Property(j => j.Key).HasMaxLength(100);
        builder.Property(j => j.Payload).IsRequired();
        builder.Property(j => j.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(j => j.Stage).HasMaxLength(60);
        builder.Property(j => j.Error).HasMaxLength(2000);
        builder.Property(j => j.CreatedBy).HasMaxLength(100);
        builder.Property(j => j.ClaimedBy).HasMaxLength(100);

        // Ticks keep ordering and comparison in SQL identical on SQL Server and SQLite.
        foreach (var name in new[] { nameof(BackgroundJob.CreatedAt), nameof(BackgroundJob.AvailableAt), nameof(BackgroundJob.StartedAt),
                     nameof(BackgroundJob.FinishedAt), nameof(BackgroundJob.LeaseUntil) })
        {
            builder.Property(name).HasConversion(
                new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset, long>(
                    v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero)));
        }

        builder.Ignore(j => j.IsFinished);
        builder.HasIndex(j => new { j.Status, j.AvailableAt });
        builder.HasIndex(j => new { j.Kind, j.Key })
            .IsUnique()
            .HasFilter("[Key] IS NOT NULL AND [Status] = 'Pending'");
    }
}

public sealed class EfJobStore(StocklineDbContext db, TimeProvider clock, IJobSignal? signal = null) : IJobStore
{
    private const int ClaimAttempts = 5;

    public async Task<Guid> EnqueueAsync(NewJob job, CancellationToken ct)
    {
        if (job.Key is not null && await ActiveIdAsync(job.Kind, job.Key, ct) is { } active)
        {
            return active;
        }

        var entity = new BackgroundJob(
            Guid.CreateVersion7(), job.Kind, job.Key, job.Payload, job.Data, job.CreatedBy, job.MaxAttempts, clock.GetUtcNow());
        db.Set<BackgroundJob>().Add(entity);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (job.Key is not null)
        {
            // Another instance enqueued the same key between our check and our insert: the unique index let only one in.
            db.Entry(entity).State = EntityState.Detached;
            if (await ActiveIdAsync(job.Kind, job.Key, ct) is { } winner)
            {
                return winner;
            }

            throw;
        }

        signal?.Notify();
        return entity.Id;
    }

    public async Task<BackgroundJob?> ClaimNextAsync(string workerId, TimeSpan lease, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var jobs = db.Set<BackgroundJob>();

        // A job whose worker died more often than allowed would otherwise be retried forever. Looking first keeps an idle
        // poll read-only: it never takes a write lock when there is nothing to fail.
        var exhausted = jobs.Where(j => j.Status == JobStatus.Running && j.LeaseUntil < now && j.Attempts >= j.MaxAttempts);
        if (await exhausted.AnyAsync(ct))
        {
            await exhausted.ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Failed)
                .SetProperty(j => j.Error, "The worker stopped before the job finished and the attempts ran out.")
                .SetProperty(j => j.FinishedAt, (DateTimeOffset?)now)
                .SetProperty(j => j.Data, (byte[]?)null)
                .SetProperty(j => j.ClaimedBy, (string?)null)
                .SetProperty(j => j.LeaseUntil, (DateTimeOffset?)null), ct);
        }

        for (var attempt = 0; attempt < ClaimAttempts; attempt++)
        {
            // A job waits while another job with the same key is running, so the work on one key is never done twice at once.
            var candidate = await jobs.AsNoTracking()
                .Where(j => ((j.Status == JobStatus.Pending && j.AvailableAt <= now)
                             || (j.Status == JobStatus.Running && j.LeaseUntil < now))
                            && (j.Key == null
                                || !jobs.Any(r => r.Id != j.Id && r.Kind == j.Kind && r.Key == j.Key
                                                  && r.Status == JobStatus.Running && r.LeaseUntil >= now)))
                .OrderBy(j => j.AvailableAt).ThenBy(j => j.CreatedAt)
                .Select(j => (Guid?)j.Id)
                .FirstOrDefaultAsync(ct);
            if (candidate is not { } id)
            {
                return null;
            }

            var until = now + lease;
            var claimed = await jobs
                .Where(j => j.Id == id
                            && ((j.Status == JobStatus.Pending && j.AvailableAt <= now)
                                || (j.Status == JobStatus.Running && j.LeaseUntil < now)))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, JobStatus.Running)
                    .SetProperty(j => j.ClaimedBy, workerId)
                    .SetProperty(j => j.LeaseUntil, (DateTimeOffset?)until)
                    .SetProperty(j => j.StartedAt, (DateTimeOffset?)now)
                    .SetProperty(j => j.Attempts, j => j.Attempts + 1), ct);
            if (claimed == 1)
            {
                return await jobs.AsNoTracking().FirstAsync(j => j.Id == id, ct);
            }

            // Lost the race to another worker; look for the next one.
        }

        return null;
    }

    public async Task<bool> ReportProgressAsync(Guid id, string workerId, int progress, string? stage, TimeSpan lease, CancellationToken ct)
    {
        var until = clock.GetUtcNow() + lease;
        var rows = await db.Set<BackgroundJob>()
            .Where(j => j.Id == id && j.ClaimedBy == workerId && j.Status == JobStatus.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Progress, Math.Clamp(progress, 0, 100))
                .SetProperty(j => j.Stage, stage)
                .SetProperty(j => j.LeaseUntil, (DateTimeOffset?)until), ct);
        return rows == 1;
    }

    public async Task CompleteAsync(Guid id, string workerId, JobOutcome outcome, CancellationToken ct)
    {
        var jobs = db.Set<BackgroundJob>();
        var now = clock.GetUtcNow();
        var mine = jobs.Where(j => j.Id == id && j.ClaimedBy == workerId && j.Status == JobStatus.Running);

        if (outcome.Kind == JobOutcomeKind.Succeeded)
        {
            await mine.ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Succeeded)
                .SetProperty(j => j.Progress, 100)
                .SetProperty(j => j.Result, outcome.Result)
                .SetProperty(j => j.Error, (string?)null)
                .SetProperty(j => j.FinishedAt, (DateTimeOffset?)now)
                .SetProperty(j => j.Data, (byte[]?)null)
                .SetProperty(j => j.ClaimedBy, (string?)null)
                .SetProperty(j => j.LeaseUntil, (DateTimeOffset?)null), ct);
            return;
        }

        var error = outcome.Error is { Length: > 2000 } text ? text[..2000] : outcome.Error;
        var attempts = await jobs.AsNoTracking().Where(j => j.Id == id).Select(j => new { j.Attempts, j.MaxAttempts }).FirstOrDefaultAsync(ct);
        if (attempts is null)
        {
            return;
        }

        if (outcome.Kind == JobOutcomeKind.Retry && attempts.Attempts < attempts.MaxAttempts)
        {
            var available = now + (outcome.RetryAfter ?? TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, attempts.Attempts))));
            await mine.ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Pending)
                .SetProperty(j => j.Error, error)
                .SetProperty(j => j.AvailableAt, available)
                .SetProperty(j => j.ClaimedBy, (string?)null)
                .SetProperty(j => j.LeaseUntil, (DateTimeOffset?)null), ct);
            return;
        }

        await mine.ExecuteUpdateAsync(s => s
            .SetProperty(j => j.Status, JobStatus.Failed)
            .SetProperty(j => j.Error, error)
            .SetProperty(j => j.FinishedAt, (DateTimeOffset?)now)
            .SetProperty(j => j.Data, (byte[]?)null)
            .SetProperty(j => j.ClaimedBy, (string?)null)
            .SetProperty(j => j.LeaseUntil, (DateTimeOffset?)null), ct);
    }

    public Task<BackgroundJob?> GetAsync(Guid id, CancellationToken ct) =>
        db.Set<BackgroundJob>().AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);

    public async Task<IReadOnlyList<BackgroundJob>> ListAsync(string? createdBy, JobKind? kind, int limit, CancellationToken ct) =>
        await db.Set<BackgroundJob>().AsNoTracking()
            .Where(j => (createdBy == null || j.CreatedBy == createdBy) && (kind == null || j.Kind == kind))
            .OrderByDescending(j => j.CreatedAt)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(ct);

    public Task<int> PurgeFinishedAsync(DateTimeOffset olderThan, CancellationToken ct) =>
        db.Set<BackgroundJob>()
            .Where(j => (j.Status == JobStatus.Succeeded || j.Status == JobStatus.Failed) && j.FinishedAt < olderThan)
            .ExecuteDeleteAsync(ct);

    private Task<Guid?> ActiveIdAsync(JobKind kind, string key, CancellationToken ct) =>
        db.Set<BackgroundJob>().AsNoTracking()
            .Where(j => j.Kind == kind && j.Key == key && j.Status == JobStatus.Pending)
            .Select(j => (Guid?)j.Id)
            .FirstOrDefaultAsync(ct);
}

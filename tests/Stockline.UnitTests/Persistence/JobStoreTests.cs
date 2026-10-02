using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Stockline.Application.Jobs;
using Stockline.Domain.Jobs;
using Stockline.Infrastructure.Persistence;

namespace Stockline.UnitTests.Persistence;

public sealed class JobStoreTests : IDisposable
{
    private sealed class MovableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"stockline-jobs-{Guid.NewGuid():N}.db");
    private readonly MovableClock _clock = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));

    public JobStoreTests()
    {
        using var context = NewContext();
        context.Database.EnsureCreated();
        context.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
    }

    private StocklineDbContext NewContext() =>
        new(new DbContextOptionsBuilder<StocklineDbContext>().UseSqlite($"Data Source={_path}").Options);

    /// <summary>Every call gets its own context, like a worker (or another instance) would.</summary>
    private async Task<T> WithStore<T>(Func<EfJobStore, Task<T>> action)
    {
        await using var context = NewContext();
        return await action(new EfJobStore(context, _clock));
    }

    private Task<Guid> Enqueue(string? key = null, int maxAttempts = 3, JobKind kind = JobKind.ErpSubmission) =>
        WithStore(s => s.EnqueueAsync(new NewJob(kind, key, "{}", MaxAttempts: maxAttempts), default));

    [Fact]
    public async Task Enqueuing_the_same_key_while_the_job_is_waiting_returns_the_same_job()
    {
        var first = await Enqueue("order-1");
        var second = await Enqueue("order-1");
        var other = await Enqueue("order-2");

        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
    }

    [Fact]
    public async Task A_request_that_arrives_while_the_job_runs_gets_its_own_job_that_waits_for_the_running_one()
    {
        var running = await Enqueue("order-1");
        await WithStore(s => s.ClaimNextAsync("w1", Lease, default));

        var next = await Enqueue("order-1");
        Assert.NotEqual(running, next);

        // Same key: not claimable while the first is still running...
        Assert.Null(await WithStore(s => s.ClaimNextAsync("w2", Lease, default)));
        await WithStore(async s => { await s.CompleteAsync(running, "w1", JobOutcome.Succeeded(), default); return 0; });

        // ...and claimable as soon as it finishes.
        Assert.Equal(next, (await WithStore(s => s.ClaimNextAsync("w2", Lease, default)))!.Id);
    }

    [Fact]
    public async Task Jobs_with_different_keys_run_at_the_same_time()
    {
        await Enqueue("order-1");
        await Enqueue("order-2");

        Assert.NotNull(await WithStore(s => s.ClaimNextAsync("w1", Lease, default)));
        Assert.NotNull(await WithStore(s => s.ClaimNextAsync("w2", Lease, default)));
    }

    [Fact]
    public async Task A_finished_job_no_longer_blocks_the_same_key()
    {
        var first = await Enqueue("order-1");
        var claimed = await WithStore(s => s.ClaimNextAsync("w1", Lease, default));
        await WithStore(async s => { await s.CompleteAsync(first, "w1", JobOutcome.Succeeded("ok"), default); return 0; });

        var again = await Enqueue("order-1");

        Assert.Equal(first, claimed!.Id);
        Assert.NotEqual(first, again);
    }

    [Fact]
    public async Task Jobs_without_a_key_are_never_merged()
    {
        Assert.NotEqual(await Enqueue(), await Enqueue());
    }

    [Fact]
    public async Task Jobs_are_claimed_oldest_first_and_only_once()
    {
        var first = await Enqueue("a");
        _clock.Now += TimeSpan.FromSeconds(1);
        var second = await Enqueue("b");

        var one = await WithStore(s => s.ClaimNextAsync("w1", Lease, default));
        var two = await WithStore(s => s.ClaimNextAsync("w2", Lease, default));
        var none = await WithStore(s => s.ClaimNextAsync("w3", Lease, default));

        Assert.Equal(first, one!.Id);
        Assert.Equal(second, two!.Id);
        Assert.Null(none);
        Assert.Equal(1, one.Attempts);
        Assert.Equal(JobStatus.Running, one.Status);
    }

    [Fact]
    public async Task Many_workers_claiming_at_once_never_run_the_same_job_twice()
    {
        const int jobs = 40;
        for (var i = 0; i < jobs; i++)
        {
            await Enqueue($"order-{i}");
        }

        var claimedIds = new System.Collections.Concurrent.ConcurrentBag<Guid>();
        var workers = Enumerable.Range(0, 8).Select(w => Task.Run(async () =>
        {
            while (await WithStore(s => s.ClaimNextAsync($"worker-{w}", Lease, default)) is { } job)
            {
                claimedIds.Add(job.Id);
            }
        }));
        await Task.WhenAll(workers);

        Assert.Equal(jobs, claimedIds.Count);
        Assert.Equal(jobs, claimedIds.Distinct().Count());
    }

    [Fact]
    public async Task A_running_job_is_taken_over_only_after_its_lease_expires()
    {
        await Enqueue("a");
        await WithStore(s => s.ClaimNextAsync("dead-worker", Lease, default));

        Assert.Null(await WithStore(s => s.ClaimNextAsync("w2", Lease, default)));

        _clock.Now += Lease + TimeSpan.FromSeconds(1);
        var taken = await WithStore(s => s.ClaimNextAsync("w2", Lease, default));

        Assert.Equal("w2", taken!.ClaimedBy);
        Assert.Equal(2, taken.Attempts);
    }

    [Fact]
    public async Task Renewing_the_lease_keeps_other_workers_away()
    {
        var id = await Enqueue("a");
        await WithStore(s => s.ClaimNextAsync("w1", Lease, default));

        _clock.Now += TimeSpan.FromSeconds(25);
        Assert.True(await WithStore(s => s.ReportProgressAsync(id, "w1", 40, "Validating", Lease, default)));
        _clock.Now += TimeSpan.FromSeconds(25);

        Assert.Null(await WithStore(s => s.ClaimNextAsync("w2", Lease, default)));
        var job = await WithStore(s => s.GetAsync(id, default));
        Assert.Equal((40, "Validating"), (job!.Progress, job.Stage));
    }

    [Fact]
    public async Task A_worker_that_lost_the_job_cannot_report_or_complete_it()
    {
        var id = await Enqueue("a");
        await WithStore(s => s.ClaimNextAsync("w1", Lease, default));
        _clock.Now += Lease + TimeSpan.FromSeconds(1);
        await WithStore(s => s.ClaimNextAsync("w2", Lease, default));

        Assert.False(await WithStore(s => s.ReportProgressAsync(id, "w1", 50, null, Lease, default)));
        await WithStore(async s => { await s.CompleteAsync(id, "w1", JobOutcome.Succeeded("late"), default); return 0; });

        var job = await WithStore(s => s.GetAsync(id, default));
        Assert.Equal(JobStatus.Running, job!.Status);
        Assert.Equal("w2", job.ClaimedBy);
    }

    [Fact]
    public async Task A_retry_waits_for_the_back_off_and_gives_up_when_the_attempts_run_out()
    {
        var id = await Enqueue("a", maxAttempts: 2);

        await WithStore(s => s.ClaimNextAsync("w1", Lease, default));
        await WithStore(async s => { await s.CompleteAsync(id, "w1", JobOutcome.Retry("boom", TimeSpan.FromSeconds(60)), default); return 0; });
        var waiting = await WithStore(s => s.GetAsync(id, default));
        Assert.Equal(JobStatus.Pending, waiting!.Status);
        Assert.Equal("boom", waiting.Error);
        Assert.Null(await WithStore(s => s.ClaimNextAsync("w1", Lease, default)));

        _clock.Now += TimeSpan.FromSeconds(61);
        await WithStore(s => s.ClaimNextAsync("w1", Lease, default));
        await WithStore(async s => { await s.CompleteAsync(id, "w1", JobOutcome.Retry("boom again"), default); return 0; });

        var failed = await WithStore(s => s.GetAsync(id, default));
        Assert.Equal(JobStatus.Failed, failed!.Status);
        Assert.Equal("boom again", failed.Error);
        Assert.NotNull(failed.FinishedAt);
    }

    [Fact]
    public async Task A_job_abandoned_on_its_last_attempt_is_failed_instead_of_run_forever()
    {
        var id = await Enqueue("a", maxAttempts: 1);
        await WithStore(s => s.ClaimNextAsync("dead-worker", Lease, default));
        _clock.Now += Lease + TimeSpan.FromSeconds(1);

        Assert.Null(await WithStore(s => s.ClaimNextAsync("w2", Lease, default)));

        var job = await WithStore(s => s.GetAsync(id, default));
        Assert.Equal(JobStatus.Failed, job!.Status);
    }

    [Fact]
    public async Task Success_stores_the_result_and_drops_the_uploaded_data()
    {
        var id = await WithStore(s => s.EnqueueAsync(new NewJob(JobKind.OrderImport, null, "{}", [1, 2, 3], "user-1"), default));
        var claimed = await WithStore(s => s.ClaimNextAsync("w1", Lease, default));
        Assert.Equal([1, 2, 3], claimed!.Data);

        await WithStore(async s => { await s.CompleteAsync(id, "w1", JobOutcome.Succeeded("{\"ok\":true}"), default); return 0; });

        var job = await WithStore(s => s.GetAsync(id, default));
        Assert.Equal((JobStatus.Succeeded, 100, "{\"ok\":true}"), (job!.Status, job.Progress, job.Result));
        Assert.Null(job.Data);
        Assert.True(job.IsFinished);
    }

    [Fact]
    public async Task Listing_filters_by_owner_and_kind_and_purge_removes_only_old_finished_jobs()
    {
        var mine = await WithStore(s => s.EnqueueAsync(new NewJob(JobKind.OrderImport, null, "{}", CreatedBy: "me"), default));
        await WithStore(s => s.EnqueueAsync(new NewJob(JobKind.OrderImport, null, "{}", CreatedBy: "you"), default));
        await Enqueue("erp");

        Assert.Equal([mine], (await WithStore(s => s.ListAsync("me", null, 10, default))).Select(j => j.Id));
        Assert.Equal(2, (await WithStore(s => s.ListAsync(null, JobKind.OrderImport, 10, default))).Count);

        await WithStore(s => s.ClaimNextAsync("w1", Lease, default));
        await WithStore(async s => { await s.CompleteAsync(mine, "w1", JobOutcome.Succeeded(), default); return 0; });
        Assert.Equal(0, await WithStore(s => s.PurgeFinishedAsync(_clock.Now.AddDays(-1), default)));
        Assert.Equal(1, await WithStore(s => s.PurgeFinishedAsync(_clock.Now.AddDays(1), default)));
        Assert.Equal(2, (await WithStore(s => s.ListAsync(null, null, 10, default))).Count);
    }

    public void Dispose()
    {
        using (var own = new SqliteConnection($"Data Source={_path}"))
        {
            SqliteConnection.ClearPool(own);
        }

        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            File.Delete(_path + suffix);
        }
    }
}

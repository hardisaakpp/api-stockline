using System.Security.Claims;
using System.Text.Json;
using Stockline.Api.Auth;
using Stockline.Application.Jobs;
using Stockline.Domain.Jobs;

namespace Stockline.Api.Orders;

public static class JobEndpoints
{
    private const long MaxTemplateBytes = 10 * 1024 * 1024;

    public static IEndpointRouteBuilder MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/orders/import/async", EnqueueImportAsync)
            .RequireAuthorization(JwtAuthentication.BuyerPolicy)
            .RequireRateLimiting(HeavyWork.PolicyName)
            .DisableAntiforgery()
            .WithTags("Jobs")
            .WithName("ImportOrderAsync");

        var group = app.MapGroup("/jobs").RequireAuthorization().WithTags("Jobs");
        group.MapGet("/{id:guid}", GetAsync).WithName("GetJob");
        group.MapGet(string.Empty, ListAsync).WithName("ListJobs");
        return app;
    }

    /// <summary>
    /// Queues the import and answers at once with the job to poll. The job's <c>result</c> holds what a synchronous import
    /// would have answered: the outcome, the created order id and every validation issue.
    /// </summary>
    private static async Task<IResult> EnqueueImportAsync(
        IFormFile? file,
        ClaimsPrincipal user,
        IJobStore jobs,
        bool? validateErp,
        bool? requireErp,
        [Microsoft.AspNetCore.Mvc.FromForm] string? corrections,
        CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            return Results.Problem(statusCode: 400, title: "A template file is required.");
        }

        if (file.Length > MaxTemplateBytes)
        {
            return Results.Problem(statusCode: 413, title: "The template is larger than 10 MB.");
        }

        if (!file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Problem(statusCode: 400, title: "Only .xlsx templates are supported.");
        }

        Dictionary<string, string>? map = null;
        if (!string.IsNullOrWhiteSpace(corrections))
        {
            try
            {
                map = JsonSerializer.Deserialize<Dictionary<string, string>>(corrections);
            }
            catch (JsonException)
            {
                map = null;
            }

            if (map is null)
            {
                return Results.Problem(
                    statusCode: 400, title: "corrections must be a JSON object such as {\"WRONG-CODE\": \"RIGHT-CODE\"}.");
            }
        }

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        var payload = JsonSerializer.Serialize(
            new ImportJobPayload(Path.GetFileName(file.FileName), validateErp == true, requireErp == true, map), ImportJobHandler.Json);
        var id = await jobs.EnqueueAsync(
            new NewJob(JobKind.OrderImport, null, payload, buffer.ToArray(), user.FindFirstValue("sub"), MaxAttempts: 1), ct);
        return Results.Accepted($"/jobs/{id}", new { id, status = JobStatus.Pending.ToString() });
    }

    private static async Task<IResult> GetAsync(Guid id, ClaimsPrincipal user, IJobStore jobs, CancellationToken ct)
    {
        var job = await jobs.GetAsync(id, ct);
        return job is null || !CanSee(job, user) ? Results.NotFound() : Results.Ok(JobResponse.From(job));
    }

    private static async Task<IResult> ListAsync(ClaimsPrincipal user, IJobStore jobs, string? kind, int? limit, CancellationToken ct)
    {
        JobKind? parsed = null;
        if (!string.IsNullOrWhiteSpace(kind))
        {
            if (!Enum.TryParse<JobKind>(kind, ignoreCase: true, out var value) || !Enum.IsDefined(value))
            {
                return Results.Problem(
                    statusCode: 400, title: $"Unknown job kind '{kind}'.",
                    extensions: new Dictionary<string, object?> { ["validKinds"] = Enum.GetNames<JobKind>() });
            }

            parsed = value;
        }

        var owner = IsAdmin(user) ? null : user.FindFirstValue("sub");
        var list = await jobs.ListAsync(owner, parsed, limit ?? 50, ct);
        return Results.Ok(list.Select(JobResponse.From));
    }

    private static bool IsAdmin(ClaimsPrincipal user) => user.FindFirstValue("is_admin") == "true";

    private static bool CanSee(BackgroundJob job, ClaimsPrincipal user) =>
        IsAdmin(user) || (job.CreatedBy is not null && job.CreatedBy == user.FindFirstValue("sub"));
}

public sealed record JobResponse(
    Guid Id,
    string Kind,
    string Status,
    int Progress,
    string? Stage,
    int Attempts,
    string? Error,
    JsonElement? Result,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt)
{
    public static JobResponse From(BackgroundJob job) => new(
        job.Id, job.Kind.ToString(), job.Status.ToString(), job.Progress, job.Stage, job.Attempts, job.Error,
        ParseResult(job.Result), job.CreatedAt, job.StartedAt, job.FinishedAt);

    private static JsonElement? ParseResult(string? result)
    {
        if (string.IsNullOrEmpty(result))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<JsonElement>(result);
        }
        catch (JsonException)
        {
            // A plain text result (such as "sent") is returned as a JSON string.
            return JsonSerializer.SerializeToElement(result);
        }
    }
}

/// <summary>Limits how many CPU-heavy requests (workbooks, PDFs, template uploads) one instance runs at once.</summary>
public static class HeavyWork
{
    public const string PolicyName = "heavy";
}

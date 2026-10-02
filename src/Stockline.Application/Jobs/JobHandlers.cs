using System.Text.Json;
using Stockline.Application.Erp;
using Stockline.Application.Orders.Import;
using Stockline.Domain.Jobs;

namespace Stockline.Application.Jobs;

/// <summary>Sends an order's documents to the ERP. The job key is the order id.</summary>
public sealed class ErpSubmissionJobHandler(SubmitOrderToErpHandler handler) : IJobHandler
{
    public JobKind Kind => JobKind.ErpSubmission;

    public async Task<JobOutcome> HandleAsync(BackgroundJob job, IJobProgress progress, CancellationToken ct)
    {
        if (!Guid.TryParse(job.Key, out var orderId))
        {
            return JobOutcome.Failed($"'{job.Key}' is not an order id.");
        }

        var result = await handler.HandleAsync(orderId, ct);

        // A failed submission is a normal result (the order moves to its failed state and the retry sweep queues it again),
        // so the job itself succeeded.
        return JobOutcome.Succeeded(JsonSerializer.Serialize(new { outcome = result.Outcome.ToString(), status = result.Status?.ToString() }));
    }
}

public sealed record ImportJobPayload(string FileName, bool ValidateErp, bool RequireErp, Dictionary<string, string>? Corrections);

public sealed record ImportJobResult(string Outcome, Guid? OrderId, IReadOnlyList<ImportIssue> Issues, string? Message);

/// <summary>Imports an uploaded template in the background and reports each stage as progress.</summary>
public sealed class ImportJobHandler(ImportOrderHandler handler) : IJobHandler
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public JobKind Kind => JobKind.OrderImport;

    public async Task<JobOutcome> HandleAsync(BackgroundJob job, IJobProgress progress, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<ImportJobPayload>(job.Payload, Json);
        if (payload is null || job.Data is null)
        {
            return JobOutcome.Failed("The uploaded file is missing.");
        }

        var options = new ImportOptions(
            payload.ValidateErp, payload.RequireErp, payload.Corrections,
            (stage, token) => progress.ReportAsync(Percent(stage), stage.ToString(), token));

        await using var stream = new MemoryStream(job.Data);
        var result = await handler.HandleAsync(stream, payload.FileName, job.CreatedBy, options, ct);

        // Validation problems are an answer for the user, not a failure of the job.
        return JobOutcome.Succeeded(JsonSerializer.Serialize(
            new ImportJobResult(result.Outcome.ToString(), result.OrderId, result.Issues, result.Message), Json));
    }

    private static int Percent(ImportStage stage) => stage switch
    {
        ImportStage.Reading => 10,
        ImportStage.Validating => 30,
        ImportStage.CheckingErp => 55,
        _ => 85,
    };
}

using System.Security.Claims;
using Stockline.Api.Auth;
using Stockline.Application.Erp;
using Stockline.Api.Background;
using Stockline.Application.Orders;
using Stockline.Application.Reports;
using Stockline.Domain.Erp;
using Stockline.Domain.Orders;

namespace Stockline.Api.Orders;

public static class ErpEndpoints
{
    public static IEndpointRouteBuilder MapErpEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/orders/{id:guid}/erp-submission").RequireAuthorization().RequireVisibleOrder().WithTags("ERP");

        group.MapPost(string.Empty, RequestAsync).WithName("RequestErpSubmission");
        group.MapGet(string.Empty, GetAsync).WithName("GetErpSubmission");
        group.MapGet("/export", ExportAsync).RequireRateLimiting(HeavyWork.PolicyName).RequireAuthorization(JwtAuthentication.PartnerPolicy).WithName("ExportErpIntegration");

        var documents = app.MapGroup("/orders/{id:guid}/erp-documents")
            .RequireAuthorization(JwtAuthentication.PartnerPolicy).RequireVisibleOrder().WithTags("ERP");
        documents.MapGet("/{documentId:guid}/pdf", PdfAsync).RequireRateLimiting(HeavyWork.PolicyName).WithName("PurchaseOrderPdf");

        var overview = app.MapGroup("/erp").RequireAuthorization(JwtAuthentication.PartnerPolicy).WithTags("ERP");
        overview.MapGet("/documents", ListDocumentsAsync).WithName("ListErpDocuments");
        overview.MapGet("/attempts", ListAttemptsAsync).WithName("ListErpAttempts");
        overview.MapGet("/items", ItemsAsync).WithName("LookUpErpItems");
        return app;
    }

    private static async Task<IResult> ExportAsync(
        Guid id, IOrderRepository orders, IErpDocumentRepository documents, IOrderReportExporter exporter, CancellationToken ct)
    {
        var order = await orders.GetAsync(id, ct);
        if (order is null)
        {
            return Results.NotFound();
        }

        var report = ErpIntegrationReport.Build(order, await documents.ListByOrderAsync(id, ct));
        return Results.File(
            exporter.Export(report),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"erp-integration-{order.Header.Code}.xlsx");
    }

    private static async Task<IResult> PdfAsync(
        Guid id,
        Guid documentId,
        IOrderRepository orders,
        IErpDocumentRepository documents,
        IPurchaseOrderPdfRenderer renderer,
        BuyerProfile buyer,
        TimeProvider clock,
        CancellationToken ct)
    {
        var order = await orders.GetAsync(id, ct);
        var document = (await documents.ListByOrderAsync(id, ct)).FirstOrDefault(d => d.Id == documentId);
        if (order is null || document is null)
        {
            return Results.NotFound();
        }

        if (document.Kind != ErpDocumentKind.PurchaseOrder)
        {
            return Results.Problem(statusCode: 400, title: "Only purchase orders can be printed.");
        }

        var sheet = PurchaseOrderSheet.Build(order, document, buyer, clock.GetUtcNow());
        return Results.File(renderer.Render(sheet), "application/pdf", $"purchase-order-{sheet.Number}.pdf");
    }

    private static async Task<IResult> ListDocumentsAsync(
        string? orderCode, string? kind, string? status, bool? retryPending, int? page, int? pageSize,
        IErpQueries queries, Microsoft.Extensions.Options.IOptions<ErpBackgroundOptions> background, CancellationToken ct)
    {
        ErpDocumentKind? parsedKind = null;
        if (!string.IsNullOrWhiteSpace(kind))
        {
            if (!Enum.TryParse<ErpDocumentKind>(kind, ignoreCase: true, out var k) || !Enum.IsDefined(k))
            {
                return Results.Problem(statusCode: 400, title: $"Unknown kind '{kind}'.", extensions: new Dictionary<string, object?> { ["validKinds"] = Enum.GetNames<ErpDocumentKind>() });
            }

            parsedKind = k;
        }

        ErpDocumentStatus? parsedStatus = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<ErpDocumentStatus>(status, ignoreCase: true, out var st) || !Enum.IsDefined(st))
            {
                return Results.Problem(statusCode: 400, title: $"Unknown status '{status}'.", extensions: new Dictionary<string, object?> { ["validStatuses"] = Enum.GetNames<ErpDocumentStatus>() });
            }

            parsedStatus = st;
        }

        var query = new ErpDocumentQuery(
            orderCode, parsedKind, parsedStatus, retryPending == true, background.Value.MaxAttempts, page ?? 1, pageSize ?? 20);
        return Results.Ok(await queries.ListDocumentsAsync(query, ct));
    }

    /// <param name="codes">Comma-separated product codes (up to 100).</param>
    private static async Task<IResult> ItemsAsync(string? codes, IErpCatalog catalog, CancellationToken ct)
    {
        var wanted = (codes ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (wanted.Count is 0 or > 100)
        {
            return Results.Problem(statusCode: 400, title: "Provide between 1 and 100 comma-separated product codes.");
        }

        var lookup = await catalog.GetItemsAsync(wanted, ct);
        if (!lookup.Succeeded)
        {
            return Results.Problem(statusCode: 502, title: lookup.Error);
        }

        return Results.Ok(wanted.Select(code => lookup.Found.TryGetValue(code, out var item)
            ? new { code, found = true, item.Name, item.Barcode, item.Pack, item.RetailPrice, item.IsActive }
            : new { code, found = false, Name = (string?)null, Barcode = (string?)null, Pack = (string?)null, RetailPrice = (decimal?)null, IsActive = false }));
    }

    private static async Task<IResult> ListAttemptsAsync(
        string? orderCode, bool? succeeded, DateTimeOffset? from, DateTimeOffset? to, int? page, int? pageSize,
        IErpQueries queries, CancellationToken ct) =>
        Results.Ok(await queries.ListAttemptsAsync(new ErpAttemptQuery(orderCode, succeeded, from, to, page ?? 1, pageSize ?? 50), ct));

    private static async Task<IResult> RequestAsync(
        Guid id, ClaimsPrincipal user, RequestErpSubmissionHandler handler, CancellationToken ct)
    {
        if (ActorResolver.Resolve(user) is not { } actor)
        {
            return Results.Problem(statusCode: 403, title: "The token has no recognized role.");
        }

        var result = await handler.HandleAsync(id, actor, ct);
        return result.Outcome switch
        {
            RequestSubmissionOutcome.Accepted => Results.Accepted($"/orders/{id}/erp-submission"),
            RequestSubmissionOutcome.NotFound => Results.NotFound(),
            _ => Results.Problem(
                statusCode: 409,
                title: $"{actor} cannot submit an order in {result.Status} to the ERP.",
                extensions: new Dictionary<string, object?> { ["currentStatus"] = result.Status.ToString() }),
        };
    }

    private static async Task<IResult> GetAsync(
        Guid id, IOrderRepository orders, IErpDocumentRepository documents, CancellationToken ct)
    {
        var order = await orders.GetAsync(id, ct);
        if (order is null)
        {
            return Results.NotFound();
        }

        var docs = await documents.ListByOrderAsync(id, ct);
        return Results.Ok(new
        {
            orderStatus = order.Status.ToString(),
            documents = docs.Select(d => new
            {
                d.Id,
                kind = d.Kind.ToString(),
                d.OriginWarehouse,
                d.DestinationLocation,
                status = d.Status.ToString(),
                d.Attempts,
                d.LastError,
                d.DocEntry,
                d.DocNum,
                d.SentAt,
                lines = d.Lines,
            }),
        });
    }
}

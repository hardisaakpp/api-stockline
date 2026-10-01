using Stockline.Api.Auth;
using Stockline.Application.Erp;
using Stockline.Application.Orders;
using Stockline.Application.Reports;

namespace Stockline.Api.Orders;

public static class ReportEndpoints
{
    private const string ExcelType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/orders/{id:guid}/reports").RequireAuthorization().WithTags("Reports");

        // Margin exposes the buyer's retail prices and the supplier's costs: buyer only.
        group.MapGet("/margin", MarginAsync)
            .RequireAuthorization(JwtAuthentication.BuyerPolicy)
            .WithName("ExportMarginReport");
        group.MapGet("/differences", DifferencesAsync).WithName("ExportDifferencesReport");
        group.MapGet("/transfers", TransfersAsync).WithName("ExportTransfersReport");
        return app;
    }

    private static async Task<IResult> MarginAsync(
        Guid id, IOrderRepository orders, IOrderReportExporter exporter, CancellationToken ct)
    {
        var order = await orders.GetAsync(id, ct);
        return order is null
            ? Results.NotFound()
            : Results.File(exporter.Export(MarginReport.Build(order)), ExcelType, $"margin-{order.Header.Code}.xlsx");
    }

    private static async Task<IResult> DifferencesAsync(
        Guid id, IOrderRepository orders, IOrderReportExporter exporter, CancellationToken ct)
    {
        var order = await orders.GetAsync(id, ct);
        return order is null
            ? Results.NotFound()
            : Results.File(exporter.Export(DifferencesReport.Build(order)), ExcelType, $"differences-{order.Header.Code}.xlsx");
    }

    private static async Task<IResult> TransfersAsync(
        Guid id, IOrderRepository orders, IErpDocumentRepository documents, IOrderReportExporter exporter, CancellationToken ct)
    {
        var order = await orders.GetAsync(id, ct);
        if (order is null)
        {
            return Results.NotFound();
        }

        var report = TransfersReport.Build(order, await documents.ListByOrderAsync(id, ct));
        return Results.File(exporter.Export(report), ExcelType, $"transfers-{order.Header.Code}.xlsx");
    }
}

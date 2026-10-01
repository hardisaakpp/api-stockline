using System.Security.Claims;
using Stockline.Api.Auth;
using Stockline.Application.Erp;
using Stockline.Application.Orders;
using Stockline.Domain.Orders;

namespace Stockline.Api.Orders;

public static class ErpEndpoints
{
    public static IEndpointRouteBuilder MapErpEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/orders/{id:guid}/erp-submission").RequireAuthorization().WithTags("ERP");

        group.MapPost(string.Empty, RequestAsync).WithName("RequestErpSubmission");
        group.MapGet(string.Empty, GetAsync).WithName("GetErpSubmission");
        return app;
    }

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

using System.Security.Claims;
using Stockline.Api.Auth;
using Stockline.Application.Erp;
using Stockline.Application.Orders;
using Stockline.Application.Orders.Redistribution;
using Stockline.Application.Orders.Supplier;
using Stockline.Domain.Orders;

namespace Stockline.Api.Orders;

public sealed record QuantityRequest(decimal Quantity);

public sealed record CostRequest(decimal? Cost);

public sealed record CommentRequest(string? Comment);

public sealed record ProductCodeRequest(string? NewCode);

public sealed record ChildOrderBody(DateOnly DeliveryDate, Dictionary<string, decimal> Portions);

public sealed record ConfirmBody(bool AcknowledgeDifference, List<ChildOrderBody>? Children);

public sealed record PlanAllocationBody(string LocationCode, decimal Quantity);

public sealed record PlanLineBody(string ProductCode, List<PlanAllocationBody> Allocations);

public sealed record PlanBody(List<PlanLineBody> Lines);

public static class OrderWorkflowEndpoints
{
    public static IEndpointRouteBuilder MapOrderWorkflowEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/orders/{id:guid}").RequireAuthorization().RequireVisibleOrder().WithTags("Order workflow");

        group.MapPatch("/lines/{productCode}/confirmed-quantity", ConfirmQuantityAsync);
        group.MapPatch("/lines/{productCode}/cost", ChangeCostAsync);
        group.MapPatch("/lines/{productCode}/comment", ChangeCommentAsync);
        group.MapPut("/lines/{productCode}/product-code", CorrectProductCodeAsync);
        group.MapGet("/difference", GetDifferenceAsync);
        group.MapPost("/confirm", ConfirmAsync);
        group.MapGet("/redistribution", GetRedistributionAsync);
        group.MapGet("/redistribution/proposal", GetProposalAsync);
        group.MapPut("/redistribution/draft", SaveDraftAsync);
        group.MapPost("/redistribution/confirm", ConfirmRedistributionAsync);
        return app;
    }

    private static async Task<IResult> ConfirmQuantityAsync(
        Guid id, string productCode, QuantityRequest body, ClaimsPrincipal user, EditOrderLineHandler handler, CancellationToken ct) =>
        ActorResolver.Resolve(user) is { } actor
            ? ToResult(await handler.ConfirmQuantityAsync(id, productCode, body.Quantity, actor, user.FindFirstValue("sub"), ct), actor)
            : NoRole();

    private static async Task<IResult> ChangeCostAsync(
        Guid id, string productCode, CostRequest body, ClaimsPrincipal user, EditOrderLineHandler handler, CancellationToken ct) =>
        ActorResolver.Resolve(user) is { } actor
            ? ToResult(await handler.ChangeCostAsync(id, productCode, body.Cost, actor, user.FindFirstValue("sub"), ct), actor)
            : NoRole();

    private static async Task<IResult> ChangeCommentAsync(
        Guid id, string productCode, CommentRequest body, ClaimsPrincipal user, EditOrderLineHandler handler, CancellationToken ct) =>
        ActorResolver.Resolve(user) is { } actor
            ? ToResult(await handler.ChangeCommentAsync(id, productCode, body.Comment, actor, user.FindFirstValue("sub"), ct), actor)
            : NoRole();

    private static async Task<IResult> CorrectProductCodeAsync(
        Guid id, string productCode, ProductCodeRequest body, ClaimsPrincipal user, CorrectProductCodeHandler handler, CancellationToken ct) =>
        ActorResolver.Resolve(user) is { } actor
            ? ToResult(await handler.HandleAsync(id, productCode, body.NewCode ?? string.Empty, actor, user.FindFirstValue("sub"), ct), actor)
            : NoRole();

    private static async Task<IResult> GetDifferenceAsync(Guid id, IOrderRepository orders, CancellationToken ct)
    {
        var order = await orders.GetAsync(id, ct);
        if (order is null)
        {
            return Results.NotFound();
        }

        var lines = order.ProductionDifference();
        return Results.Ok(new { hasDifference = lines.Count > 0, lines });
    }

    private static async Task<IResult> ConfirmAsync(
        Guid id, ConfirmBody body, ClaimsPrincipal user, ConfirmBySupplierHandler handler, CancellationToken ct)
    {
        if (ActorResolver.Resolve(user) is not { } actor)
        {
            return NoRole();
        }

        var children = (body.Children ?? [])
            .Select(c => new ChildOrderRequest(
                c.DeliveryDate, new Dictionary<string, decimal>(c.Portions, StringComparer.OrdinalIgnoreCase)))
            .ToList();
        var result = await handler.HandleAsync(
            id, new ConfirmBySupplierRequest(body.AcknowledgeDifference, children), actor, user.FindFirstValue("sub"), ct);

        return result.Outcome == CommandOutcome.Succeeded
            ? Results.Ok(new
            {
                order = OrderResponse.From(result.Order!, actor),
                childOrders = result.CreatedOrders!.Select(c => new { c.Id, c.Header.Code }),
            })
            : ToResult(result, actor);
    }

    private static async Task<IResult> GetRedistributionAsync(Guid id, IOrderRepository orders, CancellationToken ct)
    {
        var order = await orders.GetAsync(id, ct);
        if (order is null)
        {
            return Results.NotFound();
        }

        return Results.Ok(new
        {
            status = order.Status.ToString(),
            version = order.RedistributionVersion,
            locations = order.Locations,
            lines = order.Lines.Select(l => new
            {
                l.ProductCode,
                l.Description,
                l.RequestedQuantity,
                l.ConfirmedQuantity,
                allocatedQuantity = l.TotalQuantity,
                allocations = l.Allocations.Select(a => new { a.LocationCode, a.Quantity }),
            }),
            draft = order.RedistributionDraft,
        });
    }

    /// <summary>The proportional starting distribution (original quantities scaled to what the supplier confirmed).</summary>
    private static async Task<IResult> GetProposalAsync(Guid id, IOrderRepository orders, CancellationToken ct)
    {
        var order = await orders.GetAsync(id, ct);
        if (order is null)
        {
            return Results.NotFound();
        }

        if (order.Status is not (OrderStatus.SupplierConfirmed or OrderStatus.ReadyForRedistribution))
        {
            return Results.Problem(statusCode: 409, title: $"An order in {order.Status} has no redistribution to propose.");
        }

        return Results.Ok(new
        {
            status = order.Status.ToString(),
            lines = order.ProposeRedistribution().Select(p => new
            {
                p.ProductCode,
                allocations = p.Allocations.Select(a => new { a.LocationCode, a.Quantity }),
            }),
        });
    }

    private static async Task<IResult> SaveDraftAsync(
        Guid id, PlanBody body, ClaimsPrincipal user, RedistributionHandler handler, CancellationToken ct) =>
        ActorResolver.Resolve(user) is { } actor
            ? ToResult(await handler.SaveDraftAsync(id, ToPlan(body), actor, ct), actor)
            : NoRole();

    private static async Task<IResult> ConfirmRedistributionAsync(
        Guid id, PlanBody body, ClaimsPrincipal user, RedistributionHandler handler, CancellationToken ct) =>
        ActorResolver.Resolve(user) is { } actor
            ? ToResult(await handler.ConfirmAsync(id, ToPlan(body), actor, user.FindFirstValue("sub"), ct), actor)
            : NoRole();

    private static List<PlanLine> ToPlan(PlanBody body) =>
        [.. body.Lines.Select(l => new PlanLine(l.ProductCode, [.. l.Allocations.Select(a => new PlanAllocation(a.LocationCode, a.Quantity))]))];

    private static IResult NoRole() => Results.Problem(statusCode: 403, title: "The token has no recognized role.");

    private static IResult ToResult(CommandResult result, ActorRole actor) => result.Outcome switch
    {
        CommandOutcome.Succeeded => Results.Ok(OrderResponse.From(result.Order!, actor) with { Warnings = result.Issues }),
        CommandOutcome.NotFound => Results.NotFound(),
        CommandOutcome.Forbidden => Results.Problem(statusCode: 403, title: result.Message),
        CommandOutcome.InvalidState or CommandOutcome.Conflict => Results.Problem(statusCode: 409, title: result.Message),
        _ => Results.Problem(
            statusCode: 422,
            title: result.Message ?? "The request is not valid.",
            extensions: new Dictionary<string, object?> { ["issues"] = result.Issues ?? [] }),
    };
}

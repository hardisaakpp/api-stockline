using System.Security.Claims;
using Stockline.Api.Auth;
using Stockline.Application.Orders;
using Stockline.Application.Orders.Import;
using Stockline.Application.Orders.Lifecycle;
using Stockline.Domain.Orders;

namespace Stockline.Api.Orders;

public static class OrderEndpoints
{
    private const long MaxTemplateBytes = 10 * 1024 * 1024;

    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/orders").RequireAuthorization().WithTags("Orders");

        group.MapPost("/import", ImportAsync)
            .RequireAuthorization(JwtAuthentication.BuyerPolicy)
            .DisableAntiforgery()
            .WithName("ImportOrder");

        group.MapGet("/{id:guid}", GetAsync).WithName("GetOrder");
        group.MapGet("/{id:guid}/export", ExportAsync).WithName("ExportOrder");
        group.MapGet("/{id:guid}/actions", GetActionsAsync).WithName("GetOrderActions");
        group.MapPost("/{id:guid}/actions/{action}", ApplyActionAsync).WithName("ApplyOrderAction");

        app.MapOrderWorkflowEndpoints();
        app.MapOrderQueryEndpoints();
        return app;
    }

    private static async Task<IResult> ImportAsync(IFormFile? file, ImportOrderHandler handler, CancellationToken ct)
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

        await using var stream = file.OpenReadStream();
        var result = await handler.HandleAsync(stream, ct);

        return result.Outcome switch
        {
            ImportOutcome.Imported => Results.Created(
                $"/orders/{result.OrderId}", new ImportOrderResponse(result.OrderId!.Value, result.Issues)),
            ImportOutcome.InvalidFormat => Results.Problem(
                statusCode: 400, title: "The template has an invalid format.", detail: result.Message),
            ImportOutcome.DuplicateOrderCode => Results.Problem(
                statusCode: 409,
                title: "An order with this code already exists.",
                extensions: new Dictionary<string, object?> { ["issues"] = result.Issues }),
            _ => Results.Problem(
                statusCode: 422,
                title: "The template has validation errors.",
                extensions: new Dictionary<string, object?> { ["issues"] = result.Issues }),
        };
    }

    private static async Task<IResult> GetAsync(Guid id, IOrderRepository orders, CancellationToken ct)
    {
        var order = await orders.GetAsync(id, ct);
        return order is null ? Results.NotFound() : Results.Ok(OrderResponse.From(order));
    }

    private static async Task<IResult> GetActionsAsync(
        Guid id, ClaimsPrincipal user, IOrderRepository orders, CancellationToken ct)
    {
        var order = await orders.GetAsync(id, ct);
        if (order is null)
        {
            return Results.NotFound();
        }

        var actor = ActorResolver.Resolve(user);
        var actions = actor is { } role ? OrderStateMachine.AvailableActions(order.Status, role) : [];
        return Results.Ok(new { status = order.Status.ToString(), actions = actions.Select(a => a.ToString()) });
    }

    private static async Task<IResult> ApplyActionAsync(
        Guid id, string action, ClaimsPrincipal user, ApplyOrderActionHandler handler, CancellationToken ct)
    {
        if (!Enum.TryParse<OrderAction>(action.Replace("-", string.Empty), ignoreCase: true, out var parsed)
            || !Enum.IsDefined(parsed))
        {
            return Results.Problem(
                statusCode: 400,
                title: $"Unknown action '{action}'.",
                extensions: new Dictionary<string, object?> { ["validActions"] = Enum.GetNames<OrderAction>() });
        }

        if (ActorResolver.Resolve(user) is not { } actor)
        {
            return Results.Problem(statusCode: 403, title: "The token has no recognized role.");
        }

        var result = await handler.HandleAsync(id, parsed, actor, user.FindFirstValue("sub"), ct);

        return result.Outcome switch
        {
            ApplyActionOutcome.Applied => Results.Ok(OrderResponse.From(result.Order!)),
            ApplyActionOutcome.NotFound => Results.NotFound(),
            ApplyActionOutcome.ActorNotAllowed => Results.Problem(
                statusCode: 403, title: $"{actor} cannot perform '{parsed}' on an order in {result.CurrentStatus}."),
            ApplyActionOutcome.InvalidStatus => Results.Problem(
                statusCode: 409,
                title: $"'{parsed}' is not allowed while the order is {result.CurrentStatus}.",
                extensions: new Dictionary<string, object?> { ["currentStatus"] = result.CurrentStatus.ToString() }),
            _ => Results.Problem(statusCode: 409, title: "The order was modified by someone else. Reload and retry."),
        };
    }

    private static async Task<IResult> ExportAsync(Guid id, IOrderRepository orders, IOrderExporter exporter, CancellationToken ct)
    {
        var order = await orders.GetAsync(id, ct);
        return order is null
            ? Results.NotFound()
            : Results.File(
                exporter.ExportTemplate(order),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"order-{order.Header.Code}.xlsx");
    }
}

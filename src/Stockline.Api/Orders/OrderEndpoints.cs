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
        var group = app.MapGroup("/orders").RequireAuthorization().RequireVisibleOrder().WithTags("Orders");

        group.MapPost("/import", ImportAsync)
            .RequireAuthorization(JwtAuthentication.BuyerPolicy)
            .DisableAntiforgery()
            .WithName("ImportOrder");

        group.MapGet("/{id:guid}", GetAsync).WithName("GetOrder");
        group.MapDelete("/{id:guid}", DiscardAsync).WithName("DiscardOrder");
        group.MapGet("/{id:guid}/export", ExportAsync).WithName("ExportOrder");
        group.MapGet("/{id:guid}/source-file", SourceFileAsync)
            .RequireAuthorization(JwtAuthentication.BuyerPolicy)
            .WithName("DownloadSourceFile");
        group.MapGet("/{id:guid}/audit-log", AuditLogAsync).WithName("OrderAuditLog");
        group.MapGet("/{id:guid}/actions", GetActionsAsync).WithName("GetOrderActions");
        group.MapPost("/{id:guid}/actions/{action}", ApplyActionAsync).WithName("ApplyOrderAction");

        app.MapOrderWorkflowEndpoints();
        app.MapOrderQueryEndpoints();
        return app;
    }

    /// <param name="validateErp">Also check the products against the ERP (existence, active, pack and retail price).</param>
    /// <param name="requireErp">Fail the import when the ERP cannot be reached instead of only warning.</param>
    /// <param name="corrections">JSON object mapping a wrong product code to the code that replaces it.</param>
    private static async Task<IResult> ImportAsync(
        IFormFile? file,
        ClaimsPrincipal user,
        ImportOrderHandler handler,
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

        Dictionary<string, string>? correctionMap = null;
        if (!string.IsNullOrWhiteSpace(corrections))
        {
            try
            {
                correctionMap = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(corrections);
            }
            catch (System.Text.Json.JsonException)
            {
                correctionMap = null;
            }

            if (correctionMap is null)
            {
                return Results.Problem(
                    statusCode: 400, title: "corrections must be a JSON object such as {\"WRONG-CODE\": \"RIGHT-CODE\"}.");
            }
        }

        await using var stream = file.OpenReadStream();
        var options = new ImportOptions(validateErp == true, requireErp == true, correctionMap);
        var result = await handler.HandleAsync(stream, Path.GetFileName(file.FileName), user.FindFirstValue("sub"), options, ct);

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

    private static async Task<IResult> GetAsync(Guid id, ClaimsPrincipal user, IOrderRepository orders, CancellationToken ct)
    {
        if (ActorResolver.Resolve(user) is not { } actor)
        {
            return Results.Problem(statusCode: 403, title: "The token has no recognized role.");
        }

        var order = await orders.GetAsync(id, ct);
        return order is null ? Results.NotFound() : Results.Ok(OrderResponse.From(order, actor));
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
            ApplyActionOutcome.Applied => Results.Ok(OrderResponse.From(result.Order!, actor)),
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

    private static async Task<IResult> DiscardAsync(
        Guid id, ClaimsPrincipal user, DiscardOrderHandler handler, CancellationToken ct)
    {
        if (ActorResolver.Resolve(user) is not { } actor)
        {
            return Results.Problem(statusCode: 403, title: "The token has no recognized role.");
        }

        var result = await handler.HandleAsync(id, actor, user.FindFirstValue("sub"), ct);
        return result.Outcome switch
        {
            DiscardOutcome.Discarded => Results.NoContent(),
            DiscardOutcome.NotFound => Results.NotFound(),
            DiscardOutcome.Forbidden => Results.Problem(statusCode: 403, title: $"{actor} cannot discard an order."),
            DiscardOutcome.InvalidStatus => Results.Problem(
                statusCode: 409,
                title: $"An order in {result.Status} cannot be discarded; only Draft and Submitted orders can.",
                extensions: new Dictionary<string, object?> { ["currentStatus"] = result.Status.ToString() }),
            _ => Results.Problem(statusCode: 409, title: "The order was modified by someone else. Reload and retry."),
        };
    }

    private static async Task<IResult> SourceFileAsync(Guid id, IOrderFileStore files, CancellationToken ct)
    {
        var file = await files.GetAsync(id, ct);
        return file is null
            ? Results.NotFound()
            : Results.File(file.Content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", file.FileName);
    }

    private static async Task<IResult> AuditLogAsync(
        Guid id, ClaimsPrincipal user, IOrderRepository orders, CancellationToken ct)
    {
        if (ActorResolver.Resolve(user) is not { } actor)
        {
            return Results.Problem(statusCode: 403, title: "The token has no recognized role.");
        }

        var order = await orders.GetAsync(id, ct);
        return order is null ? Results.NotFound() : Results.Ok(AuditLogEntry.For(order, actor));
    }

    private static async Task<IResult> ExportAsync(
        Guid id, ClaimsPrincipal user, IOrderRepository orders, IOrderExporter exporter, CancellationToken ct)
    {
        if (ActorResolver.Resolve(user) is not { } actor)
        {
            return Results.Problem(statusCode: 403, title: "The token has no recognized role.");
        }

        var order = await orders.GetAsync(id, ct);
        return order is null
            ? Results.NotFound()
            : Results.File(
                exporter.ExportTemplate(order, actor),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"order-{order.Header.Code}.xlsx");
    }
}

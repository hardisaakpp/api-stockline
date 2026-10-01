using Stockline.Api.Auth;
using Stockline.Application.Orders;
using Stockline.Application.Orders.Queries;
using Stockline.Domain.Orders;

namespace Stockline.Api.Orders;

public static class OrderQueryEndpoints
{
    public static IEndpointRouteBuilder MapOrderQueryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/orders", ListAsync).RequireAuthorization().WithTags("Orders").WithName("ListOrders");
        app.MapGet("/orders/filters", FiltersAsync).RequireAuthorization().WithTags("Orders").WithName("OrderFilters");
        app.MapGet("/orders/exists", ExistsAsync).RequireAuthorization().WithTags("Orders").WithName("OrderExists");
        app.MapGet("/orders/last-redistributed", LastRedistributedAsync).RequireAuthorization().WithTags("Orders").WithName("LastRedistributedOrder");
        app.MapGet("/locations", LocationsAsync).RequireAuthorization().WithTags("Locations").WithName("SearchLocations");
        app.MapGet("/dashboard/summary", DashboardAsync).RequireAuthorization().WithTags("Dashboard").WithName("DashboardSummary");
        return app;
    }

    /// <param name="sort">Field name, prefixed with <c>-</c> for descending (e.g. <c>-orderDate</c>). Fields: orderDate, code, status.</param>
    private static async Task<IResult> ListAsync(
        string? status,
        string? search,
        int? page,
        int? pageSize,
        string? sort,
        DateOnly? dateFrom,
        DateOnly? dateTo,
        string? supplier,
        string? createdBy,
        string? modifiedBy,
        bool? pendingMyAction,
        System.Security.Claims.ClaimsPrincipal user,
        IOrderQueries queries,
        CancellationToken ct)
    {
        if (ActorResolver.Resolve(user) is not { } actor)
        {
            return Results.Problem(statusCode: 403, title: "The token has no recognized role.");
        }

        OrderStatus? parsedStatus = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<OrderStatus>(status, ignoreCase: true, out var value) || !Enum.IsDefined(value))
            {
                return Results.Problem(
                    statusCode: 400,
                    title: $"Unknown status '{status}'.",
                    extensions: new Dictionary<string, object?> { ["validStatuses"] = Enum.GetNames<OrderStatus>() });
            }

            parsedStatus = value;
        }

        var descending = sort?.StartsWith('-') ?? true;
        var field = OrderSortField.OrderDate;
        if (!string.IsNullOrWhiteSpace(sort)
            && (!Enum.TryParse(sort.TrimStart('-', '+'), ignoreCase: true, out field) || !Enum.IsDefined(field)))
        {
            return Results.Problem(
                statusCode: 400,
                title: $"Unknown sort field '{sort}'.",
                extensions: new Dictionary<string, object?> { ["validFields"] = Enum.GetNames<OrderSortField>() });
        }

        // "Pending my action" narrows to the statuses where this role has something to do.
        var actionable = pendingMyAction == true
            ? Enum.GetValues<OrderStatus>().Where(s => OrderStateMachine.AvailableActions(s, actor).Count > 0).ToList()
            : null;
        var query = new OrderListQuery(
            parsedStatus, search, page ?? 1, pageSize ?? 20, field, descending, dateFrom, dateTo, supplier, createdBy, modifiedBy,
            actionable, OrderVisibility.VisibleStatuses(actor));
        return Results.Ok(await queries.ListAsync(query, ct));
    }

    private static async Task<IResult> FiltersAsync(
        System.Security.Claims.ClaimsPrincipal user, IOrderQueries queries, CancellationToken ct) =>
        ActorResolver.Resolve(user) is { } actor
            ? Results.Ok(await queries.FiltersAsync(OrderVisibility.VisibleStatuses(actor), ct))
            : Results.Problem(statusCode: 403, title: "The token has no recognized role.");

    private static async Task<IResult> ExistsAsync(string? code, IOrderRepository orders, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(code)
            ? Results.Problem(statusCode: 400, title: "An order code is required.")
            : Results.Ok(new { code = code.Trim(), exists = await orders.ExistsAsync(code.Trim(), ct) });

    private static async Task<IResult> LastRedistributedAsync(
        bool? mine, System.Security.Claims.ClaimsPrincipal user, IOrderQueries queries, CancellationToken ct)
    {
        if (ActorResolver.Resolve(user) is not { } actor)
        {
            return Results.Problem(statusCode: 403, title: "The token has no recognized role.");
        }

        var modifiedBy = mine == true ? user.FindFirst("sub")?.Value : null;
        var last = await queries.LastRedistributedAsync(OrderVisibility.VisibleStatuses(actor), modifiedBy, ct);
        return last is null ? Results.NotFound() : Results.Ok(last);
    }

    private static async Task<IResult> LocationsAsync(
        string? search, string? kind, int? limit, IOrderQueries queries, CancellationToken ct)
    {
        LocationKind? parsed = null;
        if (!string.IsNullOrWhiteSpace(kind))
        {
            if (!Enum.TryParse<LocationKind>(kind, ignoreCase: true, out var value) || !Enum.IsDefined(value))
            {
                return Results.Problem(
                    statusCode: 400,
                    title: $"Unknown kind '{kind}'.",
                    extensions: new Dictionary<string, object?> { ["validKinds"] = Enum.GetNames<LocationKind>() });
            }

            parsed = value;
        }

        return Results.Ok(await queries.SearchLocationsAsync(search, parsed, limit ?? 20, ct));
    }

    private static async Task<IResult> DashboardAsync(
        System.Security.Claims.ClaimsPrincipal user, DashboardHandler handler, CancellationToken ct) =>
        ActorResolver.Resolve(user) is { } actor
            ? Results.Ok(await handler.HandleAsync(actor, ct))
            : Results.Problem(statusCode: 403, title: "The token has no recognized role.");
}

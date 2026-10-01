using Stockline.Api.Auth;
using Stockline.Application.Orders.Queries;
using Stockline.Domain.Orders;

namespace Stockline.Api.Orders;

public static class OrderQueryEndpoints
{
    public static IEndpointRouteBuilder MapOrderQueryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/orders", ListAsync).RequireAuthorization().WithTags("Orders").WithName("ListOrders");
        app.MapGet("/dashboard/summary", DashboardAsync).RequireAuthorization().WithTags("Dashboard").WithName("DashboardSummary");
        return app;
    }

    /// <param name="sort">Field name, prefixed with <c>-</c> for descending (e.g. <c>-orderDate</c>). Fields: orderDate, code, status.</param>
    private static async Task<IResult> ListAsync(
        string? status, string? search, int? page, int? pageSize, string? sort, IOrderQueries queries, CancellationToken ct)
    {
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

        var query = new OrderListQuery(parsedStatus, search, page ?? 1, pageSize ?? 20, field, descending);
        return Results.Ok(await queries.ListAsync(query, ct));
    }

    private static async Task<IResult> DashboardAsync(
        System.Security.Claims.ClaimsPrincipal user, DashboardHandler handler, CancellationToken ct) =>
        ActorResolver.Resolve(user) is { } actor
            ? Results.Ok(await handler.HandleAsync(actor, ct))
            : Results.Problem(statusCode: 403, title: "The token has no recognized role.");
}

using Stockline.Api.Auth;
using Stockline.Application.Orders;
using Stockline.Domain.Orders;

namespace Stockline.Api.Orders;

/// <summary>
/// Answers 404 (not 403, so existence is not revealed) when a role asks for an order outside the statuses it may see.
/// Only roles with a restricted view cost an extra read.
/// </summary>
public sealed class OrderVisibilityFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (http.Request.RouteValues["id"] is string raw
            && Guid.TryParse(raw, out var id)
            && ActorResolver.Resolve(http.User) is { } role
            && OrderVisibility.VisibleStatuses(role) is not null
            && await http.RequestServices.GetRequiredService<IOrderRepository>().GetAsync(id, http.RequestAborted) is { } order
            && !OrderVisibility.CanSee(role, order.Status))
        {
            return Results.NotFound();
        }

        return await next(context);
    }
}

public static class OrderVisibilityExtensions
{
    public static TBuilder RequireVisibleOrder<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter<TBuilder, OrderVisibilityFilter>();
}

using System.Security.Claims;
using Stockline.Api.Auth;
using Stockline.Domain.Orders;
using Stockline.Infrastructure.Notifications;

namespace Stockline.Api.Orders;

public static class NotificationEndpoints
{
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(15);

    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/notifications/stream", (OrderEventHub hub, ClaimsPrincipal user, Guid? orderId) =>
                ActorResolver.Resolve(user) is { } role
                    ? new OrderEventStreamResult(hub.Subscribe(), e => (orderId is null || e.OrderId == orderId) && CanSee(role, e), KeepAlive)
                    : Results.Problem(statusCode: 403, title: "The token has no recognized role."))
            .RequireAuthorization()
            .WithTags("Notifications")
            .WithName("NotificationStream");
        return app;
    }

    // Roles with a restricted view only hear about orders they may open.
    private static bool CanSee(ActorRole role, Stockline.Application.Notifications.OrderEvent e) =>
        !Enum.TryParse<OrderStatus>(e.Status, out var status) || OrderVisibility.CanSee(role, status);
}

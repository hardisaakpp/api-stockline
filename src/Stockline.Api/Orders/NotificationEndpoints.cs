using Stockline.Infrastructure.Notifications;

namespace Stockline.Api.Orders;

public static class NotificationEndpoints
{
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(15);

    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/notifications/stream", (OrderEventHub hub, Guid? orderId) =>
                new OrderEventStreamResult(hub.Subscribe(), e => orderId is null || e.OrderId == orderId, KeepAlive))
            .RequireAuthorization()
            .WithTags("Notifications")
            .WithName("NotificationStream");
        return app;
    }
}

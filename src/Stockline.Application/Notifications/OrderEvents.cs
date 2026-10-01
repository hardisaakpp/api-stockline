using Stockline.Domain.Orders;

namespace Stockline.Application.Notifications;

public sealed record OrderEvent(Guid OrderId, string OrderCode, string Type, string Status, DateTimeOffset At, string? UserId);

public interface IOrderEvents
{
    ValueTask PublishAsync(OrderEvent orderEvent, CancellationToken ct);
}

public static class OrderEventsExtensions
{
    public static ValueTask PublishAsync(
        this IOrderEvents? events, Order order, string type, string? userId, DateTimeOffset at, CancellationToken ct) =>
        events is null
            ? ValueTask.CompletedTask
            : events.PublishAsync(new OrderEvent(order.Id, order.Header.Code, type, order.Status.ToString(), at, userId), ct);
}

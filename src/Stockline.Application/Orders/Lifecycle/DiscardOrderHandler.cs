using Stockline.Application.Notifications;
using Stockline.Domain.Orders;

namespace Stockline.Application.Orders.Lifecycle;

public enum DiscardOutcome
{
    Discarded,
    NotFound,
    Forbidden,
    InvalidStatus,
    Conflict,
}

public sealed record DiscardResult(DiscardOutcome Outcome, OrderStatus? Status);

/// <summary>
/// Throws a draft or just-submitted order away entirely (as opposed to cancelling it, which keeps the record).
/// The supplier is only told when the order had already been submitted.
/// </summary>
public sealed class DiscardOrderHandler(IOrderRepository orders, TimeProvider clock, IOrderEvents? events = null)
{
    public async Task<DiscardResult> HandleAsync(Guid orderId, ActorRole actor, string? userId, CancellationToken ct)
    {
        var order = await orders.GetForUpdateAsync(orderId, ct);
        if (order is null)
        {
            return new DiscardResult(DiscardOutcome.NotFound, null);
        }

        if (actor != ActorRole.Buyer)
        {
            return new DiscardResult(DiscardOutcome.Forbidden, order.Status);
        }

        if (!order.CanBeDiscarded)
        {
            return new DiscardResult(DiscardOutcome.InvalidStatus, order.Status);
        }

        try
        {
            await orders.RemoveAsync(order, ct);
        }
        catch (OrderConcurrencyException)
        {
            return new DiscardResult(DiscardOutcome.Conflict, null);
        }

        await events.PublishAsync(order, "OrderDiscarded", userId, clock.GetUtcNow(), ct);
        return new DiscardResult(DiscardOutcome.Discarded, order.Status);
    }
}

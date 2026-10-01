using Stockline.Application.Notifications;
using Stockline.Domain.Orders;

namespace Stockline.Application.Orders.Lifecycle;

public enum ApplyActionOutcome
{
    Applied,
    NotFound,
    InvalidStatus,
    ActorNotAllowed,
    ConcurrencyConflict,
}

public sealed record ApplyActionResult(ApplyActionOutcome Outcome, Order? Order, OrderStatus? CurrentStatus);

public sealed class ApplyOrderActionHandler(IOrderRepository orders, TimeProvider clock, IOrderEvents? events = null)
{
    public async Task<ApplyActionResult> HandleAsync(
        Guid orderId, OrderAction action, ActorRole actor, string? userId, CancellationToken ct)
    {
        var order = await orders.GetForUpdateAsync(orderId, ct);
        if (order is null)
        {
            return new ApplyActionResult(ApplyActionOutcome.NotFound, null, null);
        }

        var now = clock.GetUtcNow();
        var result = order.Apply(action, actor, userId, now);
        if (!result.Succeeded)
        {
            var outcome = result.Failure == TransitionFailure.InvalidStatus
                ? ApplyActionOutcome.InvalidStatus
                : ApplyActionOutcome.ActorNotAllowed;
            return new ApplyActionResult(outcome, null, order.Status);
        }

        try
        {
            await orders.SaveChangesAsync(ct);
        }
        catch (OrderConcurrencyException)
        {
            return new ApplyActionResult(ApplyActionOutcome.ConcurrencyConflict, null, null);
        }

        await events.PublishAsync(order, action.ToString(), userId, now, ct);
        return new ApplyActionResult(ApplyActionOutcome.Applied, order, order.Status);
    }
}

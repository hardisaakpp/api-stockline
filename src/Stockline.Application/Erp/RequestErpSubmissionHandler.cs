using Stockline.Application.Orders;
using Stockline.Domain.Orders;

namespace Stockline.Application.Erp;

public enum RequestSubmissionOutcome
{
    Accepted,
    NotFound,
    InvalidState,
}

public sealed record RequestSubmissionResult(RequestSubmissionOutcome Outcome, OrderStatus? Status);

public sealed class RequestErpSubmissionHandler(IOrderRepository orders, IErpSubmissionQueue queue)
{
    private static readonly OrderStatus[] Submittable =
        [OrderStatus.RedistributionConfirmed, OrderStatus.ErpSubmissionFailed, OrderStatus.LogisticsPending];

    public async Task<RequestSubmissionResult> HandleAsync(Guid orderId, ActorRole actor, CancellationToken ct)
    {
        var order = await orders.GetAsync(orderId, ct);
        if (order is null)
        {
            return new RequestSubmissionResult(RequestSubmissionOutcome.NotFound, null);
        }

        if (actor != ActorRole.Buyer || !Submittable.Contains(order.Status))
        {
            return new RequestSubmissionResult(RequestSubmissionOutcome.InvalidState, order.Status);
        }

        await queue.EnqueueAsync(orderId, ct);
        return new RequestSubmissionResult(RequestSubmissionOutcome.Accepted, order.Status);
    }
}

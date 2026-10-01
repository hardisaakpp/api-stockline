using System.Collections.Frozen;

namespace Stockline.Domain.Orders;

public static class OrderStateMachine
{
    private sealed record Rule(FrozenSet<OrderStatus> From, OrderStatus To, FrozenSet<ActorRole> Actors);

    public static OrderStatus InitialStatus => OrderStatus.Draft;

    // Child orders carry the production difference; they are created postponed, not reached by an action.
    public static OrderStatus ChildOrderInitialStatus => OrderStatus.ProductionPostponed;

    private static readonly FrozenSet<OrderStatus> ErpRetryable =
        new[] { OrderStatus.RedistributionConfirmed, OrderStatus.ErpSubmissionFailed, OrderStatus.LogisticsPending }
            .ToFrozenSet();

    private static readonly FrozenDictionary<OrderAction, Rule> Rules = new Dictionary<OrderAction, Rule>
    {
        [OrderAction.Submit] = Define([OrderStatus.Draft], OrderStatus.Submitted, [ActorRole.Buyer]),
        [OrderAction.Open] = Define([OrderStatus.Submitted], OrderStatus.UnderReview,
            [ActorRole.Buyer, ActorRole.Supplier, ActorRole.SupplierProduction]),
        [OrderAction.ConfirmProduction] = Define([OrderStatus.UnderReview], OrderStatus.ProductionConfirmed,
            [ActorRole.SupplierProduction]),
        [OrderAction.ConfirmBySupplier] = Define([OrderStatus.UnderReview, OrderStatus.ProductionConfirmed],
            OrderStatus.SupplierConfirmed, [ActorRole.Supplier, ActorRole.Buyer]),
        [OrderAction.CancelBySupplier] = Define([OrderStatus.ProductionConfirmed], OrderStatus.CancelledBySupplier,
            [ActorRole.Supplier]),
        [OrderAction.AcceptPostponed] = Define([OrderStatus.ProductionPostponed], OrderStatus.ReadyForRedistribution,
            [ActorRole.Buyer]),
        [OrderAction.Reject] = Define([OrderStatus.ProductionPostponed], OrderStatus.Rejected, [ActorRole.Buyer]),
        [OrderAction.Cancel] = Define(
            [OrderStatus.Draft, OrderStatus.Submitted, OrderStatus.UnderReview, OrderStatus.SupplierConfirmed],
            OrderStatus.Cancelled, [ActorRole.Buyer]),
        [OrderAction.ConfirmRedistribution] = Define(
            [OrderStatus.SupplierConfirmed, OrderStatus.ReadyForRedistribution],
            OrderStatus.RedistributionConfirmed, [ActorRole.Buyer]),
        [OrderAction.MarkSent] = Define(ErpRetryable, OrderStatus.Sent, [ActorRole.System]),
        [OrderAction.MarkLogisticsPending] = Define(ErpRetryable, OrderStatus.LogisticsPending, [ActorRole.System]),
        [OrderAction.MarkErpSubmissionFailed] = Define(
            [OrderStatus.RedistributionConfirmed, OrderStatus.ErpSubmissionFailed],
            OrderStatus.ErpSubmissionFailed, [ActorRole.System]),
        [OrderAction.MarkDispatchAndInvoice] = Define([OrderStatus.Sent], OrderStatus.DispatchAndInvoice,
            [ActorRole.Supplier, ActorRole.Buyer]),
        [OrderAction.Complete] = Define([OrderStatus.DispatchAndInvoice], OrderStatus.Completed,
            [ActorRole.Supplier, ActorRole.Buyer]),
    }.ToFrozenDictionary();

    private static Rule Define(IEnumerable<OrderStatus> from, OrderStatus to, IEnumerable<ActorRole> actors) =>
        new(from.ToFrozenSet(), to, actors.ToFrozenSet());

    public static TransitionResult Apply(OrderStatus current, OrderAction action, ActorRole actor)
    {
        var rule = Rules[action];

        if (!rule.From.Contains(current))
        {
            return new TransitionResult(current, TransitionFailure.InvalidStatus);
        }

        return rule.Actors.Contains(actor)
            ? new TransitionResult(rule.To, TransitionFailure.None)
            : new TransitionResult(current, TransitionFailure.ActorNotAllowed);
    }

    public static IReadOnlyList<OrderAction> AvailableActions(OrderStatus current, ActorRole actor) =>
        Rules.Where(r => r.Value.From.Contains(current) && r.Value.Actors.Contains(actor))
            .Select(r => r.Key)
            .ToList();

    public static bool IsTerminal(OrderStatus status) =>
        Rules.Values.All(r => !r.From.Contains(status));

    public static IEnumerable<(OrderStatus From, OrderAction Action, OrderStatus To)> Transitions =>
        Rules.SelectMany(r => r.Value.From.Select(from => (from, r.Key, r.Value.To)));
}

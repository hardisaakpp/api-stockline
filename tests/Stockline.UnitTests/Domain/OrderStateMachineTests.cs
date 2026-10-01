using Stockline.Domain.Orders;

namespace Stockline.UnitTests.Domain;

public class OrderStateMachineTests
{
    private static OrderStatus Run(OrderStatus start, params (OrderAction Action, ActorRole Actor)[] steps)
    {
        var status = start;
        foreach (var (action, actor) in steps)
        {
            var result = OrderStateMachine.Apply(status, action, actor);
            Assert.True(result.Succeeded, $"{action} by {actor} failed from {status}: {result.Failure}");
            status = result.Status;
        }

        return status;
    }

    [Fact]
    public void Happy_path_goes_from_draft_to_completed()
    {
        var final = Run(OrderStateMachine.InitialStatus,
            (OrderAction.Submit, ActorRole.Buyer),
            (OrderAction.Open, ActorRole.Supplier),
            (OrderAction.ConfirmProduction, ActorRole.SupplierProduction),
            (OrderAction.ConfirmBySupplier, ActorRole.Supplier),
            (OrderAction.ConfirmRedistribution, ActorRole.Buyer),
            (OrderAction.MarkSent, ActorRole.System),
            (OrderAction.MarkDispatchAndInvoice, ActorRole.Supplier),
            (OrderAction.Complete, ActorRole.Buyer));

        Assert.Equal(OrderStatus.Completed, final);
    }

    [Fact]
    public void Supplier_can_confirm_without_production_step()
    {
        var final = Run(OrderStatus.Draft,
            (OrderAction.Submit, ActorRole.Buyer),
            (OrderAction.Open, ActorRole.Buyer),
            (OrderAction.ConfirmBySupplier, ActorRole.Supplier));

        Assert.Equal(OrderStatus.SupplierConfirmed, final);
    }

    [Fact]
    public void Erp_failure_can_be_retried_until_sent()
    {
        var final = Run(OrderStatus.RedistributionConfirmed,
            (OrderAction.MarkErpSubmissionFailed, ActorRole.System),
            (OrderAction.MarkErpSubmissionFailed, ActorRole.System),
            (OrderAction.MarkLogisticsPending, ActorRole.System),
            (OrderAction.MarkSent, ActorRole.System));

        Assert.Equal(OrderStatus.Sent, final);
    }

    [Theory]
    [InlineData(OrderAction.AcceptPostponed, OrderStatus.ReadyForRedistribution)]
    [InlineData(OrderAction.Reject, OrderStatus.Rejected)]
    public void Postponed_child_order_is_resolved_by_buyer(OrderAction action, OrderStatus expected)
    {
        var final = Run(OrderStateMachine.ChildOrderInitialStatus, (action, ActorRole.Buyer));

        Assert.Equal(expected, final);
    }

    [Fact]
    public void Supplier_can_cancel_only_after_production_confirmed()
    {
        Assert.Equal(OrderStatus.CancelledBySupplier,
            Run(OrderStatus.ProductionConfirmed, (OrderAction.CancelBySupplier, ActorRole.Supplier)));

        var result = OrderStateMachine.Apply(OrderStatus.UnderReview, OrderAction.CancelBySupplier, ActorRole.Supplier);
        Assert.Equal(TransitionFailure.InvalidStatus, result.Failure);
    }

    [Theory]
    [InlineData(OrderStatus.RedistributionConfirmed)]
    [InlineData(OrderStatus.Sent)]
    [InlineData(OrderStatus.ProductionPostponed)]
    [InlineData(OrderStatus.Completed)]
    public void Buyer_cannot_cancel_after_redistribution_or_from_postponed(OrderStatus status)
    {
        var result = OrderStateMachine.Apply(status, OrderAction.Cancel, ActorRole.Buyer);

        Assert.Equal(TransitionFailure.InvalidStatus, result.Failure);
    }

    [Theory]
    [InlineData(OrderStatus.UnderReview, OrderAction.ConfirmProduction, ActorRole.Buyer)]
    [InlineData(OrderStatus.UnderReview, OrderAction.ConfirmProduction, ActorRole.Supplier)]
    [InlineData(OrderStatus.SupplierConfirmed, OrderAction.ConfirmRedistribution, ActorRole.Supplier)]
    [InlineData(OrderStatus.RedistributionConfirmed, OrderAction.MarkSent, ActorRole.Buyer)]
    [InlineData(OrderStatus.ProductionPostponed, OrderAction.AcceptPostponed, ActorRole.Supplier)]
    public void Actions_are_rejected_for_the_wrong_role(OrderStatus status, OrderAction action, ActorRole actor)
    {
        var result = OrderStateMachine.Apply(status, action, actor);

        Assert.Equal(TransitionFailure.ActorNotAllowed, result.Failure);
        Assert.Equal(status, result.Status);
    }

    [Fact]
    public void Failed_transition_keeps_current_status()
    {
        var result = OrderStateMachine.Apply(OrderStatus.Draft, OrderAction.Complete, ActorRole.Buyer);

        Assert.False(result.Succeeded);
        Assert.Equal(OrderStatus.Draft, result.Status);
    }

    [Theory]
    [InlineData(OrderStatus.Completed)]
    [InlineData(OrderStatus.Rejected)]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.CancelledBySupplier)]
    public void Terminal_statuses_offer_no_actions_to_anyone(OrderStatus status)
    {
        Assert.True(OrderStateMachine.IsTerminal(status));
        foreach (var role in Enum.GetValues<ActorRole>())
        {
            Assert.Empty(OrderStateMachine.AvailableActions(status, role));
        }
    }

    [Fact]
    public void Only_the_four_end_states_are_terminal()
    {
        var terminal = Enum.GetValues<OrderStatus>().Where(OrderStateMachine.IsTerminal).ToHashSet();

        Assert.Equal(
            new HashSet<OrderStatus>
            {
                OrderStatus.Completed, OrderStatus.Rejected, OrderStatus.Cancelled, OrderStatus.CancelledBySupplier,
            },
            terminal);
    }

    [Fact]
    public void Every_status_is_reachable_from_an_initial_status()
    {
        var reached = new HashSet<OrderStatus> { OrderStateMachine.InitialStatus, OrderStateMachine.ChildOrderInitialStatus };
        var transitions = OrderStateMachine.Transitions.ToList();
        bool grew;
        do
        {
            grew = transitions.Where(t => reached.Contains(t.From)).Aggregate(false, (g, t) => reached.Add(t.To) || g);
        }
        while (grew);

        Assert.Equal(Enum.GetValues<OrderStatus>().ToHashSet(), reached);
    }

    [Fact]
    public void Available_actions_for_a_buyer_in_supplier_confirmed()
    {
        var actions = OrderStateMachine.AvailableActions(OrderStatus.SupplierConfirmed, ActorRole.Buyer);

        Assert.Equivalent(new[] { OrderAction.Cancel, OrderAction.ConfirmRedistribution }, actions);
    }
}

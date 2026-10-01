using Stockline.Domain;
using Stockline.Domain.Orders;

namespace Stockline.UnitTests.Domain;

public class OrderParityTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly OrderLocation[] Locations =
    [
        new("WH-1", "Warehouse", LocationKind.Warehouse, null),
        new("ST-1", "Store one", LocationKind.Store, "WH-1"),
        new("ST-2", "Store two", LocationKind.Store, "WH-1"),
    ];

    private static Order NewOrder(params OrderAction[] buyerAndSupplierSteps)
    {
        var header = new OrderHeader("PO-1", new DateOnly(2026, 9, 24), null, null, null, null, "ACME", null, null, null, null);
        var line = new OrderLine("SKU-1", null, "Tee", new Pack(12), 10m, 0m, 20m,
        [
            new Allocation("WH-1", null, LocationKind.Warehouse, 6m),
            new Allocation("ST-1", null, LocationKind.Store, 4m),
        ]);
        var order = Order.Create(Guid.NewGuid(), header, [line], Locations, "buyer-1", At);
        foreach (var action in buyerAndSupplierSteps)
        {
            Assert.True(order.Apply(action, action == OrderAction.Submit ? ActorRole.Buyer : ActorRole.Supplier, "u", At).Succeeded);
        }

        return order;
    }

    private static Order UnderReview() => NewOrder(OrderAction.Submit, OrderAction.Open);

    private static EditContext By(string user = "supplier-1") => new(ActorRole.Supplier, user, At.AddMinutes(5));

    // ---- visibility -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ActorRole.Buyer, true, true)]
    [InlineData(ActorRole.Supplier, true, false)]
    [InlineData(ActorRole.SupplierProduction, false, false)]
    public void Costs_are_hidden_from_production_and_retail_prices_from_everyone_but_the_buyer(
        ActorRole role, bool costs, bool retail)
    {
        Assert.Equal(costs, OrderVisibility.CanSeeCosts(role));
        Assert.Equal(retail, OrderVisibility.CanSeeRetailPrice(role));
    }

    [Fact]
    public void Production_only_sees_submitted_and_under_review_orders()
    {
        var visible = Enum.GetValues<OrderStatus>().Where(s => OrderVisibility.CanSee(ActorRole.SupplierProduction, s));

        Assert.Equal([OrderStatus.Submitted, OrderStatus.UnderReview], visible);
        Assert.All(Enum.GetValues<OrderStatus>(), s => Assert.True(OrderVisibility.CanSee(ActorRole.Buyer, s)));
        Assert.All(Enum.GetValues<OrderStatus>(), s => Assert.True(OrderVisibility.CanSee(ActorRole.Supplier, s)));
        Assert.Null(OrderVisibility.VisibleStatuses(ActorRole.Buyer));
    }

    // ---- state machine corrections ---------------------------------------------------------------------------------

    [Fact]
    public void An_accepted_postponed_order_can_no_longer_be_cancelled_only_redistributed()
    {
        var available = OrderStateMachine.AvailableActions(OrderStatus.ReadyForRedistribution, ActorRole.Buyer);

        Assert.DoesNotContain(OrderAction.Cancel, available);
        Assert.Contains(OrderAction.ConfirmRedistribution, available);
    }

    [Theory]
    [InlineData(OrderStatus.Draft)]
    [InlineData(OrderStatus.Submitted)]
    [InlineData(OrderStatus.UnderReview)]
    [InlineData(OrderStatus.SupplierConfirmed)]
    public void Buyer_can_still_cancel_until_the_supplier_confirms_production(OrderStatus status) =>
        Assert.Contains(OrderAction.Cancel, OrderStateMachine.AvailableActions(status, ActorRole.Buyer));

    [Fact]
    public void Only_draft_and_submitted_orders_can_be_discarded()
    {
        Assert.True(NewOrder().CanBeDiscarded);
        Assert.True(NewOrder(OrderAction.Submit).CanBeDiscarded);
        Assert.False(UnderReview().CanBeDiscarded);
    }

    // ---- audit trail, ownership --------------------------------------------------------------------------------------

    [Fact]
    public void Order_remembers_who_created_it_and_who_touched_it_last()
    {
        var order = NewOrder();
        Assert.Equal(("buyer-1", At, "buyer-1", At), (order.CreatedBy, order.CreatedAt, order.ModifiedBy, order.ModifiedAt));

        order.Apply(OrderAction.Submit, ActorRole.Buyer, "buyer-2", At.AddHours(1));

        Assert.Equal("buyer-1", order.CreatedBy);
        Assert.Equal(("buyer-2", At.AddHours(1)), (order.ModifiedBy, order.ModifiedAt));
    }

    [Fact]
    public void Line_edits_are_audited_with_old_and_new_values_and_update_the_modifier()
    {
        var order = UnderReview();

        order.ConfirmLineQuantity("SKU-1", 8m, By());
        order.ChangeLineCost("SKU-1", 11m, By());
        order.ChangeLineComment("SKU-1", "  Ships Friday ", By());

        Assert.Equal(
            [
                ("ConfirmedQuantity", "10", "8"),
                ("CostWithoutTax", "10", "11"),
                ("Comment", null, "Ships Friday"),
            ],
            order.Audit.Select(a => (a.Field, a.OldValue, a.NewValue)));
        Assert.All(order.Audit, a => Assert.Equal(("supplier-1", ActorRole.Supplier, "SKU-1"), (a.UserId, a.Actor, a.ProductCode)));
        Assert.Equal(("supplier-1", At.AddMinutes(5)), (order.ModifiedBy, order.ModifiedAt));
        Assert.Equal("Ships Friday", order.Lines[0].Comment);
    }

    [Fact]
    public void Unchanged_values_leave_no_audit_entry_and_edits_without_a_context_are_not_audited()
    {
        var order = UnderReview();

        order.ConfirmLineQuantity("SKU-1", 10m, By());
        order.ChangeLineCost("SKU-1", 10m, By());
        order.ChangeLineComment("SKU-1", null, By());
        order.ConfirmLineQuantity("SKU-1", 9m);

        Assert.Empty(order.Audit);
    }

    [Fact]
    public void Cost_audit_entries_are_flagged_so_they_can_be_hidden()
    {
        var order = UnderReview();
        order.ChangeLineCost("SKU-1", 12m, By());
        order.ConfirmLineQuantity("SKU-1", 9m, By());

        Assert.Equal([true, false], order.Audit.Select(a => a.IsCostField));
    }

    // ---- edit limits and warnings -------------------------------------------------------------------------------------

    [Fact]
    public void Confirming_under_eighty_percent_of_the_request_warns_but_is_accepted()
    {
        var order = UnderReview();

        var atLimit = order.ConfirmLineQuantity("SKU-1", 8m);
        var below = order.ConfirmLineQuantity("SKU-1", 7m);

        Assert.Empty(atLimit);
        var warning = Assert.Single(below);
        Assert.Equal("LOW_CONFIRMATION", warning.Code);
        Assert.Equal(7m, order.Lines[0].ConfirmedQuantity);
    }

    [Fact]
    public void A_cost_more_than_ten_percent_above_the_imported_one_warns_even_after_several_edits()
    {
        var order = UnderReview();

        Assert.Empty(order.ChangeLineCost("SKU-1", 11m));
        var warning = Assert.Single(order.ChangeLineCost("SKU-1", 11.5m));
        Assert.Equal("HIGH_COST_INCREASE", warning.Code);
        Assert.Empty(order.ChangeLineCost("SKU-1", 9m));
        Assert.Equal(10m, order.Lines[0].OriginalCostWithoutTax);
    }

    [Fact]
    public void Cost_has_an_upper_bound_and_comments_a_length_limit()
    {
        var order = UnderReview();

        Assert.Throws<DomainException>(() => order.ChangeLineCost("SKU-1", 1_000_001m));
        Assert.Throws<DomainException>(() => order.ChangeLineComment("SKU-1", new string('x', 501)));
        order.ChangeLineCost("SKU-1", 1_000_000m);
    }

    [Fact]
    public void Comments_follow_the_same_edit_window_as_quantities()
    {
        var order = NewOrder(OrderAction.Submit);

        Assert.Throws<InvalidStateException>(() => order.ChangeLineComment("SKU-1", "early"));
    }

    // ---- child orders and catalog stores ------------------------------------------------------------------------------

    private static Order ChildOrder()
    {
        var parent = UnderReview();
        parent.ConfirmLineQuantity("SKU-1", 6m, By());
        var child = parent.CreateChild(
            Guid.NewGuid(), 1, new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 1),
            new Dictionary<string, decimal> { ["SKU-1"] = 4m }, "supplier-1", At);
        Assert.True(child.Apply(OrderAction.AcceptPostponed, ActorRole.Buyer, "buyer-1", At).Succeeded);
        return child;
    }

    private static IReadOnlyDictionary<string, OrderLocation> Catalog => new Dictionary<string, OrderLocation>(StringComparer.OrdinalIgnoreCase)
    {
        ["ST-9"] = new("ST-9", "Store nine", LocationKind.Store, "WH-1"),
    };

    private static PlanLine PlanFor(Order order, string location, decimal quantity) =>
        new(order.Lines[0].ProductCode, [new PlanAllocation(location, quantity)]);

    [Fact]
    public void A_child_order_can_be_redistributed_to_a_store_of_the_catalog_that_the_parent_did_not_have()
    {
        var child = ChildOrder();
        Assert.Equal(4m, child.Lines[0].ConfirmedQuantity);

        var result = child.ConfirmRedistribution([PlanFor(child, "ST-9", 4m)], ActorRole.Buyer, "buyer-1", At, Catalog);

        Assert.True(result.Succeeded);
        Assert.Contains(child.Locations, l => l.Code == "ST-9" && l.SupplyingWarehouseCode == "WH-1");
        Assert.Equal([("ST-9", 4m)], child.Lines[0].Allocations.Select(a => (a.LocationCode, a.Quantity)));
    }

    [Fact]
    public void A_catalog_store_is_refused_for_a_regular_order_and_for_stores_missing_from_the_catalog()
    {
        var parent = UnderReview();
        parent.Apply(OrderAction.ConfirmBySupplier, ActorRole.Supplier, "u", At);
        var child = ChildOrder();

        var regular = parent.ConfirmRedistribution([PlanFor(parent, "ST-9", 10m)], ActorRole.Buyer, "u", At, Catalog);
        var unknown = child.ConfirmRedistribution([PlanFor(child, "ST-404", 4m)], ActorRole.Buyer, "u", At, Catalog);

        Assert.Contains(regular.Issues, i => i.Code == "UNKNOWN_LOCATION");
        Assert.Contains(unknown.Issues, i => i.Code == "UNKNOWN_LOCATION");
        Assert.DoesNotContain(parent.Locations, l => l.Code == "ST-9");
    }

    [Fact]
    public void An_invalid_plan_does_not_add_catalog_locations_to_the_child_order()
    {
        var child = ChildOrder();

        var result = child.ConfirmRedistribution([PlanFor(child, "ST-9", 3m)], ActorRole.Buyer, "u", At, Catalog);

        Assert.Contains(result.Issues, i => i.Code == "TOTAL_MISMATCH");
        Assert.DoesNotContain(child.Locations, l => l.Code == "ST-9");
    }

    [Fact]
    public void Child_orders_record_who_created_them()
    {
        var child = ChildOrder();

        Assert.Equal(("supplier-1", At), (child.CreatedBy, child.CreatedAt));
    }
}

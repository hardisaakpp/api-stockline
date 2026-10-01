using Stockline.Domain;
using Stockline.Domain.Orders;

namespace Stockline.UnitTests.Domain;

public class OrderWorkflowTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly OrderLocation[] Locations =
    [
        new("WH-1", "Warehouse", LocationKind.Warehouse, null),
        new("ST-1", "Store one", LocationKind.Store, "WH-1"),
        new("ST-2", "Store two", LocationKind.Store, "WH-1"),
        new("ST-3", "Store three", LocationKind.Store, "WH-1"),
    ];

    private static Order NewOrder(params (OrderAction Action, ActorRole Actor)[] steps)
    {
        var header = new OrderHeader("PO-1", new DateOnly(2026, 9, 24), null, "IMMEDIATE", null, null, "ACME", null, null, null, null);
        var line1 = new OrderLine("SKU-1", null, "Tee", new Pack(6), 5.6m, 0m, 12.99m,
        [
            new Allocation("WH-1", "Warehouse", LocationKind.Warehouse, 2m),
            new Allocation("ST-1", "Store one", LocationKind.Store, 1m),
            new Allocation("ST-2", "Store two", LocationKind.Store, 1m),
        ]);
        var line2 = new OrderLine("SKU-2", null, "Hoodie", new Pack(2), 15.2m, 0m, 34.99m,
            [new Allocation("ST-1", "Store one", LocationKind.Store, 3m)]);
        var order = Order.Create(Guid.NewGuid(), header, [line1, line2], Locations);
        foreach (var (action, actor) in steps)
        {
            Assert.True(order.Apply(action, actor, "u", At).Succeeded);
        }

        return order;
    }

    private static Order UnderReview() =>
        NewOrder((OrderAction.Submit, ActorRole.Buyer), (OrderAction.Open, ActorRole.Supplier));

    private static Order SupplierConfirmed() =>
        NewOrder(
            (OrderAction.Submit, ActorRole.Buyer),
            (OrderAction.Open, ActorRole.Supplier),
            (OrderAction.ConfirmBySupplier, ActorRole.Supplier));

    private static PlanLine Plan(string product, params (string Location, decimal Quantity)[] allocations) =>
        new(product, [.. allocations.Select(a => new PlanAllocation(a.Location, a.Quantity))]);

    [Fact]
    public void New_lines_request_and_confirm_the_same_quantity()
    {
        var line = NewOrder().Lines[0];

        Assert.Equal(4m, line.RequestedQuantity);
        Assert.Equal(4m, line.ConfirmedQuantity);
        Assert.Equal(0m, line.Difference);
    }

    [Fact]
    public void Supplier_can_lower_the_confirmed_quantity_while_reviewing()
    {
        var order = UnderReview();

        order.ConfirmLineQuantity("sku-1", 3m);

        var difference = Assert.Single(order.ProductionDifference());
        Assert.Equal("SKU-1", difference.ProductCode);
        Assert.Equal(1m, difference.Difference);
    }

    [Fact]
    public void Editing_is_only_allowed_while_the_supplier_is_working_on_the_order()
    {
        Assert.Throws<InvalidStateException>(() => NewOrder().ConfirmLineQuantity("SKU-1", 3m));
        Assert.Throws<InvalidStateException>(() => SupplierConfirmed().ChangeLineCost("SKU-1", 1m));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4.5)]
    public void Confirmed_quantity_must_stay_between_zero_and_the_requested(double quantity) =>
        Assert.Throws<DomainException>(() => UnderReview().ConfirmLineQuantity("SKU-1", (decimal)quantity));

    [Fact]
    public void Supplier_can_change_a_line_cost_and_unit_cost_follows()
    {
        var order = UnderReview();

        order.ChangeLineCost("SKU-2", 18m);

        Assert.Equal(3m, order.Lines[1].UnitCost);
    }

    [Fact]
    public void Unknown_product_cannot_be_edited() =>
        Assert.Throws<DomainException>(() => UnderReview().ConfirmLineQuantity("NOPE", 1m));

    [Fact]
    public void Child_order_carries_the_difference_as_a_postponed_order_without_allocations()
    {
        var parent = UnderReview();
        parent.ConfirmLineQuantity("SKU-1", 1m);

        var child = parent.CreateChild(
            Guid.NewGuid(), 2, new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 15),
            new Dictionary<string, decimal> { ["SKU-1"] = 3m, ["SKU-2"] = 0m });

        Assert.Equal("PO-1.02", child.Header.Code);
        Assert.Equal(parent.Id, child.ParentOrderId);
        Assert.Equal(OrderStatus.ProductionPostponed, child.Status);
        Assert.Equal(new DateOnly(2026, 11, 15), child.Header.DeliveryDate);
        Assert.Equal(parent.Locations, child.Locations);
        var line = Assert.Single(child.Lines);
        Assert.Equal(4m, line.RequestedQuantity);
        Assert.Equal(3m, line.ConfirmedQuantity);
        Assert.Empty(line.Allocations);
    }

    [Fact]
    public void Child_quantities_must_be_whole_units_and_not_empty()
    {
        var parent = UnderReview();

        Assert.Throws<DomainException>(() => parent.CreateChild(
            Guid.NewGuid(), 1, new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 1), new Dictionary<string, decimal> { ["SKU-1"] = 0.5m }));
        Assert.Throws<DomainException>(() => parent.CreateChild(
            Guid.NewGuid(), 1, new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 1), new Dictionary<string, decimal>()));
    }

    [Fact]
    public void Valid_plan_replaces_allocations_bumps_the_version_and_records_history()
    {
        var order = SupplierConfirmed();
        PlanLine[] plan =
        [
            Plan("SKU-1", ("WH-1", 0m), ("ST-1", 2m), ("ST-3", 2m)),
            Plan("SKU-2", ("ST-2", 3m)),
        ];

        var result = order.ConfirmRedistribution(plan, ActorRole.Buyer, "buyer-1", At);

        Assert.True(result.Succeeded);
        Assert.Equal(OrderStatus.RedistributionConfirmed, order.Status);
        Assert.Equal(1, order.RedistributionVersion);
        Assert.Equal(["ST-1", "ST-3"], order.Lines[0].Allocations.Select(a => a.LocationCode));
        Assert.Equal(LocationKind.Store, order.Lines[0].Allocations[0].Kind);
        Assert.Equal(OrderAction.ConfirmRedistribution, order.History[^1].Action);
    }

    [Fact]
    public void Plan_totals_must_match_the_confirmed_quantity_including_untouched_lines()
    {
        var order = SupplierConfirmed();

        var result = order.ConfirmRedistribution([Plan("SKU-1", ("ST-1", 5m))], ActorRole.Buyer, "b", At);

        Assert.Equal(["TOTAL_MISMATCH"], result.Issues.Select(i => i.Code));
        Assert.Equal(OrderStatus.SupplierConfirmed, order.Status);
        Assert.Equal(4m, order.Lines[0].TotalQuantity);
        Assert.Equal(0, order.RedistributionVersion);
    }

    [Fact]
    public void A_lowered_confirmation_forces_the_plan_to_cover_that_line()
    {
        var order = UnderReview();
        order.ConfirmLineQuantity("SKU-1", 3m);
        order.Apply(OrderAction.ConfirmBySupplier, ActorRole.Supplier, "s", At);

        var result = order.ConfirmRedistribution([], ActorRole.Buyer, "b", At);

        Assert.Equal("SKU-1", Assert.Single(result.Issues).ProductCode);
    }

    [Fact]
    public void Plan_with_invalid_entries_reports_every_problem()
    {
        var order = SupplierConfirmed();
        PlanLine[] plan =
        [
            Plan("SKU-1", ("ST-9", 1m), ("ST-1", -1m), ("ST-2", 2m), ("ST-2", 2m)),
            Plan("NOPE", ("ST-1", 1m)),
            Plan("SKU-2", ("ST-1", 3m)),
            Plan("sku-2", ("ST-1", 3m)),
        ];

        var codes = order.ConfirmRedistribution(plan, ActorRole.Buyer, "b", At).Issues.Select(i => i.Code).ToHashSet();

        Assert.Superset(
            new HashSet<string> { "UNKNOWN_LOCATION", "NEGATIVE_QUANTITY", "DUPLICATE_LOCATION", "UNKNOWN_PRODUCT", "DUPLICATE_PRODUCT" },
            codes);
    }

    [Fact]
    public void Redistribution_checks_status_and_role_before_the_plan()
    {
        var plan = new[] { Plan("SKU-1", ("ST-9", 99m)) };

        Assert.Equal(
            TransitionFailure.InvalidStatus,
            NewOrder().ConfirmRedistribution(plan, ActorRole.Buyer, "b", At).Failure);
        Assert.Equal(
            TransitionFailure.ActorNotAllowed,
            SupplierConfirmed().ConfirmRedistribution(plan, ActorRole.Supplier, "s", At).Failure);
    }

    [Fact]
    public void Draft_is_saved_without_requiring_totals_and_replaced_on_each_save()
    {
        var order = SupplierConfirmed();

        Assert.True(order.SaveRedistributionDraft([Plan("SKU-1", ("ST-3", 1m))]).Succeeded);
        Assert.True(order.SaveRedistributionDraft([Plan("SKU-2", ("ST-2", 1m)), Plan("SKU-1", ("ST-1", 0.5m))]).Succeeded);

        Assert.Equal(
            [("SKU-2", "ST-2", 1m), ("SKU-1", "ST-1", 0.5m)],
            order.RedistributionDraft.Select(d => (d.ProductCode, d.LocationCode, d.Quantity)));
    }

    [Fact]
    public void Invalid_draft_is_not_saved_and_wrong_status_is_refused()
    {
        var order = SupplierConfirmed();

        var invalid = order.SaveRedistributionDraft([Plan("SKU-1", ("ST-9", 1m))]);

        Assert.Equal(["UNKNOWN_LOCATION"], invalid.Issues.Select(i => i.Code));
        Assert.Empty(order.RedistributionDraft);
        Assert.Equal(TransitionFailure.InvalidStatus, NewOrder().SaveRedistributionDraft([]).Failure);
    }

    [Fact]
    public void Confirming_clears_the_draft()
    {
        var order = SupplierConfirmed();
        order.SaveRedistributionDraft([Plan("SKU-1", ("ST-1", 1m))]);

        order.ConfirmRedistribution([Plan("SKU-1", ("ST-1", 4m))], ActorRole.Buyer, "b", At);

        Assert.Empty(order.RedistributionDraft);
    }

    [Fact]
    public void Accepted_child_order_is_redistributed_across_any_order_location()
    {
        var parent = UnderReview();
        parent.ConfirmLineQuantity("SKU-1", 1m);
        var child = parent.CreateChild(
            Guid.NewGuid(), 1, new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 1),
            new Dictionary<string, decimal> { ["SKU-1"] = 3m });
        child.Apply(OrderAction.AcceptPostponed, ActorRole.Buyer, "b", At);

        var result = child.ConfirmRedistribution([Plan("SKU-1", ("ST-3", 3m))], ActorRole.Buyer, "b", At);

        Assert.True(result.Succeeded);
        Assert.Equal(3m, child.Lines[0].TotalQuantity);
    }
}

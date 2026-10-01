using Stockline.Domain;
using Stockline.Domain.Locations;
using Stockline.Domain.Orders;

namespace Stockline.UnitTests.Domain;

public class LocationAndProposalTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    // ---- location rules ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Location_codes_are_trimmed_upper_cased_and_validated()
    {
        var location = new Location(" st-010 ", " Marina ", LocationKind.Store, " wh-main ", true);

        Assert.Equal(("ST-010", "Marina", "WH-MAIN", true), (location.Code, location.Name, location.SupplyingWarehouseCode, location.IsActive));
        Assert.Throws<DomainException>(() => new Location("", "x", LocationKind.Warehouse, null, true));
        Assert.Throws<DomainException>(() => new Location("bad code!", "x", LocationKind.Warehouse, null, true));
        Assert.Throws<DomainException>(() => new Location(new string('A', 41), "x", LocationKind.Warehouse, null, true));
        Assert.Throws<DomainException>(() => new Location("WH-1", " ", LocationKind.Warehouse, null, true));
    }

    [Fact]
    public void Stores_need_a_supplying_warehouse_and_warehouses_must_not_have_one()
    {
        Assert.Throws<DomainException>(() => new Location("ST-1", "Store", LocationKind.Store, null, true));
        Assert.Throws<DomainException>(() => new Location("WH-1", "Warehouse", LocationKind.Warehouse, "WH-2", true));
        Assert.Null(new Location("WH-1", "Warehouse", LocationKind.Warehouse, " ", true).SupplyingWarehouseCode);
    }

    [Fact]
    public void Updating_a_location_keeps_its_code_and_kind_and_revalidates()
    {
        var store = new Location("ST-1", "Store", LocationKind.Store, "WH-1", true);

        store.Update("Renamed", "WH-2", false);

        Assert.Equal(("ST-1", LocationKind.Store, "Renamed", "WH-2", false), (store.Code, store.Kind, store.Name, store.SupplyingWarehouseCode, store.IsActive));
        Assert.Throws<DomainException>(() => store.Update("Renamed", null, true));
    }

    // ---- proportional redistribution proposal -----------------------------------------------------------------------------

    private static Order OrderWith(params (string Location, LocationKind Kind, decimal Quantity)[] allocations)
    {
        var header = new OrderHeader("PO-P", new DateOnly(2026, 9, 24), null, null, null, null, "ACME", null, null, null, null);
        var line = new OrderLine("SKU-1", null, null, new Pack(1), 1m, 0m, null,
            [.. allocations.Select(a => new Allocation(a.Location, null, a.Kind, a.Quantity))]);
        var order = Order.Create(
            Guid.NewGuid(), header, [line],
            [.. allocations.Select(a => new OrderLocation(a.Location, null, a.Kind, a.Kind == LocationKind.Store ? "WH-1" : null))]);
        order.Apply(OrderAction.Submit, ActorRole.Buyer, "u", At);
        order.Apply(OrderAction.Open, ActorRole.Supplier, "u", At);
        return order;
    }

    private static decimal[] Proposal(Order order) => [.. order.ProposeRedistribution()[0].Allocations.Select(a => a.Quantity)];

    [Fact]
    public void Proposal_scales_every_location_by_the_confirmed_share()
    {
        var order = OrderWith(("WH-1", LocationKind.Warehouse, 10m), ("ST-1", LocationKind.Store, 6m), ("ST-2", LocationKind.Store, 4m));
        order.ConfirmLineQuantity("SKU-1", 10m);

        Assert.Equal([5m, 3m, 2m], Proposal(order));
    }

    [Fact]
    public void The_last_location_absorbs_the_rounding_so_the_line_adds_up_exactly()
    {
        var order = OrderWith(("WH-1", LocationKind.Warehouse, 1m), ("ST-1", LocationKind.Store, 1m), ("ST-2", LocationKind.Store, 1m));
        order.ConfirmLineQuantity("SKU-1", 2m);

        var proposal = Proposal(order);

        Assert.Equal([0.6667m, 0.6667m, 0.6666m], proposal);
        Assert.Equal(2m, proposal.Sum());
    }

    [Fact]
    public void A_proposal_without_any_change_in_quantity_repeats_the_original_distribution()
    {
        var order = OrderWith(("WH-1", LocationKind.Warehouse, 4m), ("ST-1", LocationKind.Store, 3m));

        Assert.Equal([4m, 3m], Proposal(order));
    }

    [Fact]
    public void Nothing_confirmed_means_nothing_is_proposed_and_a_proposal_is_always_a_valid_plan()
    {
        var zero = OrderWith(("WH-1", LocationKind.Warehouse, 4m), ("ST-1", LocationKind.Store, 3m));
        zero.ConfirmLineQuantity("SKU-1", 0m);
        Assert.Equal([0m, 0m], Proposal(zero));

        var order = OrderWith(("WH-1", LocationKind.Warehouse, 7m), ("ST-1", LocationKind.Store, 5m), ("ST-2", LocationKind.Store, 3m));
        order.ConfirmLineQuantity("SKU-1", 9m);
        order.Apply(OrderAction.ConfirmBySupplier, ActorRole.Supplier, "u", At);

        var result = order.ConfirmRedistribution(order.ProposeRedistribution(), ActorRole.Buyer, "u", At);

        Assert.True(result.Succeeded, string.Join("; ", result.Issues.Select(i => i.Message)));
        Assert.Equal(9m, order.Lines[0].TotalQuantity);
    }

    [Fact]
    public void A_child_order_has_no_original_distribution_to_scale()
    {
        var parent = OrderWith(("WH-1", LocationKind.Warehouse, 6m), ("ST-1", LocationKind.Store, 4m));
        parent.ConfirmLineQuantity("SKU-1", 6m);
        var child = parent.CreateChild(Guid.NewGuid(), 1, new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 1), new Dictionary<string, decimal> { ["SKU-1"] = 4m });

        Assert.Empty(child.ProposeRedistribution()[0].Allocations);
    }
}

using Stockline.Domain;
using Stockline.Domain.Orders;

namespace Stockline.UnitTests.Domain;

public class OrderTests
{
    private static readonly OrderHeader Header = new(
        "PO-0001", new DateOnly(2026, 9, 24), null, "IMMEDIATE", "120 DAYS", null, "ACME SUPPLIER", "BUYER", null, "WH-1", null);

    private static OrderLine Line(string product = "SKU-1", params Allocation[] allocations) =>
        new(product, null, "Item", new Pack(6), 5.6m, 0.2m, 12.99m,
            allocations.Length > 0 ? allocations : [new Allocation("ST-1", null, LocationKind.Store, 1m)]);

    private static Order NewOrder() => Order.Create(Guid.NewGuid(), Header, [Line()]);

    [Fact]
    public void New_order_starts_as_draft_with_empty_history()
    {
        var order = NewOrder();

        Assert.Equal(OrderStatus.Draft, order.Status);
        Assert.Empty(order.History);
    }

    [Fact]
    public void Line_exposes_unit_cost_and_quantity_totals()
    {
        var line = Line("SKU-1",
            new Allocation("WH-1", null, LocationKind.Warehouse, 2m),
            new Allocation("ST-1", null, LocationKind.Store, 0.5m),
            new Allocation("ST-2", null, LocationKind.Store, 1m));

        Assert.Equal(2.8m, line.UnitCost);
        Assert.Equal(3.5m, line.TotalQuantity);
        Assert.Equal(1.5m, line.StoreQuantity);
    }

    [Fact]
    public void Order_code_is_trimmed()
    {
        var order = Order.Create(Guid.NewGuid(), Header with { Code = "  PO-9 " }, [Line()]);

        Assert.Equal("PO-9", order.Header.Code);
    }

    [Fact]
    public void Order_without_lines_is_rejected() =>
        Assert.Throws<DomainException>(() => Order.Create(Guid.NewGuid(), Header, []));

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Order_without_code_is_rejected(string code) =>
        Assert.Throws<DomainException>(() => Order.Create(Guid.NewGuid(), Header with { Code = code }, [Line()]));

    [Fact]
    public void Same_product_twice_is_rejected() =>
        Assert.Throws<DomainException>(() => Order.Create(Guid.NewGuid(), Header, [Line("sku-1"), Line("SKU-1")]));

    [Fact]
    public void Line_without_allocations_is_rejected() =>
        Assert.Throws<DomainException>(() => new OrderLine("SKU", null, null, new Pack(1), 1m, 0m, null, []));

    [Fact]
    public void Line_with_non_positive_quantity_is_rejected() =>
        Assert.Throws<DomainException>(() => Line("SKU", new Allocation("ST-1", null, LocationKind.Store, 0m)));

    [Fact]
    public void Line_with_repeated_location_is_rejected() =>
        Assert.Throws<DomainException>(() => Line(
            "SKU",
            new Allocation("ST-1", null, LocationKind.Store, 1m),
            new Allocation("st-1", null, LocationKind.Store, 2m)));

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void Line_with_out_of_range_discount_is_rejected(double discount) =>
        Assert.Throws<DomainException>(() => new OrderLine(
            "SKU", null, null, new Pack(1), 1m, (decimal)discount, null,
            [new Allocation("ST-1", null, LocationKind.Store, 1m)]));

    [Fact]
    public void Successful_transition_updates_status_and_records_history()
    {
        var order = NewOrder();
        var at = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

        var result = order.Apply(OrderAction.Submit, ActorRole.Buyer, "user-7", at);

        Assert.True(result.Succeeded);
        Assert.Equal(OrderStatus.Submitted, order.Status);
        Assert.Equal(
            new StatusChange(OrderStatus.Draft, OrderStatus.Submitted, OrderAction.Submit, ActorRole.Buyer, "user-7", at),
            Assert.Single(order.History));
    }

    [Fact]
    public void Failed_transition_changes_nothing()
    {
        var order = NewOrder();

        var result = order.Apply(OrderAction.Complete, ActorRole.Buyer, "user-7", DateTimeOffset.UtcNow);

        Assert.False(result.Succeeded);
        Assert.Equal(OrderStatus.Draft, order.Status);
        Assert.Empty(order.History);
    }
}

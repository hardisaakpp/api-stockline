using Microsoft.EntityFrameworkCore;
using Stockline.Domain.Orders;
using Stockline.Infrastructure.Persistence;

namespace Stockline.UnitTests.Persistence;

public class OrderPersistenceTests : IDisposable
{
    private readonly SqliteDb _db = new();

    public void Dispose() => _db.Dispose();

    private static Order SampleOrder(string code = "PO-1")
    {
        var header = new OrderHeader(
            code, new DateOnly(2026, 9, 24), null, "IMMEDIATE", "120 DAYS", "msg", "ACME", "BUYER", "801", "WH-MAIN", "10");
        var line1 = new OrderLine("SKU-1", "7869000000013", "Basic tee", new Pack(6), 5.6m, 0.2m, 12.99m,
        [
            new Allocation("WH-MAIN", "Main warehouse", LocationKind.Warehouse, 2m),
            new Allocation("ST-001", "Downtown", LocationKind.Store, 0.5m),
        ]);
        var line2 = new OrderLine("SKU-2", null, null, new Pack(1), null, 0m, null,
            [new Allocation("ST-002", null, LocationKind.Store, 3m)]);
        return Order.Create(Guid.CreateVersion7(), header, [line1, line2]);
    }

    private async Task Save(Order order)
    {
        await using var context = _db.NewContext();
        await new EfOrderRepository(context).AddAsync(order, default);
    }

    private async Task<Order?> Load(Guid id)
    {
        await using var context = _db.NewContext();
        return await new EfOrderRepository(context).GetAsync(id, default);
    }

    [Fact]
    public async Task Order_round_trips_with_header_lines_allocations_and_history()
    {
        var order = SampleOrder();
        order.Apply(OrderAction.Submit, ActorRole.Buyer, "user-7", new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        await Save(order);

        var loaded = await Load(order.Id);

        Assert.NotNull(loaded);
        Assert.Equal(order.Header, loaded.Header);
        Assert.Equal(OrderStatus.Submitted, loaded.Status);
        Assert.Equal(["SKU-1", "SKU-2"], loaded.Lines.Select(l => l.ProductCode).Order());
        var line = loaded.Lines.Single(l => l.ProductCode == "SKU-1");
        Assert.Equal(new Pack(6), line.Pack);
        Assert.Equal(5.6m, line.CostWithoutTax);
        Assert.Equal(0.2m, line.Discount);
        Assert.Equal(2.8m, line.UnitCost);
        Assert.Equal(2.5m, line.TotalQuantity);
        Assert.Equal(0.5m, line.StoreQuantity);
        Assert.Contains(line.Allocations, a => a is { LocationCode: "WH-MAIN", Kind: LocationKind.Warehouse, Quantity: 2m });
        var change = Assert.Single(loaded.History);
        Assert.Equal(order.History[0], change);
    }

    [Fact]
    public async Task Null_optional_values_survive_a_round_trip()
    {
        var order = SampleOrder();
        await Save(order);

        var line = (await Load(order.Id))!.Lines.Single(l => l.ProductCode == "SKU-2");

        Assert.Null(line.CostWithoutTax);
        Assert.Null(line.UnitCost);
        Assert.Null(line.RetailPrice);
        Assert.Null(line.Barcode);
    }

    [Fact]
    public async Task Order_code_exists_after_saving()
    {
        await Save(SampleOrder("PO-EXISTS"));
        await using var context = _db.NewContext();
        var repository = new EfOrderRepository(context);

        Assert.True(await repository.ExistsAsync("PO-EXISTS", default));
        Assert.False(await repository.ExistsAsync("PO-OTHER", default));
    }

    [Fact]
    public async Task Order_code_is_unique_in_the_database()
    {
        await Save(SampleOrder("PO-DUP"));

        await Assert.ThrowsAsync<DbUpdateException>(() => Save(SampleOrder("PO-DUP")));
    }

    [Fact]
    public async Task Unknown_order_returns_null() => Assert.Null(await Load(Guid.NewGuid()));

    [Fact]
    public async Task Catalog_provider_returns_only_active_locations_with_their_kind()
    {
        await using var context = _db.NewContext();

        var catalog = await new DbLocationCatalogProvider(context).GetAsync(default);

        Assert.True(catalog.TryGetKind("wh-main", out var warehouse));
        Assert.Equal(LocationKind.Warehouse, warehouse);
        Assert.True(catalog.TryGetKind("ST-001", out var store));
        Assert.Equal(LocationKind.Store, store);
        Assert.False(catalog.TryGetKind("ST-099", out _));
    }

    [Fact]
    public async Task Parent_keeps_its_locations_when_a_child_order_is_saved_with_a_copy_of_them()
    {
        OrderLocation[] locations =
        [
            new("WH-MAIN", "Main warehouse", LocationKind.Warehouse, null),
            new("ST-001", "Downtown", LocationKind.Store, "WH-MAIN"),
        ];
        var parent = Order.Create(Guid.CreateVersion7(), SampleOrder().Header with { Code = "PO-P" }, SampleOrder().Lines, locations);
        parent.Apply(OrderAction.Submit, ActorRole.Buyer, "u", DateTimeOffset.UtcNow);
        parent.Apply(OrderAction.Open, ActorRole.Supplier, "u", DateTimeOffset.UtcNow);
        parent.ConfirmLineQuantity("SKU-1", 1.5m);
        var child = parent.CreateChild(
            Guid.CreateVersion7(), 1, new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 1), new Dictionary<string, decimal> { ["SKU-1"] = 1m });

        await using (var context = _db.NewContext())
        {
            var repository = new EfOrderRepository(context);
            repository.Stage(parent);
            repository.Stage(child);
            await repository.SaveChangesAsync(default);
        }

        Assert.Equal(["ST-001", "WH-MAIN"], (await Load(parent.Id))!.Locations.Select(l => l.Code).Order());
        var loadedChild = (await Load(child.Id))!;
        Assert.Equal(["ST-001", "WH-MAIN"], loadedChild.Locations.Select(l => l.Code).Order());
        Assert.Equal(parent.Id, loadedChild.ParentOrderId);
    }
}

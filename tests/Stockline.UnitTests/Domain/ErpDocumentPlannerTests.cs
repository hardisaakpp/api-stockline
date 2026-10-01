using Stockline.Domain.Erp;
using Stockline.Domain.Orders;

namespace Stockline.UnitTests.Domain;

public class ErpDocumentPlannerTests
{
    private static readonly OrderLocation[] Locations =
    [
        new("WH-A", "Warehouse A", LocationKind.Warehouse, null),
        new("WH-B", "Warehouse B", LocationKind.Warehouse, null),
        new("ST-A1", "A one", LocationKind.Store, "WH-A"),
        new("ST-A2", "A two", LocationKind.Store, "WH-A"),
        new("ST-B1", "B one", LocationKind.Store, "WH-B"),
        new("ST-X", "Orphan", LocationKind.Store, null),
    ];

    private static Order NewOrder(params (string Product, decimal? Cost, (string Code, LocationKind Kind, decimal Qty)[] Allocations)[] lines)
    {
        var header = new OrderHeader("PO-1", new DateOnly(2026, 9, 24), null, null, null, null, "ACME", null, null, null, null);
        return Order.Create(
            Guid.NewGuid(),
            header,
            lines.Select(l => new OrderLine(l.Product, null, null, new Pack(6), l.Cost, 0.2m, null,
                l.Allocations.Select(a => new Allocation(a.Code, null, a.Kind, a.Qty)))),
            Locations);
    }

    private static (string, LocationKind, decimal) A(string code, decimal qty) =>
        (code, code.StartsWith("WH") ? LocationKind.Warehouse : LocationKind.Store, qty);

    [Fact]
    public void Single_warehouse_order_creates_one_purchase_order_and_one_transfer_per_store()
    {
        var order = NewOrder(
            ("SKU-1", 5.6m, [A("WH-A", 2m), A("ST-A1", 1m), A("ST-A2", 3m)]),
            ("SKU-2", 15.2m, [A("ST-A1", 4m)]));

        var plan = ErpDocumentPlanner.Plan(order);

        Assert.Empty(plan.Issues);
        var purchaseOrder = Assert.Single(plan.Documents, d => d.Kind == ErpDocumentKind.PurchaseOrder);
        Assert.Equal("WH-A", purchaseOrder.OriginWarehouse);
        Assert.Equal([6m, 4m], purchaseOrder.Lines.OrderBy(l => l.ProductCode).Select(l => l.Quantity));
        Assert.Equal(2.8m, purchaseOrder.Lines.First(l => l.ProductCode == "SKU-1").UnitPrice);
        Assert.Equal(20m, purchaseOrder.Lines[0].DiscountPercent);

        var transfers = plan.Documents.Where(d => d.Kind == ErpDocumentKind.TransferRequest).OrderBy(d => d.DestinationLocation).ToList();
        Assert.Equal(["ST-A1", "ST-A2"], transfers.Select(t => t.DestinationLocation));
        Assert.All(transfers, t => Assert.Equal(purchaseOrder.Id, t.PurchaseOrderDocumentId));
        Assert.Equal([1m, 4m], transfers[0].Lines.OrderBy(l => l.ProductCode).Select(l => l.Quantity));
    }

    [Fact]
    public void Mixed_order_creates_one_purchase_order_per_supplying_warehouse()
    {
        var order = NewOrder(("SKU-1", 5.6m, [A("ST-A1", 1m), A("ST-B1", 2m), A("WH-B", 1m)]));

        var plan = ErpDocumentPlanner.Plan(order);

        var purchaseOrders = plan.Documents.Where(d => d.Kind == ErpDocumentKind.PurchaseOrder).ToList();
        Assert.Equal(["WH-A", "WH-B"], purchaseOrders.Select(p => p.OriginWarehouse));
        Assert.Equal([1m, 3m], purchaseOrders.Select(p => p.Lines.Single().Quantity));
        Assert.Equal(
            ["ST-A1", "ST-B1"],
            plan.Documents.Where(d => d.Kind == ErpDocumentKind.TransferRequest).Select(d => d.DestinationLocation).Order());
    }

    [Fact]
    public void Warehouse_only_allocations_need_no_transfers()
    {
        var plan = ErpDocumentPlanner.Plan(NewOrder(("SKU-1", 1m, [A("WH-A", 5m)])));

        Assert.Equal(ErpDocumentKind.PurchaseOrder, Assert.Single(plan.Documents).Kind);
    }

    [Fact]
    public void Missing_cost_is_sent_as_zero_for_the_erp_price_list_to_resolve() =>
        Assert.Equal(0m, ErpDocumentPlanner.Plan(NewOrder(("SKU-1", null, [A("WH-A", 1m)]))).Documents[0].Lines[0].UnitPrice);

    [Fact]
    public void Store_without_supplying_warehouse_blocks_the_plan()
    {
        var plan = ErpDocumentPlanner.Plan(NewOrder(("SKU-1", 1m, [A("ST-X", 1m)])));

        Assert.Empty(plan.Documents);
        Assert.Equal("STORE_WITHOUT_WAREHOUSE", Assert.Single(plan.Issues).Code);
    }

    [Fact]
    public void Order_without_allocations_has_nothing_to_send()
    {
        var parent = NewOrder(("SKU-1", 1m, [A("WH-A", 4m)]));
        parent.Apply(OrderAction.Submit, ActorRole.Buyer, "u", DateTimeOffset.UtcNow);
        parent.Apply(OrderAction.Open, ActorRole.Supplier, "u", DateTimeOffset.UtcNow);
        parent.ConfirmLineQuantity("SKU-1", 1m);
        var child = parent.CreateChild(Guid.NewGuid(), 1, new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 1),
            new Dictionary<string, decimal> { ["SKU-1"] = 3m });

        Assert.Equal("NOTHING_TO_SEND", Assert.Single(ErpDocumentPlanner.Plan(child).Issues).Code);
    }

    [Fact]
    public void Document_tracks_attempts_and_the_last_outcome()
    {
        var document = ErpDocumentPlanner.Plan(NewOrder(("SKU-1", 1m, [A("WH-A", 1m)]))).Documents[0];

        document.MarkFailed("boom");
        Assert.Equal((ErpDocumentStatus.Failed, 1, "boom"), (document.Status, document.Attempts, document.LastError));

        document.MarkSent(10, 20, new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal((ErpDocumentStatus.Sent, 2, (string?)null, 10L, 20L), (document.Status, document.Attempts, document.LastError, document.DocEntry, document.DocNum));
    }
}

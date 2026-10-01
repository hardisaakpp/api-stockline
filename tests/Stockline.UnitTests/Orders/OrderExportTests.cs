using Stockline.Application.Orders.Import;
using Stockline.Domain.Orders;
using Stockline.Infrastructure.Excel;

namespace Stockline.UnitTests.Orders;

public class OrderExportTests
{
    private static readonly OrderLocation[] Locations =
    [
        new("ST-2", "Store two", LocationKind.Store, "WH-1"),
        new("WH-1", "Main warehouse", LocationKind.Warehouse, null),
        new("ST-1", "Store one", LocationKind.Store, "WH-1"),
        new("ST-3", "Store three", LocationKind.Store, "WH-1"),
    ];

    private static Order SampleOrder()
    {
        var header = new OrderHeader(
            "PO-77", new DateOnly(2026, 9, 24), new DateOnly(2026, 11, 1), null, "120 DAYS", "hello", "ACME SUPPLIER", "BUYER", "801", "WH-1", "10");
        var line1 = new OrderLine("SKU-1", "7869000000013", "Basic tee", new Pack(6), 5.6m, 0.2m, 12.99m,
        [
            new Allocation("WH-1", "Main warehouse", LocationKind.Warehouse, 2m),
            new Allocation("ST-1", "Store one", LocationKind.Store, 0.5m),
            new Allocation("ST-2", "Store two", LocationKind.Store, 1m),
        ]);
        var line2 = new OrderLine("SKU-2", null, null, new Pack(1), null, 0m, null, [new Allocation("ST-1", "Store one", LocationKind.Store, 3m)]);
        return Order.Create(Guid.NewGuid(), header, [line1, line2], Locations);
    }

    private static OrderTemplate RoundTrip(Order order) =>
        new ClosedXmlOrderTemplateReader().Read(new MemoryStream(new ClosedXmlOrderExporter().ExportTemplate(order)));

    [Fact]
    public void Exported_workbook_reads_back_with_the_same_header_and_quantities()
    {
        var order = SampleOrder();

        var template = RoundTrip(order);

        Assert.Equal(("PO-77", new DateOnly(2026, 9, 24), new DateOnly(2026, 11, 1)),
            (template.Header.OrderCode, template.Header.OrderDate, template.Header.DeliveryDate));
        Assert.Equal(("ACME SUPPLIER", "801", "WH-1", "10", "120 DAYS"),
            (template.Header.SupplierName, template.Header.BuyerCode, template.Header.TransferOrigin, template.Header.Series, template.Header.PaymentTerms));
        Assert.Equal(["SKU-1", "SKU-2"], template.Lines.Select(l => l.ProductCode));
        Assert.Equal(
            [("WH-1", 2m), ("ST-1", 0.5m), ("ST-2", 1m)],
            template.Lines[0].Quantities.Select(q => (q.LocationCode, q.Quantity)));
    }

    [Fact]
    public void Locations_are_exported_warehouses_first_including_unused_columns()
    {
        var template = RoundTrip(SampleOrder());

        Assert.Equal(["WH-1", "ST-1", "ST-2", "ST-3"], template.Locations!.Select(l => l.Code));
        Assert.Equal("Main warehouse", template.Locations![0].Name);
    }

    [Fact]
    public void Exported_totals_and_unit_cost_are_consistent_so_the_file_can_be_imported_again()
    {
        var order = SampleOrder();
        var template = RoundTrip(order);
        var catalog = new LocationCatalog(order.Locations.ToDictionary(l => l.Code, l => l.Kind));

        Assert.Equal(3.5m, template.Lines[0].Total);
        Assert.Equal(1.5m, template.Lines[0].Subtotal);
        Assert.Equal(2.8m, template.Lines[0].UnitCost);
        Assert.Empty(new OrderTemplateValidator().Validate(template, catalog));
    }

    [Fact]
    public void Supplier_changes_are_reflected_in_the_export()
    {
        var order = SampleOrder();
        order.Apply(OrderAction.Submit, ActorRole.Buyer, "u", DateTimeOffset.UtcNow);
        order.Apply(OrderAction.Open, ActorRole.Supplier, "u", DateTimeOffset.UtcNow);
        order.ChangeLineCost("SKU-1", 6m);

        var line = RoundTrip(order).Lines[0];

        Assert.Equal((6m, 3m), (line.CostWithoutTax, line.UnitCost));
    }

    [Fact]
    public void Missing_optional_values_stay_empty()
    {
        var line = RoundTrip(SampleOrder()).Lines[1];

        Assert.Null(line.CostWithoutTax);
        Assert.Null(line.UnitCost);
        Assert.Null(line.RetailPrice);
        Assert.Null(line.Barcode);
    }
}

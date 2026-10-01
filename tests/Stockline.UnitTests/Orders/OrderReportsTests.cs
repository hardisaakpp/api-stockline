using ClosedXML.Excel;
using Stockline.Application.Reports;
using Stockline.Domain.Erp;
using Stockline.Domain.Orders;
using Stockline.Infrastructure.Excel;

namespace Stockline.UnitTests.Orders;

public class OrderReportsTests
{
    private static readonly OrderLocation[] Locations =
    [
        new("WH-1", "Main warehouse", LocationKind.Warehouse, null),
        new("ST-1", "Store one", LocationKind.Store, "WH-1"),
        new("ST-2", "Store two", LocationKind.Store, "WH-1"),
    ];

    private static Order SampleOrder()
    {
        var header = new OrderHeader("PO-9", new DateOnly(2026, 9, 24), new DateOnly(2026, 11, 1), null, null, null, "ACME SUPPLIER", null, null, null, null);
        // X6 pack: unit cost = 5.6 / (12 / 6) = 2.8; retail 11.5 / 1.15 = 10 => margin 0.72
        var priced = new OrderLine("SKU-1", "786900", "Basic tee", new Pack(6), 5.6m, 0m, 11.5m,
        [
            new Allocation("WH-1", "Main warehouse", LocationKind.Warehouse, 4m),
            new Allocation("ST-1", "Store one", LocationKind.Store, 4m),
            new Allocation("ST-2", "Store two", LocationKind.Store, 2m),
        ]);
        var unpriced = new OrderLine("SKU-2", null, null, new Pack(1), null, 0m, null, [new Allocation("ST-1", "Store one", LocationKind.Store, 3m)]);
        return Order.Create(Guid.NewGuid(), header, [unpriced, priced], Locations);
    }

    [Fact]
    public void Margin_removes_the_sales_tax_from_the_retail_price()
    {
        Assert.Equal(0.72m, Math.Round(MarginCalculator.Calculate(2.8m, 11.5m)!.Value, 6));
    }

    [Theory]
    [InlineData(null, 11.5)]
    [InlineData(2.8, null)]
    [InlineData(2.8, 0.0)]
    public void Margin_is_unknown_without_cost_or_a_usable_retail_price(double? cost, double? retail) =>
        Assert.Null(MarginCalculator.Calculate((decimal?)cost, (decimal?)retail));

    [Fact]
    public void Margin_is_negative_when_cost_exceeds_the_net_price() =>
        Assert.True(MarginCalculator.Calculate(12m, 11.5m) < 0);

    [Fact]
    public void Margin_report_lists_products_in_code_order_with_totals()
    {
        var report = MarginReport.Build(SampleOrder());

        Assert.Equal(["SKU-1", "SKU-2"], report.Rows.Select(r => r.ProductCode));
        Assert.Equal((2.8m, 10m), (report.Rows[0].UnitCost, report.Rows[0].Quantity));
        Assert.Equal(0.72m, Math.Round(report.Rows[0].Margin!.Value, 6));
        Assert.Null(report.Rows[1].Margin);
        Assert.Equal(13m, report.TotalQuantity);
    }

    [Fact]
    public void Differences_report_compares_requested_against_confirmed_and_costs_the_gap()
    {
        var order = SampleOrder();
        order.Apply(OrderAction.Submit, ActorRole.Buyer, "u", DateTimeOffset.UtcNow);
        order.Apply(OrderAction.Open, ActorRole.Supplier, "u", DateTimeOffset.UtcNow);
        order.ConfirmLineQuantity("SKU-1", 7m);

        var report = DifferencesReport.Build(order);

        var sku1 = report.Products.Single(p => p.ProductCode == "SKU-1");
        Assert.Equal((10m, 7m, -3m), (sku1.Requested, sku1.Confirmed, sku1.Difference));
        Assert.Equal((56m, 39.2m, -16.8m), (sku1.OriginalCost, sku1.ConfirmedCost, sku1.CostDifference));
        Assert.Null(report.Products.Single(p => p.ProductCode == "SKU-2").OriginalCost);
        Assert.Equal((13m, 10m, -3m), (report.TotalRequested, report.TotalConfirmed, report.TotalDifference));
        Assert.Equal(-16.8m, report.TotalCostDifference);
        Assert.Equal([("WH-1", 4m), ("ST-1", 7m), ("ST-2", 2m)], report.Locations.Select(l => (l.LocationCode, l.Allocated)));
    }

    private static ErpDocument Transfer(Order order, string store, ErpDocumentStatus status, long docNum)
    {
        var doc = new ErpDocument(Guid.NewGuid(), order.Id, ErpDocumentKind.TransferRequest, "WH-1", store, null, []);
        if (status == ErpDocumentStatus.Sent)
        {
            doc.MarkSent(docNum, docNum, DateTimeOffset.UtcNow);
        }
        else
        {
            doc.MarkFailed("boom");
        }

        return doc;
    }

    [Fact]
    public void Transfers_report_attaches_the_transfer_number_per_store_and_flags_the_rest()
    {
        var order = SampleOrder();
        ErpDocument[] docs =
        [
            Transfer(order, "ST-1", ErpDocumentStatus.Sent, 800001),
            Transfer(order, "ST-2", ErpDocumentStatus.Failed, 0),
            new ErpDocument(Guid.NewGuid(), order.Id, ErpDocumentKind.PurchaseOrder, "WH-1", null, null, []),
        ];

        var report = TransfersReport.Build(order, docs);

        Assert.All(report.Rows.Where(r => r.LocationCode == "ST-1"), r => Assert.Equal((800001L, TransferStatus.Sent), (r.DocNum, r.Status)));
        Assert.All(report.Rows.Where(r => r.LocationCode == "ST-2"), r => Assert.Equal((null, TransferStatus.Pending), (r.DocNum, r.Status)));
        Assert.All(report.Rows.Where(r => r.LocationCode == "WH-1"), r => Assert.Equal(TransferStatus.NotApplicable, r.Status));
        Assert.Equal(["ST-2"], report.StoresWithoutDocument);
        Assert.Equal("WH-1", report.Rows[^1].LocationCode);
        Assert.Equal(13m, report.TotalAllocated);
    }

    [Fact]
    public void Transfers_report_without_any_document_marks_every_store_pending()
    {
        var report = TransfersReport.Build(SampleOrder(), []);

        Assert.Equal(["ST-1", "ST-2"], report.StoresWithoutDocument);
    }

    [Fact]
    public void Margin_workbook_contains_the_calculated_margin_and_flags_negatives()
    {
        var order = SampleOrder();
        order.Apply(OrderAction.Submit, ActorRole.Buyer, "u", DateTimeOffset.UtcNow);
        order.Apply(OrderAction.Open, ActorRole.Supplier, "u", DateTimeOffset.UtcNow);
        order.ChangeLineCost("SKU-1", 60m);

        using var workbook = new XLWorkbook(new MemoryStream(new ClosedXmlOrderReportExporter().Export(MarginReport.Build(order))));
        var sheet = workbook.Worksheet("Margin");

        Assert.Equal("SKU-1", sheet.Cell(2, 1).GetString());
        Assert.True(sheet.Cell(2, 8).GetDouble() < 0);
        Assert.Equal(XLColor.Red, sheet.Cell(2, 8).Style.Font.FontColor);
        Assert.True(sheet.Cell(3, 8).IsEmpty());
    }

    [Fact]
    public void Differences_and_transfers_workbooks_have_the_expected_sheets()
    {
        var order = SampleOrder();
        var exporter = new ClosedXmlOrderReportExporter();

        using var differences = new XLWorkbook(new MemoryStream(exporter.Export(DifferencesReport.Build(order))));
        using var transfers = new XLWorkbook(new MemoryStream(exporter.Export(TransfersReport.Build(order, []))));

        Assert.Equal(["Summary", "Differences by product", "Allocation by location"], differences.Worksheets.Select(w => w.Name));
        Assert.Equal(["Order information", "Transfers"], transfers.Worksheets.Select(w => w.Name));
        Assert.Equal("Pending", transfers.Worksheet("Transfers").Cell(2, 1).GetString());
    }
}

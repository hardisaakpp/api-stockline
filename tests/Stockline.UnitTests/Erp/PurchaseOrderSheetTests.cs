using Stockline.Application.Erp;
using Stockline.Domain.Erp;
using Stockline.Domain.Orders;
using Stockline.Infrastructure.Pdf;

namespace Stockline.UnitTests.Erp;

public class PurchaseOrderSheetTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly BuyerProfile Buyer = new("Demo Buyer, Inc.", "1234567", "1 Main St");

    private static (Order Order, ErpDocument Document) Sample(bool sent = true)
    {
        var header = new OrderHeader("PO-5", new DateOnly(2026, 9, 24), new DateOnly(2026, 11, 1), null, null, null, "ACME SUPPLIER", null, null, null, null);
        var order = Order.Create(
            Guid.NewGuid(), header,
            [
                new OrderLine("SKU-B", "7860002", "Hoodie", new Pack(12), 20m, 0.1m, 40m, [new Allocation("WH-1", null, LocationKind.Warehouse, 5m)]),
                new OrderLine("SKU-A", "7860001", "Basic tee", new Pack(12), 10m, 0m, 20m, [new Allocation("WH-1", null, LocationKind.Warehouse, 10m)]),
            ],
            [new OrderLocation("WH-1", "Main", LocationKind.Warehouse, null)]);
        var document = new ErpDocument(
            Guid.NewGuid(), order.Id, ErpDocumentKind.PurchaseOrder, "WH-1", null, null,
            [new ErpDocumentLine("SKU-B", 5m, 20m, 10m), new ErpDocumentLine("SKU-A", 10m, 10m, 0m)]);
        if (sent)
        {
            document.MarkSent(11, 9001, At);
        }

        return (order, document);
    }

    [Fact]
    public void Sheet_calculates_line_totals_discount_tax_and_total_with_lines_sorted_by_code()
    {
        var (order, document) = Sample();

        var sheet = PurchaseOrderSheet.Build(order, document, Buyer, At);

        Assert.Equal("9001", sheet.Number);
        Assert.Equal(["SKU-A", "SKU-B"], sheet.Lines.Select(l => l.ProductCode));
        Assert.Equal([100m, 90m], sheet.Lines.Select(l => l.Total));
        Assert.Equal(("7860001", "Basic tee"), (sheet.Lines[0].Barcode, sheet.Lines[0].Description));
        Assert.Equal(200m, sheet.Subtotal);   // 100 + 5 x 20
        Assert.Equal(10m, sheet.Discount);    // 10% of 100
        Assert.Equal(28.5m, sheet.Tax);       // 15% of 190
        Assert.Equal(218.5m, sheet.Total);
        Assert.Equal(("WH-1", "ACME SUPPLIER", "PO-5"), (sheet.Warehouse, sheet.SupplierName, sheet.OrderCode));
    }

    [Fact]
    public void Unsent_purchase_order_is_labelled_pending_and_transfers_cannot_be_printed()
    {
        var (order, document) = Sample(sent: false);
        var transfer = new ErpDocument(Guid.NewGuid(), order.Id, ErpDocumentKind.TransferRequest, "WH-1", "ST-1", document.Id, []);

        Assert.Equal("PENDING", PurchaseOrderSheet.Build(order, document, Buyer, At).Number);
        Assert.Throws<ArgumentException>(() => PurchaseOrderSheet.Build(order, transfer, Buyer, At));
    }

    [Fact]
    public void Renderer_produces_a_pdf_document()
    {
        var (order, document) = Sample();

        var pdf = new QuestPdfPurchaseOrderRenderer().Render(PurchaseOrderSheet.Build(order, document, Buyer, At));

        Assert.True(pdf.Length > 1000);
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(pdf, 0, 5));
    }

    [Fact]
    public void Renderer_copes_with_many_lines_that_span_several_pages_and_missing_optional_data()
    {
        var header = new OrderHeader("PO-BIG", new DateOnly(2026, 9, 24), null, null, null, null, "ACME", null, null, null, null);
        var lines = Enumerable.Range(1, 120).Select(i =>
            new OrderLine($"SKU-{i:000}", null, null, new Pack(1), 1m, 0m, null, [new Allocation("WH-1", null, LocationKind.Warehouse, i)])).ToList();
        var order = Order.Create(Guid.NewGuid(), header, lines, [new OrderLocation("WH-1", null, LocationKind.Warehouse, null)]);
        var document = new ErpDocument(
            Guid.NewGuid(), order.Id, ErpDocumentKind.PurchaseOrder, "WH-1", null, null,
            [.. lines.Select(l => new ErpDocumentLine(l.ProductCode, l.TotalQuantity, 1m, 0m))]);

        var pdf = new QuestPdfPurchaseOrderRenderer().Render(PurchaseOrderSheet.Build(order, document, new BuyerProfile("Buyer"), At));

        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(pdf, 0, 5));
    }
}

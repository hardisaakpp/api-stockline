using Stockline.Domain.Erp;
using Stockline.Infrastructure.Persistence;

namespace Stockline.UnitTests.Persistence;

public class ErpDocumentPersistenceTests : IDisposable
{
    private readonly SqliteDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Documents_round_trip_with_lines_status_and_erp_numbers()
    {
        var orderId = Guid.NewGuid();
        var purchaseOrder = new ErpDocument(
            Guid.NewGuid(), orderId, ErpDocumentKind.PurchaseOrder, "WH-A", null, null,
            [new ErpDocumentLine("SKU-1", 6m, 2.8m, 20m), new ErpDocumentLine("SKU-2", 1.5m, 0m, 0m)]);
        var transfer = new ErpDocument(
            Guid.NewGuid(), orderId, ErpDocumentKind.TransferRequest, "WH-A", "ST-A1", purchaseOrder.Id,
            [new ErpDocumentLine("SKU-1", 1m, 0m, 0m)]);
        purchaseOrder.MarkSent(11, 5011, new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        transfer.MarkFailed("Stock too low");

        await using (var context = _db.NewContext())
        {
            var repository = new EfErpDocumentRepository(context);
            repository.AddRange([transfer, purchaseOrder]);
            await context.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var loaded = await new EfErpDocumentRepository(read).ListByOrderAsync(orderId, default);

        Assert.Equal([ErpDocumentKind.PurchaseOrder, ErpDocumentKind.TransferRequest], loaded.Select(d => d.Kind));
        var po = loaded[0];
        Assert.Equal((ErpDocumentStatus.Sent, 1, 11L, 5011L), (po.Status, po.Attempts, po.DocEntry, po.DocNum));
        Assert.Equal([6m, 1.5m], po.Lines.Select(l => l.Quantity));
        Assert.Equal(20m, po.Lines[0].DiscountPercent);
        var tr = loaded[1];
        Assert.Equal((ErpDocumentStatus.Failed, "Stock too low", "ST-A1", (Guid?)purchaseOrder.Id),
            (tr.Status, tr.LastError, tr.DestinationLocation, tr.PurchaseOrderDocumentId));
    }

    [Fact]
    public async Task Documents_of_other_orders_are_not_returned()
    {
        await using (var context = _db.NewContext())
        {
            new EfErpDocumentRepository(context).AddRange(
                [new ErpDocument(Guid.NewGuid(), Guid.NewGuid(), ErpDocumentKind.PurchaseOrder, "WH-A", null, null, [new ErpDocumentLine("S", 1m, 0m, 0m)])]);
            await context.SaveChangesAsync();
        }

        await using var read = _db.NewContext();

        Assert.Empty(await new EfErpDocumentRepository(read).ListByOrderAsync(Guid.NewGuid(), default));
    }
}

using Stockline.Domain.Erp;
using Stockline.Domain.Orders;

namespace Stockline.Application.Erp;

public sealed record BuyerProfile(string Name, string? TaxId = null, string? Address = null);

public sealed record SheetLine(
    string ProductCode, string? Barcode, string? Description, decimal Quantity, decimal UnitPrice, decimal DiscountPercent, decimal Total);

/// <summary>Everything a printed purchase order shows, already calculated; renderers only lay it out.</summary>
public sealed record PurchaseOrderSheet(
    string Number,
    string OrderCode,
    string SupplierName,
    BuyerProfile Buyer,
    string Warehouse,
    DateOnly OrderDate,
    DateOnly? DeliveryDate,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<SheetLine> Lines,
    decimal Subtotal,
    decimal Discount,
    decimal Tax,
    decimal Total)
{
    public static PurchaseOrderSheet Build(Order order, ErpDocument document, BuyerProfile buyer, DateTimeOffset now)
    {
        if (document.Kind != ErpDocumentKind.PurchaseOrder)
        {
            throw new ArgumentException("Only purchase orders can be printed.", nameof(document));
        }

        var lines = document.Lines.Select(l =>
        {
            var line = order.Lines.FirstOrDefault(x => x.ProductCode.Equals(l.ProductCode, StringComparison.OrdinalIgnoreCase));
            var net = l.Quantity * l.UnitPrice * (1 - l.DiscountPercent / 100m);
            return new SheetLine(l.ProductCode, line?.Barcode, line?.Description, l.Quantity, l.UnitPrice, l.DiscountPercent, Math.Round(net, 4));
        }).OrderBy(l => l.ProductCode, StringComparer.Ordinal).ToList();

        var gross = document.Lines.Sum(l => l.Quantity * l.UnitPrice);
        var net = lines.Sum(l => l.Total);
        var discount = gross - net;
        var tax = net * (MarginCalculator.SalesTaxFactor - 1);

        return new PurchaseOrderSheet(
            document.DocNum?.ToString() ?? "PENDING",
            order.Header.Code,
            order.Header.SupplierName,
            buyer,
            document.OriginWarehouse,
            order.Header.OrderDate,
            order.Header.DeliveryDate,
            now,
            lines,
            Math.Round(gross, 2),
            Math.Round(discount, 2),
            Math.Round(tax, 2),
            Math.Round(net + tax, 2));
    }
}

public interface IPurchaseOrderPdfRenderer
{
    byte[] Render(PurchaseOrderSheet sheet);
}

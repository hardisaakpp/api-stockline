using Stockline.Application.Notifications;
using Stockline.Application.Orders;
using Stockline.Domain.Erp;

namespace Stockline.Application.Erp;

/// <summary>Prints the purchase orders an order sent to the ERP, e.g. to attach them to the supplier's email.</summary>
public sealed class PurchaseOrderAttachments(
    IOrderRepository orders,
    IErpDocumentRepository documents,
    IPurchaseOrderPdfRenderer renderer,
    BuyerProfile buyer,
    TimeProvider clock)
{
    public async Task<IReadOnlyList<EmailAttachment>> ForAsync(Guid orderId, CancellationToken ct)
    {
        var order = await orders.GetAsync(orderId, ct);
        if (order is null)
        {
            return [];
        }

        return [.. (await documents.ListByOrderAsync(orderId, ct))
            .Where(d => d.Kind == ErpDocumentKind.PurchaseOrder && d.Status == ErpDocumentStatus.Sent)
            .OrderBy(d => d.DocNum)
            .Select(d => new EmailAttachment(
                $"purchase-order-{d.DocNum}.pdf",
                "application/pdf",
                renderer.Render(PurchaseOrderSheet.Build(order, d, buyer, clock.GetUtcNow()))))];
    }
}

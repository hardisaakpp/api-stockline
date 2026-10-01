using Stockline.Application.Notifications;
using Stockline.Application.Orders;
using Stockline.Domain.Erp;
using Stockline.Domain.Orders;

namespace Stockline.Application.Erp;

public enum ErpSubmissionOutcome
{
    Sent,
    LogisticsPending,
    Failed,
    NotFound,
    InvalidState,
    Invalid,
    Conflict,
}

public sealed record ErpSubmissionResult(
    ErpSubmissionOutcome Outcome,
    OrderStatus? Status,
    IReadOnlyList<ErpDocument> Documents,
    IReadOnlyList<ValidationIssue> Issues);

public sealed class SubmitOrderToErpHandler(
    IOrderRepository orders, IErpDocumentRepository documents, IErpGateway erp, TimeProvider clock, IOrderEvents? events = null)
{
    private static readonly OrderStatus[] Submittable =
        [OrderStatus.RedistributionConfirmed, OrderStatus.ErpSubmissionFailed, OrderStatus.LogisticsPending];

    public async Task<ErpSubmissionResult> HandleAsync(Guid orderId, CancellationToken ct)
    {
        var order = await orders.GetForUpdateAsync(orderId, ct);
        if (order is null)
        {
            return Result(ErpSubmissionOutcome.NotFound, null);
        }

        if (!Submittable.Contains(order.Status))
        {
            return Result(ErpSubmissionOutcome.InvalidState, order.Status);
        }

        var existing = await documents.ListByOrderAsync(orderId, ct);
        if (existing.Count == 0)
        {
            var plan = ErpDocumentPlanner.Plan(order);
            if (plan.Issues.Count > 0)
            {
                return Result(ErpSubmissionOutcome.Invalid, order.Status, issues: plan.Issues);
            }

            existing = [.. plan.Documents];
            documents.AddRange(existing);
            await orders.SaveChangesAsync(ct);
        }

        await SendPendingAsync(order, existing, ct);

        var action = Evaluate(existing);
        order.Apply(action, ActorRole.System, "system", clock.GetUtcNow());
        try
        {
            await orders.SaveChangesAsync(ct);
        }
        catch (OrderConcurrencyException)
        {
            return Result(ErpSubmissionOutcome.Conflict, null, existing);
        }

        await events.PublishAsync(order, "ErpSubmission", "system", clock.GetUtcNow(), ct);

        var outcome = action switch
        {
            OrderAction.MarkSent => ErpSubmissionOutcome.Sent,
            OrderAction.MarkLogisticsPending => ErpSubmissionOutcome.LogisticsPending,
            _ => ErpSubmissionOutcome.Failed,
        };
        return Result(outcome, order.Status, existing);
    }

    private async Task SendPendingAsync(Order order, List<ErpDocument> docs, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        foreach (var purchaseOrder in docs.Where(d => d.Kind == ErpDocumentKind.PurchaseOrder && d.Status != ErpDocumentStatus.Sent))
        {
            var request = new PurchaseOrderRequest(
                purchaseOrder.Id.ToString(), order.Header.SupplierName, purchaseOrder.OriginWarehouse, today,
                order.Header.DeliveryDate, $"Order {order.Header.Code}", ToErpLines(purchaseOrder));
            Record(purchaseOrder, await erp.CreatePurchaseOrderAsync(request, ct));
            await orders.SaveChangesAsync(ct);
        }

        foreach (var transfer in docs.Where(d => d.Kind == ErpDocumentKind.TransferRequest && d.Status != ErpDocumentStatus.Sent))
        {
            var parent = docs.First(d => d.Id == transfer.PurchaseOrderDocumentId);
            if (parent.Status != ErpDocumentStatus.Sent)
            {
                continue;
            }

            var request = new TransferRequestRequest(
                transfer.Id.ToString(), transfer.OriginWarehouse, transfer.DestinationLocation!, today, ToErpLines(transfer));
            Record(transfer, await erp.CreateTransferRequestAsync(request, ct));
            await orders.SaveChangesAsync(ct);
        }
    }

    private void Record(ErpDocument document, ErpResult result)
    {
        if (result.Success)
        {
            document.MarkSent(result.DocEntry!.Value, result.DocNum!.Value, clock.GetUtcNow());
        }
        else
        {
            document.MarkFailed(result.Error ?? "Unknown ERP error.", result.IsTransient);
        }
    }

    private static OrderAction Evaluate(List<ErpDocument> docs)
    {
        if (docs.Any(d => d.Kind == ErpDocumentKind.PurchaseOrder && d.Status != ErpDocumentStatus.Sent))
        {
            return OrderAction.MarkErpSubmissionFailed;
        }

        return docs.All(d => d.Status == ErpDocumentStatus.Sent) ? OrderAction.MarkSent : OrderAction.MarkLogisticsPending;
    }

    private static List<ErpLine> ToErpLines(ErpDocument document) =>
        [.. document.Lines.Select(l => new ErpLine(l.ProductCode, l.Quantity, l.UnitPrice, l.DiscountPercent))];

    private static ErpSubmissionResult Result(
        ErpSubmissionOutcome outcome,
        OrderStatus? status,
        IReadOnlyList<ErpDocument>? docs = null,
        IReadOnlyList<ValidationIssue>? issues = null) =>
        new(outcome, status, docs ?? [], issues ?? []);
}

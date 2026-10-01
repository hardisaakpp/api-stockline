using Stockline.Application.Notifications;
using Stockline.Application.Orders;
using Stockline.Domain;
using Stockline.Domain.Erp;
using Stockline.Domain.Orders;

namespace Stockline.Application.Erp;

/// <summary>
/// Lets the buyer fix a product code the ERP rejected. The corrected code is checked against the ERP and the
/// unsent documents are dropped so the next submission plans them again with the right code.
/// </summary>
public sealed class CorrectProductCodeHandler(
    IOrderRepository orders, IErpDocumentRepository documents, IErpGateway erp, TimeProvider clock, IOrderEvents? events = null)
{
    public async Task<CommandResult> HandleAsync(
        Guid orderId, string productCode, string newCode, ActorRole actor, string? userId, CancellationToken ct)
    {
        if (actor != ActorRole.Buyer)
        {
            return new CommandResult(CommandOutcome.Forbidden, Message: "Only the buyer can correct a product code.");
        }

        var order = await orders.GetForUpdateAsync(orderId, ct);
        if (order is null)
        {
            return new CommandResult(CommandOutcome.NotFound);
        }

        if (order.Status != OrderStatus.ErpSubmissionFailed)
        {
            return new CommandResult(
                CommandOutcome.InvalidState,
                Message: $"Product codes can only be corrected while the ERP submission is failed, not in {order.Status}.");
        }

        var existing = await documents.ListByOrderAsync(orderId, ct);
        if (existing.Any(d => d.Status == ErpDocumentStatus.Sent))
        {
            return new CommandResult(
                CommandOutcome.Conflict,
                Message: "Some documents were already created in the ERP, so the products can no longer change.");
        }

        var trimmed = newCode?.Trim() ?? string.Empty;
        var check = await erp.CheckMasterDataAsync(new MasterDataCheck(order.Header.SupplierName, [trimmed], []), ct);
        if (check.Error is not null)
        {
            return new CommandResult(CommandOutcome.Conflict, Message: check.Error);
        }

        if (check.Problems.Any(p => p.StartsWith("items", StringComparison.Ordinal)))
        {
            return new CommandResult(
                CommandOutcome.Invalid,
                Message: $"The ERP does not know product '{trimmed}'.",
                Issues: [new ValidationIssue("ERP_ITEM_NOT_FOUND", $"Product '{trimmed}' does not exist in the ERP.", trimmed)]);
        }

        var now = clock.GetUtcNow();
        try
        {
            order.ChangeLineProductCode(productCode, trimmed, new EditContext(actor, userId, now));
            documents.RemoveRange(existing);
            await orders.SaveChangesAsync(ct);
        }
        catch (InvalidStateException ex)
        {
            return new CommandResult(CommandOutcome.InvalidState, Message: ex.Message);
        }
        catch (DomainException ex)
        {
            return new CommandResult(CommandOutcome.Invalid, Message: ex.Message);
        }
        catch (OrderConcurrencyException)
        {
            return new CommandResult(CommandOutcome.Conflict, Message: "The order was modified by someone else.");
        }

        await events.PublishAsync(order, "ProductCodeCorrected", userId, now, ct);
        return new CommandResult(CommandOutcome.Succeeded, order);
    }
}

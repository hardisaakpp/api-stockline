using Stockline.Application.Orders.Import;
using Stockline.Domain.Orders;

namespace Stockline.Api.Orders;

public sealed record ImportOrderResponse(Guid OrderId, IReadOnlyList<ImportIssue> Warnings);

public sealed record AllocationResponse(string LocationCode, string? LocationName, string Kind, decimal Quantity);

public sealed record OrderLineResponse(
    string ProductCode,
    string? Barcode,
    string? Description,
    string Pack,
    decimal? CostWithoutTax,
    decimal? UnitCost,
    decimal? Discount,
    decimal? RetailPrice,
    decimal RequestedQuantity,
    decimal ConfirmedQuantity,
    decimal TotalQuantity,
    decimal StoreQuantity,
    IReadOnlyList<AllocationResponse> Allocations,
    string? Comment = null);

public sealed record StatusChangeResponse(
    string From, string To, string Action, string Actor, string? UserId, DateTimeOffset At);

public sealed record OrderResponse(
    Guid Id,
    Guid? ParentOrderId,
    string Status,
    int RedistributionVersion,
    OrderHeader Header,
    IReadOnlyList<OrderLocation> Locations,
    IReadOnlyList<OrderLineResponse> Lines,
    IReadOnlyList<StatusChangeResponse> History,
    IReadOnlyList<ValidationIssue>? Warnings = null,
    string? CreatedBy = null,
    DateTimeOffset? CreatedAt = null,
    string? ModifiedBy = null,
    DateTimeOffset? ModifiedAt = null)
{
    /// <summary>Builds the response for <paramref name="viewer"/>, withholding the commercial data the role must not see.</summary>
    public static OrderResponse From(Order order, ActorRole viewer) => new(
        order.Id,
        order.ParentOrderId,
        order.Status.ToString(),
        order.RedistributionVersion,
        order.Header,
        order.Locations,
        [.. order.Lines.Select(l => new OrderLineResponse(
            l.ProductCode, l.Barcode, l.Description, l.Pack.ToString(),
            OrderVisibility.CanSeeCosts(viewer) ? l.CostWithoutTax : null,
            OrderVisibility.CanSeeCosts(viewer) ? l.UnitCost : null,
            OrderVisibility.CanSeeCosts(viewer) ? l.Discount : null,
            OrderVisibility.CanSeeRetailPrice(viewer) ? l.RetailPrice : null,
            l.RequestedQuantity, l.ConfirmedQuantity, l.TotalQuantity, l.StoreQuantity,
            [.. l.Allocations.Select(a => new AllocationResponse(a.LocationCode, a.LocationName, a.Kind.ToString(), a.Quantity))],
            l.Comment))],
        [.. order.History.Select(h => new StatusChangeResponse(
            h.From.ToString(), h.To.ToString(), h.Action.ToString(), h.Actor.ToString(), h.UserId, h.At))],
        null,
        order.CreatedBy,
        order.CreatedAt,
        order.ModifiedBy,
        order.ModifiedAt);
}

public sealed record AuditLogEntry(
    DateTimeOffset At, string? UserId, string Actor, string Type, string? ProductCode, string Field, string? OldValue, string? NewValue)
{
    /// <summary>Status changes and field edits, newest first; cost edits are withheld from roles that must not see costs.</summary>
    public static IReadOnlyList<AuditLogEntry> For(Order order, ActorRole viewer)
    {
        var changes = order.History.Select(h => new AuditLogEntry(
            h.At, h.UserId, h.Actor.ToString(), "StatusChange", null, "Status", h.From.ToString(), h.To.ToString()));
        var edits = order.Audit
            .Where(a => OrderVisibility.CanSeeCosts(viewer) || !a.IsCostField)
            .Select(a => new AuditLogEntry(
                a.At, a.UserId, a.Actor.ToString(), "FieldChange", a.ProductCode, a.Field, a.OldValue, a.NewValue));
        return [.. changes.Concat(edits).OrderByDescending(e => e.At)];
    }
}

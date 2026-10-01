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
    decimal Discount,
    decimal? RetailPrice,
    decimal RequestedQuantity,
    decimal ConfirmedQuantity,
    decimal TotalQuantity,
    decimal StoreQuantity,
    IReadOnlyList<AllocationResponse> Allocations);

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
    IReadOnlyList<StatusChangeResponse> History)
{
    public static OrderResponse From(Order order) => new(
        order.Id,
        order.ParentOrderId,
        order.Status.ToString(),
        order.RedistributionVersion,
        order.Header,
        order.Locations,
        [.. order.Lines.Select(l => new OrderLineResponse(
            l.ProductCode, l.Barcode, l.Description, l.Pack.ToString(), l.CostWithoutTax, l.UnitCost, l.Discount,
            l.RetailPrice, l.RequestedQuantity, l.ConfirmedQuantity, l.TotalQuantity, l.StoreQuantity,
            [.. l.Allocations.Select(a => new AllocationResponse(a.LocationCode, a.LocationName, a.Kind.ToString(), a.Quantity))]))],
        [.. order.History.Select(h => new StatusChangeResponse(
            h.From.ToString(), h.To.ToString(), h.Action.ToString(), h.Actor.ToString(), h.UserId, h.At))]);
}

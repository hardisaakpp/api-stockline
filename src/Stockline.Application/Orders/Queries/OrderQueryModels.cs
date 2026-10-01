using Stockline.Domain.Orders;

namespace Stockline.Application.Orders.Queries;

public enum OrderSortField
{
    OrderDate,
    Code,
    Status,
}

public sealed record OrderListQuery(
    OrderStatus? Status = null,
    string? Search = null,
    int Page = 1,
    int PageSize = 20,
    OrderSortField SortBy = OrderSortField.OrderDate,
    bool Descending = true,
    DateOnly? DateFrom = null,
    DateOnly? DateTo = null,
    string? Supplier = null,
    string? CreatedBy = null,
    string? ModifiedBy = null,
    IReadOnlyCollection<OrderStatus>? OnlyStatuses = null,
    IReadOnlyCollection<OrderStatus>? VisibleStatuses = null)
{
    public const int MaxPageSize = 100;

    public OrderListQuery Normalized() => this with
    {
        Page = Math.Max(1, Page),
        PageSize = Math.Clamp(PageSize, 1, MaxPageSize),
        Search = string.IsNullOrWhiteSpace(Search) ? null : Search.Trim(),
        Supplier = string.IsNullOrWhiteSpace(Supplier) ? null : Supplier.Trim(),
        CreatedBy = string.IsNullOrWhiteSpace(CreatedBy) ? null : CreatedBy.Trim(),
        ModifiedBy = string.IsNullOrWhiteSpace(ModifiedBy) ? null : ModifiedBy.Trim(),
    };
}

public sealed record OrderSummary(
    Guid Id,
    Guid? ParentOrderId,
    string Code,
    string SupplierName,
    string Status,
    DateOnly OrderDate,
    DateOnly? DeliveryDate,
    int LineCount,
    decimal RequestedQuantity,
    decimal ConfirmedQuantity,
    string? CreatedBy = null,
    string? ModifiedBy = null,
    DateTimeOffset? ModifiedAt = null);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => (int)Math.Ceiling(TotalCount / (double)PageSize);
}

public sealed record StatusCount(string Status, int Count);

public sealed record DailyCount(DateOnly Date, int Count);

public sealed record DashboardSummary(
    int TotalOrders,
    int OpenOrders,
    int AwaitingMyAction,
    int ErpFailures,
    IReadOnlyList<StatusCount> ByStatus,
    IReadOnlyList<DailyCount> Last30Days,
    IReadOnlyList<OrderSummary> Recent);

public sealed record OrderFilters(
    IReadOnlyList<string> Statuses, IReadOnlyList<string> Creators, IReadOnlyList<string> Modifiers);

public sealed record LocationSummary(string Code, string? Name, string Kind, string? SupplyingWarehouseCode);

/// <summary>Criteria the dashboard needs resolved by the caller (clock, actor) so the store stays a dumb query.</summary>
public sealed record DashboardCriteria(
    DateOnly Today,
    IReadOnlyCollection<OrderStatus> ActionableStatuses,
    IReadOnlyCollection<OrderStatus> TerminalStatuses,
    IReadOnlyCollection<OrderStatus>? VisibleStatuses = null);

public interface IOrderQueries
{
    Task<PagedResult<OrderSummary>> ListAsync(OrderListQuery query, CancellationToken ct);

    Task<DashboardSummary> DashboardAsync(DashboardCriteria criteria, CancellationToken ct);

    /// <summary>Values to offer in the list filters: statuses in use, creators and last modifiers.</summary>
    Task<OrderFilters> FiltersAsync(IReadOnlyCollection<OrderStatus>? visibleStatuses, CancellationToken ct);

    /// <summary>The order most recently imported, optionally only by <paramref name="createdBy"/>.</summary>
    Task<OrderSummary?> LastCreatedAsync(
        IReadOnlyCollection<OrderStatus>? visibleStatuses, string? createdBy, CancellationToken ct);

    /// <summary>The most recently redistributed order, optionally only those last touched by <paramref name="modifiedBy"/>.</summary>
    Task<OrderSummary?> LastRedistributedAsync(
        IReadOnlyCollection<OrderStatus>? visibleStatuses, string? modifiedBy, CancellationToken ct);

    /// <summary>Active locations of the master catalog, e.g. to pick a store to add to a redistribution.</summary>
    Task<IReadOnlyList<LocationSummary>> SearchLocationsAsync(string? search, LocationKind? kind, int limit, CancellationToken ct);
}

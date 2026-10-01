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
    bool Descending = true)
{
    public const int MaxPageSize = 100;

    public OrderListQuery Normalized() => this with
    {
        Page = Math.Max(1, Page),
        PageSize = Math.Clamp(PageSize, 1, MaxPageSize),
        Search = string.IsNullOrWhiteSpace(Search) ? null : Search.Trim(),
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
    decimal ConfirmedQuantity);

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

/// <summary>Criteria the dashboard needs resolved by the caller (clock, actor) so the store stays a dumb query.</summary>
public sealed record DashboardCriteria(
    DateOnly Today, IReadOnlyCollection<OrderStatus> ActionableStatuses, IReadOnlyCollection<OrderStatus> TerminalStatuses);

public interface IOrderQueries
{
    Task<PagedResult<OrderSummary>> ListAsync(OrderListQuery query, CancellationToken ct);

    Task<DashboardSummary> DashboardAsync(DashboardCriteria criteria, CancellationToken ct);
}

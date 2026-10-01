using Microsoft.EntityFrameworkCore;
using Stockline.Application.Orders.Queries;
using Stockline.Domain.Orders;

namespace Stockline.Infrastructure.Persistence;

public sealed class EfOrderQueries(StocklineDbContext db) : IOrderQueries
{
    private const int DashboardDays = 30;
    private const int RecentOrders = 5;

    public async Task<PagedResult<OrderSummary>> ListAsync(OrderListQuery query, CancellationToken ct)
    {
        query = query.Normalized();
        var orders = db.Orders.AsNoTracking().AsQueryable();
        if (query.Status is { } status)
        {
            orders = orders.Where(o => o.Status == status);
        }

        if (query.Search is { } search)
        {
            orders = orders.Where(o => o.Header.Code.Contains(search) || o.Header.SupplierName.Contains(search));
        }

        var total = await orders.CountAsync(ct);
        var rows = await Sort(orders, query)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(Row)
            .ToListAsync(ct);

        return new PagedResult<OrderSummary>([.. rows.Select(ToSummary)], query.Page, query.PageSize, total);
    }

    public async Task<DashboardSummary> DashboardAsync(DashboardCriteria criteria, CancellationToken ct)
    {
        var orders = db.Orders.AsNoTracking();
        var byStatus = await orders.GroupBy(o => o.Status).Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync(ct);

        var total = byStatus.Sum(s => s.Count);
        var open = byStatus.Where(s => !criteria.TerminalStatuses.Contains(s.Status)).Sum(s => s.Count);
        var awaiting = byStatus.Where(s => criteria.ActionableStatuses.Contains(s.Status)).Sum(s => s.Count);
        var failures = byStatus.Where(s => s.Status == OrderStatus.ErpSubmissionFailed).Sum(s => s.Count);

        var since = criteria.Today.AddDays(-(DashboardDays - 1));
        var perDay = (await orders.Where(o => o.Header.OrderDate >= since && o.Header.OrderDate <= criteria.Today)
                .GroupBy(o => o.Header.OrderDate)
                .Select(g => new { Date = g.Key, Count = g.Count() })
                .ToListAsync(ct))
            .ToDictionary(x => x.Date, x => x.Count);
        var days = Enumerable.Range(0, DashboardDays)
            .Select(i => since.AddDays(i))
            .Select(d => new DailyCount(d, perDay.GetValueOrDefault(d)))
            .ToList();

        var recent = await orders.OrderByDescending(o => o.Header.OrderDate).ThenByDescending(o => o.Header.Code)
            .Take(RecentOrders).Select(Row).ToListAsync(ct);

        return new DashboardSummary(
            total,
            open,
            awaiting,
            failures,
            [.. byStatus.OrderBy(s => s.Status).Select(s => new StatusCount(s.Status.ToString(), s.Count))],
            days,
            [.. recent.Select(ToSummary)]);
    }

    private static IQueryable<Order> Sort(IQueryable<Order> orders, OrderListQuery query) =>
        (query.SortBy, query.Descending) switch
        {
            (OrderSortField.Code, false) => orders.OrderBy(o => o.Header.Code),
            (OrderSortField.Code, true) => orders.OrderByDescending(o => o.Header.Code),
            (OrderSortField.Status, false) => orders.OrderBy(o => o.Status).ThenBy(o => o.Header.Code),
            (OrderSortField.Status, true) => orders.OrderByDescending(o => o.Status).ThenBy(o => o.Header.Code),
            (_, false) => orders.OrderBy(o => o.Header.OrderDate).ThenBy(o => o.Header.Code),
            _ => orders.OrderByDescending(o => o.Header.OrderDate).ThenByDescending(o => o.Header.Code),
        };

    // Quantities are summed in memory: SQLite (used by the test suite) cannot aggregate decimals in SQL.
    private static readonly System.Linq.Expressions.Expression<Func<Order, OrderRow>> Row = o => new OrderRow(
        o.Id,
        o.ParentOrderId,
        o.Header.Code,
        o.Header.SupplierName,
        o.Status,
        o.Header.OrderDate,
        o.Header.DeliveryDate,
        o.Lines.Select(l => new LineQuantities(l.RequestedQuantity, l.ConfirmedQuantity)).ToList());

    private static OrderSummary ToSummary(OrderRow r) => new(
        r.Id,
        r.ParentOrderId,
        r.Code,
        r.SupplierName,
        r.Status.ToString(),
        r.OrderDate,
        r.DeliveryDate,
        r.Lines.Count,
        r.Lines.Sum(l => l.Requested),
        r.Lines.Sum(l => l.Confirmed));

    private sealed record LineQuantities(decimal Requested, decimal Confirmed);

    private sealed record OrderRow(
        Guid Id,
        Guid? ParentOrderId,
        string Code,
        string SupplierName,
        OrderStatus Status,
        DateOnly OrderDate,
        DateOnly? DeliveryDate,
        List<LineQuantities> Lines);
}

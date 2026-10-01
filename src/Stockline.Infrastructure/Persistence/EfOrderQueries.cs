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

        if (query.OnlyStatuses is { } only)
        {
            orders = orders.Where(o => only.Contains(o.Status));
        }

        if (query.VisibleStatuses is { } visible)
        {
            orders = orders.Where(o => visible.Contains(o.Status));
        }

        if (query.Search is { } search)
        {
            orders = orders.Where(o => o.Header.Code.Contains(search) || o.Header.SupplierName.Contains(search));
        }

        if (query.DateFrom is { } from)
        {
            orders = orders.Where(o => o.Header.OrderDate >= from);
        }

        if (query.DateTo is { } to)
        {
            orders = orders.Where(o => o.Header.OrderDate <= to);
        }

        if (query.Supplier is { } supplier)
        {
            orders = orders.Where(o => o.Header.SupplierName.Contains(supplier));
        }

        if (query.CreatedBy is { } createdBy)
        {
            orders = orders.Where(o => o.CreatedBy != null && o.CreatedBy.Contains(createdBy));
        }

        if (query.ModifiedBy is { } modifiedBy)
        {
            orders = orders.Where(o => o.ModifiedBy != null && o.ModifiedBy.Contains(modifiedBy));
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
        if (criteria.VisibleStatuses is { } visibleToRole)
        {
            orders = orders.Where(o => visibleToRole.Contains(o.Status));
        }

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

    public async Task<OrderFilters> FiltersAsync(IReadOnlyCollection<OrderStatus>? visibleStatuses, CancellationToken ct)
    {
        var orders = db.Orders.AsNoTracking();
        if (visibleStatuses is not null)
        {
            orders = orders.Where(o => visibleStatuses.Contains(o.Status));
        }

        var statuses = await orders.Select(o => o.Status).Distinct().ToListAsync(ct);
        var creators = await orders.Where(o => o.CreatedBy != null).Select(o => o.CreatedBy!).Distinct().ToListAsync(ct);
        var modifiers = await orders.Where(o => o.ModifiedBy != null).Select(o => o.ModifiedBy!).Distinct().ToListAsync(ct);
        return new OrderFilters(
            [.. statuses.OrderBy(s => s).Select(s => s.ToString())],
            [.. creators.Order(StringComparer.Ordinal)],
            [.. modifiers.Order(StringComparer.Ordinal)]);
    }

    public async Task<OrderSummary?> LastRedistributedAsync(
        IReadOnlyCollection<OrderStatus>? visibleStatuses, string? modifiedBy, CancellationToken ct)
    {
        var orders = db.Orders.AsNoTracking().Where(o => o.RedistributionVersion > 0);
        if (visibleStatuses is not null)
        {
            orders = orders.Where(o => visibleStatuses.Contains(o.Status));
        }

        if (modifiedBy is not null)
        {
            orders = orders.Where(o => o.ModifiedBy == modifiedBy);
        }

        // DateTimeOffset cannot be ordered in SQL on SQLite (tests), so ties and ordering resolve in memory.
        var candidates = await orders.Select(Row).ToListAsync(ct);
        return candidates
            .OrderByDescending(r => r.ModifiedAt)
            .ThenByDescending(r => r.Code, StringComparer.Ordinal)
            .Select(ToSummary)
            .FirstOrDefault();
    }

    public async Task<IReadOnlyList<LocationSummary>> SearchLocationsAsync(
        string? search, LocationKind? kind, int limit, CancellationToken ct)
    {
        var locations = db.Locations.AsNoTracking().Where(l => l.IsActive);
        if (kind is { } wanted)
        {
            locations = locations.Where(l => l.Kind == wanted);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var text = search.Trim();
            locations = locations.Where(l => l.Code.Contains(text) || l.Name.Contains(text));
        }

        var found = await locations.OrderBy(l => l.Code).Take(Math.Clamp(limit, 1, 100)).ToListAsync(ct);
        return [.. found.Select(l => new LocationSummary(l.Code, l.Name, l.Kind.ToString(), l.SupplyingWarehouseCode))];
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
        o.Lines.Select(l => new LineQuantities(l.RequestedQuantity, l.ConfirmedQuantity)).ToList(),
        o.CreatedBy,
        o.ModifiedBy,
        o.ModifiedAt);

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
        r.Lines.Sum(l => l.Confirmed),
        r.CreatedBy,
        r.ModifiedBy,
        r.ModifiedAt);

    private sealed record LineQuantities(decimal Requested, decimal Confirmed);

    private sealed record OrderRow(
        Guid Id,
        Guid? ParentOrderId,
        string Code,
        string SupplierName,
        OrderStatus Status,
        DateOnly OrderDate,
        DateOnly? DeliveryDate,
        List<LineQuantities> Lines,
        string? CreatedBy,
        string? ModifiedBy,
        DateTimeOffset? ModifiedAt);
}

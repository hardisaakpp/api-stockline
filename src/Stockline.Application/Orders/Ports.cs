using Stockline.Application.Orders.Import;
using Stockline.Domain.Orders;

namespace Stockline.Application.Orders;

public interface IOrderRepository
{
    Task<bool> ExistsAsync(string orderCode, CancellationToken ct);

    Task AddAsync(Order order, CancellationToken ct);

    /// <summary>Tracks a new order without saving; persisted by the next <see cref="SaveChangesAsync"/>.</summary>
    void Stage(Order order);

    Task<int> CountChildrenAsync(Guid parentOrderId, CancellationToken ct);

    Task<Order?> GetAsync(Guid id, CancellationToken ct);

    Task<Order?> GetForUpdateAsync(Guid id, CancellationToken ct);

    /// <exception cref="OrderConcurrencyException">The order changed since it was loaded.</exception>
    Task SaveChangesAsync(CancellationToken ct);
}

public sealed class OrderConcurrencyException() : Exception("The order was modified by someone else.");

public interface ILocationCatalogProvider
{
    Task<LocationCatalog> GetAsync(CancellationToken ct);
}

using Microsoft.EntityFrameworkCore;
using Stockline.Application.Orders;
using Stockline.Domain.Orders;

namespace Stockline.Infrastructure.Persistence;

public sealed class EfOrderRepository(StocklineDbContext db) : IOrderRepository
{
    public Task<bool> ExistsAsync(string orderCode, CancellationToken ct) =>
        db.Orders.AnyAsync(o => o.Header.Code == orderCode, ct);

    public async Task AddAsync(Order order, CancellationToken ct)
    {
        db.Orders.Add(order);
        await db.SaveChangesAsync(ct);
    }

    public void Stage(Order order) => db.Orders.Add(order);

    public Task<int> CountChildrenAsync(Guid parentOrderId, CancellationToken ct) =>
        db.Orders.CountAsync(o => o.ParentOrderId == parentOrderId, ct);

    public Task<Order?> GetAsync(Guid id, CancellationToken ct) =>
        db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, ct);

    public Task<Order?> GetForUpdateAsync(Guid id, CancellationToken ct) =>
        db.Orders.FirstOrDefaultAsync(o => o.Id == id, ct);

    public async Task SaveChangesAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new OrderConcurrencyException();
        }
    }
}

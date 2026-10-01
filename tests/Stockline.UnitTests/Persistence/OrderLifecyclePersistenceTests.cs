using Stockline.Application.Orders;
using Stockline.Domain.Orders;
using Stockline.Infrastructure.Persistence;

namespace Stockline.UnitTests.Persistence;

public class OrderLifecyclePersistenceTests : IDisposable
{
    private readonly SqliteDb _db = new();

    public void Dispose() => _db.Dispose();

    private async Task<Guid> SaveNewOrder()
    {
        var order = Order.Create(
            Guid.CreateVersion7(),
            new OrderHeader("PO-L", new DateOnly(2026, 9, 24), null, null, null, null, "ACME", null, null, null, null),
            [new OrderLine("SKU", null, null, new Pack(1), 1m, 0m, null, [new Allocation("ST-001", null, LocationKind.Store, 1m)])]);
        await using var context = _db.NewContext();
        await new EfOrderRepository(context).AddAsync(order, default);
        return order.Id;
    }

    [Fact]
    public async Task Applied_action_persists_status_and_history()
    {
        var id = await SaveNewOrder();
        var at = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

        await using (var context = _db.NewContext())
        {
            var repository = new EfOrderRepository(context);
            var order = (await repository.GetForUpdateAsync(id, default))!;
            order.Apply(OrderAction.Submit, ActorRole.Buyer, "user-1", at);
            order.Apply(OrderAction.Open, ActorRole.Supplier, "user-2", at);
            await repository.SaveChangesAsync(default);
        }

        await using var read = _db.NewContext();
        var loaded = (await new EfOrderRepository(read).GetAsync(id, default))!;
        Assert.Equal(OrderStatus.UnderReview, loaded.Status);
        Assert.Equal(["Submit", "Open"], loaded.History.Select(h => h.Action.ToString()));
        Assert.Equal(["user-1", "user-2"], loaded.History.Select(h => h.UserId));
    }

    [Fact]
    public async Task Second_writer_loses_when_the_status_changed_underneath()
    {
        var id = await SaveNewOrder();
        await using var first = _db.NewContext();
        await using var second = _db.NewContext();
        var firstRepository = new EfOrderRepository(first);
        var secondRepository = new EfOrderRepository(second);
        var orderForFirst = (await firstRepository.GetForUpdateAsync(id, default))!;
        var orderForSecond = (await secondRepository.GetForUpdateAsync(id, default))!;

        orderForFirst.Apply(OrderAction.Submit, ActorRole.Buyer, "user-1", DateTimeOffset.UtcNow);
        await firstRepository.SaveChangesAsync(default);
        orderForSecond.Apply(OrderAction.Cancel, ActorRole.Buyer, "user-2", DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<OrderConcurrencyException>(() => secondRepository.SaveChangesAsync(default));
    }
}

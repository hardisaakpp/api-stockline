using Stockline.Application.Orders;
using Stockline.Application.Orders.Lifecycle;
using Stockline.Domain.Orders;

namespace Stockline.UnitTests.Orders;

public class ApplyOrderActionHandlerTests
{
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeOrders(Order? order, bool failOnSave = false) : IOrderRepository
    {
        public int Saves { get; private set; }

        public Task<bool> ExistsAsync(string orderCode, CancellationToken ct) => Task.FromResult(false);

        public Task AddAsync(Order o, CancellationToken ct) => Task.CompletedTask;

        public void Stage(Order o)
        {
        }

        public Task<int> CountChildrenAsync(Guid parentOrderId, CancellationToken ct) => Task.FromResult(0);

        public Task<Order?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(order);

        public Task<Order?> GetForUpdateAsync(Guid id, CancellationToken ct) => Task.FromResult(order);

        public Task SaveChangesAsync(CancellationToken ct)
        {
            Saves++;
            return failOnSave ? throw new OrderConcurrencyException() : Task.CompletedTask;
        }
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 1, 15, 0, 0, TimeSpan.Zero);

    private static Order NewOrder() => Order.Create(
        Guid.NewGuid(),
        new OrderHeader("PO-1", new DateOnly(2026, 9, 24), null, null, null, null, "ACME", null, null, null, null),
        [new OrderLine("SKU", null, null, new Pack(1), 1m, 0m, null, [new Allocation("ST-1", null, LocationKind.Store, 1m)])]);

    private static ApplyOrderActionHandler Handler(FakeOrders orders) => new(orders, new FixedClock(Now));

    [Fact]
    public async Task Allowed_action_is_applied_saved_and_stamped_with_the_clock()
    {
        var order = NewOrder();
        var orders = new FakeOrders(order);

        var result = await Handler(orders).HandleAsync(order.Id, OrderAction.Submit, ActorRole.Buyer, "user-9", default);

        Assert.Equal(ApplyActionOutcome.Applied, result.Outcome);
        Assert.Equal(OrderStatus.Submitted, result.CurrentStatus);
        Assert.Equal(1, orders.Saves);
        var change = Assert.Single(order.History);
        Assert.Equal("user-9", change.UserId);
        Assert.Equal(Now, change.At);
    }

    [Fact]
    public async Task Unknown_order_is_not_found()
    {
        var result = await Handler(new FakeOrders(null)).HandleAsync(
            Guid.NewGuid(), OrderAction.Submit, ActorRole.Buyer, null, default);

        Assert.Equal(ApplyActionOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task Wrong_status_is_reported_without_saving()
    {
        var order = NewOrder();
        var orders = new FakeOrders(order);

        var result = await Handler(orders).HandleAsync(order.Id, OrderAction.Complete, ActorRole.Buyer, null, default);

        Assert.Equal(ApplyActionOutcome.InvalidStatus, result.Outcome);
        Assert.Equal(OrderStatus.Draft, result.CurrentStatus);
        Assert.Equal(0, orders.Saves);
    }

    [Fact]
    public async Task Wrong_actor_is_reported_without_saving_or_recording_history()
    {
        var order = NewOrder();
        var orders = new FakeOrders(order);

        var result = await Handler(orders).HandleAsync(order.Id, OrderAction.Submit, ActorRole.Supplier, null, default);

        Assert.Equal(ApplyActionOutcome.ActorNotAllowed, result.Outcome);
        Assert.Equal(0, orders.Saves);
        Assert.Empty(order.History);
    }

    [Fact]
    public async Task Concurrent_modification_is_reported_as_conflict()
    {
        var order = NewOrder();

        var result = await Handler(new FakeOrders(order, failOnSave: true)).HandleAsync(
            order.Id, OrderAction.Submit, ActorRole.Buyer, null, default);

        Assert.Equal(ApplyActionOutcome.ConcurrencyConflict, result.Outcome);
        Assert.Null(result.Order);
    }
}

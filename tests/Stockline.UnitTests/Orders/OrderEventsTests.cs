using Stockline.Application.Erp;
using Stockline.Application.Notifications;
using Stockline.Application.Orders.Lifecycle;
using Stockline.Application.Orders.Supplier;
using Stockline.Domain.Orders;
using Stockline.Infrastructure.Notifications;
using Stockline.UnitTests.Support;

namespace Stockline.UnitTests.Orders;

public class OrderEventsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => At;
    }

    private sealed class Recorder : IOrderEvents
    {
        public List<OrderEvent> Events { get; } = [];

        public ValueTask PublishAsync(OrderEvent orderEvent, CancellationToken ct)
        {
            Events.Add(orderEvent);
            return ValueTask.CompletedTask;
        }
    }

    private static Order NewOrder()
    {
        var order = Order.Create(
            Guid.NewGuid(),
            new OrderHeader("PO-9", new DateOnly(2026, 9, 24), null, null, null, null, "ACME", null, null, null, null),
            [new OrderLine("SKU", null, null, new Pack(1), 1m, 0m, null, [new Allocation("WH-A", null, LocationKind.Warehouse, 4m)])],
            [new OrderLocation("WH-A", null, LocationKind.Warehouse, null)]);
        return order;
    }

    [Fact]
    public async Task Applied_actions_publish_an_event_with_the_new_status_and_the_user()
    {
        var store = new InMemoryStore();
        var order = NewOrder();
        store.Orders.Add(order);
        var recorder = new Recorder();

        await new ApplyOrderActionHandler(store, new FixedClock(), recorder)
            .HandleAsync(order.Id, OrderAction.Submit, ActorRole.Buyer, "user-5", default);

        Assert.Equal(new OrderEvent(order.Id, "PO-9", "Submit", "Submitted", At, "user-5"), Assert.Single(recorder.Events));
    }

    [Fact]
    public async Task Rejected_actions_publish_nothing()
    {
        var store = new InMemoryStore();
        var order = NewOrder();
        store.Orders.Add(order);
        var recorder = new Recorder();

        await new ApplyOrderActionHandler(store, new FixedClock(), recorder)
            .HandleAsync(order.Id, OrderAction.Complete, ActorRole.Buyer, "u", default);

        Assert.Empty(recorder.Events);
    }

    [Fact]
    public async Task Supplier_confirmation_publishes_events_for_the_order_and_each_child()
    {
        var store = new InMemoryStore();
        var order = NewOrder();
        order.Apply(OrderAction.Submit, ActorRole.Buyer, "u", At);
        order.Apply(OrderAction.Open, ActorRole.Supplier, "u", At);
        order.ConfirmLineQuantity("SKU", 1m);
        store.Orders.Add(order);
        var recorder = new Recorder();
        var children = new[] { new ChildOrderRequest(new DateOnly(2026, 11, 1), new Dictionary<string, decimal> { ["SKU"] = 3m }) };

        await new ConfirmBySupplierHandler(store, new FixedClock(), recorder)
            .HandleAsync(order.Id, new ConfirmBySupplierRequest(false, children), ActorRole.Supplier, "s", default);

        Assert.Equal(["ConfirmBySupplier", "ChildOrderCreated"], recorder.Events.Select(e => e.Type));
        Assert.Equal(["SupplierConfirmed", "ProductionPostponed"], recorder.Events.Select(e => e.Status));
        Assert.Equal("PO-9.01", recorder.Events[1].OrderCode);
    }

    [Fact]
    public async Task Erp_submission_publishes_the_final_status()
    {
        var store = new InMemoryStore();
        var order = NewOrder();
        order.Apply(OrderAction.Submit, ActorRole.Buyer, "u", At);
        order.Apply(OrderAction.Open, ActorRole.Supplier, "u", At);
        order.Apply(OrderAction.ConfirmBySupplier, ActorRole.Supplier, "u", At);
        order.ConfirmRedistribution([new PlanLine("SKU", [new PlanAllocation("WH-A", 4m)])], ActorRole.Buyer, "u", At);
        store.Orders.Add(order);
        var recorder = new Recorder();

        await new SubmitOrderToErpHandler(store, store, new FakeErpGateway(), new FixedClock(), recorder).HandleAsync(order.Id, default);

        var published = Assert.Single(recorder.Events);
        Assert.Equal(("ErpSubmission", "Sent", "system"), (published.Type, published.Status, published.UserId));
    }

    [Fact]
    public async Task Hub_delivers_events_to_every_subscriber_that_matches_the_filter()
    {
        var hub = new OrderEventHub();
        var wanted = Guid.NewGuid();
        var all = hub.Subscribe();
        var filtered = hub.Subscribe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await hub.PublishAsync(new OrderEvent(Guid.NewGuid(), "A", "t", "s", At, null), default);
        await hub.PublishAsync(new OrderEvent(wanted, "B", "t", "s", At, null), default);

        var allFirst = await all.ReadAllAsync(null, cts.Token).FirstAsync(cts.Token);
        var filteredFirst = await filtered.ReadAllAsync(e => e.OrderId == wanted, cts.Token).FirstAsync(cts.Token);
        Assert.Equal("A", allFirst.OrderCode);
        Assert.Equal("B", filteredFirst.OrderCode);
    }

    [Fact]
    public async Task Hub_forgets_subscribers_that_stop_listening_and_ignores_the_past()
    {
        var hub = new OrderEventHub();
        await hub.PublishAsync(new OrderEvent(Guid.NewGuid(), "old", "t", "s", At, null), default);
        var subscription = hub.Subscribe();
        Assert.Equal(1, hub.SubscriberCount);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in subscription.ReadAllAsync(null, cts.Token))
            {
                Assert.Fail("A late subscriber must not receive past events.");
            }
        });

        Assert.Equal(0, hub.SubscriberCount);
    }
}

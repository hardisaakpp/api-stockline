using Stockline.Application.Erp;
using Stockline.Domain.Erp;
using Stockline.Domain.Orders;
using Stockline.UnitTests.Support;

namespace Stockline.UnitTests.Erp;

public class RequestErpSubmissionHandlerTests
{
    private sealed class RecordingQueue : IErpSubmissionQueue
    {
        public List<Guid> Queued { get; } = [];

        public ValueTask EnqueueAsync(Guid orderId, CancellationToken ct)
        {
            Queued.Add(orderId);
            return ValueTask.CompletedTask;
        }
    }

    private static Order OrderIn(params OrderAction[] buyerAndSupplierSteps)
    {
        var order = Order.Create(
            Guid.NewGuid(),
            new OrderHeader("PO-1", new DateOnly(2026, 9, 24), null, null, null, null, "ACME", null, null, null, null),
            [new OrderLine("SKU", null, null, new Pack(1), 1m, 0m, null, [new Allocation("WH-A", null, LocationKind.Warehouse, 1m)])],
            [new OrderLocation("WH-A", null, LocationKind.Warehouse, null)]);
        var at = DateTimeOffset.UtcNow;
        foreach (var action in buyerAndSupplierSteps)
        {
            var actor = action is OrderAction.Submit or OrderAction.ConfirmRedistribution ? ActorRole.Buyer : ActorRole.Supplier;
            if (action == OrderAction.ConfirmRedistribution)
            {
                Assert.True(order.ConfirmRedistribution([new PlanLine("SKU", [new PlanAllocation("WH-A", 1m)])], actor, "u", at).Succeeded);
            }
            else
            {
                Assert.True(order.Apply(action, actor, "u", at).Succeeded);
            }
        }

        return order;
    }

    private static Order Ready() => OrderIn(OrderAction.Submit, OrderAction.Open, OrderAction.ConfirmBySupplier, OrderAction.ConfirmRedistribution);

    private static async Task<(RequestSubmissionResult Result, RecordingQueue Queue)> Run(Order? order, ActorRole actor, Guid? id = null)
    {
        var store = new InMemoryStore();
        if (order is not null)
        {
            store.Orders.Add(order);
        }

        var queue = new RecordingQueue();
        var result = await new RequestErpSubmissionHandler(store, queue).HandleAsync(id ?? order!.Id, actor, default);
        return (result, queue);
    }

    [Fact]
    public async Task Buyer_request_for_a_ready_order_is_queued()
    {
        var order = Ready();

        var (result, queue) = await Run(order, ActorRole.Buyer);

        Assert.Equal(RequestSubmissionOutcome.Accepted, result.Outcome);
        Assert.Equal([order.Id], queue.Queued);
    }

    [Theory]
    [InlineData(ActorRole.Supplier)]
    [InlineData(ActorRole.SupplierProduction)]
    public async Task Only_the_buyer_can_request_a_submission(ActorRole actor)
    {
        var (result, queue) = await Run(Ready(), actor);

        Assert.Equal(RequestSubmissionOutcome.InvalidState, result.Outcome);
        Assert.Empty(queue.Queued);
    }

    [Fact]
    public async Task Orders_that_are_not_ready_are_refused_with_their_current_status()
    {
        var (result, queue) = await Run(OrderIn(OrderAction.Submit), ActorRole.Buyer);

        Assert.Equal((RequestSubmissionOutcome.InvalidState, (OrderStatus?)OrderStatus.Submitted), (result.Outcome, result.Status));
        Assert.Empty(queue.Queued);
    }

    [Fact]
    public async Task Unknown_order_is_not_found() =>
        Assert.Equal(RequestSubmissionOutcome.NotFound, (await Run(null, ActorRole.Buyer, Guid.NewGuid())).Result.Outcome);

    [Fact]
    public async Task Only_transient_failures_under_the_attempt_cap_are_retryable()
    {
        var store = new InMemoryStore();
        ErpDocument Failed(Guid orderId, bool transient, int attempts)
        {
            var d = new ErpDocument(Guid.NewGuid(), orderId, ErpDocumentKind.PurchaseOrder, "WH", null, null, [new ErpDocumentLine("S", 1m, 0m, 0m)]);
            for (var i = 0; i < attempts; i++)
            {
                d.MarkFailed("x", transient);
            }

            return d;
        }

        var retryable = Guid.NewGuid();
        store.AddRange([Failed(retryable, transient: true, attempts: 2), Failed(Guid.NewGuid(), transient: false, attempts: 1), Failed(Guid.NewGuid(), transient: true, attempts: 5)]);

        Assert.Equal([retryable], await store.ListRetryableOrderIdsAsync(5, default));
    }
}

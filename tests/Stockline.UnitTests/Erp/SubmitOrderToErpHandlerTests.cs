using Stockline.Application.Erp;
using Stockline.Domain.Erp;
using Stockline.Domain.Orders;
using Stockline.UnitTests.Support;

namespace Stockline.UnitTests.Erp;

public class SubmitOrderToErpHandlerTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => At;
    }

    private static readonly OrderLocation[] Locations =
    [
        new("WH-A", null, LocationKind.Warehouse, null),
        new("ST-A1", null, LocationKind.Store, "WH-A"),
        new("ST-A2", null, LocationKind.Store, "WH-A"),
        new("ST-X", null, LocationKind.Store, null),
    ];

    private static Order ConfirmedOrder(string storeWithoutWarehouse = "")
    {
        var header = new OrderHeader("PO-1", new DateOnly(2026, 9, 24), new DateOnly(2026, 11, 1), null, null, null, "ACME", null, null, null, null);
        var allocations = new List<Allocation>
        {
            new("WH-A", null, LocationKind.Warehouse, 2m),
            new("ST-A1", null, LocationKind.Store, 1m),
            new("ST-A2", null, LocationKind.Store, 1m),
        };
        if (storeWithoutWarehouse.Length > 0)
        {
            allocations.Add(new Allocation(storeWithoutWarehouse, null, LocationKind.Store, 1m));
        }

        var order = Order.Create(
            Guid.NewGuid(), header, [new OrderLine("SKU-1", null, null, new Pack(6), 5.6m, 0m, null, allocations)], Locations);
        order.Apply(OrderAction.Submit, ActorRole.Buyer, "u", At);
        order.Apply(OrderAction.Open, ActorRole.Supplier, "u", At);
        order.Apply(OrderAction.ConfirmBySupplier, ActorRole.Supplier, "u", At);
        var plan = order.Lines.Select(l => new PlanLine(l.ProductCode, [.. l.Allocations.Select(a => new PlanAllocation(a.LocationCode, a.Quantity))])).ToList();
        Assert.True(order.ConfirmRedistribution(plan, ActorRole.Buyer, "u", At).Succeeded);
        return order;
    }

    private static (SubmitOrderToErpHandler Handler, InMemoryStore Store, FakeErpGateway Erp, Order Order) Setup(Order? order = null)
    {
        var store = new InMemoryStore();
        var erp = new FakeErpGateway();
        var o = order ?? ConfirmedOrder();
        store.Orders.Add(o);
        return (new SubmitOrderToErpHandler(store, store, erp, new FixedClock()), store, erp, o);
    }

    [Fact]
    public async Task Purchase_order_goes_first_then_one_transfer_per_store_and_the_order_is_sent()
    {
        var (handler, store, erp, order) = Setup();

        var result = await handler.HandleAsync(order.Id, default);

        Assert.Equal(ErpSubmissionOutcome.Sent, result.Outcome);
        Assert.Equal(OrderStatus.Sent, order.Status);
        Assert.Equal(["PO:WH-A", "TR:WH-A>ST-A1", "TR:WH-A>ST-A2"], erp.Calls);
        Assert.All(store.Documents, d => Assert.Equal(ErpDocumentStatus.Sent, d.Status));
        Assert.All(store.Documents, d => Assert.NotNull(d.DocEntry));
        Assert.Equal(OrderAction.MarkSent, order.History[^1].Action);
        Assert.Equal(ActorRole.System, order.History[^1].Actor);
    }

    [Fact]
    public async Task Failed_purchase_order_marks_the_order_failed_and_skips_transfers_then_a_retry_completes()
    {
        var (handler, store, erp, order) = Setup();
        erp.Override = call => call.StartsWith("PO") ? ErpResult.Failure("ERP down", isTransient: true) : null;

        var first = await handler.HandleAsync(order.Id, default);

        Assert.Equal(ErpSubmissionOutcome.Failed, first.Outcome);
        Assert.Equal(OrderStatus.ErpSubmissionFailed, order.Status);
        Assert.Equal(["PO:WH-A"], erp.Calls);
        var purchaseOrder = store.Documents.Single(d => d.Kind == ErpDocumentKind.PurchaseOrder);
        Assert.Equal((ErpDocumentStatus.Failed, 1, "ERP down"), (purchaseOrder.Status, purchaseOrder.Attempts, purchaseOrder.LastError));

        erp.Override = null;
        erp.Calls.Clear();
        var retry = await handler.HandleAsync(order.Id, default);

        Assert.Equal(ErpSubmissionOutcome.Sent, retry.Outcome);
        Assert.Equal(["PO:WH-A", "TR:WH-A>ST-A1", "TR:WH-A>ST-A2"], erp.Calls);
        Assert.Equal(2, store.Documents.Single(d => d.Kind == ErpDocumentKind.PurchaseOrder).Attempts);
        Assert.Equal(3, store.Documents.Count);
    }

    [Fact]
    public async Task Failed_transfer_leaves_the_order_logistics_pending_and_a_retry_only_resends_that_transfer()
    {
        var (handler, store, erp, order) = Setup();
        erp.Override = call => call == "TR:WH-A>ST-A2" ? ErpResult.Failure("Stock too low", isTransient: false) : null;

        var first = await handler.HandleAsync(order.Id, default);

        Assert.Equal(ErpSubmissionOutcome.LogisticsPending, first.Outcome);
        Assert.Equal(OrderStatus.LogisticsPending, order.Status);

        erp.Override = null;
        erp.Calls.Clear();
        var retry = await handler.HandleAsync(order.Id, default);

        Assert.Equal(ErpSubmissionOutcome.Sent, retry.Outcome);
        Assert.Equal(["TR:WH-A>ST-A2"], erp.Calls);
        Assert.Equal(OrderStatus.Sent, order.Status);
        Assert.Equal(3, store.Documents.Count);
    }

    [Fact]
    public async Task Documents_are_planned_once_so_retries_reuse_their_idempotency_keys()
    {
        var (handler, store, erp, order) = Setup();
        erp.Override = _ => ErpResult.Failure("down", isTransient: true);
        await handler.HandleAsync(order.Id, default);
        var ids = store.Documents.Select(d => d.Id).ToList();

        await handler.HandleAsync(order.Id, default);

        Assert.Equal(ids, store.Documents.Select(d => d.Id));
    }

    [Fact]
    public async Task Order_that_is_not_ready_cannot_be_submitted()
    {
        var order = ConfirmedOrder();
        order.Apply(OrderAction.MarkSent, ActorRole.System, "system", At);
        var (handler, _, erp, _) = Setup(order);

        var result = await handler.HandleAsync(order.Id, default);

        Assert.Equal(ErpSubmissionOutcome.InvalidState, result.Outcome);
        Assert.Equal(OrderStatus.Sent, result.Status);
        Assert.Empty(erp.Calls);
    }

    [Fact]
    public async Task Unknown_order_is_not_found()
    {
        var (handler, _, _, _) = Setup();

        Assert.Equal(ErpSubmissionOutcome.NotFound, (await handler.HandleAsync(Guid.NewGuid(), default)).Outcome);
    }

    [Fact]
    public async Task Planning_problems_are_reported_without_calling_the_erp_or_changing_the_order()
    {
        var (handler, store, erp, order) = Setup(ConfirmedOrder("ST-X"));

        var result = await handler.HandleAsync(order.Id, default);

        Assert.Equal(ErpSubmissionOutcome.Invalid, result.Outcome);
        Assert.Equal("STORE_WITHOUT_WAREHOUSE", Assert.Single(result.Issues).Code);
        Assert.Equal(OrderStatus.RedistributionConfirmed, order.Status);
        Assert.Empty(erp.Calls);
        Assert.Empty(store.Documents);
    }
}

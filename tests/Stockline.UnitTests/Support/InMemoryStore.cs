using Stockline.Application.Erp;
using Stockline.Application.Orders;
using Stockline.Domain.Erp;
using Stockline.Domain.Orders;

namespace Stockline.UnitTests.Support;

public sealed class InMemoryStore : IOrderRepository, IErpDocumentRepository
{
    public List<Order> Orders { get; } = [];

    public List<ErpDocument> Documents { get; } = [];

    public Task<bool> ExistsAsync(string orderCode, CancellationToken ct) =>
        Task.FromResult(Orders.Any(o => o.Header.Code == orderCode));

    public Task AddAsync(Order order, CancellationToken ct)
    {
        Orders.Add(order);
        return Task.CompletedTask;
    }

    public void Stage(Order order) => Orders.Add(order);

    public Task<int> CountChildrenAsync(Guid parentOrderId, CancellationToken ct) =>
        Task.FromResult(Orders.Count(o => o.ParentOrderId == parentOrderId));

    public Task RemoveAsync(Order order, CancellationToken ct)
    {
        Orders.Remove(order);
        return Task.CompletedTask;
    }

    public Task<Order?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(Orders.FirstOrDefault(o => o.Id == id));

    public Task<Order?> GetForUpdateAsync(Guid id, CancellationToken ct) => GetAsync(id, ct);

    public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<List<ErpDocument>> ListByOrderAsync(Guid orderId, CancellationToken ct) =>
        Task.FromResult(Documents.Where(d => d.OrderId == orderId).ToList());

    public void AddRange(IEnumerable<ErpDocument> documents) => Documents.AddRange(documents);

    public Task<IReadOnlyList<Guid>> ListRetryableOrderIdsAsync(int maxAttempts, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Guid>>(Documents
            .Where(d => d.Status == ErpDocumentStatus.Failed && d.LastFailureWasTransient && d.Attempts < maxAttempts)
            .Select(d => d.OrderId).Distinct().ToList());
}

public sealed class FakeErpGateway : IErpGateway
{
    private long _next = 1000;

    public List<string> Calls { get; } = [];

    public Func<string, ErpResult?>? Override { get; set; }

    public Task<ErpResult> CreatePurchaseOrderAsync(PurchaseOrderRequest request, CancellationToken ct)
    {
        lock (Calls)
        {
            PurchaseOrders.Add(request);
        }

        return Respond($"PO:{request.WarehouseCode}", request.IdempotencyKey);
    }

    public Task<ErpResult> CreateTransferRequestAsync(TransferRequestRequest request, CancellationToken ct) =>
        Respond($"TR:{request.FromWarehouse}>{request.ToWarehouse}", request.IdempotencyKey);

    public MasterDataResult MasterData { get; set; } = MasterDataResult.Ok;

    public List<MasterDataCheck> Checks { get; } = [];

    public List<PurchaseOrderRequest> PurchaseOrders { get; } = [];

    public Task<MasterDataResult> CheckMasterDataAsync(MasterDataCheck check, CancellationToken ct)
    {
        lock (Calls)
        {
            Checks.Add(check);
        }

        return Task.FromResult(MasterData);
    }

    private Task<ErpResult> Respond(string call, string key)
    {
        lock (Calls)
        {
            Calls.Add(call);
        }

        var result = Override?.Invoke(call) ?? ErpResult.Ok(_next++, _next++);
        return Task.FromResult(result);
    }
}

public sealed class FakeEmailSender : Stockline.Application.Notifications.IEmailSender
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<Stockline.Application.Notifications.EmailMessage> _sent = new();

    public IReadOnlyCollection<Stockline.Application.Notifications.EmailMessage> Sent => _sent;

    public Exception? FailWith { get; set; }

    public Task SendAsync(Stockline.Application.Notifications.EmailMessage message, CancellationToken ct)
    {
        if (FailWith is { } error)
        {
            throw error;
        }

        _sent.Enqueue(message);
        return Task.CompletedTask;
    }
}

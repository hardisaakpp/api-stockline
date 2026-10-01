using Stockline.Domain.Erp;

namespace Stockline.Application.Erp;

public sealed record ErpLine(string ProductCode, decimal Quantity, decimal UnitPrice, decimal DiscountPercent);

public sealed record PurchaseOrderRequest(
    string IdempotencyKey,
    string SupplierName,
    string WarehouseCode,
    DateOnly DocDate,
    DateOnly? DueDate,
    string? Comments,
    IReadOnlyList<ErpLine> Lines);

public sealed record TransferRequestRequest(
    string IdempotencyKey,
    string FromWarehouse,
    string ToWarehouse,
    DateOnly DocDate,
    IReadOnlyList<ErpLine> Lines);

public sealed record ErpResult(bool Success, long? DocEntry, long? DocNum, string? Error, bool IsTransient)
{
    public static ErpResult Ok(long docEntry, long docNum) => new(true, docEntry, docNum, null, false);

    public static ErpResult Failure(string error, bool isTransient) => new(false, null, null, error, isTransient);
}

/// <summary>Creates documents in the ERP. Implementations must be idempotent per <c>IdempotencyKey</c>.</summary>
public interface IErpGateway
{
    Task<ErpResult> CreatePurchaseOrderAsync(PurchaseOrderRequest request, CancellationToken ct);

    Task<ErpResult> CreateTransferRequestAsync(TransferRequestRequest request, CancellationToken ct);
}

public interface IErpDocumentRepository
{
    Task<List<ErpDocument>> ListByOrderAsync(Guid orderId, CancellationToken ct);

    void AddRange(IEnumerable<ErpDocument> documents);

    /// <summary>Orders with failed documents that may be retried automatically (transient failures under the attempt cap).</summary>
    Task<IReadOnlyList<Guid>> ListRetryableOrderIdsAsync(int maxAttempts, CancellationToken ct);
}

public interface IErpSubmissionQueue
{
    ValueTask EnqueueAsync(Guid orderId, CancellationToken ct);
}

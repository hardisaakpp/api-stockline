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

public sealed record MasterDataCheck(
    string SupplierName, IReadOnlyCollection<string> ItemCodes, IReadOnlyCollection<string> WarehouseCodes);

/// <summary>Outcome of checking that everything a document refers to exists in the ERP before creating it.</summary>
/// <param name="Problems">What is missing (empty when everything exists).</param>
/// <param name="Error">Set when the check itself could not be completed (e.g. the ERP is unreachable).</param>
public sealed record MasterDataResult(IReadOnlyList<string> Problems, string? Error = null, bool IsTransient = false)
{
    public static MasterDataResult Ok { get; } = new([]);

    public bool IsValid => Problems.Count == 0 && Error is null;

    public string Describe() => Error ?? $"ERP master data check failed: {string.Join("; ", Problems)}";
}

/// <summary>Creates documents in the ERP. Implementations must be idempotent per <c>IdempotencyKey</c>.</summary>
public interface IErpGateway
{
    Task<ErpResult> CreatePurchaseOrderAsync(PurchaseOrderRequest request, CancellationToken ct);

    Task<ErpResult> CreateTransferRequestAsync(TransferRequestRequest request, CancellationToken ct);

    /// <summary>Confirms the supplier, items and warehouses exist so documents are not half-created against missing masters.</summary>
    Task<MasterDataResult> CheckMasterDataAsync(MasterDataCheck check, CancellationToken ct);
}

public interface IErpDocumentRepository
{
    Task<List<ErpDocument>> ListByOrderAsync(Guid orderId, CancellationToken ct);

    void AddRange(IEnumerable<ErpDocument> documents);

    void RemoveRange(IEnumerable<ErpDocument> documents);

    /// <summary>Orders with failed documents that may be retried automatically (transient failures under the attempt cap).</summary>
    Task<IReadOnlyList<Guid>> ListRetryableOrderIdsAsync(int maxAttempts, CancellationToken ct);
}

public interface IErpSubmissionQueue
{
    ValueTask EnqueueAsync(Guid orderId, CancellationToken ct);
}

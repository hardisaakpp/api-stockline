namespace Stockline.Domain.Erp;

public enum ErpDocumentKind
{
    PurchaseOrder,
    TransferRequest,
}

public enum ErpDocumentStatus
{
    Pending,
    Sent,
    Failed,
}

/// <summary>One try at creating the document in the ERP; kept as a log of what happened and when.</summary>
public sealed record ErpAttempt(DateTimeOffset At, bool Succeeded, string? Error, bool Transient, long? DocNum);

public sealed record ErpDocumentLine(string ProductCode, decimal Quantity, decimal UnitPrice, decimal DiscountPercent);

public sealed class ErpDocument
{
    private readonly List<ErpDocumentLine> _lines;
    private readonly List<ErpAttempt> _attemptLog = [];

    // Parameterless constructor required by EF Core materialization.
    private ErpDocument()
    {
        OriginWarehouse = null!;
        _lines = [];
    }

    public ErpDocument(
        Guid id,
        Guid orderId,
        ErpDocumentKind kind,
        string originWarehouse,
        string? destinationLocation,
        Guid? purchaseOrderDocumentId,
        IEnumerable<ErpDocumentLine> lines)
    {
        Id = id;
        OrderId = orderId;
        Kind = kind;
        OriginWarehouse = originWarehouse;
        DestinationLocation = destinationLocation;
        PurchaseOrderDocumentId = purchaseOrderDocumentId;
        _lines = lines.ToList();
        Status = ErpDocumentStatus.Pending;
    }

    public Guid Id { get; }

    public Guid OrderId { get; }

    public ErpDocumentKind Kind { get; }

    public string OriginWarehouse { get; }

    public string? DestinationLocation { get; }

    public Guid? PurchaseOrderDocumentId { get; }

    public IReadOnlyList<ErpDocumentLine> Lines => _lines;

    public IReadOnlyList<ErpAttempt> AttemptLog => _attemptLog;

    public ErpDocumentStatus Status { get; private set; }

    public int Attempts { get; private set; }

    public string? LastError { get; private set; }

    public bool LastFailureWasTransient { get; private set; }

    public long? DocEntry { get; private set; }

    public long? DocNum { get; private set; }

    public DateTimeOffset? SentAt { get; private set; }

    public void MarkSent(long docEntry, long docNum, DateTimeOffset at)
    {
        Attempts++;
        Status = ErpDocumentStatus.Sent;
        DocEntry = docEntry;
        DocNum = docNum;
        LastError = null;
        LastFailureWasTransient = false;
        SentAt = at;
        _attemptLog.Add(new ErpAttempt(at, true, null, false, docNum));
    }

    public void MarkFailed(string error, bool transient = true, DateTimeOffset? at = null)
    {
        Attempts++;
        Status = ErpDocumentStatus.Failed;
        LastError = error;
        LastFailureWasTransient = transient;
        _attemptLog.Add(new ErpAttempt(at ?? DateTimeOffset.UtcNow, false, error, transient, null));
    }
}

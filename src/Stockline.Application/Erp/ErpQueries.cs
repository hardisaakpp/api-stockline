using Stockline.Domain.Erp;

namespace Stockline.Application.Erp;

public sealed record ErpDocumentQuery(
    string? OrderCode = null,
    ErpDocumentKind? Kind = null,
    ErpDocumentStatus? Status = null,
    bool RetryPendingOnly = false,
    int MaxAttempts = 5,
    int Page = 1,
    int PageSize = 20);

public sealed record ErpDocumentRow(
    Guid Id,
    Guid OrderId,
    string OrderCode,
    string Kind,
    string OriginWarehouse,
    string? DestinationLocation,
    string Status,
    int Attempts,
    string? LastError,
    bool LastFailureWasTransient,
    long? DocEntry,
    long? DocNum,
    DateTimeOffset? SentAt);

public sealed record ErpAttemptQuery(
    string? OrderCode = null,
    bool? Succeeded = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int Page = 1,
    int PageSize = 50);

public sealed record ErpAttemptRow(
    Guid DocumentId,
    Guid OrderId,
    string OrderCode,
    string Kind,
    string? DestinationLocation,
    DateTimeOffset At,
    bool Succeeded,
    string? Error,
    bool Transient,
    long? DocNum);

public sealed record ErpPage<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

/// <summary>Cross-order views of the ERP integration: what was sent, what is pending a retry and the log of every attempt.</summary>
public interface IErpQueries
{
    Task<ErpPage<ErpDocumentRow>> ListDocumentsAsync(ErpDocumentQuery query, CancellationToken ct);

    Task<ErpPage<ErpAttemptRow>> ListAttemptsAsync(ErpAttemptQuery query, CancellationToken ct);
}

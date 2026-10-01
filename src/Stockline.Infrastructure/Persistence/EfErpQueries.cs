using Microsoft.EntityFrameworkCore;
using Stockline.Application.Erp;
using Stockline.Domain.Erp;

namespace Stockline.Infrastructure.Persistence;

public sealed class EfErpQueries(StocklineDbContext db) : IErpQueries
{
    private const int MaxPageSize = 200;

    public async Task<ErpPage<ErpDocumentRow>> ListDocumentsAsync(ErpDocumentQuery query, CancellationToken ct)
    {
        var page = Math.Max(1, query.Page);
        var size = Math.Clamp(query.PageSize, 1, MaxPageSize);

        var rows = from d in db.ErpDocuments.AsNoTracking()
                   join o in db.Orders.AsNoTracking() on d.OrderId equals o.Id
                   select new { Document = d, OrderCode = o.Header.Code };
        if (query.OrderCode is { Length: > 0 } code)
        {
            rows = rows.Where(r => r.OrderCode.Contains(code));
        }

        if (query.Kind is { } kind)
        {
            rows = rows.Where(r => r.Document.Kind == kind);
        }

        if (query.Status is { } status)
        {
            rows = rows.Where(r => r.Document.Status == status);
        }

        if (query.RetryPendingOnly)
        {
            var max = query.MaxAttempts;
            rows = rows.Where(r => r.Document.Status == ErpDocumentStatus.Failed
                                   && r.Document.LastFailureWasTransient && r.Document.Attempts < max);
        }

        var total = await rows.CountAsync(ct);
        var items = await rows
            .OrderBy(r => r.OrderCode).ThenBy(r => r.Document.Kind).ThenBy(r => r.Document.DestinationLocation)
            .Skip((page - 1) * size).Take(size)
            .ToListAsync(ct);

        return new ErpPage<ErpDocumentRow>(
            [.. items.Select(r => new ErpDocumentRow(
                r.Document.Id, r.Document.OrderId, r.OrderCode, r.Document.Kind.ToString(), r.Document.OriginWarehouse,
                r.Document.DestinationLocation, r.Document.Status.ToString(), r.Document.Attempts, r.Document.LastError,
                r.Document.LastFailureWasTransient, r.Document.DocEntry, r.Document.DocNum, r.Document.SentAt))],
            page, size, total);
    }

    public async Task<ErpPage<ErpAttemptRow>> ListAttemptsAsync(ErpAttemptQuery query, CancellationToken ct)
    {
        var page = Math.Max(1, query.Page);
        var size = Math.Clamp(query.PageSize, 1, MaxPageSize);

        var rows = from d in db.ErpDocuments.AsNoTracking()
                   join o in db.Orders.AsNoTracking() on d.OrderId equals o.Id
                   from a in d.AttemptLog
                   select new { Document = d, OrderCode = o.Header.Code, Attempt = a };
        if (query.OrderCode is { Length: > 0 } code)
        {
            rows = rows.Where(r => r.OrderCode.Contains(code));
        }

        if (query.Succeeded is { } succeeded)
        {
            rows = rows.Where(r => r.Attempt.Succeeded == succeeded);
        }

        if (query.From is { } from)
        {
            rows = rows.Where(r => r.Attempt.At >= from);
        }

        if (query.To is { } to)
        {
            rows = rows.Where(r => r.Attempt.At <= to);
        }

        var total = await rows.CountAsync(ct);
        var items = await rows.OrderByDescending(r => r.Attempt.At)
            .Skip((page - 1) * size).Take(size)
            .ToListAsync(ct);

        return new ErpPage<ErpAttemptRow>(
            [.. items.Select(r => new ErpAttemptRow(
                r.Document.Id, r.Document.OrderId, r.OrderCode, r.Document.Kind.ToString(), r.Document.DestinationLocation,
                r.Attempt.At, r.Attempt.Succeeded, r.Attempt.Error, r.Attempt.Transient, r.Attempt.DocNum))],
            page, size, total);
    }
}

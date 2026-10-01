using Microsoft.EntityFrameworkCore;
using Stockline.Application.Erp;
using Stockline.Domain.Erp;

namespace Stockline.Infrastructure.Persistence;

public sealed class EfErpDocumentRepository(StocklineDbContext db) : IErpDocumentRepository
{
    public Task<List<ErpDocument>> ListByOrderAsync(Guid orderId, CancellationToken ct) =>
        db.Set<ErpDocument>().Where(d => d.OrderId == orderId).OrderBy(d => d.Kind).ThenBy(d => d.DestinationLocation).ToListAsync(ct);

    public async Task<IReadOnlyList<Guid>> ListRetryableOrderIdsAsync(int maxAttempts, CancellationToken ct) =>
        await db.Set<ErpDocument>()
            .Where(d => d.Status == ErpDocumentStatus.Failed && d.LastFailureWasTransient && d.Attempts < maxAttempts)
            .Select(d => d.OrderId)
            .Distinct()
            .ToListAsync(ct);

    public void AddRange(IEnumerable<ErpDocument> documents) => db.Set<ErpDocument>().AddRange(documents);

    public void RemoveRange(IEnumerable<ErpDocument> documents) => db.Set<ErpDocument>().RemoveRange(documents);
}

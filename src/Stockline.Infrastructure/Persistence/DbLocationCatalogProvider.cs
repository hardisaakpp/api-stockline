using Microsoft.EntityFrameworkCore;
using Stockline.Application.Orders;
using Stockline.Application.Orders.Import;

namespace Stockline.Infrastructure.Persistence;

public sealed class DbLocationCatalogProvider(StocklineDbContext db) : ILocationCatalogProvider
{
    public async Task<LocationCatalog> GetAsync(CancellationToken ct)
    {
        var locations = await db.Locations.AsNoTracking().Where(l => l.IsActive).ToListAsync(ct);
        return new LocationCatalog(locations.Select(l => KeyValuePair.Create(l.Code, new LocationInfo(l.Kind, l.SupplyingWarehouseCode))));
    }
}

using Stockline.Domain.Orders;

namespace Stockline.Application.Orders.Import;

public sealed record LocationInfo(LocationKind Kind, string? SupplyingWarehouseCode = null, string? Name = null);

public sealed class LocationCatalog
{
    private readonly Dictionary<string, LocationInfo> _locations;

    public LocationCatalog(IEnumerable<KeyValuePair<string, LocationKind>> kinds)
        : this(kinds.Select(k => KeyValuePair.Create(k.Key, new LocationInfo(k.Value))))
    {
    }

    public LocationCatalog(IEnumerable<KeyValuePair<string, LocationInfo>> locations) =>
        _locations = new(locations, StringComparer.OrdinalIgnoreCase);

    public bool TryGetKind(string code, out LocationKind kind)
    {
        var found = _locations.TryGetValue(code, out var info);
        kind = info?.Kind ?? default;
        return found;
    }

    public IEnumerable<KeyValuePair<string, LocationInfo>> All => _locations;

    public bool TryGetInfo(string code, out LocationInfo info)
    {
        var found = _locations.TryGetValue(code, out var value);
        info = value!;
        return found;
    }
}

using Stockline.Domain.Orders;

namespace Stockline.Domain.Locations;

public sealed class Location(string code, string name, LocationKind kind, string? supplyingWarehouseCode, bool isActive)
{
    public string Code { get; } = code;

    public string Name { get; } = name;

    public LocationKind Kind { get; } = kind;

    public string? SupplyingWarehouseCode { get; } = supplyingWarehouseCode;

    public bool IsActive { get; } = isActive;
}

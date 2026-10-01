namespace Stockline.Domain.Orders;

public sealed record OrderLocation(string Code, string? Name, LocationKind Kind, string? SupplyingWarehouseCode);

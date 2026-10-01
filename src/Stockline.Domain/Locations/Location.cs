using Stockline.Domain.Orders;

namespace Stockline.Domain.Locations;

/// <summary>A warehouse or store of the master catalog. Stores are supplied by one warehouse.</summary>
public sealed class Location
{
    public const int MaxCodeLength = 40;
    public const int MaxNameLength = 200;

    // Parameterless constructor required by EF Core materialization.
    private Location()
    {
        Code = null!;
        Name = null!;
    }

    public Location(string code, string name, LocationKind kind, string? supplyingWarehouseCode, bool isActive)
    {
        var trimmed = code?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > MaxCodeLength || !trimmed.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
        {
            throw new DomainException($"A location code has 1-{MaxCodeLength} characters: letters, digits, dots, hyphens and underscores.");
        }

        Code = trimmed.ToUpperInvariant();
        Kind = kind;
        Name = string.Empty;
        Apply(name, supplyingWarehouseCode, isActive);
    }

    public string Code { get; private set; }

    public string Name { get; private set; }

    public LocationKind Kind { get; private set; }

    public string? SupplyingWarehouseCode { get; private set; }

    public bool IsActive { get; private set; }

    public void Update(string name, string? supplyingWarehouseCode, bool isActive) =>
        Apply(name, supplyingWarehouseCode, isActive);

    private void Apply(string name, string? supplyingWarehouseCode, bool isActive)
    {
        var trimmedName = name?.Trim() ?? string.Empty;
        if (trimmedName.Length is 0 or > MaxNameLength)
        {
            throw new DomainException($"A location needs a name of up to {MaxNameLength} characters.");
        }

        var warehouse = string.IsNullOrWhiteSpace(supplyingWarehouseCode) ? null : supplyingWarehouseCode.Trim().ToUpperInvariant();
        if (Kind == LocationKind.Store && warehouse is null)
        {
            throw new DomainException("A store needs the warehouse that supplies it.");
        }

        if (Kind == LocationKind.Warehouse && warehouse is not null)
        {
            throw new DomainException("A warehouse is not supplied by another warehouse.");
        }

        Name = trimmedName;
        SupplyingWarehouseCode = warehouse;
        IsActive = isActive;
    }
}

using Stockline.Domain;
using Stockline.Domain.Locations;
using Stockline.Domain.Orders;

namespace Stockline.Application.Locations;

public interface ILocationRepository
{
    Task<IReadOnlyList<Location>> ListAsync(CancellationToken ct);

    Task<Location?> GetAsync(string code, CancellationToken ct);

    void Add(Location location);

    Task SaveChangesAsync(CancellationToken ct);
}

public enum LocationAdminOutcome
{
    Succeeded,
    NotFound,
    Conflict,
    Invalid,
}

public sealed record LocationAdminResult(LocationAdminOutcome Outcome, Location? Location = null, string? Message = null);

public sealed class LocationAdminHandler(ILocationRepository locations)
{
    public async Task<LocationAdminResult> CreateAsync(
        string code, string name, LocationKind kind, string? supplyingWarehouse, bool isActive, CancellationToken ct)
    {
        Location location;
        try
        {
            location = new Location(code, name, kind, supplyingWarehouse, isActive);
        }
        catch (DomainException ex)
        {
            return new LocationAdminResult(LocationAdminOutcome.Invalid, Message: ex.Message);
        }

        if (await locations.GetAsync(location.Code, ct) is not null)
        {
            return new LocationAdminResult(LocationAdminOutcome.Conflict, Message: $"Location '{location.Code}' already exists.");
        }

        if (await CheckWarehouseAsync(location, ct) is { } problem)
        {
            return problem;
        }

        locations.Add(location);
        await locations.SaveChangesAsync(ct);
        return new LocationAdminResult(LocationAdminOutcome.Succeeded, location);
    }

    public async Task<LocationAdminResult> UpdateAsync(
        string code, string name, string? supplyingWarehouse, bool isActive, CancellationToken ct)
    {
        var location = await locations.GetAsync(code.Trim().ToUpperInvariant(), ct);
        if (location is null)
        {
            return new LocationAdminResult(LocationAdminOutcome.NotFound);
        }

        try
        {
            location.Update(name, supplyingWarehouse, isActive);
        }
        catch (DomainException ex)
        {
            return new LocationAdminResult(LocationAdminOutcome.Invalid, Message: ex.Message);
        }

        if (await CheckWarehouseAsync(location, ct) is { } problem)
        {
            return problem;
        }

        await locations.SaveChangesAsync(ct);
        return new LocationAdminResult(LocationAdminOutcome.Succeeded, location);
    }

    /// <summary>A store must be supplied by an existing, active warehouse.</summary>
    private async Task<LocationAdminResult?> CheckWarehouseAsync(Location location, CancellationToken ct)
    {
        if (location.Kind != LocationKind.Store)
        {
            return null;
        }

        var warehouse = await locations.GetAsync(location.SupplyingWarehouseCode!, ct);
        return warehouse is { Kind: LocationKind.Warehouse, IsActive: true }
            ? null
            : new LocationAdminResult(
                LocationAdminOutcome.Invalid, Message: $"'{location.SupplyingWarehouseCode}' is not an active warehouse.");
    }
}

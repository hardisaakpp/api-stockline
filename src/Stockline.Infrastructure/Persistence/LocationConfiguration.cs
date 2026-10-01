using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stockline.Domain.Locations;
using Stockline.Domain.Orders;

namespace Stockline.Infrastructure.Persistence;

internal sealed class LocationConfiguration : IEntityTypeConfiguration<Location>
{
    public void Configure(EntityTypeBuilder<Location> builder)
    {
        builder.ToTable("locations");
        builder.HasKey(l => l.Code);
        builder.Property(l => l.Code).HasMaxLength(40);
        builder.Property(l => l.Name).HasMaxLength(200).IsRequired();
        builder.Property(l => l.Kind).HasConversion<string>().HasMaxLength(20);
        builder.Property(l => l.SupplyingWarehouseCode).HasMaxLength(40);
        builder.Property(l => l.IsActive);

        builder.HasData(
            new Location("WH-MAIN", "Main warehouse", LocationKind.Warehouse, null, true),
            new Location("WH-OUTLET", "Outlet warehouse", LocationKind.Warehouse, null, true),
            new Location("WH-REGIONAL", "Regional warehouse", LocationKind.Warehouse, null, true),
            new Location("ST-001", "Downtown", LocationKind.Store, "WH-MAIN", true),
            new Location("ST-002", "Riverside", LocationKind.Store, "WH-MAIN", true),
            new Location("ST-003", "Lakeside", LocationKind.Store, "WH-MAIN", true),
            new Location("ST-004", "Hillcrest", LocationKind.Store, "WH-OUTLET", true),
            new Location("ST-005", "Airport", LocationKind.Store, "WH-OUTLET", true),
            new Location("ST-006", "Harbor", LocationKind.Store, "WH-REGIONAL", true),
            new Location("ST-099", "Closed store", LocationKind.Store, "WH-MAIN", false));
    }
}

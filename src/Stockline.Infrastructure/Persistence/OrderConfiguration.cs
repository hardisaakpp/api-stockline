using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stockline.Domain.Orders;

namespace Stockline.Infrastructure.Persistence;

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("orders");
        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).ValueGeneratedNever();
        builder.Property(o => o.Status).HasConversion<string>().HasMaxLength(40).IsConcurrencyToken();
        builder.Property(o => o.ParentOrderId);
        builder.HasIndex(o => o.ParentOrderId);
        builder.Property(o => o.RedistributionVersion);

        builder.OwnsOne(o => o.Header, h =>
        {
            h.Property(x => x.Code).HasColumnName("Code").HasMaxLength(100).IsRequired();
            h.HasIndex(x => x.Code).IsUnique();
            h.Property(x => x.OrderDate).HasColumnName("OrderDate");
            h.Property(x => x.DeliveryDate).HasColumnName("DeliveryDate");
            h.Property(x => x.DeliveryNote).HasColumnName("DeliveryNote").HasMaxLength(200);
            h.Property(x => x.PaymentTerms).HasColumnName("PaymentTerms").HasMaxLength(100);
            h.Property(x => x.Message).HasColumnName("Message").HasMaxLength(500);
            h.Property(x => x.SupplierName).HasColumnName("SupplierName").HasMaxLength(200).IsRequired();
            h.Property(x => x.BuyerName).HasColumnName("BuyerName").HasMaxLength(200);
            h.Property(x => x.BuyerCode).HasColumnName("BuyerCode").HasMaxLength(50);
            h.Property(x => x.TransferOrigin).HasColumnName("TransferOrigin").HasMaxLength(40);
            h.Property(x => x.Series).HasColumnName("Series").HasMaxLength(50);
        });
        builder.Navigation(o => o.Header).IsRequired();

        builder.OwnsMany(o => o.Lines, l =>
        {
            l.ToTable("order_lines");
            l.WithOwner().HasForeignKey("OrderId");
            l.Property<int>("Id");
            l.HasKey("Id");
            l.Property(x => x.ProductCode).HasMaxLength(60).IsRequired();
            l.Property(x => x.Barcode).HasMaxLength(60);
            l.Property(x => x.Description).HasMaxLength(300);
            l.Property(x => x.Pack).HasConversion(p => p.Units, units => new Pack(units));
            l.Property(x => x.CostWithoutTax).HasPrecision(18, 4);
            l.Property(x => x.RequestedQuantity).HasPrecision(18, 4);
            l.Property(x => x.ConfirmedQuantity).HasPrecision(18, 4);
            l.Property(x => x.Discount).HasPrecision(5, 4);
            l.Property(x => x.RetailPrice).HasPrecision(18, 4);

            l.OwnsMany(x => x.Allocations, a =>
            {
                a.ToTable("order_line_allocations");
                a.WithOwner().HasForeignKey("OrderLineId");
                a.Property<int>("Id");
                a.HasKey("Id");
                a.Property(x => x.LocationCode).HasMaxLength(40).IsRequired();
                a.Property(x => x.LocationName).HasMaxLength(200);
                a.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
                a.Property(x => x.Quantity).HasPrecision(18, 4);
            });
            l.Navigation(x => x.Allocations).UsePropertyAccessMode(PropertyAccessMode.Field);
        });
        builder.Navigation(o => o.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.OwnsMany(o => o.Locations, l =>
        {
            l.ToTable("order_locations");
            l.WithOwner().HasForeignKey("OrderId");
            l.Property<int>("Id");
            l.HasKey("Id");
            l.Property(x => x.Code).HasMaxLength(40).IsRequired();
            l.Property(x => x.Name).HasMaxLength(200);
            l.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            l.Property(x => x.SupplyingWarehouseCode).HasMaxLength(40);
        });
        builder.Navigation(o => o.Locations).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.OwnsMany(o => o.RedistributionDraft, d =>
        {
            d.ToTable("order_redistribution_draft");
            d.WithOwner().HasForeignKey("OrderId");
            d.Property<int>("Id");
            d.HasKey("Id");
            d.Property(x => x.ProductCode).HasMaxLength(60).IsRequired();
            d.Property(x => x.LocationCode).HasMaxLength(40).IsRequired();
            d.Property(x => x.Quantity).HasPrecision(18, 4);
        });
        builder.Navigation(o => o.RedistributionDraft).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.OwnsMany(o => o.History, h =>
        {
            h.ToTable("order_status_history");
            h.WithOwner().HasForeignKey("OrderId");
            h.Property<int>("Id");
            h.HasKey("Id");
            h.Property(x => x.From).HasConversion<string>().HasMaxLength(40);
            h.Property(x => x.To).HasConversion<string>().HasMaxLength(40);
            h.Property(x => x.Action).HasConversion<string>().HasMaxLength(40);
            h.Property(x => x.Actor).HasConversion<string>().HasMaxLength(40);
            h.Property(x => x.UserId).HasMaxLength(100);
        });
        builder.Navigation(o => o.History).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

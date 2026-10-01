using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stockline.Domain.Erp;

namespace Stockline.Infrastructure.Persistence;

internal sealed class ErpDocumentConfiguration : IEntityTypeConfiguration<ErpDocument>
{
    public void Configure(EntityTypeBuilder<ErpDocument> builder)
    {
        builder.ToTable("erp_documents");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();
        builder.Property(d => d.OrderId);
        builder.HasIndex(d => d.OrderId);
        builder.Property(d => d.Kind).HasConversion<string>().HasMaxLength(30);
        builder.Property(d => d.OriginWarehouse).HasMaxLength(40).IsRequired();
        builder.Property(d => d.DestinationLocation).HasMaxLength(40);
        builder.Property(d => d.PurchaseOrderDocumentId);
        builder.Property(d => d.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(d => d.Attempts);
        builder.Property(d => d.LastError).HasMaxLength(1000);
        builder.Property(d => d.LastFailureWasTransient);
        builder.Property(d => d.DocEntry);
        builder.Property(d => d.DocNum);
        builder.Property(d => d.SentAt);

        builder.OwnsMany(d => d.Lines, l =>
        {
            l.ToTable("erp_document_lines");
            l.WithOwner().HasForeignKey("DocumentId");
            l.Property<int>("Id");
            l.HasKey("Id");
            l.Property(x => x.ProductCode).HasMaxLength(60).IsRequired();
            l.Property(x => x.Quantity).HasPrecision(18, 4);
            l.Property(x => x.UnitPrice).HasPrecision(18, 4);
            l.Property(x => x.DiscountPercent).HasPrecision(9, 4);
        });
        builder.Navigation(d => d.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.OwnsMany(d => d.AttemptLog, a =>
        {
            a.ToTable("erp_document_attempts");
            a.WithOwner().HasForeignKey("DocumentId");
            a.Property<int>("Id");
            a.HasKey("Id");
            // Stored as UTC datetime so the log can be filtered and ordered by date in SQL (also on SQLite).
            a.Property(x => x.At).HasConversion(
                v => v.UtcDateTime, v => new DateTimeOffset(DateTime.SpecifyKind(v, DateTimeKind.Utc)));
            a.Property(x => x.Error).HasMaxLength(1000);
        });
        builder.Navigation(d => d.AttemptLog).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

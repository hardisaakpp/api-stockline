using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stockline.Application.Identity;
using Stockline.Application.Orders;
using Stockline.Domain.Identity;
using Stockline.Domain.Orders;

namespace Stockline.Infrastructure.Persistence;

internal sealed class OrderSourceFileConfiguration : IEntityTypeConfiguration<OrderSourceFile>
{
    public void Configure(EntityTypeBuilder<OrderSourceFile> builder)
    {
        builder.ToTable("order_source_files");
        builder.HasKey(f => f.OrderId);
        builder.Property(f => f.OrderId).ValueGeneratedNever();
        builder.Property(f => f.FileName).HasMaxLength(260).IsRequired();
        builder.Property(f => f.Content).IsRequired();
        builder.Property(f => f.UploadedBy).HasMaxLength(100);
        builder.Property(f => f.UploadedAt);
    }
}

internal sealed class UserPreferenceConfiguration : IEntityTypeConfiguration<UserPreference>
{
    public void Configure(EntityTypeBuilder<UserPreference> builder)
    {
        builder.ToTable("user_preferences");
        builder.HasKey(p => new { p.UserId, p.Key });
        builder.Property(p => p.Key).HasMaxLength(UserPreference.MaxKeyLength);
        builder.Property(p => p.Json).IsRequired();
    }
}

public sealed class EfOrderFileStore(StocklineDbContext db) : IOrderFileStore
{
    public async Task SaveAsync(OrderSourceFile file, CancellationToken ct)
    {
        db.OrderSourceFiles.Add(file);
        await db.SaveChangesAsync(ct);
    }

    public Task<OrderSourceFile?> GetAsync(Guid orderId, CancellationToken ct) =>
        db.OrderSourceFiles.AsNoTracking().FirstOrDefaultAsync(f => f.OrderId == orderId, ct);
}

public sealed class EfUserPreferenceRepository(StocklineDbContext db) : IUserPreferenceRepository
{
    public async Task<IReadOnlyList<UserPreference>> ListAsync(Guid userId, CancellationToken ct) =>
        (await db.UserPreferences.AsNoTracking().Where(p => p.UserId == userId).ToListAsync(ct))
            .OrderBy(p => p.Key, StringComparer.Ordinal).ToList();

    public Task<UserPreference?> GetAsync(Guid userId, string key, CancellationToken ct) =>
        db.UserPreferences.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId && p.Key == key, ct);

    public async Task SetAsync(Guid userId, string key, string json, CancellationToken ct)
    {
        var existing = await db.UserPreferences.FirstOrDefaultAsync(p => p.UserId == userId && p.Key == key, ct);
        if (existing is null)
        {
            db.UserPreferences.Add(new UserPreference(userId, key, json));
        }
        else
        {
            existing.Replace(json);
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> DeleteAsync(Guid userId, string key, CancellationToken ct) =>
        await db.UserPreferences.Where(p => p.UserId == userId && p.Key == key).ExecuteDeleteAsync(ct) > 0;
}

public sealed class EfLocationRepository(StocklineDbContext db) : Stockline.Application.Locations.ILocationRepository
{
    public async Task<IReadOnlyList<Stockline.Domain.Locations.Location>> ListAsync(CancellationToken ct) =>
        (await db.Locations.AsNoTracking().ToListAsync(ct)).OrderBy(l => l.Kind).ThenBy(l => l.Code, StringComparer.Ordinal).ToList();

    public Task<Stockline.Domain.Locations.Location?> GetAsync(string code, CancellationToken ct) =>
        db.Locations.FirstOrDefaultAsync(l => l.Code == code, ct);

    public void Add(Stockline.Domain.Locations.Location location) => db.Locations.Add(location);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}

using Microsoft.EntityFrameworkCore;
using Stockline.Domain.Erp;
using Stockline.Domain.Identity;
using Stockline.Domain.Locations;
using Stockline.Domain.Notifications;
using Stockline.Domain.Orders;

namespace Stockline.Infrastructure.Persistence;

public sealed class StocklineDbContext(DbContextOptions<StocklineDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();

    public DbSet<Location> Locations => Set<Location>();

    public DbSet<ErpDocument> ErpDocuments => Set<ErpDocument>();

    public DbSet<User> Users => Set<User>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<EmailSubscription> EmailSubscriptions => Set<EmailSubscription>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(StocklineDbContext).Assembly);
}

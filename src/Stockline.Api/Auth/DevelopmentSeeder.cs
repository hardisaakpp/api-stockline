using Microsoft.EntityFrameworkCore;
using Stockline.Application.Identity;
using Stockline.Domain.Identity;
using Stockline.Domain.Orders;
using Stockline.Infrastructure.Persistence;

namespace Stockline.Api.Auth;

/// <summary>
/// Creates fictitious demo accounts on an empty database. Runs only in Development and only when
/// <c>Seed:DemoPassword</c> is configured, so no credential ships in the default configuration.
/// </summary>
public static class DevelopmentSeeder
{
    private static readonly (string Email, string Name, ActorRole Role, bool Admin)[] DemoUsers =
    [
        ("admin@stockline.test", "Ada Admin", ActorRole.Buyer, true),
        ("buyer@stockline.test", "Bruno Buyer", ActorRole.Buyer, false),
        ("supplier@stockline.test", "Sofia Supplier", ActorRole.Supplier, false),
        ("production@stockline.test", "Pablo Production", ActorRole.SupplierProduction, false),
    ];

    public static async Task SeedAsync(IServiceProvider services, IConfiguration configuration, ILogger logger)
    {
        var password = configuration["Seed:DemoPassword"];
        if (string.IsNullOrEmpty(password))
        {
            logger.LogInformation("Seed:DemoPassword is not set; skipping demo users.");
            return;
        }

        var db = services.GetRequiredService<StocklineDbContext>();
        if (await db.Users.AnyAsync())
        {
            return;
        }

        var hasher = services.GetRequiredService<IPasswordHasher>();
        var clock = services.GetRequiredService<TimeProvider>();
        foreach (var (email, name, role, admin) in DemoUsers)
        {
            db.Users.Add(User.Create(Guid.NewGuid(), email, name, hasher.Hash(password), role, admin, clock.GetUtcNow()));
        }

        await db.SaveChangesAsync();
        logger.LogInformation("Seeded {Count} demo users.", DemoUsers.Length);
    }
}

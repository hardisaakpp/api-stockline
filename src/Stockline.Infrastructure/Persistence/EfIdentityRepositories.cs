using Microsoft.EntityFrameworkCore;
using Stockline.Application.Identity;
using Stockline.Domain.Identity;

namespace Stockline.Infrastructure.Persistence;

public sealed class EfUserRepository(StocklineDbContext db) : IUserRepository
{
    public Task<User?> GetAsync(Guid id, CancellationToken ct) => db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);

    public Task<User?> FindByEmailAsync(string normalizedEmail, CancellationToken ct) =>
        db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail, ct);

    public Task<User?> FindByExternalAsync(string provider, string externalId, CancellationToken ct) =>
        db.Users.FirstOrDefaultAsync(u => u.ExternalProvider == provider && u.ExternalId == externalId, ct);

    public Task<bool> EmailExistsAsync(string normalizedEmail, CancellationToken ct) =>
        db.Users.AnyAsync(u => u.Email == normalizedEmail, ct);

    public async Task<IReadOnlyList<User>> ListAsync(CancellationToken ct) =>
        await db.Users.AsNoTracking().OrderBy(u => u.DisplayName).ThenBy(u => u.Email).ToListAsync(ct);

    public Task<int> CountActiveAdminsAsync(CancellationToken ct) =>
        db.Users.CountAsync(u => u.IsAdmin && u.IsActive, ct);

    public void Add(User user) => db.Users.Add(user);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveWithDuplicateDetectionAsync(ct);
}

public sealed class EfRefreshTokenRepository(StocklineDbContext db) : IRefreshTokenRepository
{
    public Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken ct) =>
        db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct);

    public void Add(RefreshToken token) => db.RefreshTokens.Add(token);

    public async Task RevokeAllAsync(Guid userId, DateTimeOffset at, CancellationToken ct)
    {
        var active = await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null).ToListAsync(ct);
        foreach (var token in active)
        {
            token.Revoke(at);
        }
    }

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveWithDuplicateDetectionAsync(ct);
}

internal static class DuplicateAwareSave
{
    /// <summary>
    /// Saves; if a unique index rejected a duplicate, drops everything the context was tracking (it holds the rejected
    /// row) and reports it as <see cref="DuplicateAccountException"/> so the caller can retry from a clean slate.
    /// </summary>
    public static async Task SaveWithDuplicateDetectionAsync(this StocklineDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (UniqueViolations.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            throw new DuplicateAccountException(ex);
        }
    }
}

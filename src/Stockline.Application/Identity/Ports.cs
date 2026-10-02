using Stockline.Domain.Identity;

namespace Stockline.Application.Identity;

public interface IUserRepository
{
    Task<User?> GetAsync(Guid id, CancellationToken ct);

    Task<User?> FindByEmailAsync(string normalizedEmail, CancellationToken ct);

    Task<User?> FindByExternalAsync(string provider, string externalId, CancellationToken ct);

    Task<bool> EmailExistsAsync(string normalizedEmail, CancellationToken ct);

    Task<IReadOnlyList<User>> ListAsync(CancellationToken ct);

    Task<int> CountActiveAdminsAsync(CancellationToken ct);

    void Add(User user);

    Task SaveChangesAsync(CancellationToken ct);
}

public interface IRefreshTokenRepository
{
    Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken ct);

    void Add(RefreshToken token);

    /// <summary>Revokes every active refresh token of a user (logout everywhere, password reset, deactivation).</summary>
    Task RevokeAllAsync(Guid userId, DateTimeOffset at, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
}

public interface IPasswordHasher
{
    string Hash(string password);

    bool Verify(string password, string hash);

    /// <summary>A valid hash of a random secret, verified against when the account does not exist to even out response time.</summary>
    string DummyHash { get; }
}

public sealed record AccessToken(string Value, DateTimeOffset ExpiresAt);

public interface IAccessTokenIssuer
{
    AccessToken Issue(User user, DateTimeOffset now);
}

public sealed record RefreshTokenOptions(TimeSpan RefreshTokenLifetime);

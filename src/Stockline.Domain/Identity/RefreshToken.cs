namespace Stockline.Domain.Identity;

/// <summary>A rotating refresh token. Only a hash of the secret is stored.</summary>
public sealed class RefreshToken
{
    // Parameterless constructor required by EF Core materialization.
    private RefreshToken() => TokenHash = null!;

    public RefreshToken(Guid id, Guid userId, string tokenHash, DateTimeOffset createdAt, DateTimeOffset expiresAt)
    {
        Id = id;
        UserId = userId;
        TokenHash = tokenHash;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
    }

    public Guid Id { get; }

    public Guid UserId { get; }

    public string TokenHash { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset ExpiresAt { get; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && now < ExpiresAt;

    public void Revoke(DateTimeOffset at) => RevokedAt ??= at;
}

using Stockline.Domain.Identity;

namespace Stockline.Application.Identity;

/// <summary>Creates the access/refresh pair for a user. The caller persists with <c>SaveChangesAsync</c>.</summary>
public sealed class SessionIssuer(
    IAccessTokenIssuer accessTokens, IRefreshTokenRepository refreshTokens, RefreshTokenOptions options)
{
    public AuthSession Issue(User user, DateTimeOffset now)
    {
        var access = accessTokens.Issue(user, now);
        var secret = RefreshTokenSecret.Generate();
        var expiresAt = now + options.RefreshTokenLifetime;
        refreshTokens.Add(new RefreshToken(Guid.NewGuid(), user.Id, RefreshTokenSecret.Hash(secret), now, expiresAt));
        return new AuthSession(access.Value, access.ExpiresAt, secret, expiresAt, UserSummary.From(user));
    }
}

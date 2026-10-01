namespace Stockline.Application.Identity;

public sealed class RefreshSessionHandler(
    IUserRepository users, IRefreshTokenRepository refreshTokens, SessionIssuer sessions, TimeProvider clock)
{
    /// <summary>
    /// Rotates the refresh token. Presenting an already-used (revoked) token is treated as theft and
    /// revokes the user's whole token family.
    /// </summary>
    public async Task<AuthSession?> HandleAsync(string refreshToken, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var stored = await refreshTokens.FindByHashAsync(RefreshTokenSecret.Hash(refreshToken), ct);
        if (stored is null)
        {
            return null;
        }

        if (stored.RevokedAt is not null)
        {
            await refreshTokens.RevokeAllAsync(stored.UserId, now, ct);
            await refreshTokens.SaveChangesAsync(ct);
            return null;
        }

        var user = await users.GetAsync(stored.UserId, ct);
        if (!stored.IsActive(now) || user is not { IsActive: true })
        {
            return null;
        }

        stored.Revoke(now);
        var session = sessions.Issue(user, now);
        await refreshTokens.SaveChangesAsync(ct);
        return session;
    }
}

public sealed class LogoutHandler(IRefreshTokenRepository refreshTokens, TimeProvider clock)
{
    public async Task HandleAsync(string refreshToken, CancellationToken ct)
    {
        var stored = await refreshTokens.FindByHashAsync(RefreshTokenSecret.Hash(refreshToken), ct);
        if (stored is null)
        {
            return;
        }

        stored.Revoke(clock.GetUtcNow());
        await refreshTokens.SaveChangesAsync(ct);
    }
}

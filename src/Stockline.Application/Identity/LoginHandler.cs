using Stockline.Domain.Identity;

namespace Stockline.Application.Identity;

public sealed class LoginHandler(
    IUserRepository users,
    IRefreshTokenRepository refreshTokens,
    IPasswordHasher hasher,
    SessionIssuer sessions,
    TimeProvider clock)
{
    /// <returns>The new session, or <c>null</c> when the credentials are wrong or the account is disabled.</returns>
    public async Task<AuthSession?> HandleAsync(string email, string password, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(User.NormalizeEmail(email), ct);
        if (user is null)
        {
            hasher.Verify(password, hasher.DummyHash);
            return null;
        }

        // Directory accounts have no local password: they sign in through the directory only.
        if (user.IsExternal || !hasher.Verify(password, user.PasswordHash) || !user.IsActive)
        {
            return null;
        }

        var session = sessions.Issue(user, clock.GetUtcNow());
        await refreshTokens.SaveChangesAsync(ct);
        return session;
    }
}

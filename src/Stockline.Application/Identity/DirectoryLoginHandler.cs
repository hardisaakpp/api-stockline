using Stockline.Domain;
using Stockline.Domain.Identity;

namespace Stockline.Application.Identity;

public enum DirectoryLoginOutcome
{
    Succeeded,
    InvalidCredentials,

    /// <summary>The directory knows the person, but no group gives them access to Stockline, or an administrator disabled them here.</summary>
    NotAuthorized,

    /// <summary>The directory's email belongs to a local account: two people must never share one.</summary>
    EmailConflict,
    Unavailable,
    NotEnabled,
}

public sealed record DirectoryLoginResult(DirectoryLoginOutcome Outcome, AuthSession? Session = null);

/// <summary>
/// Signs in through the directory. The directory owns who the person is and what they may do (name, email,
/// groups); Stockline keeps a local record so orders, history and refresh tokens have something to point at,
/// and refreshes it on every sign-in.
/// </summary>
public sealed class DirectoryLoginHandler(
    IDirectoryAuthenticator directory,
    DirectoryAccessOptions access,
    IUserRepository users,
    IRefreshTokenRepository refreshTokens,
    SessionIssuer sessions,
    TimeProvider clock)
{
    public async Task<DirectoryLoginResult> HandleAsync(string username, string password, CancellationToken ct)
    {
        if (!directory.IsEnabled)
        {
            return new DirectoryLoginResult(DirectoryLoginOutcome.NotEnabled);
        }

        var auth = await directory.AuthenticateAsync(username.Trim(), password, ct);
        if (auth.Identity is not { } identity)
        {
            return new DirectoryLoginResult(auth.Status == DirectoryAuthStatus.Unavailable
                ? DirectoryLoginOutcome.Unavailable
                : DirectoryLoginOutcome.InvalidCredentials);
        }

        try
        {
            return await SignInAsync(identity, ct);
        }
        catch (DuplicateAccountException)
        {
            // Two first sign-ins of the same person raced and the other one created the record: now it exists, so this finds it.
            return await SignInAsync(identity, ct);
        }
    }

    private async Task<DirectoryLoginResult> SignInAsync(DirectoryIdentity identity, CancellationToken ct)
    {

        if (DirectoryAccessPolicy.Resolve(access, identity.Groups) is not { } grant)
        {
            return new DirectoryLoginResult(DirectoryLoginOutcome.NotAuthorized);
        }

        var user = await users.FindByExternalAsync(IdentityProviders.Directory, identity.ExternalId, ct);
        var email = User.NormalizeEmail(identity.Email);
        var now = clock.GetUtcNow();

        try
        {
            if (user is null)
            {
                if (await users.EmailExistsAsync(email, ct))
                {
                    return new DirectoryLoginResult(DirectoryLoginOutcome.EmailConflict);
                }

                user = User.CreateExternal(
                    Guid.NewGuid(), IdentityProviders.Directory, identity.ExternalId, email, identity.DisplayName,
                    grant.Role, grant.IsAdmin, now);
                users.Add(user);
            }
            else
            {
                var takenByAnother = await users.FindByEmailAsync(email, ct) is { } owner && owner.Id != user.Id;
                if (takenByAnother)
                {
                    return new DirectoryLoginResult(DirectoryLoginOutcome.EmailConflict);
                }

                user.SyncFromDirectory(email, identity.DisplayName, grant.Role, grant.IsAdmin);
            }
        }
        catch (DomainException)
        {
            // The directory returned something Stockline cannot store (for example no usable display name).
            return new DirectoryLoginResult(DirectoryLoginOutcome.NotAuthorized);
        }

        if (!user.IsActive)
        {
            await users.SaveChangesAsync(ct);
            return new DirectoryLoginResult(DirectoryLoginOutcome.NotAuthorized);
        }

        var session = sessions.Issue(user, now);
        await refreshTokens.SaveChangesAsync(ct);
        return new DirectoryLoginResult(DirectoryLoginOutcome.Succeeded, session);
    }
}

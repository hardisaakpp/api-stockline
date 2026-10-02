using Stockline.Domain.Identity;
using Stockline.Domain.Orders;

namespace Stockline.Application.Identity;

/// <summary>A person the directory has just authenticated.</summary>
/// <param name="ExternalId">The account name (sAMAccountName): stable, unlike the email or the display name.</param>
/// <param name="Groups">Short names of the groups the person belongs to (the CN of each memberOf entry).</param>
public sealed record DirectoryIdentity(
    string ExternalId, string Email, string DisplayName, IReadOnlyList<string> Groups);

public enum DirectoryAuthStatus
{
    Authenticated,

    /// <summary>Unknown account, wrong password, or an account the directory has disabled: deliberately indistinguishable.</summary>
    InvalidCredentials,

    /// <summary>The directory could not be reached or is misconfigured. Not the person's fault, so not a 401.</summary>
    Unavailable,
}

public sealed record DirectoryAuthResult(DirectoryAuthStatus Status, DirectoryIdentity? Identity = null);

/// <summary>Checks a username and password against the corporate directory.</summary>
public interface IDirectoryAuthenticator
{
    bool IsEnabled { get; }

    Task<DirectoryAuthResult> AuthenticateAsync(string username, string password, CancellationToken ct);
}

/// <summary>Which directory groups grant which access. Group names are compared ignoring case.</summary>
public sealed class DirectoryAccessOptions
{
    /// <summary>Members of any of these groups also get the administrator permission.</summary>
    public string[] AdminGroups { get; set; } = [];

    /// <summary>Role name (<see cref="ActorRole"/>) to the groups that grant it. Without a matching group there is no access.</summary>
    public Dictionary<string, string[]> RoleGroups { get; set; } = [];
}

public sealed record DirectoryAccess(ActorRole Role, bool IsAdmin);

public static class DirectoryAccessPolicy
{
    /// <summary>Role precedence when someone belongs to several role groups: the most capable first.</summary>
    private static readonly ActorRole[] Precedence = [.. Permissions.AssignableRoles];

    /// <returns>The access the groups grant, or <c>null</c> when none of them grants a role (no access at all).</returns>
    public static DirectoryAccess? Resolve(DirectoryAccessOptions options, IReadOnlyCollection<string> groups)
    {
        var member = new HashSet<string>(groups, StringComparer.OrdinalIgnoreCase);

        foreach (var role in Precedence)
        {
            var grantedBy = options.RoleGroups.FirstOrDefault(entry =>
                string.Equals(entry.Key, role.ToString(), StringComparison.OrdinalIgnoreCase)).Value ?? [];
            if (grantedBy.Any(member.Contains))
            {
                return new DirectoryAccess(role, options.AdminGroups.Any(member.Contains));
            }
        }

        return null;
    }
}

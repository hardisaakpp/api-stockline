using Stockline.Application.Identity;

namespace Stockline.UnitTests.Support;

/// <summary>A directory held in memory: accounts with a password and a set of groups.</summary>
public sealed class FakeDirectoryAuthenticator : IDirectoryAuthenticator
{
    private readonly Dictionary<string, (string Password, DirectoryIdentity Identity)> _accounts =
        new(StringComparer.OrdinalIgnoreCase);

    public bool IsEnabled { get; set; } = true;

    public bool Unreachable { get; set; }

    public string? LastUsername { get; private set; }

    public DirectoryIdentity Add(string account, string password, string email, string displayName, params string[] groups)
    {
        var identity = new DirectoryIdentity(account, email, displayName, groups);
        _accounts[account] = (password, identity);
        return identity;
    }

    public void SetGroups(string account, params string[] groups) =>
        _accounts[account] = (_accounts[account].Password, _accounts[account].Identity with { Groups = groups });

    public Task<DirectoryAuthResult> AuthenticateAsync(string username, string password, CancellationToken ct)
    {
        LastUsername = username;
        if (Unreachable)
        {
            return Task.FromResult(new DirectoryAuthResult(DirectoryAuthStatus.Unavailable));
        }

        return Task.FromResult(_accounts.TryGetValue(username, out var account) && account.Password == password
            ? new DirectoryAuthResult(DirectoryAuthStatus.Authenticated, account.Identity)
            : new DirectoryAuthResult(DirectoryAuthStatus.InvalidCredentials));
    }
}

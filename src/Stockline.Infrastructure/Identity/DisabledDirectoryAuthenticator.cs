using Stockline.Application.Identity;

namespace Stockline.Infrastructure.Identity;

/// <summary>Stands in when directory sign-in is not configured, so the rest of the app never has to ask.</summary>
public sealed class DisabledDirectoryAuthenticator : IDirectoryAuthenticator
{
    public bool IsEnabled => false;

    public Task<DirectoryAuthResult> AuthenticateAsync(string username, string password, CancellationToken ct) =>
        Task.FromResult(new DirectoryAuthResult(DirectoryAuthStatus.InvalidCredentials));
}

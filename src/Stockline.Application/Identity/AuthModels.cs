using Stockline.Domain.Identity;

namespace Stockline.Application.Identity;

public sealed record UserSummary(
    Guid Id,
    string Email,
    string DisplayName,
    string Role,
    bool IsAdmin,
    bool IsActive,
    IReadOnlyList<string> Permissions,
    DateTimeOffset CreatedAt)
{
    public static UserSummary From(User user) => new(
        user.Id, user.Email, user.DisplayName, user.Role.ToString(), user.IsAdmin, user.IsActive,
        user.Permissions, user.CreatedAt);
}

public sealed record AuthSession(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    UserSummary User);

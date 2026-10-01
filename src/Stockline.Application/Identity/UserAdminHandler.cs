using Stockline.Domain;
using Stockline.Domain.Identity;
using Stockline.Domain.Orders;

namespace Stockline.Application.Identity;

public enum UserAdminOutcome
{
    Succeeded,
    NotFound,
    Invalid,
    EmailTaken,
    LastAdmin,
}

public sealed record UserAdminResult(UserAdminOutcome Outcome, UserSummary? User = null, string? Message = null);

public sealed record CreateUserCommand(string Email, string DisplayName, string Password, ActorRole Role, bool IsAdmin);

public sealed record UpdateUserCommand(string DisplayName, ActorRole Role, bool IsAdmin, bool IsActive);

public sealed class UserAdminHandler(
    IUserRepository users, IRefreshTokenRepository refreshTokens, IPasswordHasher hasher, TimeProvider clock)
{
    public async Task<IReadOnlyList<UserSummary>> ListAsync(CancellationToken ct) =>
        [.. (await users.ListAsync(ct)).Select(UserSummary.From)];

    public async Task<UserAdminResult> CreateAsync(CreateUserCommand command, CancellationToken ct)
    {
        if (PasswordPolicy.Validate(command.Password) is { } passwordError)
        {
            return new UserAdminResult(UserAdminOutcome.Invalid, Message: passwordError);
        }

        User user;
        try
        {
            user = User.Create(
                Guid.NewGuid(), command.Email, command.DisplayName, hasher.Hash(command.Password),
                command.Role, command.IsAdmin, clock.GetUtcNow());
        }
        catch (DomainException ex)
        {
            return new UserAdminResult(UserAdminOutcome.Invalid, Message: ex.Message);
        }

        if (await users.EmailExistsAsync(user.Email, ct))
        {
            return new UserAdminResult(UserAdminOutcome.EmailTaken, Message: "A user with this email already exists.");
        }

        users.Add(user);
        await users.SaveChangesAsync(ct);
        return new UserAdminResult(UserAdminOutcome.Succeeded, UserSummary.From(user));
    }

    public async Task<UserAdminResult> UpdateAsync(Guid id, UpdateUserCommand command, CancellationToken ct)
    {
        var user = await users.GetAsync(id, ct);
        if (user is null)
        {
            return new UserAdminResult(UserAdminOutcome.NotFound);
        }

        var losesAdminAccess = user is { IsAdmin: true, IsActive: true } && !(command.IsAdmin && command.IsActive);
        if (losesAdminAccess && await users.CountActiveAdminsAsync(ct) <= 1)
        {
            return new UserAdminResult(UserAdminOutcome.LastAdmin, Message: "At least one active administrator is required.");
        }

        try
        {
            user.Update(command.DisplayName, command.Role, command.IsAdmin, command.IsActive);
        }
        catch (DomainException ex)
        {
            return new UserAdminResult(UserAdminOutcome.Invalid, Message: ex.Message);
        }

        if (!user.IsActive)
        {
            await refreshTokens.RevokeAllAsync(user.Id, clock.GetUtcNow(), ct);
        }

        await users.SaveChangesAsync(ct);
        return new UserAdminResult(UserAdminOutcome.Succeeded, UserSummary.From(user));
    }

    public async Task<UserAdminResult> ResetPasswordAsync(Guid id, string newPassword, CancellationToken ct)
    {
        if (PasswordPolicy.Validate(newPassword) is { } passwordError)
        {
            return new UserAdminResult(UserAdminOutcome.Invalid, Message: passwordError);
        }

        var user = await users.GetAsync(id, ct);
        if (user is null)
        {
            return new UserAdminResult(UserAdminOutcome.NotFound);
        }

        user.SetPasswordHash(hasher.Hash(newPassword));
        await refreshTokens.RevokeAllAsync(user.Id, clock.GetUtcNow(), ct);
        await users.SaveChangesAsync(ct);
        return new UserAdminResult(UserAdminOutcome.Succeeded, UserSummary.From(user));
    }
}

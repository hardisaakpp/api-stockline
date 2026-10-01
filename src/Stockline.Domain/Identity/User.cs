using Stockline.Domain.Orders;

namespace Stockline.Domain.Identity;

public sealed class User
{
    // Parameterless constructor required by EF Core materialization.
    private User()
    {
        Email = null!;
        DisplayName = null!;
        PasswordHash = null!;
    }

    private User(Guid id, string email, string displayName, string passwordHash, ActorRole role, bool isAdmin, DateTimeOffset createdAt)
    {
        Id = id;
        Email = email;
        DisplayName = displayName;
        PasswordHash = passwordHash;
        Role = role;
        IsAdmin = isAdmin;
        IsActive = true;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }

    public string Email { get; private set; }

    public string DisplayName { get; private set; }

    public string PasswordHash { get; private set; }

    public ActorRole Role { get; private set; }

    public bool IsAdmin { get; private set; }

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public IReadOnlyList<string> Permissions => Identity.Permissions.For(Role, IsAdmin);

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    public static User Create(
        Guid id, string email, string displayName, string passwordHash, ActorRole role, bool isAdmin, DateTimeOffset createdAt)
    {
        var normalized = NormalizeEmail(email);
        if (!IsPlausibleEmail(normalized))
        {
            throw new DomainException("The email address is not valid.");
        }

        EnsureDisplayName(displayName);
        EnsureAssignable(role);
        return new User(id, normalized, displayName.Trim(), passwordHash, role, isAdmin, createdAt);
    }

    public void Update(string displayName, ActorRole role, bool isAdmin, bool isActive)
    {
        EnsureDisplayName(displayName);
        EnsureAssignable(role);
        DisplayName = displayName.Trim();
        Role = role;
        IsAdmin = isAdmin;
        IsActive = isActive;
    }

    public void SetPasswordHash(string passwordHash) => PasswordHash = passwordHash;

    private static bool IsPlausibleEmail(string email)
    {
        var at = email.IndexOf('@');
        return at > 0 && at < email.Length - 3 && email.IndexOf('.', at) > at && !email.Contains(' ');
    }

    private static void EnsureDisplayName(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new DomainException("The display name is required.");
        }
    }

    private static void EnsureAssignable(ActorRole role)
    {
        if (!Identity.Permissions.AssignableRoles.Contains(role))
        {
            throw new DomainException($"The role '{role}' cannot be assigned to a user.");
        }
    }
}

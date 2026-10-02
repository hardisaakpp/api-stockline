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

    /// <summary>The stored hash of people who sign in through the corporate directory: no password ever verifies against it.</summary>
    public const string UnusablePasswordHash = "!directory";

    /// <summary>The provider that owns this account's credentials (see <see cref="IdentityProviders"/>), or null for a local account.</summary>
    public string? ExternalProvider { get; private set; }

    /// <summary>The account name in the external provider (the directory's <c>sAMAccountName</c>).</summary>
    public string? ExternalId { get; private set; }

    public bool IsExternal => ExternalProvider is not null;

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

    /// <summary>A person who signs in through the directory: it owns their password, name, role and administrator flag.</summary>
    public static User CreateExternal(
        Guid id, string provider, string externalId, string email, string displayName, ActorRole role, bool isAdmin,
        DateTimeOffset createdAt)
    {
        var user = Create(id, email, displayName, UnusablePasswordHash, role, isAdmin, createdAt);
        user.ExternalProvider = provider;
        user.ExternalId = externalId;
        return user;
    }

    /// <summary>Brings an external account in line with what the directory says at sign-in. Whether it is active stays a local decision.</summary>
    public void SyncFromDirectory(string email, string displayName, ActorRole role, bool isAdmin)
    {
        if (!IsExternal)
        {
            throw new DomainException("Only directory accounts are synchronized from the directory.");
        }

        var normalized = NormalizeEmail(email);
        if (!IsPlausibleEmail(normalized))
        {
            throw new DomainException("The email address is not valid.");
        }

        EnsureDisplayName(displayName);
        EnsureAssignable(role);
        Email = normalized;
        DisplayName = displayName.Trim();
        Role = role;
        IsAdmin = isAdmin;
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

    /// <summary>Enables or disables the account without touching anything else (used for directory accounts).</summary>
    public void SetActive(bool isActive) => IsActive = isActive;

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

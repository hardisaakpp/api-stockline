namespace Stockline.Domain.Identity;

/// <summary>Where an account's credentials live.</summary>
public static class IdentityProviders
{
    /// <summary>The corporate directory (Active Directory or any LDAP server that speaks its schema).</summary>
    public const string Directory = "Directory";
}

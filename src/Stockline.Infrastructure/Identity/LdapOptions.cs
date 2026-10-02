namespace Stockline.Infrastructure.Identity;

/// <summary>Connection to the corporate directory (section <c>Directory</c>). Off unless <see cref="Enabled"/> is set.</summary>
public sealed class LdapOptions
{
    public bool Enabled { get; set; }

    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 636;

    /// <summary>Use LDAPS. Plain LDAP sends the password in the clear: only for a directory on the same machine.</summary>
    public bool UseSsl { get; set; } = true;

    /// <summary>
    /// Check the directory's certificate. Switch off only for a development directory with a self-signed one.
    /// With it on, a directory using a private CA must have that CA in the system trust store (or <c>LDAPTLS_CACERT</c> set) on Linux.
    /// </summary>
    public bool ValidateCertificate { get; set; } = true;

    /// <summary>The read-only service account used to find the person's entry before binding as them.</summary>
    public string BindDn { get; set; } = "";

    public string BindPassword { get; set; } = "";

    public string SearchBase { get; set; } = "";

    /// <summary>Which entries count as people. The default skips computers, which are also <c>objectClass=user</c> in AD.</summary>
    public string UserFilter { get; set; } = "(&(objectCategory=person)(objectClass=user))";

    /// <summary>Ask the directory for groups at any depth (an AD feature); falls back to direct membership if unsupported.</summary>
    public bool NestedGroups { get; set; } = true;

    public int TimeoutSeconds { get; set; } = 15;

    public bool IsComplete =>
        Enabled && !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(BindDn)
        && !string.IsNullOrWhiteSpace(BindPassword) && !string.IsNullOrWhiteSpace(SearchBase);
}

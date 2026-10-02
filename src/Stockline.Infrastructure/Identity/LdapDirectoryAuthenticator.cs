using System.DirectoryServices.Protocols;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stockline.Application.Identity;

namespace Stockline.Infrastructure.Identity;

/// <summary>
/// Authenticates against Active Directory (or any LDAP server with its schema) the way the directory expects:
/// find the person's entry with a read-only service account, then prove the password by binding as that entry.
/// The password is never compared here and never leaves this method.
/// </summary>
public sealed class LdapDirectoryAuthenticator(IOptions<LdapOptions> options, ILogger<LdapDirectoryAuthenticator> logger)
    : IDirectoryAuthenticator
{
    private const int InvalidCredentialsCode = 49;
    private const int AccountDisabledFlag = 0x2;
    private const string InChainRule = "1.2.840.113556.1.4.1941";
    private static readonly string[] UserAttributes =
        ["sAMAccountName", "userPrincipalName", "mail", "displayName", "memberOf", "userAccountControl"];

    private readonly LdapOptions _options = options.Value;

    // On Linux the .NET LDAP client cannot take a certificate callback; the native library (libldap) is configured
    // through its environment instead. It reads this when it first sets up TLS, so it must be set before any connection.
    private readonly bool _skipCertificateCheckNatively = Prepare(options.Value);

    private static bool Prepare(LdapOptions options)
    {
        if (!options.Enabled || !options.UseSsl || options.ValidateCertificate || OperatingSystem.IsWindows())
        {
            return false;
        }

        // Environment.SetEnvironmentVariable would not do: .NET keeps its own copy, which native libraries never see.
        return NativeEnvironment.Set("LDAPTLS_REQCERT", "never");
    }

    public bool IsEnabled => _options.Enabled;

    public Task<DirectoryAuthResult> AuthenticateAsync(string username, string password, CancellationToken ct) =>
        Task.Run(() => Authenticate(username, password), ct);

    private DirectoryAuthResult Authenticate(string username, string password)
    {
        // An empty password makes many servers treat the bind as anonymous, which "succeeds": never let it get that far.
        if (string.IsNullOrWhiteSpace(username) || username.Length > 256 || string.IsNullOrEmpty(password))
        {
            return Rejected();
        }

        if (!_options.IsComplete)
        {
            logger.LogError("Directory sign-in is enabled but the connection settings are incomplete.");
            return Unavailable();
        }

        try
        {
            using var service = Connect();
            service.Bind(new NetworkCredential(_options.BindDn, _options.BindPassword));

            var entry = FindUser(service, username);
            if (entry is null)
            {
                return Rejected();
            }

            using var asUser = Connect();
            asUser.Bind(new NetworkCredential(entry.DistinguishedName, password));

            return ReadIdentity(service, entry);
        }
        catch (LdapException ex) when (ex.ErrorCode == InvalidCredentialsCode)
        {
            // The service account itself being refused is a configuration problem, not a wrong password.
            logger.LogInformation("Directory rejected the credentials for {Username}.", username);
            return Rejected();
        }
        catch (Exception ex) when (ex is LdapException or DirectoryOperationException or DirectoryException)
        {
            logger.LogError(ex, "The directory at {Host}:{Port} could not be used.", _options.Host, _options.Port);
            return Unavailable();
        }
    }

    private LdapConnection Connect()
    {
        var connection = new LdapConnection(new LdapDirectoryIdentifier(_options.Host, _options.Port))
        {
            AuthType = AuthType.Basic,
            Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds),
        };
        connection.SessionOptions.ProtocolVersion = 3;
        connection.SessionOptions.SecureSocketLayer = _options.UseSsl;
        connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
        if (_options.UseSsl && !_options.ValidateCertificate && !_skipCertificateCheckNatively)
        {
            connection.SessionOptions.VerifyServerCertificate = (_, _) => true;
        }

        return connection;
    }

    private SearchResultEntry? FindUser(LdapConnection service, string username)
    {
        var escaped = LdapText.EscapeFilterValue(username);
        var filter = $"(&{_options.UserFilter}(|(sAMAccountName={escaped})(userPrincipalName={escaped})(mail={escaped})))";
        var request = new SearchRequest(_options.SearchBase, filter, SearchScope.Subtree, UserAttributes);
        var response = (SearchResponse)service.SendRequest(request);

        // More than one match would mean signing the wrong person in: refuse rather than pick one.
        return response.Entries.Count == 1 ? response.Entries[0] : null;
    }

    private DirectoryAuthResult ReadIdentity(LdapConnection service, SearchResultEntry entry)
    {
        if (IsDisabled(Single(entry, "userAccountControl")))
        {
            return Rejected();
        }

        var account = Single(entry, "sAMAccountName");
        if (account is null)
        {
            logger.LogError("The directory entry {Dn} has no sAMAccountName.", entry.DistinguishedName);
            return Unavailable();
        }

        var email = Single(entry, "mail") ?? Single(entry, "userPrincipalName") ?? $"{account}@directory.invalid";
        var displayName = Single(entry, "displayName") ?? account;
        return new DirectoryAuthResult(
            DirectoryAuthStatus.Authenticated,
            new DirectoryIdentity(account, email, displayName, Groups(service, entry)));
    }

    private IReadOnlyList<string> Groups(LdapConnection service, SearchResultEntry entry)
    {
        if (_options.NestedGroups)
        {
            try
            {
                var request = new SearchRequest(
                    _options.SearchBase,
                    $"(&(objectClass=group)(member:{InChainRule}:={LdapText.EscapeFilterValue(entry.DistinguishedName)}))",
                    SearchScope.Subtree, "cn");
                var response = (SearchResponse)service.SendRequest(request);
                return [.. response.Entries.Cast<SearchResultEntry>().Select(e => Single(e, "cn")).OfType<string>()];
            }
            catch (Exception ex) when (ex is LdapException or DirectoryOperationException)
            {
                logger.LogWarning("The directory does not support nested group lookup; using direct membership. {Reason}", ex.Message);
            }
        }

        return [.. Values(entry, "memberOf").Select(LdapText.CommonName).OfType<string>()];
    }

    private static bool IsDisabled(string? userAccountControl) =>
        int.TryParse(userAccountControl, out var flags) && (flags & AccountDisabledFlag) != 0;

    private static string? Single(SearchResultEntry entry, string attribute) => Values(entry, attribute).FirstOrDefault();

    private static IEnumerable<string> Values(SearchResultEntry entry, string attribute) =>
        entry.Attributes.Contains(attribute)
            ? entry.Attributes[attribute].GetValues(typeof(string)).Cast<string>().Where(v => v.Length > 0)
            : [];

    private static DirectoryAuthResult Rejected() => new(DirectoryAuthStatus.InvalidCredentials);

    private static DirectoryAuthResult Unavailable() => new(DirectoryAuthStatus.Unavailable);
}

internal static class NativeEnvironment
{
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "setenv")]
    private static extern int SetEnv(string name, string value, int overwrite);

    /// <summary>Sets a variable in the real process environment (Unix only). Returns whether it took effect.</summary>
    public static bool Set(string name, string value)
    {
        try
        {
            return SetEnv(name, value, 1) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }
}

using System.Text;

namespace Stockline.Infrastructure.Identity;

/// <summary>The two bits of LDAP text handling that are easy to get wrong.</summary>
public static class LdapText
{
    /// <summary>
    /// Escapes a value for use inside a search filter (RFC 4515). Without it, a username such as
    /// <c>*)(objectClass=*</c> changes the meaning of the query.
    /// </summary>
    public static string EscapeFilterValue(string value)
    {
        var escaped = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': escaped.Append("\\5c"); break;
                case '*': escaped.Append("\\2a"); break;
                case '(': escaped.Append("\\28"); break;
                case ')': escaped.Append("\\29"); break;
                case '\0': escaped.Append("\\00"); break;
                default: escaped.Append(c); break;
            }
        }

        return escaped.ToString();
    }

    /// <summary>The name of a group from its distinguished name: <c>CN=Buyers,OU=Groups,DC=x</c> gives <c>Buyers</c>.</summary>
    public static string? CommonName(string distinguishedName)
    {
        if (!distinguishedName.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var name = new StringBuilder();
        for (var i = 3; i < distinguishedName.Length; i++)
        {
            var c = distinguishedName[i];
            if (c == '\\' && i + 1 < distinguishedName.Length)
            {
                name.Append(distinguishedName[++i]);
            }
            else if (c == ',')
            {
                break;
            }
            else
            {
                name.Append(c);
            }
        }

        return name.Length == 0 ? null : name.ToString();
    }
}

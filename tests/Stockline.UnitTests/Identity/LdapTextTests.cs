using Stockline.Infrastructure.Identity;

namespace Stockline.UnitTests.Identity;

public class LdapTextTests
{
    [Theory]
    [InlineData("ana.buyer", "ana.buyer")]
    [InlineData("a*b", "a\\2ab")]
    [InlineData("*)(objectClass=*", "\\2a\\29\\28objectClass=\\2a")]
    [InlineData("back\\slash", "back\\5cslash")]
    [InlineData("nul\0byte", "nul\\00byte")]
    public void Filter_values_are_escaped_so_a_username_cannot_change_the_query(string input, string expected)
    {
        Assert.Equal(expected, LdapText.EscapeFilterValue(input));
    }

    [Theory]
    [InlineData("CN=stockline-buyers,CN=Users,DC=stockline,DC=test", "stockline-buyers")]
    [InlineData("cn=Purchasing Team,OU=Groups,DC=x", "Purchasing Team")]
    [InlineData("CN=Smith\\, Ana,OU=People,DC=x", "Smith, Ana")]
    public void The_group_name_is_the_first_common_name_of_the_distinguished_name(string dn, string expected)
    {
        Assert.Equal(expected, LdapText.CommonName(dn));
    }

    [Theory]
    [InlineData("OU=Groups,DC=x")]
    [InlineData("CN=")]
    [InlineData("")]
    public void Anything_else_has_no_group_name(string dn)
    {
        Assert.Null(LdapText.CommonName(dn));
    }
}

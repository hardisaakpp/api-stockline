using Stockline.Application.Identity;
using Stockline.Domain.Orders;

namespace Stockline.UnitTests.Identity;

public class DirectoryAccessPolicyTests
{
    private static readonly DirectoryAccessOptions Options = new()
    {
        AdminGroups = ["Stockline-Admins"],
        RoleGroups = new()
        {
            ["Buyer"] = ["stockline-buyers", "purchasing"],
            ["Supplier"] = ["stockline-suppliers"],
            ["SupplierProduction"] = ["stockline-production"],
        },
    };

    [Fact]
    public void A_role_group_grants_that_role()
    {
        var access = DirectoryAccessPolicy.Resolve(Options, ["stockline-suppliers"]);

        Assert.Equal(new DirectoryAccess(ActorRole.Supplier, false), access);
    }

    [Fact]
    public void Group_names_are_compared_ignoring_case()
    {
        var access = DirectoryAccessPolicy.Resolve(Options, ["PURCHASING", "stockline-admins"]);

        Assert.Equal(new DirectoryAccess(ActorRole.Buyer, true), access);
    }

    [Fact]
    public void Someone_in_several_role_groups_gets_the_most_capable_role()
    {
        var access = DirectoryAccessPolicy.Resolve(Options, ["stockline-production", "stockline-suppliers", "stockline-buyers"]);

        Assert.Equal(ActorRole.Buyer, access!.Role);
    }

    [Fact]
    public void The_admin_group_alone_grants_nothing_because_a_person_also_needs_a_role()
    {
        Assert.Null(DirectoryAccessPolicy.Resolve(Options, ["stockline-admins"]));
    }

    [Fact]
    public void Unrelated_groups_or_none_mean_no_access()
    {
        Assert.Null(DirectoryAccessPolicy.Resolve(Options, ["domain users", "finance"]));
        Assert.Null(DirectoryAccessPolicy.Resolve(Options, []));
    }

    [Fact]
    public void Nothing_is_granted_when_the_mapping_is_empty()
    {
        Assert.Null(DirectoryAccessPolicy.Resolve(new DirectoryAccessOptions(), ["stockline-buyers"]));
    }
}

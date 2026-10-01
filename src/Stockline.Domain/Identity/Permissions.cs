using Stockline.Domain.Orders;

namespace Stockline.Domain.Identity;

/// <summary>Permission names follow the <c>module.action</c> convention shared with the web client.</summary>
public static class Permissions
{
    public const string OrdersView = "orders.view";
    public const string OrdersImport = "orders.import";
    public const string OrdersAct = "orders.act";
    public const string ErpSubmit = "erp.submit";
    public const string DashboardView = "dashboard.view";
    public const string AdminUsers = "admin.users";

    public static IReadOnlyList<string> All { get; } =
        [OrdersView, OrdersImport, OrdersAct, ErpSubmit, DashboardView, AdminUsers];

    /// <summary>Roles that can be assigned to a person (<see cref="ActorRole.System"/> is reserved for background work).</summary>
    public static IReadOnlyList<ActorRole> AssignableRoles { get; } =
        [ActorRole.Buyer, ActorRole.Supplier, ActorRole.SupplierProduction];

    public static IReadOnlyList<string> For(ActorRole role, bool isAdmin)
    {
        var permissions = new List<string>();
        switch (role)
        {
            case ActorRole.Buyer:
                permissions.AddRange([OrdersView, OrdersImport, OrdersAct, ErpSubmit, DashboardView]);
                break;
            case ActorRole.Supplier:
            case ActorRole.SupplierProduction:
                permissions.AddRange([OrdersView, OrdersAct, DashboardView]);
                break;
        }

        if (isAdmin)
        {
            permissions.Add(AdminUsers);
        }

        return permissions;
    }
}

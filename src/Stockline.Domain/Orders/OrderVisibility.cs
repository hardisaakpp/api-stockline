using System.Collections.Frozen;

namespace Stockline.Domain.Orders;

/// <summary>What each role may see of an order. Costs and prices are commercial secrets of the buyer–supplier deal.</summary>
public static class OrderVisibility
{
    /// <summary>Supplier production only works on orders that are waiting for or under review; afterwards the supplier admin takes over.</summary>
    private static readonly FrozenSet<OrderStatus> ProductionStatuses =
        new[] { OrderStatus.Submitted, OrderStatus.UnderReview }.ToFrozenSet();

    /// <summary>Statuses the role can see, or null when it can see every status.</summary>
    public static IReadOnlyCollection<OrderStatus>? VisibleStatuses(ActorRole role) =>
        role == ActorRole.SupplierProduction ? ProductionStatuses : null;

    public static bool CanSee(ActorRole role, OrderStatus status) =>
        VisibleStatuses(role) is not { } statuses || statuses.Contains(status);

    /// <summary>Cost, unit cost, discount and margin: hidden from supplier production.</summary>
    public static bool CanSeeCosts(ActorRole role) => role != ActorRole.SupplierProduction;

    /// <summary>The retail price (PVP) belongs to the buyer: nobody else sees it.</summary>
    public static bool CanSeeRetailPrice(ActorRole role) => role is ActorRole.Buyer or ActorRole.System;
}

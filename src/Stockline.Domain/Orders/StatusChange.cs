namespace Stockline.Domain.Orders;

public sealed record StatusChange(
    OrderStatus From,
    OrderStatus To,
    OrderAction Action,
    ActorRole Actor,
    string? UserId,
    DateTimeOffset At);

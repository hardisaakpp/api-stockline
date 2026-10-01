namespace Stockline.Domain.Orders;

public enum TransitionFailure
{
    None,
    InvalidStatus,
    ActorNotAllowed,
}

public readonly record struct TransitionResult(OrderStatus Status, TransitionFailure Failure)
{
    public bool Succeeded => Failure == TransitionFailure.None;
}

namespace Stockline.Domain.Orders;

public static class AuditFields
{
    public const string ConfirmedQuantity = nameof(ConfirmedQuantity);
    public const string CostWithoutTax = nameof(CostWithoutTax);
    public const string Comment = nameof(Comment);
    public const string ProductCode = nameof(ProductCode);
}

/// <summary>Who is making a change and when; recorded in the order's audit trail.</summary>
public sealed record EditContext(ActorRole Actor, string? UserId, DateTimeOffset At);

public sealed record AuditEntry(
    DateTimeOffset At,
    string? UserId,
    ActorRole Actor,
    string? ProductCode,
    string Field,
    string? OldValue,
    string? NewValue)
{
    /// <summary>Cost changes are hidden from roles that must not see costs.</summary>
    public bool IsCostField => Field == AuditFields.CostWithoutTax;
}

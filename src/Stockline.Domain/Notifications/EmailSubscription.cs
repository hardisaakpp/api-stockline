namespace Stockline.Domain.Notifications;

public enum EmailTopic
{
    OrderSubmitted,
    OrderCancelledByBuyer,
    OrderRejected,
    OrderCancelledBySupplier,
    ProductionConfirmed,
    RedistributionConfirmed,
    ReadyForDispatch,
    ErpSubmissionFailed,
}

public enum EmailAudience
{
    Buyer,
    Supplier,
    Technical,
}

public sealed class EmailSubscription
{
    // Parameterless constructor required by EF Core materialization.
    private EmailSubscription() => Email = null!;

    public EmailSubscription(Guid id, string email, EmailTopic topic)
    {
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@') || email.Trim().Length > 200)
        {
            throw new DomainException("A valid email address is required.");
        }

        Id = id;
        Email = email.Trim().ToLowerInvariant();
        Topic = topic;
    }

    public Guid Id { get; }

    public string Email { get; }

    public EmailTopic Topic { get; }
}

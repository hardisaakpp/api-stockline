using Stockline.Domain.Notifications;

namespace Stockline.Application.Notifications;

public sealed record EmailAttachment(string FileName, string ContentType, byte[] Content);

public sealed record EmailMessage(
    IReadOnlyList<string> To,
    string Subject,
    string HtmlBody,
    string TextBody,
    IReadOnlyList<EmailAttachment>? Attachments = null);

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct);
}

public interface IEmailSubscriptionRepository
{
    Task<IReadOnlyList<EmailSubscription>> ListAsync(CancellationToken ct);

    Task<IReadOnlyList<string>> RecipientsAsync(EmailTopic topic, CancellationToken ct);

    /// <summary>Adds the subscription unless it already exists; returns the existing or new one.</summary>
    Task<(EmailSubscription Subscription, bool Created)> AddAsync(string email, EmailTopic topic, CancellationToken ct);

    Task<bool> RemoveAsync(Guid id, CancellationToken ct);
}

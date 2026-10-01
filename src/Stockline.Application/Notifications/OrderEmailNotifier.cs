using Microsoft.Extensions.Logging;

namespace Stockline.Application.Notifications;

/// <summary>Turns an order event into an email for the subscribers of its topic. Never throws: email is best-effort.</summary>
public sealed class OrderEmailNotifier(
    IEmailSubscriptionRepository subscriptions, IEmailSender sender, ILogger<OrderEmailNotifier> logger)
{
    public async Task<bool> NotifyAsync(OrderEvent orderEvent, string? orderUrlTemplate, CancellationToken ct)
    {
        if (EmailTopics.For(orderEvent) is not { } topic)
        {
            return false;
        }

        try
        {
            var recipients = await subscriptions.RecipientsAsync(topic, ct);
            if (recipients.Count == 0)
            {
                logger.LogInformation("No subscribers for {Topic}; email for order {Order} skipped.", topic, orderEvent.OrderCode);
                return false;
            }

            var orderUrl = orderUrlTemplate?.Replace("{id}", orderEvent.OrderId.ToString(), StringComparison.Ordinal);
            var content = OrderEmailBuilder.Build(topic, orderEvent, orderUrl);
            await sender.SendAsync(new EmailMessage(recipients, content.Subject, content.HtmlBody, content.TextBody), ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Sending the {Topic} email for order {Order} failed.", topic, orderEvent.OrderCode);
            return false;
        }
    }
}

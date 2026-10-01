using Microsoft.Extensions.Logging;
using Stockline.Application.Notifications;

namespace Stockline.Infrastructure.Email;

/// <summary>Stand-in used when no SMTP host is configured: the email is only logged.</summary>
public sealed class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        logger.LogInformation(
            "Email (not sent, no SMTP host configured) to {Recipients}: {Subject}", string.Join(", ", message.To), message.Subject);
        return Task.CompletedTask;
    }
}

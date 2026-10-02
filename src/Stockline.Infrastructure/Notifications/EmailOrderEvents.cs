using Microsoft.Extensions.DependencyInjection;
using Stockline.Application.Jobs;
using Stockline.Application.Notifications;
using Stockline.Domain.Jobs;

namespace Stockline.Infrastructure.Notifications;

/// <summary>
/// Stores a job for each order event that sends email, so the request that raised it never waits on SMTP and the e-mail
/// is still sent if the instance restarts before a worker gets to it.
/// </summary>
public sealed class EmailOrderEvents(IServiceScopeFactory scopes) : IOrderEvents
{
    public async ValueTask PublishAsync(OrderEvent orderEvent, CancellationToken ct)
    {
        if (EmailTopics.For(orderEvent) is null)
        {
            return;
        }

        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IJobStore>().EnqueueAsync(
            new NewJob(
                JobKind.EmailNotification, null, System.Text.Json.JsonSerializer.Serialize(orderEvent), CreatedBy: orderEvent.UserId, MaxAttempts: 1),
            ct);
    }
}

/// <summary>Forwards each event to every sink (live stream and email queue).</summary>
public sealed class CompositeOrderEvents(IEnumerable<IOrderEvents> sinks) : IOrderEvents
{
    private readonly IOrderEvents[] _sinks = [.. sinks];

    public async ValueTask PublishAsync(OrderEvent orderEvent, CancellationToken ct)
    {
        foreach (var sink in _sinks)
        {
            await sink.PublishAsync(orderEvent, ct);
        }
    }
}

using System.Threading.Channels;
using Stockline.Application.Notifications;

namespace Stockline.Infrastructure.Notifications;

/// <summary>
/// Queues the order events that send email so the request that raised them never waits on SMTP.
/// A worker drains <see cref="Reader"/>.
/// </summary>
public sealed class EmailOrderEvents : IOrderEvents
{
    private readonly Channel<OrderEvent> _channel = Channel.CreateBounded<OrderEvent>(
        new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    public ChannelReader<OrderEvent> Reader => _channel.Reader;

    public ValueTask PublishAsync(OrderEvent orderEvent, CancellationToken ct)
    {
        if (EmailTopics.For(orderEvent) is not null)
        {
            _channel.Writer.TryWrite(orderEvent);
        }

        return ValueTask.CompletedTask;
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

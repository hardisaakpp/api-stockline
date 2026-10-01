using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Stockline.Application.Notifications;

namespace Stockline.Infrastructure.Notifications;

public sealed class OrderEventHub : IOrderEvents
{
    private readonly ConcurrentDictionary<Guid, Channel<OrderEvent>> _subscribers = new();

    public int SubscriberCount => _subscribers.Count;

    public ValueTask PublishAsync(OrderEvent orderEvent, CancellationToken ct)
    {
        foreach (var channel in _subscribers.Values)
        {
            channel.Writer.TryWrite(orderEvent);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Registers the subscriber immediately, so no event published after this call is missed.</summary>
    public EventSubscription Subscribe()
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<OrderEvent>(
            new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        _subscribers[id] = channel;
        return new EventSubscription(channel.Reader, () => _subscribers.TryRemove(id, out _));
    }
}

public sealed class EventSubscription(ChannelReader<OrderEvent> reader, Action onClosed)
{
    public async IAsyncEnumerable<OrderEvent> ReadAllAsync(
        Func<OrderEvent, bool>? filter = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        try
        {
            await foreach (var orderEvent in reader.ReadAllAsync(ct))
            {
                if (filter is null || filter(orderEvent))
                {
                    yield return orderEvent;
                }
            }
        }
        finally
        {
            onClosed();
        }
    }
}

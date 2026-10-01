using System.Text.Json;
using Stockline.Application.Notifications;
using Stockline.Infrastructure.Notifications;

namespace Stockline.Api.Orders;

/// <summary>
/// Server-sent events stream that opens immediately (headers plus a first comment) and sends periodic keep-alive
/// comments, which <c>TypedResults.ServerSentEvents</c> does not do until the first real event.
/// </summary>
public sealed class OrderEventStreamResult(
    EventSubscription subscription, Func<OrderEvent, bool>? filter, TimeSpan keepAlive) : IResult
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        var ct = httpContext.RequestAborted;
        var response = httpContext.Response;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";
        await response.StartAsync(ct);
        await WriteAsync(response, ": connected\n\n", ct);

        try
        {
            await using var events = subscription.ReadAllAsync(filter, ct).GetAsyncEnumerator(ct);
            var next = events.MoveNextAsync().AsTask();
            while (true)
            {
                var finished = await Task.WhenAny(next, Task.Delay(keepAlive, ct));
                if (finished != next)
                {
                    await WriteAsync(response, ": keep-alive\n\n", ct);
                    continue;
                }

                if (!await next)
                {
                    break;
                }

                await WriteAsync(response, $"event: order.updated\ndata: {JsonSerializer.Serialize(events.Current, Json)}\n\n", ct);
                next = events.MoveNextAsync().AsTask();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task WriteAsync(HttpResponse response, string text, CancellationToken ct)
    {
        await response.WriteAsync(text, ct);
        await response.Body.FlushAsync(ct);
    }
}

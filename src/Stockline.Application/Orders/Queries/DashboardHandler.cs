using Stockline.Domain.Orders;

namespace Stockline.Application.Orders.Queries;

public sealed class DashboardHandler(IOrderQueries queries, TimeProvider clock)
{
    private static readonly OrderStatus[] AllStatuses = Enum.GetValues<OrderStatus>();

    public Task<DashboardSummary> HandleAsync(ActorRole actor, CancellationToken ct)
    {
        var actionable = AllStatuses.Where(s => OrderStateMachine.AvailableActions(s, actor).Count > 0).ToList();
        var terminal = AllStatuses.Where(OrderStateMachine.IsTerminal).ToList();
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        return queries.DashboardAsync(new DashboardCriteria(today, actionable, terminal), ct);
    }
}

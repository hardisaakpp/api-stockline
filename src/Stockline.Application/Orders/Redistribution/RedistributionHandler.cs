using Stockline.Application.Notifications;
using Stockline.Domain.Orders;

namespace Stockline.Application.Orders.Redistribution;

public sealed class RedistributionHandler(IOrderRepository orders, TimeProvider clock, IOrderEvents? events = null)
{
    public async Task<CommandResult> SaveDraftAsync(
        Guid orderId, IReadOnlyList<PlanLine> plan, ActorRole actor, CancellationToken ct)
    {
        if (actor != ActorRole.Buyer)
        {
            return new CommandResult(CommandOutcome.Forbidden, Message: "Only the buyer can redistribute.");
        }

        var order = await orders.GetForUpdateAsync(orderId, ct);
        if (order is null)
        {
            return new CommandResult(CommandOutcome.NotFound);
        }

        var result = order.SaveRedistributionDraft(plan);
        if (result.Failure != TransitionFailure.None)
        {
            return new CommandResult(CommandOutcome.InvalidState, Message: $"The order is {order.Status}.");
        }

        if (result.Issues.Count > 0)
        {
            return new CommandResult(CommandOutcome.Invalid, Issues: result.Issues);
        }

        return await SaveAsync(order, ct);
    }

    public async Task<CommandResult> ConfirmAsync(
        Guid orderId, IReadOnlyList<PlanLine> plan, ActorRole actor, string? userId, CancellationToken ct)
    {
        var order = await orders.GetForUpdateAsync(orderId, ct);
        if (order is null)
        {
            return new CommandResult(CommandOutcome.NotFound);
        }

        var now = clock.GetUtcNow();
        var result = order.ConfirmRedistribution(plan, actor, userId, now);
        if (result.Failure != TransitionFailure.None)
        {
            return new CommandResult(
                result.Failure == TransitionFailure.InvalidStatus ? CommandOutcome.InvalidState : CommandOutcome.Forbidden,
                Message: $"{actor} cannot confirm the redistribution of an order in {order.Status}.");
        }

        if (result.Issues.Count > 0)
        {
            return new CommandResult(CommandOutcome.Invalid, Issues: result.Issues, Message: "The redistribution is not valid.");
        }

        var saved = await SaveAsync(order, ct);
        if (saved.Outcome == CommandOutcome.Succeeded)
        {
            await events.PublishAsync(order, nameof(OrderAction.ConfirmRedistribution), userId, now, ct);
        }

        return saved;
    }

    private async Task<CommandResult> SaveAsync(Order order, CancellationToken ct)
    {
        try
        {
            await orders.SaveChangesAsync(ct);
        }
        catch (OrderConcurrencyException)
        {
            return new CommandResult(CommandOutcome.Conflict, Message: "The order was modified by someone else.");
        }

        return new CommandResult(CommandOutcome.Succeeded, order);
    }
}

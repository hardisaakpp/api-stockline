using Stockline.Application.Notifications;
using Stockline.Domain;
using Stockline.Domain.Orders;

namespace Stockline.Application.Orders.Supplier;

public sealed class EditOrderLineHandler(IOrderRepository orders, TimeProvider clock)
{
    public Task<CommandResult> ConfirmQuantityAsync(
        Guid orderId, string productCode, decimal quantity, ActorRole actor, string? userId, CancellationToken ct) =>
        EditAsync(orderId, actor, userId, (o, by) => o.ConfirmLineQuantity(productCode, quantity, by), ct);

    public Task<CommandResult> ChangeCostAsync(
        Guid orderId, string productCode, decimal? cost, ActorRole actor, string? userId, CancellationToken ct) =>
        // Production never sees costs, so it cannot edit them blind.
        !OrderVisibility.CanSeeCosts(actor)
            ? Task.FromResult(new CommandResult(CommandOutcome.Forbidden, Message: "Only the supplier can change costs."))
            : EditAsync(orderId, actor, userId, (o, by) => o.ChangeLineCost(productCode, cost, by), ct);

    public Task<CommandResult> ChangeCommentAsync(
        Guid orderId, string productCode, string? comment, ActorRole actor, string? userId, CancellationToken ct) =>
        EditAsync(orderId, actor, userId, (o, by) =>
        {
            o.ChangeLineComment(productCode, comment, by);
            return [];
        }, ct);

    private async Task<CommandResult> EditAsync(
        Guid orderId,
        ActorRole actor,
        string? userId,
        Func<Order, EditContext, IReadOnlyList<ValidationIssue>> edit,
        CancellationToken ct)
    {
        if (actor is not (ActorRole.Supplier or ActorRole.SupplierProduction))
        {
            return new CommandResult(CommandOutcome.Forbidden, Message: "Only the supplier can edit the order lines.");
        }

        var order = await orders.GetForUpdateAsync(orderId, ct);
        if (order is null)
        {
            return new CommandResult(CommandOutcome.NotFound);
        }

        IReadOnlyList<ValidationIssue> warnings;
        try
        {
            warnings = edit(order, new EditContext(actor, userId, clock.GetUtcNow()));
            await orders.SaveChangesAsync(ct);
        }
        catch (InvalidStateException ex)
        {
            return new CommandResult(CommandOutcome.InvalidState, Message: ex.Message);
        }
        catch (DomainException ex)
        {
            return new CommandResult(CommandOutcome.Invalid, Message: ex.Message);
        }
        catch (OrderConcurrencyException)
        {
            return new CommandResult(CommandOutcome.Conflict, Message: "The order was modified by someone else.");
        }

        return new CommandResult(CommandOutcome.Succeeded, order, Issues: warnings);
    }
}

public sealed record ChildOrderRequest(DateOnly DeliveryDate, IReadOnlyDictionary<string, decimal> Portions);

public sealed record ConfirmBySupplierRequest(bool AcknowledgeDifference, IReadOnlyList<ChildOrderRequest> Children);

public sealed class ConfirmBySupplierHandler(IOrderRepository orders, TimeProvider clock, IOrderEvents? events = null)
{
    private const decimal SplitTolerance = 0.001m;

    public async Task<CommandResult> HandleAsync(
        Guid orderId, ConfirmBySupplierRequest request, ActorRole actor, string? userId, CancellationToken ct)
    {
        var order = await orders.GetForUpdateAsync(orderId, ct);
        if (order is null)
        {
            return new CommandResult(CommandOutcome.NotFound);
        }

        var transition = OrderStateMachine.Apply(order.Status, OrderAction.ConfirmBySupplier, actor);
        if (!transition.Succeeded)
        {
            return new CommandResult(
                transition.Failure == TransitionFailure.InvalidStatus ? CommandOutcome.InvalidState : CommandOutcome.Forbidden,
                Message: $"{actor} cannot confirm an order in {order.Status}.");
        }

        var difference = order.ProductionDifference();
        var issues = ValidateDifferenceHandling(difference, request);
        if (issues.Count > 0)
        {
            return new CommandResult(CommandOutcome.Invalid, Issues: issues, Message: "The production difference is not resolved.");
        }

        var now = clock.GetUtcNow();
        order.Apply(OrderAction.ConfirmBySupplier, actor, userId, now);

        var created = await CreateChildrenAsync(order, request, userId, now, ct);
        try
        {
            await orders.SaveChangesAsync(ct);
        }
        catch (OrderConcurrencyException)
        {
            return new CommandResult(CommandOutcome.Conflict, Message: "The order was modified by someone else.");
        }

        await events.PublishAsync(order, nameof(OrderAction.ConfirmBySupplier), userId, now, ct);
        foreach (var child in created)
        {
            await events.PublishAsync(child, "ChildOrderCreated", userId, now, ct);
        }

        return new CommandResult(CommandOutcome.Succeeded, order, CreatedOrders: created);
    }

    private async Task<List<Order>> CreateChildrenAsync(
        Order parent, ConfirmBySupplierRequest request, string? createdBy, DateTimeOffset now, CancellationToken ct)
    {
        var created = new List<Order>();
        var sequence = await orders.CountChildrenAsync(parent.Id, ct);
        foreach (var child in request.Children)
        {
            sequence++;
            var childOrder = parent.CreateChild(
                Guid.CreateVersion7(), sequence, DateOnly.FromDateTime(now.UtcDateTime), child.DeliveryDate, child.Portions,
                createdBy, now);
            orders.Stage(childOrder);
            created.Add(childOrder);
        }

        return created;
    }

    private static List<ValidationIssue> ValidateDifferenceHandling(
        IReadOnlyList<LineDifference> difference, ConfirmBySupplierRequest request)
    {
        var issues = new List<ValidationIssue>();
        if (request.Children.Count == 0)
        {
            if (difference.Count > 0 && !request.AcknowledgeDifference)
            {
                issues.AddRange(difference.Select(d => new ValidationIssue(
                    "DIFFERENCE_NOT_ACKNOWLEDGED",
                    $"{d.Difference} units of '{d.ProductCode}' will not be delivered; acknowledge it or create child orders.",
                    d.ProductCode)));
            }

            return issues;
        }

        if (difference.Count == 0)
        {
            issues.Add(new ValidationIssue("NO_DIFFERENCE", "There is no production difference to split into child orders."));
            return issues;
        }

        foreach (var line in difference)
        {
            var assigned = request.Children.Sum(c => c.Portions.GetValueOrDefault(line.ProductCode));
            if (Math.Abs(assigned - line.Difference) > SplitTolerance)
            {
                issues.Add(new ValidationIssue(
                    "INVALID_SPLIT",
                    $"The child orders must cover exactly {line.Difference} units of '{line.ProductCode}' (assigned {assigned}).",
                    line.ProductCode));
            }
        }

        foreach (var product in request.Children.SelectMany(c => c.Portions.Keys)
                     .Where(k => difference.All(d => !d.ProductCode.Equals(k, StringComparison.OrdinalIgnoreCase)))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            issues.Add(new ValidationIssue("INVALID_SPLIT", $"'{product}' has no production difference.", product));
        }

        return issues;
    }
}

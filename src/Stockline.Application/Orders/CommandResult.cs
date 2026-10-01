using Stockline.Domain.Orders;

namespace Stockline.Application.Orders;

public enum CommandOutcome
{
    Succeeded,
    NotFound,
    Forbidden,
    InvalidState,
    Invalid,
    Conflict,
}

public sealed record CommandResult(
    CommandOutcome Outcome,
    Order? Order = null,
    string? Message = null,
    IReadOnlyList<ValidationIssue>? Issues = null,
    IReadOnlyList<Order>? CreatedOrders = null);

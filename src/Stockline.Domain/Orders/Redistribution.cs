namespace Stockline.Domain.Orders;

public sealed record PlanAllocation(string LocationCode, decimal Quantity);

public sealed record PlanLine(string ProductCode, IReadOnlyList<PlanAllocation> Allocations);

public sealed record DraftAllocation(string ProductCode, string LocationCode, decimal Quantity);

public sealed record ValidationIssue(string Code, string Message, string? ProductCode = null, string? LocationCode = null);

public sealed record LineDifference(string ProductCode, string? Description, decimal Requested, decimal Confirmed)
{
    public decimal Difference => Requested - Confirmed;
}

public sealed record RedistributionResult(TransitionFailure Failure, IReadOnlyList<ValidationIssue> Issues)
{
    public bool Succeeded => Failure == TransitionFailure.None && Issues.Count == 0;
}

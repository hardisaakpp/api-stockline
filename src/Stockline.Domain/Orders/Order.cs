namespace Stockline.Domain.Orders;

public sealed class Order
{
    private const decimal TotalTolerance = 0.001m;

    private readonly List<OrderLine> _lines;
    private readonly List<StatusChange> _history = [];
    private readonly List<OrderLocation> _locations;
    private readonly List<DraftAllocation> _redistributionDraft = [];
    private readonly List<AuditEntry> _audit = [];

    /// <summary>Below this share of the requested quantity a supplier confirmation deserves a warning.</summary>
    private const decimal LowConfirmationShare = 0.8m;

    /// <summary>Cost increases over the imported cost above this share deserve a warning.</summary>
    private const decimal HighCostIncrease = 0.10m;

    // Parameterless constructor required by EF Core materialization.
    private Order()
    {
        Header = null!;
        _lines = [];
        _locations = [];
    }

    private Order(
        Guid id,
        OrderHeader header,
        List<OrderLine> lines,
        List<OrderLocation> locations,
        OrderStatus status,
        Guid? parentOrderId,
        string? createdBy,
        DateTimeOffset? createdAt)
    {
        Id = id;
        Header = header;
        _lines = lines;
        _locations = locations;
        Status = status;
        ParentOrderId = parentOrderId;
        CreatedBy = createdBy;
        CreatedAt = createdAt;
        ModifiedBy = createdBy;
        ModifiedAt = createdAt;
    }

    public Guid Id { get; }

    public string? CreatedBy { get; private set; }

    public DateTimeOffset? CreatedAt { get; private set; }

    public string? ModifiedBy { get; private set; }

    public DateTimeOffset? ModifiedAt { get; private set; }

    public IReadOnlyList<AuditEntry> Audit => _audit;

    public Guid? ParentOrderId { get; }

    public OrderHeader Header { get; }

    public OrderStatus Status { get; private set; }

    public int RedistributionVersion { get; private set; }

    public IReadOnlyList<OrderLine> Lines => _lines;

    public IReadOnlyList<OrderLocation> Locations => _locations;

    public IReadOnlyList<StatusChange> History => _history;

    public IReadOnlyList<DraftAllocation> RedistributionDraft => _redistributionDraft;

    /// <summary>Only orders that have not been worked on by the supplier can be thrown away.</summary>
    public bool CanBeDiscarded => Status is OrderStatus.Draft or OrderStatus.Submitted;

    public bool IsSupplierEditable => Status is OrderStatus.UnderReview or OrderStatus.ProductionConfirmed;

    public static Order Create(
        Guid id,
        OrderHeader header,
        IEnumerable<OrderLine> lines,
        IEnumerable<OrderLocation>? locations = null,
        string? createdBy = null,
        DateTimeOffset? createdAt = null)
    {
        if (string.IsNullOrWhiteSpace(header.Code))
        {
            throw new DomainException("An order needs a code.");
        }

        if (string.IsNullOrWhiteSpace(header.SupplierName))
        {
            throw new DomainException("An order needs a supplier.");
        }

        var list = lines.ToList();
        if (list.Count == 0)
        {
            throw new DomainException("An order needs at least one line.");
        }

        if (list.GroupBy(l => l.ProductCode, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
        {
            throw new DomainException("A product can only appear once per order.");
        }

        var orderLocations = (locations ?? DeriveLocations(list)).ToList();
        return new Order(
            id, header with { Code = header.Code.Trim() }, list, orderLocations, OrderStateMachine.InitialStatus, null, createdBy, createdAt);
    }

    public TransitionResult Apply(OrderAction action, ActorRole actor, string? userId, DateTimeOffset at)
    {
        var result = OrderStateMachine.Apply(Status, action, actor);
        if (result.Succeeded)
        {
            _history.Add(new StatusChange(Status, result.Status, action, actor, userId, at));
            Status = result.Status;
            Touch(userId, at);
        }

        return result;
    }

    /// <returns>Non-blocking warnings about the new value.</returns>
    public IReadOnlyList<ValidationIssue> ConfirmLineQuantity(string productCode, decimal quantity, EditContext? by = null)
    {
        EnsureSupplierEditable();
        var line = FindLine(productCode);
        var before = line.ConfirmedQuantity;
        line.ConfirmQuantity(quantity);
        Record(by, line, AuditFields.ConfirmedQuantity, before, quantity);

        return line.RequestedQuantity > 0 && quantity < line.RequestedQuantity * LowConfirmationShare
            ? [new ValidationIssue(
                "LOW_CONFIRMATION",
                $"Only {quantity} of the {line.RequestedQuantity} requested units are confirmed (under {LowConfirmationShare:P0}).",
                line.ProductCode)]
            : [];
    }

    /// <returns>Non-blocking warnings about the new value.</returns>
    public IReadOnlyList<ValidationIssue> ChangeLineCost(string productCode, decimal? cost, EditContext? by = null)
    {
        EnsureSupplierEditable();
        var line = FindLine(productCode);
        var before = line.CostWithoutTax;
        line.ChangeCost(cost);
        Record(by, line, AuditFields.CostWithoutTax, before, cost);

        return line.OriginalCostWithoutTax is > 0 and var original && cost > original * (1 + HighCostIncrease)
            ? [new ValidationIssue(
                "HIGH_COST_INCREASE",
                $"The cost rises {(cost.Value - original) / original:P1} over the imported cost of {original}.",
                line.ProductCode)]
            : [];
    }

    public void ChangeLineComment(string productCode, string? comment, EditContext? by = null)
    {
        EnsureSupplierEditable();
        var line = FindLine(productCode);
        var before = line.Comment;
        line.ChangeComment(comment);
        Record(by, line, AuditFields.Comment, before, line.Comment);
    }

    private void Record(EditContext? by, OrderLine line, string field, object? before, object? after)
    {
        if (by is null)
        {
            return;
        }

        string? Text(object? value) => value switch
        {
            decimal d => d.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => value?.ToString(),
        };
        if (Text(before) == Text(after))
        {
            return;
        }

        _audit.Add(new AuditEntry(by.At, by.UserId, by.Actor, line.ProductCode, field, Text(before), Text(after)));
        Touch(by.UserId, by.At);
    }

    private void Touch(string? userId, DateTimeOffset at)
    {
        ModifiedBy = userId;
        ModifiedAt = at;
    }

    public IReadOnlyList<LineDifference> ProductionDifference() =>
        _lines.Where(l => l.Difference > 0)
            .Select(l => new LineDifference(l.ProductCode, l.Description, l.RequestedQuantity, l.ConfirmedQuantity))
            .ToList();

    public Order CreateChild(
        Guid childId,
        int sequence,
        DateOnly orderDate,
        DateOnly deliveryDate,
        IReadOnlyDictionary<string, decimal> portions,
        string? createdBy = null,
        DateTimeOffset? createdAt = null)
    {
        var childLines = new List<OrderLine>();
        foreach (var (productCode, quantity) in portions.Where(p => p.Value > 0))
        {
            if (quantity != decimal.Truncate(quantity))
            {
                throw new DomainException($"Line {productCode}: child quantities must be whole units.");
            }

            childLines.Add(OrderLine.ForChild(FindLine(productCode), quantity));
        }

        if (childLines.Count == 0)
        {
            throw new DomainException("A child order needs at least one line with a quantity.");
        }

        var header = Header with
        {
            Code = $"{Header.Code}.{sequence:00}",
            OrderDate = orderDate,
            DeliveryDate = deliveryDate,
            DeliveryNote = null,
        };
        // Copies, not shared instances: EF Core owned entities belong to exactly one owner.
        return new Order(childId, header, childLines, [.. _locations.Select(l => l with { })], OrderStatus.ProductionPostponed, Id, createdBy, createdAt);
    }

    /// <param name="catalog">Locations of the master catalog; a child order may redistribute to any of them.</param>
    public RedistributionResult SaveRedistributionDraft(
        IReadOnlyList<PlanLine> plan, IReadOnlyDictionary<string, OrderLocation>? catalog = null)
    {
        if (Status is not (OrderStatus.SupplierConfirmed or OrderStatus.ReadyForRedistribution))
        {
            return new RedistributionResult(TransitionFailure.InvalidStatus, []);
        }

        var issues = ValidatePlan(plan, requireTotals: false, catalog);
        if (issues.Count == 0)
        {
            _redistributionDraft.Clear();
            _redistributionDraft.AddRange(
                plan.SelectMany(p => p.Allocations.Select(a => new DraftAllocation(p.ProductCode, a.LocationCode, a.Quantity))));
        }

        return new RedistributionResult(TransitionFailure.None, issues);
    }

    /// <param name="catalog">Locations of the master catalog; a child order may redistribute to any of them.</param>
    public RedistributionResult ConfirmRedistribution(
        IReadOnlyList<PlanLine> plan,
        ActorRole actor,
        string? userId,
        DateTimeOffset at,
        IReadOnlyDictionary<string, OrderLocation>? catalog = null)
    {
        var transition = OrderStateMachine.Apply(Status, OrderAction.ConfirmRedistribution, actor);
        if (!transition.Succeeded)
        {
            return new RedistributionResult(transition.Failure, []);
        }

        var issues = ValidatePlan(plan, requireTotals: true, catalog);
        if (issues.Count > 0)
        {
            return new RedistributionResult(TransitionFailure.None, issues);
        }

        foreach (var planLine in plan)
        {
            var allocations = planLine.Allocations
                .Select(a =>
                {
                    var location = ResolveLocation(a.LocationCode, catalog)
                                   ?? throw new DomainException($"Unknown location {a.LocationCode}.");
                    EnsureLocation(location);
                    return new Allocation(location.Code, location.Name, location.Kind, a.Quantity);
                })
                .ToList();
            FindLine(planLine.ProductCode).ReplaceAllocations(allocations);
        }

        RedistributionVersion++;
        _redistributionDraft.Clear();
        Apply(OrderAction.ConfirmRedistribution, actor, userId, at);
        return new RedistributionResult(TransitionFailure.None, []);
    }

    private List<ValidationIssue> ValidatePlan(
        IReadOnlyList<PlanLine> plan, bool requireTotals, IReadOnlyDictionary<string, OrderLocation>? catalog)
    {
        var issues = new List<ValidationIssue>();
        var planned = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        foreach (var planLine in plan)
        {
            if (!_lines.Any(l => string.Equals(l.ProductCode, planLine.ProductCode, StringComparison.OrdinalIgnoreCase)))
            {
                issues.Add(new ValidationIssue("UNKNOWN_PRODUCT", $"Product '{planLine.ProductCode}' is not in the order.", planLine.ProductCode));
                continue;
            }

            if (!planned.TryAdd(planLine.ProductCode, planLine.Allocations.Sum(a => a.Quantity)))
            {
                issues.Add(new ValidationIssue("DUPLICATE_PRODUCT", $"Product '{planLine.ProductCode}' appears twice in the plan.", planLine.ProductCode));
            }

            ValidatePlanAllocations(planLine, catalog, issues);
        }

        if (requireTotals)
        {
            AddTotalIssues(planned, issues);
        }

        return issues;
    }

    private void ValidatePlanAllocations(
        PlanLine planLine, IReadOnlyDictionary<string, OrderLocation>? catalog, List<ValidationIssue> issues)
    {
        foreach (var allocation in planLine.Allocations)
        {
            if (allocation.Quantity < 0)
            {
                issues.Add(new ValidationIssue("NEGATIVE_QUANTITY", "Quantities cannot be negative.", planLine.ProductCode, allocation.LocationCode));
            }

            if (ResolveLocation(allocation.LocationCode, catalog) is null)
            {
                issues.Add(new ValidationIssue("UNKNOWN_LOCATION", $"Location '{allocation.LocationCode}' does not belong to the order.", planLine.ProductCode, allocation.LocationCode));
            }
        }

        foreach (var duplicate in planLine.Allocations
                     .GroupBy(a => a.LocationCode, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
        {
            issues.Add(new ValidationIssue("DUPLICATE_LOCATION", $"Location '{duplicate.Key}' appears twice.", planLine.ProductCode, duplicate.Key));
        }
    }

    private void AddTotalIssues(Dictionary<string, decimal> planned, List<ValidationIssue> issues)
    {
        foreach (var line in _lines)
        {
            var total = planned.TryGetValue(line.ProductCode, out var plannedTotal) ? plannedTotal : line.TotalQuantity;
            if (Math.Abs(total - line.ConfirmedQuantity) > TotalTolerance)
            {
                issues.Add(new ValidationIssue(
                    "TOTAL_MISMATCH",
                    $"Allocated {total} but the confirmed quantity is {line.ConfirmedQuantity}.",
                    line.ProductCode));
            }
        }
    }

    /// <summary>An order location, or — for child orders only — any location of the master catalog (then added to the order).</summary>
    private OrderLocation? ResolveLocation(string code, IReadOnlyDictionary<string, OrderLocation>? catalog)
    {
        if (LocationOf(code) is { } own)
        {
            return own;
        }

        if (ParentOrderId is null || catalog is null || !catalog.TryGetValue(code, out var extra))
        {
            return null;
        }

        return extra;
    }

    private void EnsureLocation(OrderLocation location)
    {
        if (LocationOf(location.Code) is null)
        {
            _locations.Add(location);
        }
    }

    private OrderLocation? LocationOf(string code) =>
        _locations.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));

    private OrderLine FindLine(string productCode) =>
        _lines.FirstOrDefault(l => string.Equals(l.ProductCode, productCode, StringComparison.OrdinalIgnoreCase))
        ?? throw new DomainException($"Product '{productCode}' is not in the order.");

    private void EnsureSupplierEditable()
    {
        if (!IsSupplierEditable)
        {
            throw new InvalidStateException($"The order cannot be edited by the supplier while it is {Status}.");
        }
    }

    private static IEnumerable<OrderLocation> DeriveLocations(IEnumerable<OrderLine> lines) =>
        lines.SelectMany(l => l.Allocations)
            .GroupBy(a => a.LocationCode, StringComparer.OrdinalIgnoreCase)
            .Select(g => new OrderLocation(g.Key, g.First().LocationName, g.First().Kind, null));
}

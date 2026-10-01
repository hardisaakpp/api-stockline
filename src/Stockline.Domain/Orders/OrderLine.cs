namespace Stockline.Domain.Orders;

public sealed record Allocation(string LocationCode, string? LocationName, LocationKind Kind, decimal Quantity);

public sealed class OrderLine
{
    public const decimal MaxCost = 1_000_000m;
    public const int MaxCommentLength = 500;

    private readonly List<Allocation> _allocations;

    // Parameterless constructor required by EF Core materialization.
    private OrderLine()
    {
        ProductCode = null!;
        _allocations = [];
    }

    public OrderLine(
        string productCode,
        string? barcode,
        string? description,
        Pack pack,
        decimal? costWithoutTax,
        decimal discount,
        decimal? retailPrice,
        IEnumerable<Allocation> allocations)
    {
        if (string.IsNullOrWhiteSpace(productCode))
        {
            throw new DomainException("A line needs a product code.");
        }

        if (costWithoutTax < 0 || retailPrice < 0)
        {
            throw new DomainException($"Line {productCode}: prices cannot be negative.");
        }

        if (discount is < 0 or > 1)
        {
            throw new DomainException($"Line {productCode}: discount must be between 0 and 1.");
        }

        _allocations = allocations.ToList();
        if (_allocations.Count == 0)
        {
            throw new DomainException($"Line {productCode}: at least one allocation is required.");
        }

        ValidateAllocations(productCode, _allocations);

        ProductCode = productCode.Trim();
        Barcode = barcode;
        Description = description;
        Pack = pack;
        CostWithoutTax = costWithoutTax;
        OriginalCostWithoutTax = costWithoutTax;
        Discount = discount;
        RetailPrice = retailPrice;
        RequestedQuantity = _allocations.Sum(a => a.Quantity);
        ConfirmedQuantity = RequestedQuantity;
    }

    private OrderLine(OrderLine parent, decimal confirmedQuantity)
    {
        _allocations = [];
        ProductCode = parent.ProductCode;
        Barcode = parent.Barcode;
        Description = parent.Description;
        Pack = parent.Pack;
        CostWithoutTax = parent.CostWithoutTax;
        OriginalCostWithoutTax = parent.OriginalCostWithoutTax;
        Discount = parent.Discount;
        RetailPrice = parent.RetailPrice;
        RequestedQuantity = parent.RequestedQuantity;
        ConfirmedQuantity = confirmedQuantity;
    }

    public string ProductCode { get; }

    public string? Barcode { get; }

    public string? Description { get; }

    public Pack Pack { get; }

    public decimal? CostWithoutTax { get; private set; }

    /// <summary>The cost as imported; supplier changes are compared against it.</summary>
    public decimal? OriginalCostWithoutTax { get; private set; }

    public string? Comment { get; private set; }

    public decimal Discount { get; }

    public decimal? RetailPrice { get; }

    public decimal RequestedQuantity { get; private set; }

    public decimal ConfirmedQuantity { get; private set; }

    public IReadOnlyList<Allocation> Allocations => _allocations;

    public decimal? UnitCost => CostWithoutTax is { } cost ? Pack.ToUnitCost(cost) : null;

    public decimal? Margin => MarginCalculator.Calculate(UnitCost, RetailPrice);

    public decimal TotalQuantity => _allocations.Sum(a => a.Quantity);

    public decimal StoreQuantity => _allocations.Where(a => a.Kind == LocationKind.Store).Sum(a => a.Quantity);

    public decimal Difference => Math.Max(0, RequestedQuantity - ConfirmedQuantity);

    public static OrderLine ForChild(OrderLine parent, decimal quantity) => new(parent, quantity);

    public void ConfirmQuantity(decimal quantity)
    {
        if (quantity < 0 || quantity > RequestedQuantity)
        {
            throw new DomainException(
                $"Line {ProductCode}: confirmed quantity must be between 0 and the requested {RequestedQuantity}.");
        }

        ConfirmedQuantity = quantity;
    }

    public void ChangeCost(decimal? cost)
    {
        if (cost < 0)
        {
            throw new DomainException($"Line {ProductCode}: cost cannot be negative.");
        }

        if (cost > MaxCost)
        {
            throw new DomainException($"Line {ProductCode}: cost cannot exceed {MaxCost:N0}.");
        }

        CostWithoutTax = cost;
    }

    public void ChangeComment(string? comment)
    {
        var text = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        if (text?.Length > MaxCommentLength)
        {
            throw new DomainException($"Line {ProductCode}: the comment cannot exceed {MaxCommentLength} characters.");
        }

        Comment = text;
    }

    public void ReplaceAllocations(IEnumerable<Allocation> allocations)
    {
        var list = allocations.Where(a => a.Quantity > 0).ToList();
        ValidateAllocations(ProductCode, list);
        _allocations.Clear();
        _allocations.AddRange(list);
    }

    private static void ValidateAllocations(string productCode, List<Allocation> allocations)
    {
        if (allocations.Any(a => a.Quantity <= 0))
        {
            throw new DomainException($"Line {productCode}: allocated quantities must be positive.");
        }

        if (allocations.GroupBy(a => a.LocationCode, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
        {
            throw new DomainException($"Line {productCode}: a location can only be allocated once.");
        }
    }
}

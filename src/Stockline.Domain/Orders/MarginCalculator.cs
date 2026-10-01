namespace Stockline.Domain.Orders;

public static class MarginCalculator
{
    /// <summary>Retail prices include a 15% sales tax that is removed before comparing against cost.</summary>
    public const decimal SalesTaxFactor = 1.15m;

    /// <summary>
    /// Profit margin as a fraction (0.35 = 35%): <c>1 - unitCost / (retailPrice / 1.15)</c>.
    /// Null when the cost is unknown or the retail price is missing or zero.
    /// </summary>
    public static decimal? Calculate(decimal? unitCost, decimal? retailPrice)
    {
        if (unitCost is not { } cost || retailPrice is not { } retail || retail == 0)
        {
            return null;
        }

        return 1 - cost / (retail / SalesTaxFactor);
    }
}

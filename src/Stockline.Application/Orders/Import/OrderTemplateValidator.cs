using Stockline.Domain.Orders;

namespace Stockline.Application.Orders.Import;

/// <summary>Discounts arrive either as a fraction (0.2) or as a percentage (20); both mean 20%.</summary>
public static class DiscountRule
{
    /// <returns>The discount as a fraction between 0 and 1, or null when the value is outside 0–100%.</returns>
    public static decimal? ToFraction(decimal? raw) => raw switch
    {
        null => 0m,
        >= 0 and <= 1 => raw,
        > 1 and <= 100 => raw / 100m,
        _ => null,
    };
}

public sealed partial class OrderTemplateValidator(TimeProvider? clock = null)
{
    private const decimal QuantityTolerance = 0.0001m;
    private const decimal CostTolerance = 0.01m;
    private const int MaxOrderCodeLength = 100;
    private const int MaxMessageLength = 100;
    private const int MaxProductCodeLength = 50;
    private const decimal HighDiscount = 0.5m;
    private const decimal LowPriceMargin = 0.9m;
    private const decimal MaxQuantityPerLocation = 10_000m;
    private const decimal MaxQuantityPerProduct = 100_000m;

    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z0-9.\-\s]+$")]
    private static partial System.Text.RegularExpressions.Regex ProductCodePattern();

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public IReadOnlyList<ImportIssue> Validate(OrderTemplate template, LocationCatalog catalog)
    {
        var issues = new List<ImportIssue>(template.ReadIssues);

        ValidateHeader(template.Header, DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime), issues);

        if (template.Lines.Count == 0 || template.Lines.All(l => l.Quantities.Count == 0))
        {
            issues.Add(Error("NO_QUANTITIES", "The template must contain at least one product with quantities."));
        }

        foreach (var line in template.Lines)
        {
            ValidateLine(line, catalog, issues);
        }

        var usedCodes = template.Lines.SelectMany(l => l.Quantities).Select(q => q.LocationCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var column in template.Locations ?? [])
        {
            if (!usedCodes.Contains(column.Code) && !catalog.TryGetKind(column.Code, out _))
            {
                issues.Add(Error("UNKNOWN_LOCATION", $"Location '{column.Code}' is not in the catalog.", field: column.Code));
            }
        }

        foreach (var duplicate in template.Lines
                     .GroupBy(l => l.ProductCode, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
        {
            issues.Add(Error(
                "DUPLICATE_PRODUCT",
                $"Product '{duplicate.Key}' appears more than once.",
                duplicate.Last().Row,
                "Codigo Producto"));
        }

        return issues;
    }

    private static void ValidateHeader(OrderTemplateHeader header, DateOnly today, List<ImportIssue> issues)
    {
        Require(header.OrderCode, "CODIGO PEDIDO", issues);
        Require(header.SupplierName, "NOMBRE PROVEEDOR", issues);
        if (header.OrderDate is null)
        {
            issues.Add(Error("REQUIRED_FIELD", "FECHA DE PEDIDO is required.", field: "FECHA DE PEDIDO"));
        }
        else if (header.OrderDate < today.AddYears(-1))
        {
            issues.Add(Warning("OLD_ORDER_DATE", $"The order date {header.OrderDate:yyyy-MM-dd} is more than a year old.", field: "FECHA DE PEDIDO"));
        }

        if (header.OrderCode?.Trim().Length > MaxOrderCodeLength)
        {
            issues.Add(Error("FIELD_TOO_LONG", $"CODIGO PEDIDO cannot exceed {MaxOrderCodeLength} characters.", field: "CODIGO PEDIDO"));
        }

        if (header.Message?.Trim().Length > MaxMessageLength)
        {
            issues.Add(Error("FIELD_TOO_LONG", $"MENSAJE cannot exceed {MaxMessageLength} characters.", field: "MENSAJE"));
        }

        if (header.DeliveryDate < today)
        {
            issues.Add(Error("PAST_DELIVERY_DATE", $"The delivery date {header.DeliveryDate:yyyy-MM-dd} is in the past.", field: "FECHA DE ENTREGA"));
        }
    }

    private static void Require(string? value, string field, List<ImportIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            issues.Add(Error("REQUIRED_FIELD", $"{field} is required.", field: field));
        }
    }

    private static void ValidateLine(OrderTemplateLine line, LocationCatalog catalog, List<ImportIssue> issues)
    {
        ValidateProductCode(line, issues);
        if (line.Quantities.Count == 0)
        {
            issues.Add(new ImportIssue(
                IssueSeverity.Warning, "LINE_WITHOUT_QUANTITIES", "The line has no quantities and will be ignored.", line.Row));
        }

        ValidatePrices(line, issues);
        ValidatePackAndCost(line, issues);
        ValidateLocations(line, catalog, issues);
        ValidateTotals(line, catalog, issues);
    }

    private static void ValidateProductCode(OrderTemplateLine line, List<ImportIssue> issues)
    {
        var code = line.ProductCode.Trim();
        if (code.Length > MaxProductCodeLength || !ProductCodePattern().IsMatch(code))
        {
            issues.Add(Error(
                "INVALID_PRODUCT_CODE",
                $"Product code '{code}' must be 1-{MaxProductCodeLength} characters: letters, digits, dots, hyphens and spaces.",
                line.Row,
                "Codigo Producto"));
        }
    }

    private static void ValidatePrices(OrderTemplateLine line, List<ImportIssue> issues)
    {
        if (line.CostWithoutTax < 0 || line.RetailPrice < 0)
        {
            issues.Add(Error("NEGATIVE_PRICE", "Prices cannot be negative.", line.Row));
        }

        if (line.CostWithoutTax == 0)
        {
            issues.Add(Warning("ZERO_COST", "The cost is zero; check that it is correct.", line.Row, "Costo sin IVA"));
        }

        var discount = DiscountRule.ToFraction(line.Discount);
        if (discount is null)
        {
            issues.Add(Error("INVALID_DISCOUNT", "Discount must be between 0 and 100% (0.2 or 20 for 20%).", line.Row, "Descuento"));
        }
        else if (discount > HighDiscount)
        {
            issues.Add(Warning("HIGH_DISCOUNT", $"The discount is very high ({discount:P0}); check that it is correct.", line.Row, "Descuento"));
        }

        ValidateRetailPrice(line, discount ?? 0m, issues);
    }

    private static void ValidateRetailPrice(OrderTemplateLine line, decimal discount, List<ImportIssue> issues)
    {
        if (line.RetailPrice == 0)
        {
            issues.Add(Warning("ZERO_RETAIL_PRICE", "The retail price is zero; check that it is correct.", line.Row, "PVP"));
        }

        if (line.RetailPrice is > 0 and { } retail && line.CostWithoutTax is { } cost
            && retail < cost * MarginCalculator.SalesTaxFactor * (1 - discount) * LowPriceMargin)
        {
            issues.Add(Warning(
                "LOW_RETAIL_PRICE",
                $"The retail price {retail} is below the cost with tax and discount; check the figures.",
                line.Row,
                "PVP"));
        }
    }

    private static void ValidatePackAndCost(OrderTemplateLine line, List<ImportIssue> issues)
    {
        if (!Pack.TryParse(line.Pack, out var pack))
        {
            issues.Add(Error("INVALID_PACK", $"Pack '{line.Pack}' is not valid (expected e.g. X6).", line.Row, "Pack"));
            return;
        }

        if (line.CostWithoutTax is { } cost && line.UnitCost is { } unitCost
            && Math.Abs(pack.ToUnitCost(cost) - unitCost) > CostTolerance)
        {
            issues.Add(Error(
                "UNIT_COST_MISMATCH",
                $"Unit cost {unitCost} does not match the expected {Math.Round(pack.ToUnitCost(cost), 4)}.",
                line.Row,
                "Costo Unitario"));
        }
    }

    private static void ValidateLocations(OrderTemplateLine line, LocationCatalog catalog, List<ImportIssue> issues)
    {
        foreach (var duplicate in line.Quantities
                     .GroupBy(q => q.LocationCode, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
        {
            issues.Add(Error("DUPLICATE_LOCATION", $"Location '{duplicate.Key}' appears in more than one column.", line.Row, duplicate.Key));
        }

        if (line.Quantities.Sum(q => q.Quantity) > MaxQuantityPerProduct)
        {
            issues.Add(Warning("LARGE_QUANTITY", $"The total quantity exceeds {MaxQuantityPerProduct:N0}; check that it is correct.", line.Row, "TOTAL"));
        }

        foreach (var quantity in line.Quantities)
        {
            if (quantity.Quantity > MaxQuantityPerLocation)
            {
                issues.Add(Warning("LARGE_QUANTITY", $"{quantity.LocationCode} exceeds {MaxQuantityPerLocation:N0} units; check that it is correct.", line.Row, quantity.LocationCode));
            }

            if (quantity.Quantity < 0)
            {
                issues.Add(Error("NEGATIVE_QUANTITY", $"Negative quantity for {quantity.LocationCode}.", line.Row, quantity.LocationCode));
            }

            if (!catalog.TryGetKind(quantity.LocationCode, out _))
            {
                issues.Add(Error("UNKNOWN_LOCATION", $"Location '{quantity.LocationCode}' is not in the catalog.", line.Row, quantity.LocationCode));
            }
        }
    }

    private static void ValidateTotals(OrderTemplateLine line, LocationCatalog catalog, List<ImportIssue> issues)
    {
        if (line.Total is { } total && Math.Abs(total - line.Quantities.Sum(q => q.Quantity)) > QuantityTolerance)
        {
            issues.Add(Error("TOTAL_MISMATCH", "TOTAL does not match the sum of all location quantities.", line.Row, "TOTAL"));
        }

        if (line.Subtotal is { } subtotal)
        {
            var stores = line.Quantities
                .Where(q => catalog.TryGetKind(q.LocationCode, out var kind) && kind == LocationKind.Store)
                .Sum(q => q.Quantity);
            if (Math.Abs(subtotal - stores) > QuantityTolerance)
            {
                issues.Add(Error("SUBTOTAL_MISMATCH", "SUBTOTAL does not match the sum of store quantities.", line.Row, "SUBTOTAL"));
            }
        }
    }

    private static ImportIssue Warning(string code, string message, int? row = null, string? field = null) =>
        new(IssueSeverity.Warning, code, message, row, field);

    private static ImportIssue Error(string code, string message, int? row = null, string? field = null) =>
        new(IssueSeverity.Error, code, message, row, field);
}

using Stockline.Domain.Orders;

namespace Stockline.Application.Orders.Import;

public sealed class OrderTemplateValidator
{
    private const decimal QuantityTolerance = 0.0001m;
    private const decimal CostTolerance = 0.01m;

    public IReadOnlyList<ImportIssue> Validate(OrderTemplate template, LocationCatalog catalog)
    {
        var issues = new List<ImportIssue>(template.ReadIssues);

        ValidateHeader(template.Header, issues);

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

    private static void ValidateHeader(OrderTemplateHeader header, List<ImportIssue> issues)
    {
        Require(header.OrderCode, "CODIGO PEDIDO", issues);
        Require(header.SupplierName, "NOMBRE PROVEEDOR", issues);
        if (header.OrderDate is null)
        {
            issues.Add(Error("REQUIRED_FIELD", "FECHA DE PEDIDO is required.", field: "FECHA DE PEDIDO"));
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

    private static void ValidatePrices(OrderTemplateLine line, List<ImportIssue> issues)
    {
        if (line.CostWithoutTax < 0 || line.RetailPrice < 0)
        {
            issues.Add(Error("NEGATIVE_PRICE", "Prices cannot be negative.", line.Row));
        }

        if (line.Discount is < 0 or > 1)
        {
            issues.Add(Error("INVALID_DISCOUNT", "Discount must be between 0 and 1 (e.g. 0.2 for 20%).", line.Row, "Descuento"));
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

        foreach (var quantity in line.Quantities)
        {
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

    private static ImportIssue Error(string code, string message, int? row = null, string? field = null) =>
        new(IssueSeverity.Error, code, message, row, field);
}

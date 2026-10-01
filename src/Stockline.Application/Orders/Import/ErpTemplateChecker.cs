using Stockline.Application.Erp;
using Stockline.Domain.Orders;

namespace Stockline.Application.Orders.Import;

/// <param name="ValidateWithErp">Check the products against the ERP's master data.</param>
/// <param name="RequireErp">When the ERP cannot be reached: fail the import (true) or only warn (false).</param>
/// <param name="Corrections">Product codes to replace before validating, e.g. after the buyer picked a suggestion.</param>
public sealed record ImportOptions(
    bool ValidateWithErp = false, bool RequireErp = false, IReadOnlyDictionary<string, string>? Corrections = null)
{
    public static ImportOptions Default { get; } = new();
}

/// <summary>Validates a template's products against the ERP: they must exist and be active; pack and retail price are compared.</summary>
public sealed class ErpTemplateChecker(IErpCatalog catalog)
{
    private const int MaxSuggestions = 5;
    private const decimal PriceTolerance = 0.01m;

    public async Task<IReadOnlyList<ImportIssue>> CheckAsync(OrderTemplate template, bool requireErp, CancellationToken ct)
    {
        var lines = template.Lines.Where(l => l.Quantities.Count > 0).ToList();
        if (lines.Count == 0)
        {
            return [];
        }

        var lookup = await catalog.GetItemsAsync([.. lines.Select(l => l.ProductCode.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)], ct);
        if (!lookup.Succeeded)
        {
            return
            [
                new ImportIssue(
                    requireErp ? IssueSeverity.Error : IssueSeverity.Warning,
                    "ERP_UNAVAILABLE",
                    $"The products could not be checked against the ERP: {lookup.Error}"),
            ];
        }

        var issues = new List<ImportIssue>();
        foreach (var line in lines)
        {
            issues.AddRange(await CheckLineAsync(line, lookup.Found, ct));
        }

        return issues;
    }

    private async Task<IEnumerable<ImportIssue>> CheckLineAsync(
        OrderTemplateLine line, IReadOnlyDictionary<string, ErpItemInfo> found, CancellationToken ct)
    {
        var code = line.ProductCode.Trim();
        if (!found.TryGetValue(code, out var item))
        {
            var suggestions = await catalog.SuggestItemsAsync(code, MaxSuggestions, ct);
            return
            [
                new ImportIssue(
                    IssueSeverity.Error, "ERP_ITEM_NOT_FOUND", $"Product '{code}' does not exist in the ERP.", line.Row, "Codigo Producto",
                    [.. suggestions.Select(s => s.Code)]),
            ];
        }

        if (!item.IsActive)
        {
            var suggestions = await catalog.SuggestItemsAsync(code, MaxSuggestions, ct);
            return
            [
                new ImportIssue(
                    IssueSeverity.Error, "ERP_ITEM_INACTIVE", $"Product '{code}' is inactive in the ERP; use an active code.", line.Row,
                    "Codigo Producto", [.. suggestions.Where(s => !s.Code.Equals(code, StringComparison.OrdinalIgnoreCase)).Select(s => s.Code)]),
            ];
        }

        return Differences(line, item);
    }

    private static IEnumerable<ImportIssue> Differences(OrderTemplateLine line, ErpItemInfo item)
    {
        if (item.Pack is not null && Pack.TryParse(item.Pack, out var erpPack) && Pack.TryParse(line.Pack, out var templatePack)
            && erpPack != templatePack)
        {
            yield return new ImportIssue(
                IssueSeverity.Warning, "ERP_PACK_MISMATCH",
                $"The template says pack {templatePack} but the ERP has {erpPack} for '{item.Code}'.", line.Row, "Pack");
        }

        if (item.RetailPrice is { } erpPrice && line.RetailPrice is { } templatePrice
            && Math.Abs(erpPrice - templatePrice) > PriceTolerance)
        {
            yield return new ImportIssue(
                IssueSeverity.Warning, "ERP_RETAIL_PRICE_MISMATCH",
                $"The template says retail price {templatePrice} but the ERP has {erpPrice} for '{item.Code}'.", line.Row, "PVP");
        }
    }
}

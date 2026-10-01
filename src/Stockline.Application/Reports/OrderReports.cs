using Stockline.Domain.Erp;
using Stockline.Domain.Orders;

namespace Stockline.Application.Reports;

public sealed record MarginRow(
    string ProductCode,
    string? Description,
    string? Barcode,
    string Pack,
    decimal? RetailPrice,
    decimal? CostWithoutTax,
    decimal? UnitCost,
    decimal? Margin,
    decimal Quantity);

public sealed record MarginReport(Order Order, IReadOnlyList<MarginRow> Rows, decimal TotalQuantity)
{
    public static MarginReport Build(Order order)
    {
        var rows = order.Lines
            .OrderBy(l => l.ProductCode, StringComparer.Ordinal)
            .Select(l => new MarginRow(
                l.ProductCode, l.Description, l.Barcode, l.Pack.ToString(), l.RetailPrice, l.CostWithoutTax,
                l.UnitCost, l.Margin, l.TotalQuantity))
            .ToList();
        return new MarginReport(order, rows, rows.Sum(r => r.Quantity));
    }
}

public sealed record DifferenceProductRow(
    string ProductCode,
    string? Description,
    decimal Requested,
    decimal Confirmed,
    decimal Allocated,
    decimal Difference,
    decimal? OriginalCost,
    decimal? ConfirmedCost,
    decimal? CostDifference);

public sealed record DifferenceLocationRow(string LocationCode, string? LocationName, LocationKind Kind, decimal Allocated);

public sealed record DifferencesReport(
    Order Order,
    IReadOnlyList<DifferenceProductRow> Products,
    IReadOnlyList<DifferenceLocationRow> Locations,
    decimal TotalRequested,
    decimal TotalConfirmed,
    decimal TotalAllocated,
    decimal TotalDifference,
    decimal? TotalOriginalCost,
    decimal? TotalConfirmedCost)
{
    public decimal? TotalCostDifference => TotalConfirmedCost - TotalOriginalCost;

    /// <summary>Cost figures are left out for roles that must not see them.</summary>
    public static DifferencesReport Build(Order order, ActorRole viewer)
    {
        var showCosts = OrderVisibility.CanSeeCosts(viewer);
        var products = order.Lines
            .OrderBy(l => l.ProductCode, StringComparer.Ordinal)
            .Select(l =>
            {
                var original = showCosts && l.CostWithoutTax is { } c ? c * l.RequestedQuantity : (decimal?)null;
                var confirmed = showCosts && l.CostWithoutTax is { } c2 ? c2 * l.ConfirmedQuantity : (decimal?)null;
                return new DifferenceProductRow(
                    l.ProductCode, l.Description, l.RequestedQuantity, l.ConfirmedQuantity, l.TotalQuantity,
                    l.ConfirmedQuantity - l.RequestedQuantity, original, confirmed, confirmed - original);
            })
            .ToList();

        var names = order.Locations.ToDictionary(l => l.Code, l => l.Name, StringComparer.OrdinalIgnoreCase);
        var locations = order.Lines
            .SelectMany(l => l.Allocations)
            .GroupBy(a => a.LocationCode, StringComparer.OrdinalIgnoreCase)
            .Select(g => new DifferenceLocationRow(
                g.Key, names.GetValueOrDefault(g.Key), g.First().Kind, g.Sum(a => a.Quantity)))
            .OrderBy(l => l.Kind)
            .ThenBy(l => l.LocationCode, StringComparer.Ordinal)
            .ToList();

        return new DifferencesReport(
            order,
            products,
            locations,
            products.Sum(p => p.Requested),
            products.Sum(p => p.Confirmed),
            products.Sum(p => p.Allocated),
            products.Sum(p => p.Difference),
            showCosts ? products.Sum(p => p.OriginalCost ?? 0) : null,
            showCosts ? products.Sum(p => p.ConfirmedCost ?? 0) : null);
    }
}

public enum TransferStatus
{
    /// <summary>The store has a transfer request document in the ERP.</summary>
    Sent,

    /// <summary>The store has no ERP document yet (not submitted, failed or pending).</summary>
    Pending,

    /// <summary>Warehouses receive stock through the purchase order, not a transfer.</summary>
    NotApplicable,
}

public sealed record TransferRow(
    string LocationCode,
    string? LocationName,
    LocationKind Kind,
    long? DocNum,
    TransferStatus Status,
    string ProductCode,
    string? Description,
    string? Barcode,
    decimal Allocated,
    decimal ProductRequested,
    decimal ProductConfirmed);

public sealed record TransferMatrixRow(
    string ProductCode, string? Description, string? Barcode, IReadOnlyDictionary<string, decimal> Quantities, decimal Total);

public sealed record TransfersReport(
    Order Order,
    IReadOnlyList<TransferRow> Rows,
    IReadOnlyList<string> StoresWithoutDocument,
    decimal TotalAllocated)
{
    /// <summary>Locations in column order: stores first (by code), warehouses last.</summary>
    public IReadOnlyList<(string Code, string? Name, long? DocNum)> MatrixColumns =>
        [.. Rows.GroupBy(r => r.LocationCode, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(r => r.Kind == LocationKind.Warehouse)
            .ThenBy(r => r.LocationCode, StringComparer.Ordinal)
            .Select(r => (r.LocationCode, r.LocationName, r.DocNum))];

    /// <summary>One row per product with the allocated quantity in every location, as in the redistribution screen.</summary>
    public IReadOnlyList<TransferMatrixRow> Matrix =>
        [.. Rows.GroupBy(r => r.ProductCode, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new TransferMatrixRow(
                g.Key, g.First().Description, g.First().Barcode,
                g.ToDictionary(r => r.LocationCode, r => r.Allocated, StringComparer.OrdinalIgnoreCase),
                g.Sum(r => r.Allocated)))];

    public static TransfersReport Build(Order order, IEnumerable<ErpDocument> documents)
    {
        var docNums = documents
            .Where(d => d.Kind == ErpDocumentKind.TransferRequest && d.Status == ErpDocumentStatus.Sent && d.DestinationLocation is not null)
            .GroupBy(d => d.DestinationLocation!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().DocNum, StringComparer.OrdinalIgnoreCase);
        var names = order.Locations.ToDictionary(l => l.Code, l => l.Name, StringComparer.OrdinalIgnoreCase);

        var rows = order.Lines
            .SelectMany(line => line.Allocations.Select(a => (line, a)))
            .Select(x =>
            {
                var (line, a) = x;
                var docNum = a.Kind == LocationKind.Store ? docNums.GetValueOrDefault(a.LocationCode) : null;
                var status = a.Kind == LocationKind.Warehouse
                    ? TransferStatus.NotApplicable
                    : docNum is null ? TransferStatus.Pending : TransferStatus.Sent;
                return new TransferRow(
                    a.LocationCode, names.GetValueOrDefault(a.LocationCode) ?? a.LocationName, a.Kind, docNum, status,
                    line.ProductCode, line.Description, line.Barcode, a.Quantity, line.RequestedQuantity, line.ConfirmedQuantity);
            })
            .OrderBy(r => r.Kind == LocationKind.Warehouse)
            .ThenBy(r => r.LocationCode, StringComparer.Ordinal)
            .ThenBy(r => r.ProductCode, StringComparer.Ordinal)
            .ToList();

        var missing = rows
            .Where(r => r.Status == TransferStatus.Pending)
            .Select(r => r.LocationCode)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new TransfersReport(order, rows, missing, rows.Sum(r => r.Allocated));
    }
}

/// <summary>The ERP documents of an order with their lines and the log of every attempt to create them.</summary>
public sealed record ErpIntegrationReport(Order Order, IReadOnlyList<ErpDocument> Documents)
{
    public static ErpIntegrationReport Build(Order order, IEnumerable<ErpDocument> documents) =>
        new(order, [.. documents
            .OrderBy(d => d.Kind)
            .ThenBy(d => d.OriginWarehouse, StringComparer.Ordinal)
            .ThenBy(d => d.DestinationLocation, StringComparer.Ordinal)]);
}

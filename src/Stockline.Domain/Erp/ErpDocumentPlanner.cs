using Stockline.Domain.Orders;

namespace Stockline.Domain.Erp;

public sealed record ErpPlanResult(IReadOnlyList<ErpDocument> Documents, IReadOnlyList<ValidationIssue> Issues);

public static class ErpDocumentPlanner
{
    public static ErpPlanResult Plan(Order order)
    {
        var issues = new List<ValidationIssue>();
        var lines = new List<(OrderLine Line, string Origin, string? Store, decimal Quantity)>();

        foreach (var line in order.Lines)
        {
            foreach (var allocation in line.Allocations)
            {
                var location = order.Locations.FirstOrDefault(
                    l => string.Equals(l.Code, allocation.LocationCode, StringComparison.OrdinalIgnoreCase));
                if (location is null)
                {
                    issues.Add(new ValidationIssue("UNKNOWN_LOCATION", $"Location '{allocation.LocationCode}' is not part of the order.", line.ProductCode, allocation.LocationCode));
                    continue;
                }

                if (location.Kind == LocationKind.Warehouse)
                {
                    lines.Add((line, location.Code, null, allocation.Quantity));
                }
                else if (string.IsNullOrWhiteSpace(location.SupplyingWarehouseCode))
                {
                    issues.Add(new ValidationIssue("STORE_WITHOUT_WAREHOUSE", $"Store '{location.Code}' has no supplying warehouse.", line.ProductCode, location.Code));
                }
                else
                {
                    lines.Add((line, location.SupplyingWarehouseCode, location.Code, allocation.Quantity));
                }
            }
        }

        if (issues.Count == 0 && lines.Count == 0)
        {
            issues.Add(new ValidationIssue("NOTHING_TO_SEND", "The order has no quantities to send to the ERP."));
        }

        return issues.Count > 0 ? new ErpPlanResult([], issues) : new ErpPlanResult(BuildDocuments(order, lines), []);
    }

    private static List<ErpDocument> BuildDocuments(
        Order order, List<(OrderLine Line, string Origin, string? Store, decimal Quantity)> lines)
    {
        var documents = new List<ErpDocument>();
        foreach (var warehouse in lines.GroupBy(l => l.Origin, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key))
        {
            var purchaseOrder = new ErpDocument(
                Guid.CreateVersion7(), order.Id, ErpDocumentKind.PurchaseOrder, warehouse.Key, null, null,
                ToDocumentLines(warehouse));
            documents.Add(purchaseOrder);

            foreach (var store in warehouse.Where(l => l.Store is not null).GroupBy(l => l.Store!, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key))
            {
                documents.Add(new ErpDocument(
                    Guid.CreateVersion7(), order.Id, ErpDocumentKind.TransferRequest, warehouse.Key, store.Key, purchaseOrder.Id,
                    ToDocumentLines(store)));
            }
        }

        return documents;
    }

    private static List<ErpDocumentLine> ToDocumentLines(
        IEnumerable<(OrderLine Line, string Origin, string? Store, decimal Quantity)> lines) =>
        lines.GroupBy(l => l.Line.ProductCode, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ErpDocumentLine(
                g.Key,
                g.Sum(l => l.Quantity),
                Math.Round(g.First().Line.UnitCost ?? 0m, 4),
                g.First().Line.Discount * 100m))
            .ToList();
}

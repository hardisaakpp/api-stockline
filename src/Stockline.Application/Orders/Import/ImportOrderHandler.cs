using Stockline.Application.Notifications;
using Stockline.Domain.Orders;

namespace Stockline.Application.Orders.Import;

public enum ImportOutcome
{
    Imported,
    InvalidFormat,
    ValidationFailed,
    DuplicateOrderCode,
}

public sealed record ImportOrderResult(
    ImportOutcome Outcome,
    Guid? OrderId,
    IReadOnlyList<ImportIssue> Issues,
    string? Message = null);

public sealed class ImportOrderHandler(
    IOrderTemplateReader reader,
    OrderTemplateValidator validator,
    ILocationCatalogProvider catalogs,
    IOrderRepository orders,
    IOrderEvents? events = null,
    TimeProvider? clock = null)
{
    public async Task<ImportOrderResult> HandleAsync(Stream content, CancellationToken ct)
    {
        OrderTemplate template;
        try
        {
            template = reader.Read(content);
        }
        catch (InvalidOrderTemplateException ex)
        {
            return new ImportOrderResult(ImportOutcome.InvalidFormat, null, [], ex.Message);
        }

        var catalog = await catalogs.GetAsync(ct);
        var issues = validator.Validate(template, catalog);
        if (issues.Any(i => i.Severity == IssueSeverity.Error))
        {
            return new ImportOrderResult(ImportOutcome.ValidationFailed, null, issues);
        }

        if (await orders.ExistsAsync(template.Header.OrderCode!.Trim(), ct))
        {
            ImportIssue[] duplicate =
            [
                new(IssueSeverity.Error, "DUPLICATE_ORDER_CODE",
                    $"An order with code '{template.Header.OrderCode}' already exists.", Field: "CODIGO PEDIDO"),
            ];
            return new ImportOrderResult(ImportOutcome.DuplicateOrderCode, null, duplicate);
        }

        var order = ToOrder(template, catalog);
        await orders.AddAsync(order, ct);
        await events.PublishAsync(order, "OrderImported", null, (clock ?? TimeProvider.System).GetUtcNow(), ct);
        return new ImportOrderResult(ImportOutcome.Imported, order.Id, issues);
    }

    private static Order ToOrder(OrderTemplate template, LocationCatalog catalog)
    {
        var h = template.Header;
        var header = new OrderHeader(
            h.OrderCode!, h.OrderDate!.Value, h.DeliveryDate, h.DeliveryNote, h.PaymentTerms, h.Message,
            h.SupplierName!, h.BuyerName, h.BuyerCode, h.TransferOrigin, h.Series);

        var lines = template.Lines.Where(l => l.Quantities.Count > 0).Select(l =>
        {
            Pack.TryParse(l.Pack, out var pack);
            var allocations = l.Quantities.Select(q =>
            {
                catalog.TryGetKind(q.LocationCode, out var kind);
                return new Allocation(q.LocationCode, q.LocationName, kind, q.Quantity);
            });
            return new OrderLine(
                l.ProductCode, l.Barcode, l.Description, pack, l.CostWithoutTax, l.Discount ?? 0m, l.RetailPrice,
                allocations);
        });

        return Order.Create(Guid.CreateVersion7(), header, lines, BuildLocations(template, catalog));
    }

    private static List<OrderLocation> BuildLocations(OrderTemplate template, LocationCatalog catalog)
    {
        var fromColumns = template.Locations ?? [];
        var names = fromColumns.ToDictionary(l => l.Code, l => l.Name, StringComparer.OrdinalIgnoreCase);
        var codes = fromColumns.Select(l => l.Code)
            .Concat(template.Lines.SelectMany(l => l.Quantities).Select(q => q.LocationCode))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var locations = new List<OrderLocation>();
        foreach (var code in codes)
        {
            if (!catalog.TryGetInfo(code, out var info))
            {
                continue;
            }

            var name = names.GetValueOrDefault(code)
                ?? template.Lines.SelectMany(l => l.Quantities).FirstOrDefault(q => q.LocationCode.Equals(code, StringComparison.OrdinalIgnoreCase))?.LocationName;
            locations.Add(new OrderLocation(code, name, info.Kind, info.SupplyingWarehouseCode));
        }

        return locations;
    }
}

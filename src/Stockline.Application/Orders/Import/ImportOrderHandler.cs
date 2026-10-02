using Stockline.Application.Notifications;
using Stockline.Domain.Orders;

namespace Stockline.Application.Orders.Import;

public enum ImportOutcome
{
    Imported,

    /// <summary>Dry run only: the template is valid and nothing was saved.</summary>
    Valid,
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
    TimeProvider? clock = null,
    IOrderFileStore? files = null,
    ErpTemplateChecker? erpChecker = null)
{
    public Task<ImportOrderResult> HandleAsync(Stream content, CancellationToken ct) =>
        HandleAsync(content, null, null, ImportOptions.Default, ct);

    public Task<ImportOrderResult> HandleAsync(Stream content, string? fileName, string? userId, CancellationToken ct) =>
        HandleAsync(content, fileName, userId, ImportOptions.Default, ct);

    /// <summary>Checks a template exactly as an import would, without saving anything (dry run).</summary>
    public async Task<ImportOrderResult> ValidateAsync(Stream content, ImportOptions options, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        buffer.Position = 0;

        var prepared = await PrepareAsync(buffer, options, ct);
        return prepared.Failure ?? new ImportOrderResult(ImportOutcome.Valid, null, prepared.Issues);
    }

    /// <param name="fileName">Original name of the uploaded file; the file is kept for later download.</param>
    /// <param name="userId">The buyer who imports the order.</param>
    public async Task<ImportOrderResult> HandleAsync(
        Stream content, string? fileName, string? userId, ImportOptions options, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        buffer.Position = 0;

        var prepared = await PrepareAsync(buffer, options, ct);
        if (prepared.Failure is not null)
        {
            return prepared.Failure;
        }

        var (template, catalog, issues) = (prepared.Template!, prepared.Catalog!, prepared.Issues);
        await StageAsync(options, ImportStage.Saving, ct);
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var order = ToOrder(template, catalog, userId, now);
        await orders.AddAsync(order, ct);
        if (files is not null)
        {
            await files.SaveAsync(new OrderSourceFile(order.Id, fileName ?? $"{order.Header.Code}.xlsx", buffer.ToArray(), userId, now), ct);
        }

        await events.PublishAsync(order, "OrderImported", userId, now, ct);
        return new ImportOrderResult(ImportOutcome.Imported, order.Id, issues);
    }

    private static Task StageAsync(ImportOptions options, ImportStage stage, CancellationToken ct) =>
        options.OnStage?.Invoke(stage, ct) ?? Task.CompletedTask;

    private sealed record Prepared(
        OrderTemplate? Template, LocationCatalog? Catalog, IReadOnlyList<ImportIssue> Issues, ImportOrderResult? Failure);

    /// <summary>Reads and validates a template; <see cref="Prepared.Failure"/> is set when it cannot become an order.</summary>
    private async Task<Prepared> PrepareAsync(Stream content, ImportOptions options, CancellationToken ct)
    {
        OrderTemplate template;
        await StageAsync(options, ImportStage.Reading, ct);
        try
        {
            template = reader.Read(content);
        }
        catch (InvalidOrderTemplateException ex)
        {
            return new Prepared(null, null, [], new ImportOrderResult(ImportOutcome.InvalidFormat, null, [], ex.Message));
        }

        template = ApplyCorrections(template, options.Corrections);
        await StageAsync(options, ImportStage.Validating, ct);
        var catalog = await catalogs.GetAsync(ct);
        var issues = validator.Validate(template, catalog).ToList();
        if (!issues.Any(i => i.Severity == IssueSeverity.Error) && options.ValidateWithErp && erpChecker is not null)
        {
            await StageAsync(options, ImportStage.CheckingErp, ct);
            issues.AddRange(await erpChecker.CheckAsync(template, options.RequireErp, ct));
        }

        if (issues.Any(i => i.Severity == IssueSeverity.Error))
        {
            return new Prepared(template, catalog, issues, new ImportOrderResult(ImportOutcome.ValidationFailed, null, issues));
        }

        if (await orders.ExistsAsync(template.Header.OrderCode!.Trim(), ct))
        {
            ImportIssue[] duplicate =
            [
                new(IssueSeverity.Error, "DUPLICATE_ORDER_CODE",
                    $"An order with code '{template.Header.OrderCode}' already exists.", Field: "CODIGO PEDIDO"),
            ];
            return new Prepared(template, catalog, issues, new ImportOrderResult(ImportOutcome.DuplicateOrderCode, null, duplicate));
        }

        return new Prepared(template, catalog, issues, null);
    }

    /// <summary>Replaces product codes (matched ignoring case) before validation; the buyer's chosen corrections.</summary>
    private static OrderTemplate ApplyCorrections(OrderTemplate template, IReadOnlyDictionary<string, string>? corrections)
    {
        if (corrections is not { Count: > 0 })
        {
            return template;
        }

        var map = new Dictionary<string, string>(corrections, StringComparer.OrdinalIgnoreCase);
        return template with
        {
            Lines = [.. template.Lines.Select(l => map.TryGetValue(l.ProductCode.Trim(), out var corrected)
                ? l with { ProductCode = corrected.Trim() }
                : l)],
        };
    }

    private static Order ToOrder(OrderTemplate template, LocationCatalog catalog, string? userId, DateTimeOffset now)
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
                l.ProductCode, l.Barcode, l.Description, pack, l.CostWithoutTax, DiscountRule.ToFraction(l.Discount) ?? 0m, l.RetailPrice,
                allocations);
        });

        return Order.Create(Guid.CreateVersion7(), header, lines, BuildLocations(template, catalog), userId, now);
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

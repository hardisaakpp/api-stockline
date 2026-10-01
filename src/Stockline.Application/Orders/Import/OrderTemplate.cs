namespace Stockline.Application.Orders.Import;

public sealed record OrderTemplate(
    OrderTemplateHeader Header,
    IReadOnlyList<OrderTemplateLine> Lines,
    IReadOnlyList<ImportIssue> ReadIssues,
    IReadOnlyList<TemplateLocation>? Locations = null);

public sealed record TemplateLocation(string Code, string? Name);

public sealed record OrderTemplateHeader(
    string? OrderCode,
    DateOnly? OrderDate,
    DateOnly? DeliveryDate,
    string? DeliveryNote,
    string? PaymentTerms,
    string? Message,
    string? SupplierName,
    string? BuyerName,
    string? BuyerCode,
    string? TransferOrigin,
    string? Series);

public sealed record OrderTemplateLine(
    int Row,
    string ProductCode,
    string? Barcode,
    string? Description,
    string? Pack,
    decimal? CostWithoutTax,
    decimal? UnitCost,
    decimal? Discount,
    decimal? RetailPrice,
    decimal? Total,
    decimal? Subtotal,
    IReadOnlyList<LocationQuantity> Quantities);

public sealed record LocationQuantity(string LocationCode, string? LocationName, decimal Quantity);

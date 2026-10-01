namespace Stockline.Domain.Orders;

public sealed record OrderHeader(
    string Code,
    DateOnly OrderDate,
    DateOnly? DeliveryDate,
    string? DeliveryNote,
    string? PaymentTerms,
    string? Message,
    string SupplierName,
    string? BuyerName,
    string? BuyerCode,
    string? TransferOrigin,
    string? Series);

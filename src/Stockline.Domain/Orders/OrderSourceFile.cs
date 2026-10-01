namespace Stockline.Domain.Orders;

/// <summary>The Excel file an order was imported from, kept so it can be downloaded again.</summary>
public sealed class OrderSourceFile
{
    // Parameterless constructor required by EF Core materialization.
    private OrderSourceFile()
    {
        FileName = null!;
        Content = null!;
    }

    public OrderSourceFile(Guid orderId, string fileName, byte[] content, string? uploadedBy, DateTimeOffset uploadedAt)
    {
        OrderId = orderId;
        FileName = fileName;
        Content = content;
        UploadedBy = uploadedBy;
        UploadedAt = uploadedAt;
    }

    public Guid OrderId { get; }

    public string FileName { get; }

    public byte[] Content { get; }

    public string? UploadedBy { get; }

    public DateTimeOffset UploadedAt { get; }
}

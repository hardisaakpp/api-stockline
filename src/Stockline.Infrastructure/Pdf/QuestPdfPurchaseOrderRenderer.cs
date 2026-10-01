using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Stockline.Application.Erp;

namespace Stockline.Infrastructure.Pdf;

public sealed class QuestPdfPurchaseOrderRenderer : IPurchaseOrderPdfRenderer
{
    private const string Accent = "#173650";
    private const string Muted = "#7f8c8d";
    private const string Border = "#bdc3c7";
    private const string Stripe = "#eef2f6";

    static QuestPdfPurchaseOrderRenderer() => QuestPDF.Settings.License = LicenseType.Community;

    public byte[] Render(PurchaseOrderSheet sheet) =>
        Document.Create(document => document.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(1.5f, Unit.Centimetre);
            page.DefaultTextStyle(t => t.FontSize(8).FontColor("#2c3e50"));
            page.Header().Element(c => Header(c, sheet));
            page.Content().PaddingVertical(10).Column(column =>
            {
                column.Spacing(10);
                column.Item().Element(c => Lines(c, sheet));
                column.Item().Element(c => Summary(c, sheet));
                column.Item().PaddingTop(30).Element(Signatures);
            });
            page.Footer().AlignCenter().Text(t =>
            {
                t.Span($"Generated {sheet.GeneratedAt:yyyy-MM-dd HH:mm} UTC - page ").FontColor(Muted);
                t.CurrentPageNumber().FontColor(Muted);
            });
        })).GeneratePdf();

    private static void Header(IContainer container, PurchaseOrderSheet sheet) =>
        container.Column(column =>
        {
            column.Item().Background(Accent).Padding(8).Text($"PURCHASE ORDER No. {sheet.Number}")
                .FontSize(14).Bold().FontColor(Colors.White);
            column.Item().PaddingTop(6).Row(row =>
            {
                row.RelativeItem().Column(c =>
                {
                    c.Item().Text("Supplier").Bold().FontColor(Muted);
                    c.Item().Text(sheet.SupplierName).Bold();
                });
                row.RelativeItem().Column(c =>
                {
                    c.Item().Text("Buyer").Bold().FontColor(Muted);
                    c.Item().Text(sheet.Buyer.Name).Bold();
                    if (sheet.Buyer.TaxId is not null)
                    {
                        c.Item().Text($"Tax ID: {sheet.Buyer.TaxId}");
                    }

                    if (sheet.Buyer.Address is not null)
                    {
                        c.Item().Text(sheet.Buyer.Address);
                    }
                });
                row.RelativeItem().Column(c =>
                {
                    c.Item().Text($"Order: {sheet.OrderCode}");
                    c.Item().Text($"Warehouse: {sheet.Warehouse}");
                    c.Item().Text($"Order date: {sheet.OrderDate:yyyy-MM-dd}");
                    c.Item().Text($"Delivery: {(sheet.DeliveryDate is { } d ? d.ToString("yyyy-MM-dd") : "as soon as possible")}");
                });
            });
        });

    private static void Lines(IContainer container, PurchaseOrderSheet sheet) =>
        container.Table(table =>
        {
            table.ColumnsDefinition(c =>
            {
                c.ConstantColumn(80);
                c.ConstantColumn(60);
                c.RelativeColumn();
                c.ConstantColumn(40);
                c.ConstantColumn(55);
                c.ConstantColumn(40);
                c.ConstantColumn(65);
            });
            table.Header(h =>
            {
                foreach (var title in new[] { "Barcode", "SKU", "Description", "Qty", "Unit cost", "Disc. %", "Total" })
                {
                    h.Cell().Background(Accent).Padding(4).Text(title).Bold().FontColor(Colors.White);
                }
            });

            for (var i = 0; i < sheet.Lines.Count; i++)
            {
                var line = sheet.Lines[i];
                var background = i % 2 == 1 ? Color.FromHex(Stripe) : Colors.White;
                IContainer Cell() => table.Cell().Background(background).BorderBottom(0.5f).BorderColor(Border).Padding(4);
                Cell().Text(line.Barcode ?? string.Empty);
                Cell().Text(line.ProductCode);
                Cell().Text(line.Description ?? string.Empty);
                Cell().AlignRight().Text(Number(line.Quantity));
                Cell().AlignRight().Text(Money(line.UnitPrice));
                Cell().AlignRight().Text(Number(line.DiscountPercent));
                Cell().AlignRight().Text(Money(line.Total));
            }
        });

    private static void Summary(IContainer container, PurchaseOrderSheet sheet) =>
        container.AlignRight().Width(200).Table(table =>
        {
            table.ColumnsDefinition(c =>
            {
                c.RelativeColumn();
                c.ConstantColumn(80);
            });
            Row("Subtotal", sheet.Subtotal, false);
            Row("Discount", -sheet.Discount, false);
            Row("Tax", sheet.Tax, false);
            Row("TOTAL", sheet.Total, true);

            void Row(string label, decimal value, bool total)
            {
                var background = total ? Color.FromHex(Accent) : Colors.White;
                var color = total ? Colors.White : Color.FromHex("#2c3e50");
                table.Cell().Background(background).Padding(4).Text(label).Bold().FontColor(color);
                table.Cell().Background(background).Padding(4).AlignRight().Text(Money(value)).Bold().FontColor(color);
            }
        });

    private static void Signatures(IContainer container) =>
        container.Row(row =>
        {
            foreach (var title in new[] { "Buyer - brand manager", "Buyer - finance manager" })
            {
                row.RelativeItem().PaddingHorizontal(20).Column(c =>
                {
                    c.Item().BorderBottom(0.5f).BorderColor(Border).Height(30);
                    c.Item().AlignCenter().Text(title).FontColor(Muted);
                });
            }
        });

    private static string Money(decimal value) => value.ToString("#,##0.00##", CultureInfo.InvariantCulture);

    private static string Number(decimal value) => value.ToString("#,##0.####", CultureInfo.InvariantCulture);
}

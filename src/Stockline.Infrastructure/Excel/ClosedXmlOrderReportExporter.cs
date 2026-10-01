using ClosedXML.Excel;
using Stockline.Application.Reports;
using Stockline.Domain.Orders;

namespace Stockline.Infrastructure.Excel;

public sealed class ClosedXmlOrderReportExporter : IOrderReportExporter
{
    private const string MoneyFormat = "#,##0.00";
    private const string PercentFormat = "0.00%";

    public byte[] Export(MarginReport report)
    {
        using var workbook = new XLWorkbook();
        WriteInfo(workbook, "MARGIN ANALYSIS - ORDER INFORMATION", report.Order,
        [
            ("Margin formula", "1 - (Unit cost / (Retail price / 1.15))"),
            ("Unit cost", "Cost without tax divided by the pack's dozen factor."),
        ]);

        var sheet = workbook.AddWorksheet("Margin");
        WriteHeaders(sheet, "Code", "Description", "Barcode", "Pack", "Retail price", "Cost without tax", "Unit cost", "Margin", "Quantity");
        var row = 2;
        foreach (var r in report.Rows)
        {
            sheet.Cell(row, 1).Value = r.ProductCode;
            SetText(sheet.Cell(row, 2), r.Description);
            SetText(sheet.Cell(row, 3), r.Barcode);
            sheet.Cell(row, 4).Value = r.Pack;
            SetNumber(sheet.Cell(row, 5), r.RetailPrice, MoneyFormat);
            SetNumber(sheet.Cell(row, 6), r.CostWithoutTax, MoneyFormat);
            SetNumber(sheet.Cell(row, 7), r.UnitCost, MoneyFormat);
            SetNumber(sheet.Cell(row, 8), r.Margin, PercentFormat);
            if (r.Margin < 0)
            {
                sheet.Cell(row, 8).Style.Font.FontColor = XLColor.Red;
            }

            sheet.Cell(row, 9).Value = r.Quantity;
            row++;
        }

        sheet.Cell(row + 1, 1).Value = "TOTAL";
        sheet.Cell(row + 1, 9).Value = report.TotalQuantity;
        sheet.Row(row + 1).Style.Font.Bold = true;
        Finish(sheet);
        return Save(workbook);
    }

    public byte[] Export(DifferencesReport report)
    {
        using var workbook = new XLWorkbook();
        var summary = workbook.AddWorksheet("Summary");
        WriteHeaders(summary, "Concept", "Value");
        var rows = new (string, decimal?)[]
        {
            ("Products", report.Products.Count),
            ("Total requested quantity", report.TotalRequested),
            ("Total confirmed quantity", report.TotalConfirmed),
            ("Total allocated quantity", report.TotalAllocated),
            ("Quantity difference (confirmed - requested)", report.TotalDifference),
            ("Original cost", report.TotalOriginalCost),
            ("Confirmed cost", report.TotalConfirmedCost),
            ("Cost difference", report.TotalCostDifference),
        };
        var r = 2;
        foreach (var (concept, value) in rows.Where(x => x.Item2 is not null))
        {
            summary.Cell(r, 1).Value = concept;
            summary.Cell(r, 2).Value = value!.Value;
            r++;
        }

        Finish(summary);

        var products = workbook.AddWorksheet("Differences by product");
        WriteHeaders(products, "Code", "Description", "Requested", "Confirmed", "Allocated", "Difference", "Original cost", "Confirmed cost", "Cost difference");
        r = 2;
        foreach (var p in report.Products)
        {
            products.Cell(r, 1).Value = p.ProductCode;
            SetText(products.Cell(r, 2), p.Description);
            products.Cell(r, 3).Value = p.Requested;
            products.Cell(r, 4).Value = p.Confirmed;
            products.Cell(r, 5).Value = p.Allocated;
            products.Cell(r, 6).Value = p.Difference;
            SetNumber(products.Cell(r, 7), p.OriginalCost, MoneyFormat);
            SetNumber(products.Cell(r, 8), p.ConfirmedCost, MoneyFormat);
            SetNumber(products.Cell(r, 9), p.CostDifference, MoneyFormat);
            r++;
        }

        Finish(products);

        var locations = workbook.AddWorksheet("Allocation by location");
        WriteHeaders(locations, "Code", "Name", "Type", "Allocated");
        r = 2;
        foreach (var l in report.Locations)
        {
            locations.Cell(r, 1).Value = l.LocationCode;
            SetText(locations.Cell(r, 2), l.LocationName);
            locations.Cell(r, 3).Value = l.Kind.ToString();
            locations.Cell(r, 4).Value = l.Allocated;
            r++;
        }

        Finish(locations);
        return Save(workbook);
    }

    public byte[] Export(TransfersReport report)
    {
        using var workbook = new XLWorkbook();
        WriteInfo(workbook, "TRANSFER REQUESTS - ORDER INFORMATION", report.Order,
        [
            ("Stores without transfer number", report.StoresWithoutDocument.Count == 0 ? "None" : string.Join(", ", report.StoresWithoutDocument)),
            ("Transfer number scope", "One transfer request per destination store; the number repeats on every line of that store."),
            ("Warehouses", "Warehouses receive stock through the purchase order and have no transfer request."),
        ]);

        var sheet = workbook.AddWorksheet("Transfers");
        WriteHeaders(sheet, "Transfer number", "Location code", "Location name", "Product code", "Description", "Barcode", "Allocated quantity");
        var row = 2;
        foreach (var t in report.Rows)
        {
            if (t.DocNum is { } docNum)
            {
                sheet.Cell(row, 1).Value = docNum;
            }
            else
            {
                sheet.Cell(row, 1).Value = t.Status == TransferStatus.NotApplicable ? "N/A (warehouse)" : "Pending";
            }

            sheet.Cell(row, 2).Value = t.LocationCode;
            SetText(sheet.Cell(row, 3), t.LocationName);
            sheet.Cell(row, 4).Value = t.ProductCode;
            SetText(sheet.Cell(row, 5), t.Description);
            SetText(sheet.Cell(row, 6), t.Barcode);
            sheet.Cell(row, 7).Value = t.Allocated;
            row++;
        }

        sheet.Cell(row + 1, 1).Value = "TOTAL";
        sheet.Cell(row + 1, 7).Value = report.TotalAllocated;
        sheet.Row(row + 1).Style.Font.Bold = true;
        Finish(sheet);
        return Save(workbook);
    }

    private static void WriteInfo(XLWorkbook workbook, string title, Order order, (string, string)[] notes)
    {
        var sheet = workbook.AddWorksheet("Order information");
        sheet.Cell(1, 1).Value = title;
        sheet.Cell(1, 1).Style.Font.Bold = true;
        var fields = new List<(string, string)>
        {
            ("Order code", order.Header.Code),
            ("Order ID", order.Id.ToString()),
            ("Status", order.Status.ToString()),
            ("Supplier", order.Header.SupplierName),
            ("Order date", order.Header.OrderDate.ToString("yyyy-MM-dd")),
            ("Delivery date", order.Header.DeliveryDate?.ToString("yyyy-MM-dd") ?? order.Header.DeliveryNote ?? "N/A"),
        };
        fields.AddRange(notes);

        var row = 3;
        foreach (var (key, value) in fields)
        {
            sheet.Cell(row, 1).Value = key;
            sheet.Cell(row, 1).Style.Font.Bold = true;
            sheet.Cell(row, 2).Value = value;
            sheet.Cell(row, 2).Style.Alignment.WrapText = true;
            row++;
        }

        sheet.Column(1).Width = 30;
        sheet.Column(2).Width = 80;
    }

    private static void WriteHeaders(IXLWorksheet sheet, params string[] headers)
    {
        for (var i = 0; i < headers.Length; i++)
        {
            sheet.Cell(1, i + 1).Value = headers[i];
        }

        sheet.Row(1).Style.Font.Bold = true;
    }

    private static void SetText(IXLCell cell, string? text)
    {
        if (text is not null)
        {
            cell.Value = text;
        }
    }

    private static void SetNumber(IXLCell cell, decimal? value, string format)
    {
        if (value is { } number)
        {
            cell.Value = number;
            cell.Style.NumberFormat.Format = format;
        }
    }

    private static void Finish(IXLWorksheet sheet)
    {
        sheet.SheetView.FreezeRows(1);
        sheet.Columns().AdjustToContents();
    }

    private static byte[] Save(XLWorkbook workbook)
    {
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }
}

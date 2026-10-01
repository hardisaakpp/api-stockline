using System.Globalization;
using ClosedXML.Excel;
using Stockline.Application.Orders;
using Stockline.Domain.Orders;

namespace Stockline.Infrastructure.Excel;

public sealed class ClosedXmlOrderExporter : IOrderExporter
{
    private static readonly string[] ProductHeaders =
        ["Codigo Producto", "Codigo de Barras", "Descripcion SAP", "Pack", "Costo sin IVA", "Costo Unitario", "Descuento", "PVP"];

    public byte[] ExportTemplate(Order order, ActorRole viewer)
    {
        using var workbook = new XLWorkbook();
        WriteHeader(workbook.AddWorksheet("cabecera"), order.Header);
        WriteDetail(workbook.AddWorksheet("detalle"), order, viewer);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static void WriteHeader(IXLWorksheet sheet, OrderHeader header)
    {
        sheet.Cell(1, 1).Value = "CAMPO";
        sheet.Cell(1, 2).Value = "VALOR";
        var rows = new (string Key, XLCellValue Value)[]
        {
            ("CODIGO PEDIDO", header.Code),
            ("FECHA DE PEDIDO", header.OrderDate.ToDateTime(TimeOnly.MinValue)),
            ("FECHA DE ENTREGA", header.DeliveryDate is { } due ? (XLCellValue)due.ToDateTime(TimeOnly.MinValue) : Val(header.DeliveryNote)),
            ("FORMA DE PAGO", Val(header.PaymentTerms)),
            ("MENSAJE", Val(header.Message)),
            ("NOMBRE PROVEEDOR", header.SupplierName),
            ("COMPRADOR NOMBRE", Val(header.BuyerName)),
            ("COD COMPRADOR", Val(header.BuyerCode)),
            ("ORIGEN TRASLADO", Val(header.TransferOrigin)),
            ("SERIE", Val(header.Series)),
        };

        var row = 2;
        foreach (var (key, value) in rows.Where(r => !r.Value.IsBlank))
        {
            sheet.Cell(row, 1).Value = key;
            sheet.Cell(row, 2).Value = value;
            row++;
        }

        sheet.Column(1).AdjustToContents();
        sheet.Column(2).AdjustToContents();
    }

    private static void WriteDetail(IXLWorksheet sheet, Order order, ActorRole viewer)
    {
        var locations = order.Locations.OrderBy(l => l.Kind).ThenBy(l => l.Code, StringComparer.Ordinal).ToList();
        var firstLocation = ProductHeaders.Length + 1;
        var lastLocation = firstLocation + locations.Count - 1;
        var firstStore = firstLocation + locations.Count(l => l.Kind == LocationKind.Warehouse);
        var totalColumn = lastLocation + 1;

        for (var c = 0; c < ProductHeaders.Length; c++)
        {
            sheet.Cell(1, c + 1).Value = ProductHeaders[c];
        }

        for (var i = 0; i < locations.Count; i++)
        {
            sheet.Cell(1, firstLocation + i).Value = locations[i].Name ?? locations[i].Code;
            sheet.Cell(2, firstLocation + i).Value = locations[i].Code;
        }

        sheet.Cell(1, totalColumn).Value = "TOTAL";
        sheet.Cell(1, totalColumn + 1).Value = "SUBTOTAL";
        sheet.Row(1).Style.Font.Bold = true;

        var row = 3;
        foreach (var line in order.Lines)
        {
            sheet.Cell(row, 1).Value = line.ProductCode;
            sheet.Cell(row, 2).Value = Val(line.Barcode);
            sheet.Cell(row, 3).Value = Val(line.Description);
            sheet.Cell(row, 4).Value = line.Pack.ToString();
            if (OrderVisibility.CanSeeCosts(viewer) && line.CostWithoutTax is { } cost)
            {
                sheet.Cell(row, 5).Value = cost;
                sheet.Cell(row, 6).FormulaA1 = $"IFERROR(E{row}*VALUE(RIGHT(D{row},LEN(D{row})-1))/12,\"\")";
            }

            if (OrderVisibility.CanSeeCosts(viewer))
            {
                sheet.Cell(row, 7).Value = line.Discount;
            }

            if (OrderVisibility.CanSeeRetailPrice(viewer) && line.RetailPrice is { } retail)
            {
                sheet.Cell(row, 8).Value = retail;
            }

            for (var i = 0; i < locations.Count; i++)
            {
                var allocation = line.Allocations.FirstOrDefault(a => a.LocationCode.Equals(locations[i].Code, StringComparison.OrdinalIgnoreCase));
                if (allocation is not null)
                {
                    sheet.Cell(row, firstLocation + i).Value = allocation.Quantity;
                }
            }

            var all = $"{Column(firstLocation)}{row}:{Column(lastLocation)}{row}";
            sheet.Cell(row, totalColumn).FormulaA1 = $"SUM({all})";
            sheet.Cell(row, totalColumn + 1).FormulaA1 = firstStore <= lastLocation
                ? $"SUM({Column(firstStore)}{row}:{Column(lastLocation)}{row})"
                : "0";
            row++;
        }

        sheet.SheetView.FreezeRows(2);
        sheet.Column(3).Width = 40;
    }

    private static XLCellValue Val(string? text) => text is null ? Blank.Value : text;

    private static string Column(int number) => XLHelper.GetColumnLetterFromNumber(number);
}

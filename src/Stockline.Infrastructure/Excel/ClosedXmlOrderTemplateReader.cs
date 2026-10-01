using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using Stockline.Application.Orders.Import;

namespace Stockline.Infrastructure.Excel;

public sealed class ClosedXmlOrderTemplateReader : IOrderTemplateReader
{
    private const string HeaderSheet = "cabecera";
    private const string DetailSheet = "detalle";
    private const int MaxHeaderSearchRows = 10;

    private static readonly Dictionary<string, string[]> ProductColumns = new()
    {
        ["product"] = ["CODIGO PRODUCTO", "PRODUCT CODE"],
        ["barcode"] = ["CODIGO DE BARRAS", "CODIGO BARRAS", "BARCODE"],
        ["description"] = ["DESCRIPCION SAP", "DESCRIPCION", "DESCRIPTION"],
        ["pack"] = ["PACK"],
        ["cost"] = ["COSTO SIN IVA", "COST WITHOUT TAX"],
        ["unitCost"] = ["COSTO UNITARIO", "UNIT COST"],
        ["discount"] = ["DESCUENTO", "DISCOUNT"],
        ["retail"] = ["PVP", "RETAIL PRICE"],
        ["total"] = ["TOTAL"],
        ["subtotal"] = ["SUBTOTAL"],
    };

    public OrderTemplate Read(Stream content)
    {
        using var workbook = OpenWorkbook(content);

        if (!workbook.Worksheets.TryGetWorksheet(HeaderSheet, out var headerSheet)
            || !workbook.Worksheets.TryGetWorksheet(DetailSheet, out var detailSheet))
        {
            throw new InvalidOrderTemplateException(
                $"The workbook must contain the sheets '{HeaderSheet}' and '{DetailSheet}'.");
        }

        var issues = new List<ImportIssue>();
        var (lines, locations) = ReadLines(detailSheet, issues);
        return new OrderTemplate(ReadHeader(headerSheet), lines, issues, locations);
    }

    private static XLWorkbook OpenWorkbook(Stream content)
    {
        try
        {
            return new XLWorkbook(content);
        }
        catch (Exception ex) when (ex is not InvalidOrderTemplateException)
        {
            throw new InvalidOrderTemplateException($"The file is not a valid Excel workbook: {ex.Message}");
        }
    }

    private static OrderTemplateHeader ReadHeader(IXLWorksheet sheet)
    {
        var values = new Dictionary<string, IXLCell>();
        foreach (var row in sheet.RowsUsed().Skip(1))
        {
            var key = Normalize(Text(row.Cell(1)));
            if (key.Length > 0)
            {
                values[key] = row.Cell(2);
            }
        }

        string? Get(string key) => values.TryGetValue(key, out var cell) ? Text(cell) : null;
        DateOnly? GetDate(string key) => values.TryGetValue(key, out var cell) ? Date(cell) : null;

        var delivery = values.GetValueOrDefault("FECHA DE ENTREGA");
        var deliveryDate = delivery is null ? null : Date(delivery);

        return new OrderTemplateHeader(
            OrderCode: Get("CODIGO PEDIDO"),
            OrderDate: GetDate("FECHA DE PEDIDO"),
            DeliveryDate: deliveryDate,
            DeliveryNote: deliveryDate is null && delivery is not null ? Text(delivery) : null,
            PaymentTerms: Get("FORMA DE PAGO"),
            Message: Get("MENSAJE"),
            SupplierName: Get("NOMBRE PROVEEDOR"),
            BuyerName: Get("COMPRADOR NOMBRE"),
            BuyerCode: Get("COD COMPRADOR"),
            TransferOrigin: Get("ORIGEN TRASLADO"),
            Series: Get("SERIE"));
    }

    private static (List<OrderTemplateLine> Lines, List<TemplateLocation> Locations) ReadLines(
        IXLWorksheet sheet, List<ImportIssue> issues)
    {
        var headerRow = FindHeaderRow(sheet);
        var codesRow = headerRow + 1;
        var lastColumn = sheet.LastColumnUsed()?.ColumnNumber() ?? 0;

        var fields = MapProductColumns(sheet, headerRow, lastColumn);
        var locations = Enumerable.Range(1, lastColumn)
            .Where(c => !fields.ContainsValue(c))
            .Select(c => (Column: c, Code: Text(sheet.Cell(codesRow, c)), Name: Text(sheet.Cell(headerRow, c))))
            .Where(l => l.Code is not null)
            .Select(l => (l.Column, Code: l.Code!.ToUpperInvariant(), l.Name))
            .ToList();

        var lines = new List<OrderTemplateLine>();
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 0;
        for (var row = codesRow + 1; row <= lastRow; row++)
        {
            var productCode = Text(sheet.Cell(row, fields["product"]));
            if (productCode is null)
            {
                continue;
            }

            lines.Add(ReadLine(sheet, row, productCode, fields, locations, issues));
        }

        return (lines, [.. locations.Select(l => new TemplateLocation(l.Code, l.Name))]);
    }

    private static OrderTemplateLine ReadLine(
        IXLWorksheet sheet,
        int row,
        string productCode,
        Dictionary<string, int> fields,
        List<(int Column, string Code, string? Name)> locations,
        List<ImportIssue> issues)
    {
        string? Str(string key) => fields.TryGetValue(key, out var c) ? Text(sheet.Cell(row, c)) : null;
        decimal? Num(string key) => fields.TryGetValue(key, out var c) ? Decimal(sheet.Cell(row, c)) : null;

        var quantities = new List<LocationQuantity>();
        foreach (var (column, code, name) in locations)
        {
            var cell = sheet.Cell(row, column);
            if (Text(cell) is null)
            {
                continue;
            }

            if (Decimal(cell) is not { } quantity)
            {
                issues.Add(new ImportIssue(
                    IssueSeverity.Error, "INVALID_QUANTITY", $"'{Text(cell)}' is not a number.", row, code));
                continue;
            }

            if (quantity != 0)
            {
                quantities.Add(new LocationQuantity(code, name, quantity));
            }
        }

        return new OrderTemplateLine(
            row, productCode, Str("barcode"), Str("description"), Str("pack"), Num("cost"), Num("unitCost"),
            Num("discount"), Num("retail"), Num("total"), Num("subtotal"), quantities);
    }

    private static int FindHeaderRow(IXLWorksheet sheet)
    {
        var productHeaders = ProductColumns["product"];
        var lastRow = Math.Min(sheet.LastRowUsed()?.RowNumber() ?? 0, MaxHeaderSearchRows);
        for (var row = 1; row <= lastRow; row++)
        {
            if (sheet.Row(row).CellsUsed().Any(c => productHeaders.Contains(Normalize(Text(c)))))
            {
                return row;
            }
        }

        throw new InvalidOrderTemplateException(
            $"The sheet '{DetailSheet}' has no header row with '{productHeaders[0]}' in its first {MaxHeaderSearchRows} rows.");
    }

    private static Dictionary<string, int> MapProductColumns(IXLWorksheet sheet, int headerRow, int lastColumn)
    {
        var map = new Dictionary<string, int>();
        for (var column = 1; column <= lastColumn; column++)
        {
            var header = Normalize(Text(sheet.Cell(headerRow, column)));
            var field = ProductColumns.FirstOrDefault(p => p.Value.Contains(header)).Key;
            if (field is not null)
            {
                map.TryAdd(field, column);
            }
        }

        return map;
    }

    private static string? Text(IXLCell cell)
    {
        var value = cell.Value;
        if (value.IsBlank)
        {
            return null;
        }

        var text = value switch
        {
            { IsNumber: true } => value.GetNumber().ToString("0.############", CultureInfo.InvariantCulture),
            { IsDateTime: true } => value.GetDateTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            _ => value.ToString().Trim(),
        };

        return text.Length == 0 ? null : text;
    }

    private static decimal? Decimal(IXLCell cell)
    {
        var value = cell.Value;
        if (value.IsNumber)
        {
            return (decimal)value.GetNumber();
        }

        var text = Text(cell)?.Replace(',', '.');
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static DateOnly? Date(IXLCell cell)
    {
        if (cell.Value.IsDateTime)
        {
            return DateOnly.FromDateTime(cell.Value.GetDateTime());
        }

        return DateOnly.TryParse(Text(cell), CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark))
        {
            builder.Append(char.ToUpperInvariant(ch));
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}

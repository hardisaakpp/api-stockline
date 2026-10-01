using ClosedXML.Excel;
using Stockline.Application.Orders.Import;
using Stockline.Infrastructure.Excel;

namespace Stockline.UnitTests.Orders;

public class ClosedXmlOrderTemplateReaderTests
{
    private static MemoryStream Workbook(Action<IXLWorksheet, IXLWorksheet> build)
    {
        using var workbook = new XLWorkbook();
        build(workbook.AddWorksheet("cabecera"), workbook.AddWorksheet("detalle"));
        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }

    private static void FillHeader(IXLWorksheet sheet)
    {
        sheet.Cell("A1").Value = "CAMPO";
        sheet.Cell("B1").Value = "VALOR";
        var rows = new (string Key, XLCellValue Value)[]
        {
            ("CODIGO PEDIDO", "PO-0001"),
            ("FECHA DE PEDIDO", new DateTime(2026, 9, 24)),
            ("FECHA DE ENTREGA", "INMEDIATO"),
            ("FORMA DE PAGO", "120 DIAS"),
            ("NOMBRE PROVEEDOR", "ACME SUPPLIER"),
            ("COD COMPRADOR", 801),
            ("ORIGEN TRASLADO", "WH-1"),
        };
        for (var i = 0; i < rows.Length; i++)
        {
            sheet.Cell(i + 2, 1).Value = rows[i].Key;
            sheet.Cell(i + 2, 2).Value = rows[i].Value;
        }
    }

    private static void FillDetail(IXLWorksheet sheet)
    {
        string[] headers =
            ["Codigo Producto", "Codigo de Barras", "Descripción SAP", "Pack", "Costo sin IVA", "Costo Unitario",
             "Descuento", "PVP", "BODEGA UNO", "TIENDA UNO", "TIENDA DOS", "TOTAL", "SUBTOTAL"];
        for (var c = 0; c < headers.Length; c++)
        {
            sheet.Cell(1, c + 1).Value = headers[c];
        }

        sheet.Cell("I2").Value = "WH-1";
        sheet.Cell("J2").Value = "st-1";
        sheet.Cell("K2").Value = "ST-2";

        sheet.Cell("A3").Value = "SKU-1";
        sheet.Cell("B3").Value = 7869000000013;
        sheet.Cell("C3").Value = "Basic tee";
        sheet.Cell("D3").Value = "X6";
        sheet.Cell("E3").Value = 5.6;
        // ClosedXML 0.105 evaluates MID() as empty, so the unit-cost formula here uses RIGHT/LEN instead.
        sheet.Cell("F3").FormulaA1 = "=IFERROR(E3/(12/VALUE(RIGHT(D3,LEN(D3)-1))),\"\")";
        sheet.Cell("G3").Value = 0.2;
        sheet.Cell("H3").Value = 12.99;
        sheet.Cell("I3").Value = 2;
        sheet.Cell("J3").Value = 0.5;
        sheet.Cell("K3").Value = 0;
        sheet.Cell("L3").FormulaA1 = "=SUM(I3:K3)";
        sheet.Cell("M3").FormulaA1 = "=SUM(J3:K3)";
        sheet.Cell("A4").Value = "SKU-2";
        sheet.Cell("D4").Value = "X1";
        sheet.Cell("K4").Value = "many";
    }

    private static OrderTemplate Read(Action<IXLWorksheet, IXLWorksheet> build) =>
        new ClosedXmlOrderTemplateReader().Read(Workbook(build));

    private static OrderTemplate ReadStandard() =>
        Read((header, detail) => { FillHeader(header); FillDetail(detail); });

    [Fact]
    public void Reads_the_header_with_text_dates_and_numeric_codes()
    {
        var header = ReadStandard().Header;

        Assert.Equal("PO-0001", header.OrderCode);
        Assert.Equal(new DateOnly(2026, 9, 24), header.OrderDate);
        Assert.Null(header.DeliveryDate);
        Assert.Equal("INMEDIATO", header.DeliveryNote);
        Assert.Equal("801", header.BuyerCode);
        Assert.Equal("WH-1", header.TransferOrigin);
    }

    [Fact]
    public void Reads_product_fields_by_header_name_ignoring_accents_and_case()
    {
        var line = ReadStandard().Lines[0];

        Assert.Equal(3, line.Row);
        Assert.Equal("SKU-1", line.ProductCode);
        Assert.Equal("7869000000013", line.Barcode);
        Assert.Equal("Basic tee", line.Description);
        Assert.Equal("X6", line.Pack);
        Assert.Equal(5.6m, line.CostWithoutTax);
        Assert.Equal(0.2m, line.Discount);
        Assert.Equal(12.99m, line.RetailPrice);
    }

    [Fact]
    public void Evaluates_formulas_even_without_cached_values()
    {
        var line = ReadStandard().Lines[0];

        Assert.Equal(2.8m, line.UnitCost);
        Assert.Equal(2.5m, line.Total);
        Assert.Equal(0.5m, line.Subtotal);
    }

    [Fact]
    public void Reads_locations_from_the_codes_row_skipping_zero_and_blank_cells()
    {
        var quantities = ReadStandard().Lines[0].Quantities;

        Assert.Equal(["WH-1", "ST-1"], quantities.Select(q => q.LocationCode));
        Assert.Equal([2m, 0.5m], quantities.Select(q => q.Quantity));
        Assert.Equal("BODEGA UNO", quantities[0].LocationName);
    }

    [Fact]
    public void Non_numeric_quantity_becomes_a_read_issue_with_its_location()
    {
        var template = ReadStandard();

        var issue = Assert.Single(template.ReadIssues);
        Assert.Equal("INVALID_QUANTITY", issue.Code);
        Assert.Equal(4, issue.Row);
        Assert.Equal("ST-2", issue.Field);
        Assert.Empty(template.Lines[1].Quantities);
    }

    [Fact]
    public void Header_row_does_not_have_to_be_the_first_row()
    {
        var template = Read((header, detail) =>
        {
            FillHeader(header);
            detail.Cell("A1").Value = "Group title";
            detail.Cell("A2").Value = "Codigo Producto";
            detail.Cell("B2").Value = "Pack";
            detail.Cell("C2").Value = "Store";
            detail.Cell("C3").Value = "ST-1";
            detail.Cell("A4").Value = "SKU-9";
            detail.Cell("B4").Value = "X2";
            detail.Cell("C4").Value = 3;
        });

        var line = Assert.Single(template.Lines);
        Assert.Equal("SKU-9", line.ProductCode);
        Assert.Equal(3m, Assert.Single(line.Quantities).Quantity);
    }

    [Fact]
    public void Missing_sheets_are_a_format_error()
    {
        using var workbook = new XLWorkbook();
        workbook.AddWorksheet("Sheet1");
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;

        var ex = Assert.Throws<InvalidOrderTemplateException>(() => new ClosedXmlOrderTemplateReader().Read(stream));
        Assert.Contains("cabecera", ex.Message);
    }

    [Fact]
    public void Missing_product_header_is_a_format_error() =>
        Assert.Throws<InvalidOrderTemplateException>(() =>
            Read((header, detail) => { FillHeader(header); detail.Cell("A1").Value = "Something else"; }));

    [Fact]
    public void Garbage_bytes_are_a_format_error() =>
        Assert.Throws<InvalidOrderTemplateException>(() =>
            new ClosedXmlOrderTemplateReader().Read(new MemoryStream([1, 2, 3, 4])));

    [Fact]
    public void Reader_and_validator_agree_on_a_consistent_template()
    {
        var template = Read((header, detail) =>
        {
            FillHeader(header);
            FillDetail(detail);
            detail.Row(4).Delete();
        });
        var catalog = new LocationCatalog(new Dictionary<string, Stockline.Domain.Orders.LocationKind>
        {
            ["WH-1"] = Stockline.Domain.Orders.LocationKind.Warehouse,
            ["ST-1"] = Stockline.Domain.Orders.LocationKind.Store,
            ["ST-2"] = Stockline.Domain.Orders.LocationKind.Store,
        });

        Assert.Empty(new OrderTemplateValidator().Validate(template, catalog));
    }
}

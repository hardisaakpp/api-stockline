using Stockline.Application.Orders.Import;
using Stockline.Domain.Orders;

namespace Stockline.UnitTests.Orders;

public class OrderTemplateValidatorTests
{
    private static readonly LocationCatalog Catalog = new(new Dictionary<string, LocationKind>
    {
        ["WH-1"] = LocationKind.Warehouse,
        ["ST-1"] = LocationKind.Store,
        ["ST-2"] = LocationKind.Store,
    });

    private static readonly OrderTemplateHeader ValidHeader = new(
        "PO-0001", new DateOnly(2026, 9, 24), null, "IMMEDIATE", "120 DAYS", null, "ACME SUPPLIER", "BUYER",
        null, "WH-1", null);

    private static OrderTemplateLine Line(Action<LineBuilder>? tweak = null)
    {
        var b = new LineBuilder();
        tweak?.Invoke(b);
        return b.Build();
    }

    private static OrderTemplate Template(params OrderTemplateLine[] lines) =>
        new(ValidHeader, lines, []);

    private static IReadOnlyList<string> Codes(OrderTemplate template) =>
        new OrderTemplateValidator().Validate(template, Catalog).Select(i => i.Code).ToList();

    private sealed class LineBuilder
    {
        public string Pack = "X6";
        public decimal? Cost = 5.6m;
        public decimal? UnitCost = 2.8m;
        public decimal? Total = 5m;
        public decimal? Subtotal = 3m;
        public List<LocationQuantity> Quantities =
            [new("WH-1", null, 2m), new("ST-1", null, 1m), new("ST-2", null, 2m)];

        public OrderTemplateLine Build() =>
            new(3, "SKU-1", null, "Item", Pack, Cost, UnitCost, 0m, 12.99m, Total, Subtotal, Quantities);
    }

    [Fact]
    public void Valid_template_has_no_issues() => Assert.Empty(Codes(Template(Line())));

    [Fact]
    public void Missing_header_fields_are_reported()
    {
        var template = Template(Line()) with { Header = ValidHeader with { OrderCode = " ", SupplierName = null, OrderDate = null } };

        Assert.Equal(["REQUIRED_FIELD", "REQUIRED_FIELD", "REQUIRED_FIELD"], Codes(template));
    }

    [Fact]
    public void Template_without_quantities_is_rejected()
    {
        Assert.Contains("NO_QUANTITIES", Codes(Template()));
        Assert.Contains("NO_QUANTITIES", Codes(Template(Line(b => b.Quantities = []))));
    }

    [Fact]
    public void Invalid_pack_is_reported_and_skips_cost_check() =>
        Assert.Equal(["INVALID_PACK"], Codes(Template(Line(b => { b.Pack = "6"; b.UnitCost = 99m; }))));

    [Fact]
    public void Unit_cost_must_match_the_independent_calculation() =>
        Assert.Equal(["UNIT_COST_MISMATCH"], Codes(Template(Line(b => b.UnitCost = 3.5m))));

    [Fact]
    public void Missing_unit_cost_is_not_an_error() =>
        Assert.Empty(Codes(Template(Line(b => b.UnitCost = null))));

    [Fact]
    public void Total_must_equal_the_sum_of_all_locations() =>
        Assert.Equal(["TOTAL_MISMATCH"], Codes(Template(Line(b => b.Total = 6m))));

    [Fact]
    public void Subtotal_must_equal_the_sum_of_stores_only() =>
        Assert.Equal(["SUBTOTAL_MISMATCH"], Codes(Template(Line(b => b.Subtotal = 5m))));

    [Fact]
    public void Decimal_quantities_are_summed_exactly() =>
        Assert.Empty(Codes(Template(Line(b =>
        {
            b.Quantities = [new("WH-1", null, 0.5m), new("ST-1", null, 1.25m)];
            b.Total = 1.75m;
            b.Subtotal = 1.25m;
        }))));

    [Fact]
    public void Unknown_and_negative_locations_are_reported() =>
        Assert.Equal(
            ["NEGATIVE_QUANTITY", "UNKNOWN_LOCATION"],
            Codes(Template(Line(b =>
            {
                b.Quantities = [new("ST-9", null, -1m)];
                b.Total = -1m;
                b.Subtotal = null;
            }))));

    [Fact]
    public void Read_issues_are_carried_over()
    {
        var read = new ImportIssue(IssueSeverity.Error, "INVALID_QUANTITY", "x", 3, "ST-1");

        Assert.Contains("INVALID_QUANTITY", Codes(Template(Line()) with { ReadIssues = [read] }));
    }
}

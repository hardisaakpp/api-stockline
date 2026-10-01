using Stockline.Application.Orders.Import;
using Stockline.Domain.Orders;

namespace Stockline.UnitTests.Orders;

public class OrderTemplateValidatorInvariantTests
{
    private static readonly LocationCatalog Catalog = new(new Dictionary<string, LocationKind>
    {
        ["WH-1"] = LocationKind.Warehouse,
        ["ST-1"] = LocationKind.Store,
    });

    private static readonly OrderTemplateHeader Header = new(
        "PO-1", new DateOnly(2026, 9, 24), null, null, null, null, "ACME", null, null, null, null);

    private static OrderTemplateLine Line(string product = "SKU-1", int row = 3, params LocationQuantity[] quantities) =>
        new(row, product, null, null, "X1", null, null, 0m, null, null, null,
            quantities.Length > 0 ? quantities : [new LocationQuantity("ST-1", null, 1m)]);

    private static IReadOnlyList<ImportIssue> Validate(params OrderTemplateLine[] lines) =>
        new OrderTemplateValidator().Validate(new OrderTemplate(Header, lines, []), Catalog);

    [Fact]
    public void Duplicate_products_are_reported_ignoring_case() =>
        Assert.Equal(["DUPLICATE_PRODUCT"], Validate(Line("sku-1"), Line("SKU-1", 4)).Select(i => i.Code));

    [Fact]
    public void A_location_in_two_columns_is_reported() =>
        Assert.Equal(
            ["DUPLICATE_LOCATION"],
            Validate(Line(quantities: [new("ST-1", null, 1m), new("st-1", null, 2m)])).Select(i => i.Code));

    [Theory]
    [InlineData(-0.5)]
    [InlineData(20)]
    public void Discount_must_be_a_fraction_between_zero_and_one(double discount) =>
        Assert.Equal(["INVALID_DISCOUNT"], Validate(Line() with { Discount = (decimal)discount }).Select(i => i.Code));

    [Fact]
    public void Negative_prices_are_reported() =>
        Assert.Equal(["NEGATIVE_PRICE"], Validate(Line() with { RetailPrice = -1m }).Select(i => i.Code));

    [Fact]
    public void Line_without_quantities_is_only_a_warning_when_other_lines_have_quantities()
    {
        var issues = Validate(Line("SKU-1"), Line("SKU-2", 4, []) with { Quantities = [] });

        var issue = Assert.Single(issues);
        Assert.Equal("LINE_WITHOUT_QUANTITIES", issue.Code);
        Assert.Equal(IssueSeverity.Warning, issue.Severity);
        Assert.Equal(4, issue.Row);
    }
}

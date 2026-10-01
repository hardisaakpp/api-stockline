using Stockline.UnitTests.Support;
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
        FixedTimeProvider.Validator().Validate(new OrderTemplate(Header, lines, []), Catalog);

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
    [InlineData(101)]
    public void Discount_must_be_between_zero_and_a_hundred_percent(double discount) =>
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

    private static IReadOnlyList<ImportIssue> ValidateWith(OrderTemplateHeader header, params OrderTemplateLine[] lines) =>
        FixedTimeProvider.Validator().Validate(new OrderTemplate(header, lines, []), Catalog);

    private static (IssueSeverity Severity, string Code)[] Codes(IEnumerable<ImportIssue> issues) =>
        [.. issues.Select(i => (i.Severity, i.Code))];

    [Theory]
    [InlineData(20, 0.2)]
    [InlineData(0.2, 0.2)]
    [InlineData(1, 1)]
    [InlineData(100, 1)]
    [InlineData(0, 0)]
    public void Discounts_are_accepted_as_fractions_or_percentages(double raw, double expected) =>
        Assert.Equal((decimal)expected, DiscountRule.ToFraction((decimal)raw));

    [Fact]
    public void A_missing_discount_means_none_and_out_of_range_ones_have_no_value()
    {
        Assert.Equal(0m, DiscountRule.ToFraction(null));
        Assert.Null(DiscountRule.ToFraction(100.5m));
        Assert.Null(DiscountRule.ToFraction(-1m));
    }

    [Fact]
    public void A_percentage_discount_is_not_an_error_but_a_very_high_one_is_a_warning() =>
        Assert.Equal([(IssueSeverity.Warning, "HIGH_DISCOUNT")], Codes(Validate(Line() with { Discount = 60m })));

    [Theory]
    [InlineData("SKU 1.A-2", false)]
    [InlineData("SKU_1", true)]
    [InlineData("SKU/1", true)]
    [InlineData("SKU#1", true)]
    public void Product_codes_may_only_use_letters_digits_dots_hyphens_and_spaces(string code, bool invalid) =>
        Assert.Equal(invalid, Validate(Line(code)).Any(i => i.Code == "INVALID_PRODUCT_CODE"));

    [Fact]
    public void Product_codes_are_limited_to_fifty_characters()
    {
        Assert.Empty(Validate(Line(new string('A', 50))));
        Assert.Contains(Validate(Line(new string('A', 51))), i => i.Code == "INVALID_PRODUCT_CODE");
    }

    [Fact]
    public void Zero_cost_and_zero_retail_price_are_warnings() =>
        Assert.Equal(
            [(IssueSeverity.Warning, "ZERO_COST"), (IssueSeverity.Warning, "ZERO_RETAIL_PRICE")],
            Codes(Validate(Line() with { CostWithoutTax = 0m, UnitCost = 0m, RetailPrice = 0m })));

    [Theory]
    [InlineData(10.0, 11.0, 0.0, false)]
    [InlineData(10.0, 10.0, 0.0, true)]
    [InlineData(10.0, 10.4, 0.0, false)]
    [InlineData(10.0, 8.0, 0.2, true)]
    [InlineData(10.0, 8.5, 0.2, false)]
    public void A_retail_price_well_below_the_cost_with_tax_and_discount_is_a_warning(
        double cost, double retail, double discount, bool warns)
    {
        // The threshold is cost * 1.15 tax * (1 - discount) * 0.9.
        var issues = Validate(Line() with { CostWithoutTax = (decimal)cost, UnitCost = (decimal)cost, RetailPrice = (decimal)retail, Discount = (decimal)discount });

        Assert.Equal(warns, issues.Any(i => i.Code == "LOW_RETAIL_PRICE"));
    }

    [Fact]
    public void Quantities_beyond_the_sanity_limits_are_warnings()
    {
        var perStore = Validate(Line(quantities: [new("ST-1", null, 10_001m)]));
        var perProduct = Validate(Line(quantities: [new("ST-1", null, 9_000m), new("WH-1", null, 95_000m)]));

        Assert.Equal([(IssueSeverity.Warning, "LARGE_QUANTITY")], Codes(perStore.Where(i => i.Code == "LARGE_QUANTITY").Take(1)));
        Assert.Equal(2, perProduct.Count(i => i.Code == "LARGE_QUANTITY" && i.Severity == IssueSeverity.Warning));
    }

    [Fact]
    public void Header_text_fields_are_length_limited()
    {
        var header = Header with { OrderCode = new string('P', 101), Message = new string('m', 101) };

        var issues = ValidateWith(header, Line());

        Assert.Equal(2, issues.Count(i => i.Code == "FIELD_TOO_LONG"));
        Assert.Empty(ValidateWith(Header with { OrderCode = new string('P', 100), Message = new string('m', 100) }, Line()));
    }

    [Fact]
    public void An_order_dated_more_than_a_year_ago_warns_and_the_delivery_date_cannot_be_in_the_past()
    {
        var old = ValidateWith(Header with { OrderDate = new DateOnly(2025, 9, 30) }, Line());
        var recent = ValidateWith(Header with { OrderDate = new DateOnly(2025, 10, 1) }, Line());
        var past = ValidateWith(Header with { DeliveryDate = new DateOnly(2026, 9, 30) }, Line());
        var today = ValidateWith(Header with { DeliveryDate = new DateOnly(2026, 10, 1) }, Line());

        Assert.Equal([(IssueSeverity.Warning, "OLD_ORDER_DATE")], Codes(old));
        Assert.Empty(recent);
        Assert.Equal([(IssueSeverity.Error, "PAST_DELIVERY_DATE")], Codes(past));
        Assert.Empty(today);
    }
}

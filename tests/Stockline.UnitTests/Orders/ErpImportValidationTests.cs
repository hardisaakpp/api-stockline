using Stockline.Application.Erp;
using Stockline.Application.Orders;
using Stockline.Application.Orders.Import;
using Stockline.Domain.Orders;
using Stockline.UnitTests.Support;

namespace Stockline.UnitTests.Orders;

public class ErpImportValidationTests
{
    private sealed class FakeCatalog : IErpCatalog
    {
        public Dictionary<string, ErpItemInfo> Items { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? Error { get; set; }

        public int Lookups { get; private set; }

        public Task<ErpItemLookup> GetItemsAsync(IReadOnlyCollection<string> codes, CancellationToken ct)
        {
            Lookups++;
            return Task.FromResult(Error is null
                ? new ErpItemLookup(codes.Where(Items.ContainsKey).ToDictionary(c => c, c => Items[c], StringComparer.OrdinalIgnoreCase))
                : new ErpItemLookup(new Dictionary<string, ErpItemInfo>(), Error));
        }

        public Task<IReadOnlyList<ErpItemInfo>> SuggestItemsAsync(string code, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ErpItemInfo>>([.. Items.Values.Where(i => i.IsActive && i.Code.StartsWith(code.Split('-')[0], StringComparison.OrdinalIgnoreCase)).Take(limit)]);
    }

    private static OrderTemplateLine Line(string code, int row = 3, string pack = "X6", decimal? retail = 12.99m, decimal unitCost = 2.8m) =>
        new(row, code, null, null, pack, 5.6m, unitCost, 0m, retail, 2m, 2m, [new LocationQuantity("ST-1", null, 2m)]);

    private static OrderTemplate Template(params OrderTemplateLine[] lines) => new(
        new OrderTemplateHeader("PO-1", new DateOnly(2026, 9, 24), null, null, null, null, "ACME", null, null, null, null), lines, []);

    private static ErpItemInfo Item(string code, string? pack = "X6", decimal? price = 12.99m, bool active = true) =>
        new(code, "Name", "123", pack, price, active);

    [Fact]
    public async Task Known_active_products_with_matching_pack_and_price_produce_no_issues()
    {
        var catalog = new FakeCatalog { Items = { ["SKU-1"] = Item("SKU-1") } };

        var issues = await new ErpTemplateChecker(catalog).CheckAsync(Template(Line("sku-1")), false, default);

        Assert.Empty(issues);
    }

    [Fact]
    public async Task Unknown_products_are_errors_with_suggestions_taken_from_the_erp()
    {
        var catalog = new FakeCatalog { Items = { ["SHIRT-RED"] = Item("SHIRT-RED"), ["SHIRT-BLUE"] = Item("SHIRT-BLUE"), ["PANTS-1"] = Item("PANTS-1") } };

        var issues = await new ErpTemplateChecker(catalog).CheckAsync(Template(Line("SHIRT-GREEN", 4)), false, default);

        var issue = Assert.Single(issues);
        Assert.Equal((IssueSeverity.Error, "ERP_ITEM_NOT_FOUND", 4), (issue.Severity, issue.Code, issue.Row));
        Assert.Equal(["SHIRT-RED", "SHIRT-BLUE"], issue.Suggestions);
    }

    [Fact]
    public async Task Inactive_products_are_errors_that_do_not_suggest_themselves()
    {
        var catalog = new FakeCatalog { Items = { ["SKU-1"] = Item("SKU-1", active: false), ["SKU-2"] = Item("SKU-2") } };

        var issues = await new ErpTemplateChecker(catalog).CheckAsync(Template(Line("SKU-1")), false, default);

        var issue = Assert.Single(issues);
        Assert.Equal("ERP_ITEM_INACTIVE", issue.Code);
        Assert.Equal(["SKU-2"], issue.Suggestions);
    }

    [Fact]
    public async Task Pack_and_retail_price_differences_are_warnings_and_small_price_gaps_are_ignored()
    {
        var catalog = new FakeCatalog
        {
            Items = { ["A"] = Item("A", "X12", 12.99m), ["B"] = Item("B", "X6", 13.50m), ["C"] = Item("C", "X6", 12.995m), ["D"] = Item("D", null, null) },
        };

        var issues = await new ErpTemplateChecker(catalog).CheckAsync(
            Template(Line("A", 3), Line("B", 4), Line("C", 5), Line("D", 6)), false, default);

        Assert.Equal(
            [("ERP_PACK_MISMATCH", 3), ("ERP_RETAIL_PRICE_MISMATCH", 4)],
            issues.Select(i => (i.Code, i.Row!.Value)));
        Assert.All(issues, i => Assert.Equal(IssueSeverity.Warning, i.Severity));
    }

    [Theory]
    [InlineData(false, IssueSeverity.Warning)]
    [InlineData(true, IssueSeverity.Error)]
    public async Task An_unreachable_erp_only_warns_unless_it_is_required(bool require, IssueSeverity expected)
    {
        var catalog = new FakeCatalog { Error = "The ERP is unreachable." };

        var issue = Assert.Single(await new ErpTemplateChecker(catalog).CheckAsync(Template(Line("SKU-1")), require, default));

        Assert.Equal(("ERP_UNAVAILABLE", expected), (issue.Code, issue.Severity));
    }

    [Fact]
    public async Task Lines_without_quantities_are_not_looked_up()
    {
        var catalog = new FakeCatalog();
        var empty = Line("SKU-1") with { Quantities = [] };

        Assert.Empty(await new ErpTemplateChecker(catalog).CheckAsync(Template(empty), false, default));
        Assert.Equal(0, catalog.Lookups);
    }

    // ---- through the import handler -------------------------------------------------------------------------------------

    private sealed class Reader(OrderTemplate template) : IOrderTemplateReader
    {
        public OrderTemplate Read(Stream content) => template;
    }

    private sealed class Catalogs : ILocationCatalogProvider
    {
        public Task<LocationCatalog> GetAsync(CancellationToken ct) => Task.FromResult(new LocationCatalog(
            new Dictionary<string, LocationKind> { ["ST-1"] = LocationKind.Store }));
    }

    private static (ImportOrderHandler Handler, InMemoryStore Store, FakeCatalog Erp) Setup(OrderTemplate template)
    {
        var store = new InMemoryStore();
        var erp = new FakeCatalog { Items = { ["SKU-1"] = Item("SKU-1") } };
        var handler = new ImportOrderHandler(
            new Reader(template), FixedTimeProvider.Validator(), new Catalogs(), store, erpChecker: new ErpTemplateChecker(erp));
        return (handler, store, erp);
    }

    [Fact]
    public async Task The_erp_is_only_consulted_when_asked_to()
    {
        var (handler, store, erp) = Setup(Template(Line("UNKNOWN-1")));

        var plain = await handler.HandleAsync(Stream.Null, "f.xlsx", "u", ImportOptions.Default, default);

        Assert.Equal(ImportOutcome.Imported, plain.Outcome);
        Assert.Equal(0, erp.Lookups);
        Assert.Single(store.Orders);
    }

    [Fact]
    public async Task Erp_errors_stop_the_import_and_report_suggestions_and_nothing_is_saved()
    {
        var (handler, store, erp) = Setup(Template(Line("SKU-9")));
        erp.Items["SKU-2"] = Item("SKU-2");

        var result = await handler.HandleAsync(Stream.Null, "f.xlsx", "u", new ImportOptions(ValidateWithErp: true), default);

        Assert.Equal(ImportOutcome.ValidationFailed, result.Outcome);
        Assert.Equal(["SKU-1", "SKU-2"], result.Issues.Single(i => i.Code == "ERP_ITEM_NOT_FOUND").Suggestions);
        Assert.Empty(store.Orders);
    }

    [Fact]
    public async Task Corrections_replace_product_codes_before_validation_and_land_in_the_saved_order()
    {
        var (handler, store, _) = Setup(Template(Line("SKU-9")));

        var result = await handler.HandleAsync(
            Stream.Null, "f.xlsx", "u",
            new ImportOptions(ValidateWithErp: true, Corrections: new Dictionary<string, string> { ["sku-9"] = " SKU-1 " }), default);

        Assert.Equal(ImportOutcome.Imported, result.Outcome);
        Assert.Equal("SKU-1", Assert.Single(store.Orders).Lines[0].ProductCode);
    }

    [Fact]
    public async Task Erp_warnings_do_not_block_the_import_but_are_reported()
    {
        var (handler, store, erp) = Setup(Template(Line("SKU-1", pack: "X12", unitCost: 5.6m)));

        var result = await handler.HandleAsync(Stream.Null, "f.xlsx", "u", new ImportOptions(ValidateWithErp: true), default);

        Assert.Equal(ImportOutcome.Imported, result.Outcome);
        Assert.Contains(result.Issues, i => i.Code == "ERP_PACK_MISMATCH");
        Assert.Single(store.Orders);
        Assert.Equal(1, erp.Lookups);
    }

    [Fact]
    public async Task Local_validation_errors_are_reported_without_calling_the_erp()
    {
        var (handler, _, erp) = Setup(Template(Line("bad/code")));

        var result = await handler.HandleAsync(Stream.Null, "f.xlsx", "u", new ImportOptions(ValidateWithErp: true), default);

        Assert.Equal(ImportOutcome.ValidationFailed, result.Outcome);
        Assert.Equal(0, erp.Lookups);
    }
}

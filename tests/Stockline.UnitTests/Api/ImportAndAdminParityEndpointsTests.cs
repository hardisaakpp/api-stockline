using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Stockline.Domain.Orders;

namespace Stockline.UnitTests.Api;

public class ImportAndAdminParityEndpointsTests : IClassFixture<StocklineApiFactory>
{
    private readonly StocklineApiFactory _factory;

    public ImportAndAdminParityEndpointsTests(StocklineApiFactory factory) => _factory = factory;

    private HttpClient Admin() => _factory.ClientAs("Buyer", admin: true);

    private static async Task<JsonElement> Body(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static MultipartFormDataContent InvalidTemplate(string code, string storeColumn = "ST-NOPE")
    {
        // The rich template with an unknown store column: UNKNOWN_LOCATION.
        using var workbook = new XLWorkbook();
        var header = workbook.AddWorksheet("cabecera");
        header.Cell("A1").Value = "CAMPO";
        header.Cell("A2").Value = "CODIGO PEDIDO";
        header.Cell("B2").Value = code;
        header.Cell("A3").Value = "FECHA DE PEDIDO";
        header.Cell("B3").Value = DateTime.Today;
        header.Cell("A4").Value = "NOMBRE PROVEEDOR";
        header.Cell("B4").Value = "ACME SUPPLIER";
        var detail = workbook.AddWorksheet("detalle");
        detail.Cell("A1").Value = "Codigo Producto";
        detail.Cell("B1").Value = "Pack";
        detail.Cell("C1").Value = "Tienda";
        detail.Cell("C2").Value = storeColumn;
        detail.Cell("A3").Value = "SKU-1";
        detail.Cell("B3").Value = "X6";
        detail.Cell("C3").Value = 2;
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return StocklineApiFactory.Upload(stream.ToArray(), "bad.xlsx");
    }

    // ---- dry run ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Validation_dry_run_reports_findings_without_saving_anything()
    {
        var valid = await Body(await _factory.ClientAs("Buyer").PostAsync("/orders/import/validate", StocklineApiFactory.PricedTemplateUpload("DRY-1")));
        var invalid = await Body(await _factory.ClientAs("Buyer").PostAsync("/orders/import/validate", InvalidTemplate("DRY-2")));

        Assert.True(valid.GetProperty("valid").GetBoolean());
        Assert.Equal(("Valid", 0), (valid.GetProperty("outcome").GetString(), valid.GetProperty("errors").GetInt32()));
        Assert.False(invalid.GetProperty("valid").GetBoolean());
        Assert.Equal("ValidationFailed", invalid.GetProperty("outcome").GetString());
        Assert.Contains(invalid.GetProperty("issues").EnumerateArray(), i => i.GetProperty("code").GetString() == "UNKNOWN_LOCATION");
        Assert.False((await Body(await _factory.ClientAs("Buyer").GetAsync("/orders/exists?code=DRY-1"))).GetProperty("exists").GetBoolean());
    }

    [Fact]
    public async Task Validation_dry_run_detects_duplicates_and_can_return_the_log_as_a_workbook()
    {
        await _factory.ClientAs("Buyer").PostAsync("/orders/import", StocklineApiFactory.PricedTemplateUpload("DRY-DUP"));

        var duplicate = await Body(await _factory.ClientAs("Buyer").PostAsync("/orders/import/validate", StocklineApiFactory.PricedTemplateUpload("DRY-DUP")));
        var log = await _factory.ClientAs("Buyer").PostAsync("/orders/import/validate?format=xlsx", InvalidTemplate("DRY-LOG"));

        Assert.Equal("DuplicateOrderCode", duplicate.GetProperty("outcome").GetString());
        Assert.Equal(HttpStatusCode.OK, log.StatusCode);
        Assert.StartsWith("validation-log-bad", log.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        using var workbook = new XLWorkbook(await log.Content.ReadAsStreamAsync());
        Assert.Equal(["Summary", "Issues"], workbook.Worksheets.Select(w => w.Name));
        Assert.Equal(1, workbook.Worksheet("Summary").Cell(2, 2).GetDouble());
        Assert.Equal("Error", workbook.Worksheet("Issues").Cell(2, 1).GetString());
        Assert.Equal("UNKNOWN_LOCATION", workbook.Worksheet("Issues").Cell(2, 2).GetString());
    }

    [Fact]
    public async Task Validation_dry_run_enforces_the_same_input_rules_and_the_buyer_role()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _factory.ClientAs("Buyer").PostAsync("/orders/import/validate", new MultipartFormDataContent())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _factory.ClientAs("Buyer").PostAsync("/orders/import/validate", StocklineApiFactory.Upload([1, 2, 3], "x.txt"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _factory.ClientAs("Buyer").PostAsync("/orders/import/validate", StocklineApiFactory.Upload([1, 2, 3], "x.xlsx"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _factory.ClientAs("Buyer").PostAsync("/orders/import/validate?format=pdf", StocklineApiFactory.PricedTemplateUpload("DRY-F"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await _factory.ClientAs("Supplier").PostAsync("/orders/import/validate", StocklineApiFactory.PricedTemplateUpload("DRY-R"))).StatusCode);
    }

    // ---- proposal ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Proposal_endpoint_scales_the_distribution_to_the_confirmed_quantity_and_requires_the_right_status()
    {
        var import = await _factory.ClientAs("Buyer").PostAsync("/orders/import", StocklineApiFactory.PricedTemplateUpload("PROP-1"));
        var id = (await Body(import)).GetProperty("orderId").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await _factory.ClientAs("Buyer").GetAsync($"/orders/{id}/redistribution/proposal")).StatusCode);
        await _factory.ClientAs("Buyer").PostAsync($"/orders/{id}/actions/submit", null);
        await _factory.ClientAs("Supplier").PostAsync($"/orders/{id}/actions/open", null);
        await _factory.ClientAs("Supplier").PatchAsync($"/orders/{id}/lines/SKU-1/confirmed-quantity", JsonContent.Create(new { quantity = 5 }));
        await _factory.ClientAs("Supplier").PostAsJsonAsync($"/orders/{id}/confirm", new { acknowledgeDifference = true });

        var proposal = await Body(await _factory.ClientAs("Buyer").GetAsync($"/orders/{id}/redistribution/proposal"));

        var allocations = proposal.GetProperty("lines")[0].GetProperty("allocations").EnumerateArray()
            .ToDictionary(a => a.GetProperty("locationCode").GetString()!, a => a.GetProperty("quantity").GetDecimal());
        Assert.Equal(new Dictionary<string, decimal> { ["WH-MAIN"] = 3m, ["ST-001"] = 2m }, allocations);   // 6 and 4 scaled by 5/10
        var confirm = await _factory.ClientAs("Buyer").PostAsJsonAsync($"/orders/{id}/redistribution/confirm", new
        {
            lines = new[] { new { productCode = "SKU-1", allocations = allocations.Select(a => new { locationCode = a.Key, quantity = a.Value }) } },
        });
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _factory.ClientAs("Buyer").GetAsync($"/orders/{Guid.NewGuid()}/redistribution/proposal")).StatusCode);
    }

    // ---- last created, source file info ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Last_created_order_can_be_limited_to_the_callers_own_uploads_and_the_source_file_has_info()
    {
        var (buyer, user, _) = await _factory.SignedInAsync(ActorRole.Buyer);
        var (otherBuyer, _, _) = await _factory.SignedInAsync(ActorRole.Buyer);
        var mine = await buyer.PostAsync("/orders/import", StocklineApiFactory.PricedTemplateUpload("LASTC-MINE"));
        var id = (await Body(mine)).GetProperty("orderId").GetGuid();
        await Task.Delay(20);
        await otherBuyer.PostAsync("/orders/import", StocklineApiFactory.PricedTemplateUpload("LASTC-OTHER"));

        var lastMine = await Body(await buyer.GetAsync("/orders/last-created?mine=true"));
        var lastAny = await Body(await buyer.GetAsync("/orders/last-created"));
        var info = await Body(await buyer.GetAsync($"/orders/{id}/source-file/info"));

        Assert.Equal("LASTC-MINE", lastMine.GetProperty("code").GetString());
        Assert.Equal(user.Id.ToString(), lastMine.GetProperty("createdBy").GetString());
        Assert.Equal("LASTC-OTHER", lastAny.GetProperty("code").GetString());
        Assert.Equal("priced.xlsx", info.GetProperty("fileName").GetString());
        Assert.True(info.GetProperty("sizeBytes").GetInt32() > 100);
        Assert.Equal(user.Id.ToString(), info.GetProperty("uploadedBy").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientAs("Supplier").GetAsync($"/orders/{id}/source-file/info")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _factory.ClientAs("Buyer").GetAsync($"/orders/{Guid.NewGuid()}/source-file/info")).StatusCode);
    }

    // ---- location administration -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Admin_creates_and_edits_locations_that_become_available_to_imports_and_search()
    {
        var created = await Admin().PostAsJsonAsync("/admin/locations", new { code = "st-777", name = "New mall", kind = "Store", supplyingWarehouseCode = "wh-main" });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var location = await Body(created);
        Assert.Equal(("ST-777", "Store", "WH-MAIN", true), (location.GetProperty("code").GetString(), location.GetProperty("kind").GetString(), location.GetProperty("supplyingWarehouseCode").GetString(), location.GetProperty("isActive").GetBoolean()));
        Assert.Equal(["ST-777"], (await Body(await _factory.ClientAs("Buyer").GetAsync("/locations?search=777"))).EnumerateArray().Select(l => l.GetProperty("code").GetString()));

        var updated = await Admin().PatchAsJsonAsync("/admin/locations/st-777", new { name = "New mall (closed)", supplyingWarehouseCode = "WH-OUTLET", isActive = false });

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Empty((await Body(await _factory.ClientAs("Buyer").GetAsync("/locations?search=777"))).EnumerateArray());
        var all = (await Body(await Admin().GetAsync("/admin/locations"))).EnumerateArray().ToList();
        Assert.Contains(all, l => l.GetProperty("code").GetString() == "ST-777" && !l.GetProperty("isActive").GetBoolean());
        Assert.Contains(all, l => l.GetProperty("code").GetString() == "ST-099");   // inactive ones are listed for admins
    }

    [Fact]
    public async Task Location_administration_validates_input_and_is_admin_only()
    {
        Assert.Equal(HttpStatusCode.Conflict,
            (await Admin().PostAsJsonAsync("/admin/locations", new { code = "WH-MAIN", name = "Dup", kind = "Warehouse" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await Admin().PostAsJsonAsync("/admin/locations", new { code = "ST-X1", name = "No warehouse", kind = "Store" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await Admin().PostAsJsonAsync("/admin/locations", new { code = "ST-X2", name = "Bad warehouse", kind = "Store", supplyingWarehouseCode = "ST-001" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await Admin().PostAsJsonAsync("/admin/locations", new { code = "ST-X3", name = "Missing warehouse", kind = "Store", supplyingWarehouseCode = "NOPE" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await Admin().PostAsJsonAsync("/admin/locations", new { code = "bad code", name = "x", kind = "Warehouse" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await Admin().PostAsJsonAsync("/admin/locations", new { code = "WH-X", name = "x", kind = "Moon" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await Admin().PatchAsJsonAsync("/admin/locations/NOPE", new { name = "x", isActive = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await Admin().PatchAsJsonAsync("/admin/locations/ST-001", new { name = "x", isActive = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientAs("Buyer").GetAsync("/admin/locations")).StatusCode);
    }

    [Fact]
    public async Task A_location_added_by_an_admin_is_accepted_by_the_next_import()
    {
        var before = await Body(await _factory.ClientAs("Buyer").PostAsync("/orders/import/validate", InvalidTemplate("DRY-LATE-1", "ST-LATER")));
        await Admin().PostAsJsonAsync("/admin/locations", new { code = "ST-LATER", name = "Added later", kind = "Store", supplyingWarehouseCode = "WH-MAIN" });

        var after = await Body(await _factory.ClientAs("Buyer").PostAsync("/orders/import/validate", InvalidTemplate("DRY-LATE-2", "ST-LATER")));

        Assert.False(before.GetProperty("valid").GetBoolean());
        Assert.True(after.GetProperty("valid").GetBoolean());
    }

    // ---- notification administration --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Email_topics_can_be_previewed_and_the_live_subscriber_count_is_available_to_admins()
    {
        var preview = await Body(await Admin().GetAsync("/admin/notifications/topics/ProductionConfirmed/preview"));
        var status = await Body(await Admin().GetAsync("/admin/notifications/status"));

        Assert.Equal("Order confirmed by production - PO-2026-0001", preview.GetProperty("subject").GetString());
        Assert.Contains("<table", preview.GetProperty("html").GetString());
        Assert.Contains("PO-2026-0001", preview.GetProperty("text").GetString());
        Assert.True(status.GetProperty("liveSubscribers").GetInt32() >= 0);
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin().GetAsync("/admin/notifications/topics/Nope/preview")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientAs("Buyer").GetAsync("/admin/notifications/status")).StatusCode);
    }
}

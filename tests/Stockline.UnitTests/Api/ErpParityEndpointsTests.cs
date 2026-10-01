using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Stockline.Application.Erp;

namespace Stockline.UnitTests.Api;

public class ErpParityEndpointsTests : IClassFixture<StocklineApiFactory>
{
    private readonly StocklineApiFactory _factory;

    public ErpParityEndpointsTests(StocklineApiFactory factory)
    {
        _factory = factory;
        factory.Erp.Override = null;
        factory.Erp.MasterData = MasterDataResult.Ok;
        factory.Erp.CatalogError = null;
    }

    private Task<HttpResponseMessage> Act(string role, Guid id, string action) =>
        _factory.ClientAs(role).PostAsync($"/orders/{id}/actions/{action}", null);

    private async Task<Guid> ReadyForErp(string code)
    {
        var import = await _factory.ClientAs("Buyer").PostAsync("/orders/import", StocklineApiFactory.RichTemplateUpload(code));
        var id = (await import.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("orderId").GetGuid();
        await Act("Buyer", id, "submit");
        await Act("Supplier", id, "open");
        await _factory.ClientAs("Supplier").PostAsJsonAsync($"/orders/{id}/confirm", new { });
        var plan = new
        {
            lines = new object[]
            {
                new { productCode = "SKU-1", allocations = new[] { new { locationCode = "WH-MAIN", quantity = 2m }, new { locationCode = "ST-001", quantity = 1m }, new { locationCode = "ST-002", quantity = 1m } } },
                new { productCode = "SKU-2", allocations = new[] { new { locationCode = "ST-001", quantity = 3m } } },
            },
        };
        Assert.Equal(HttpStatusCode.OK, (await _factory.ClientAs("Buyer").PostAsJsonAsync($"/orders/{id}/redistribution/confirm", plan)).StatusCode);
        return id;
    }

    private async Task<JsonElement> WaitForStatus(Guid id, string status)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        JsonElement last;
        do
        {
            last = await (await _factory.ClientAs("Buyer").GetAsync($"/orders/{id}/erp-submission")).Content.ReadFromJsonAsync<JsonElement>();
            if (last.GetProperty("orderStatus").GetString() == status)
            {
                return last;
            }

            await Task.Delay(50);
        }
        while (DateTime.UtcNow < deadline);

        throw new Xunit.Sdk.XunitException($"Order did not reach {status}.");
    }

    private async Task<Guid> Sent(string code)
    {
        var id = await ReadyForErp(code);
        await _factory.ClientAs("Buyer").PostAsync($"/orders/{id}/erp-submission", null);
        await WaitForStatus(id, "Sent");
        return id;
    }

    private async Task<Guid> PurchaseOrderId(Guid orderId) =>
        (await (await _factory.ClientAs("Buyer").GetAsync($"/orders/{orderId}/erp-submission")).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("documents").EnumerateArray().First(d => d.GetProperty("kind").GetString() == "PurchaseOrder").GetProperty("id").GetGuid();

    // ---- printing -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Purchase_order_can_be_downloaded_as_pdf_but_a_transfer_cannot()
    {
        var id = await Sent("PDF-1");
        var purchaseOrder = await PurchaseOrderId(id);
        var documents = (await (await _factory.ClientAs("Buyer").GetAsync($"/orders/{id}/erp-submission")).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("documents").EnumerateArray().ToList();
        var transfer = documents.First(d => d.GetProperty("kind").GetString() == "TransferRequest").GetProperty("id").GetGuid();

        var pdf = await _factory.ClientAs("Supplier").GetAsync($"/orders/{id}/erp-documents/{purchaseOrder}/pdf");

        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString((await pdf.Content.ReadAsByteArrayAsync())[..5]));
        Assert.Equal(HttpStatusCode.BadRequest, (await _factory.ClientAs("Buyer").GetAsync($"/orders/{id}/erp-documents/{transfer}/pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _factory.ClientAs("Buyer").GetAsync($"/orders/{id}/erp-documents/{Guid.NewGuid()}/pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientAs("SupplierProduction").GetAsync($"/orders/{id}/erp-documents/{purchaseOrder}/pdf")).StatusCode);
    }

    [Fact]
    public async Task Ready_for_dispatch_email_carries_the_purchase_order_pdf()
    {
        await _factory.ClientAs("Buyer", admin: true).PostAsJsonAsync(
            "/admin/notifications/subscriptions", new { email = "dispatch@example.com", topic = "ReadyForDispatch" });

        var id = await Sent("PDF-MAIL");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Stockline.Application.Notifications.EmailMessage? message = null;
        while (message is null && !cts.IsCancellationRequested)
        {
            message = _factory.Emails.Sent.FirstOrDefault(m => m.Subject.EndsWith("PDF-MAIL") && m.To.Contains("dispatch@example.com"));
            await Task.Delay(50);
        }

        Assert.NotNull(message);
        var attachment = Assert.Single(message.Attachments!);
        Assert.StartsWith("purchase-order-", attachment.FileName);
        Assert.Equal("application/pdf", attachment.ContentType);
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(attachment.Content, 0, 5));
        Assert.NotEqual(Guid.Empty, id);
    }

    // ---- integration views ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Integration_workbook_lists_documents_lines_and_attempts()
    {
        var id = await Sent("INTEG-1");

        var bytes = await _factory.ClientAs("Buyer").GetByteArrayAsync($"/orders/{id}/erp-submission/export");

        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        Assert.Equal(["Order information", "Documents", "Lines", "Attempts"], workbook.Worksheets.Select(w => w.Name));
        var documents = workbook.Worksheet("Documents");
        Assert.Equal("PurchaseOrder", documents.Cell(2, 1).GetString());
        Assert.Equal("Sent", documents.Cell(2, 4).GetString());
        Assert.True(documents.Cell(2, 6).GetDouble() > 0);
        Assert.Equal(3, workbook.Worksheet("Attempts").LastRowUsed()!.RowNumber() - 1);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientAs("SupplierProduction").GetAsync($"/orders/{id}/erp-submission/export")).StatusCode);
    }

    [Fact]
    public async Task Document_list_filters_by_order_status_kind_and_pending_retry()
    {
        var ok = await Sent("DOCS-OK");
        _factory.Erp.Override = call => call.StartsWith("TR:") ? Stockline.Application.Erp.ErpResult.Failure("boom", isTransient: true) : null;
        var partial = await ReadyForErp("DOCS-PARTIAL");
        await _factory.ClientAs("Buyer").PostAsync($"/orders/{partial}/erp-submission", null);
        await WaitForStatus(partial, "LogisticsPending");
        _factory.Erp.Override = _ => Stockline.Application.Erp.ErpResult.Failure("down", isTransient: true);

        async Task<List<JsonElement>> Items(string query) =>
            (await (await _factory.ClientAs("Supplier").GetAsync($"/erp/documents?{query}")).Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("items").EnumerateArray().ToList();

        Assert.Equal(["DOCS-OK", "DOCS-OK", "DOCS-OK"], (await Items("orderCode=DOCS-OK")).Select(i => i.GetProperty("orderCode").GetString()));
        Assert.Equal(2, (await Items("orderCode=DOCS-PARTIAL&status=Failed")).Count);
        Assert.Single(await Items("orderCode=DOCS-PARTIAL&kind=PurchaseOrder"));
        Assert.Equal(["DOCS-PARTIAL", "DOCS-PARTIAL"], (await Items("retryPending=true&orderCode=DOCS-")).Select(i => i.GetProperty("orderCode").GetString()));
        Assert.Equal(HttpStatusCode.BadRequest, (await _factory.ClientAs("Buyer").GetAsync("/erp/documents?kind=nope")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _factory.ClientAs("Buyer").GetAsync("/erp/documents?status=nope")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientAs("SupplierProduction").GetAsync("/erp/documents")).StatusCode);
        Assert.NotEqual(Guid.Empty, ok);
    }

    [Fact]
    public async Task Attempt_log_records_every_try_with_its_result_and_can_be_filtered()
    {
        _factory.Erp.Override = call => call.StartsWith("PO:") ? ErpResult.Failure("ERP down", isTransient: true) : null;
        var id = await ReadyForErp("LOG-1");
        await _factory.ClientAs("Buyer").PostAsync($"/orders/{id}/erp-submission", null);
        await WaitForStatus(id, "ErpSubmissionFailed");
        _factory.Erp.Override = null;
        await _factory.ClientAs("Buyer").PostAsync($"/orders/{id}/erp-submission", null);
        await WaitForStatus(id, "Sent");

        async Task<List<JsonElement>> Attempts(string query) =>
            (await (await _factory.ClientAs("Buyer").GetAsync($"/erp/attempts?orderCode=LOG-1&{query}")).Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("items").EnumerateArray().ToList();

        var all = await Attempts(string.Empty);
        var failed = await Attempts("succeeded=false");
        var succeeded = await Attempts("succeeded=true");

        Assert.Equal(4, all.Count);   // failed PO, then PO + 2 transfers
        var failure = Assert.Single(failed);
        Assert.Equal(("ERP down", true, "PurchaseOrder"), (failure.GetProperty("error").GetString(), failure.GetProperty("transient").GetBoolean(), failure.GetProperty("kind").GetString()));
        Assert.Equal(3, succeeded.Count);
        Assert.Empty(await Attempts($"from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"))}"));
        Assert.Equal(4, (await Attempts($"from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O"))}&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"))}")).Count);
    }

    // ---- product code correction ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Buyer_corrects_a_product_the_erp_rejected_and_the_resubmission_uses_the_new_code()
    {
        _factory.Erp.MasterData = new MasterDataResult(["items not found: SKU-2"]);
        var id = await ReadyForErp("FIX-1");
        await _factory.ClientAs("Buyer").PostAsync($"/orders/{id}/erp-submission", null);
        await WaitForStatus(id, "ErpSubmissionFailed");
        _factory.Erp.MasterData = MasterDataResult.Ok;

        var fix = await _factory.ClientAs("Buyer").PutAsJsonAsync($"/orders/{id}/lines/SKU-2/product-code", new { newCode = "SKU-2B" });
        await _factory.ClientAs("Buyer").PostAsync($"/orders/{id}/erp-submission", null);
        var done = await WaitForStatus(id, "Sent");

        Assert.Equal(HttpStatusCode.OK, fix.StatusCode);
        var codes = (await fix.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("productCode").GetString());
        Assert.Contains("SKU-2B", codes);
        var sentCodes = done.GetProperty("documents").EnumerateArray()
            .SelectMany(d => d.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("productCode").GetString())).Distinct().Order();
        Assert.Equal(["SKU-1", "SKU-2B"], sentCodes);
        var audit = await (await _factory.ClientAs("Buyer").GetAsync($"/orders/{id}/audit-log")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(audit.EnumerateArray(), e => e.GetProperty("field").GetString() == "ProductCode"
                                                     && e.GetProperty("oldValue").GetString() == "SKU-2"
                                                     && e.GetProperty("newValue").GetString() == "SKU-2B");
    }

    [Fact]
    public async Task Product_code_correction_is_refused_unless_the_erp_failed_the_buyer_asks_and_the_erp_knows_the_code()
    {
        var healthy = await ReadyForErp("FIX-STATE");
        Assert.Equal(HttpStatusCode.Conflict,
            (await _factory.ClientAs("Buyer").PutAsJsonAsync($"/orders/{healthy}/lines/SKU-2/product-code", new { newCode = "X" })).StatusCode);

        _factory.Erp.MasterData = new MasterDataResult(["items not found: SKU-2"]);
        var failed = await ReadyForErp("FIX-RULES");
        await _factory.ClientAs("Buyer").PostAsync($"/orders/{failed}/erp-submission", null);
        await WaitForStatus(failed, "ErpSubmissionFailed");

        _factory.Erp.MasterData = new MasterDataResult(["items not found: SKU-9"]);
        var unknown = await _factory.ClientAs("Buyer").PutAsJsonAsync($"/orders/{failed}/lines/SKU-2/product-code", new { newCode = "SKU-9" });
        _factory.Erp.MasterData = MasterDataResult.Ok;
        var duplicate = await _factory.ClientAs("Buyer").PutAsJsonAsync($"/orders/{failed}/lines/SKU-2/product-code", new { newCode = "sku-1" });
        var malformed = await _factory.ClientAs("Buyer").PutAsJsonAsync($"/orders/{failed}/lines/SKU-2/product-code", new { newCode = "bad/code" });
        var wrongRole = await _factory.ClientAs("Supplier").PutAsJsonAsync($"/orders/{failed}/lines/SKU-2/product-code", new { newCode = "SKU-3" });
        var missingLine = await _factory.ClientAs("Buyer").PutAsJsonAsync($"/orders/{failed}/lines/NOPE/product-code", new { newCode = "SKU-3" });
        _factory.Erp.MasterData = new MasterDataResult([], "The ERP is unreachable.", IsTransient: true);
        var erpDown = await _factory.ClientAs("Buyer").PutAsJsonAsync($"/orders/{failed}/lines/SKU-2/product-code", new { newCode = "SKU-3" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
        Assert.Equal("ERP_ITEM_NOT_FOUND", (await unknown.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("issues")[0].GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, malformed.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, wrongRole.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missingLine.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, erpDown.StatusCode);
    }

    [Fact]
    public async Task Order_response_carries_totals_and_hides_the_cost_total_from_production()
    {
        var response = await _factory.ClientAs("Buyer").PostAsync("/orders/import", StocklineApiFactory.PricedTemplateUpload("TOTALS-1"));
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("orderId").GetGuid();
        await Act("Buyer", id, "submit");

        var buyer = (await (await _factory.ClientAs("Buyer").GetAsync($"/orders/{id}")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("totals");
        var production = (await (await _factory.ClientAs("SupplierProduction").GetAsync($"/orders/{id}")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("totals");

        Assert.Equal((10m, 10m, 10m, 4m), (buyer.GetProperty("requestedQuantity").GetDecimal(), buyer.GetProperty("confirmedQuantity").GetDecimal(), buyer.GetProperty("allocatedQuantity").GetDecimal(), buyer.GetProperty("storeQuantity").GetDecimal()));
        Assert.Equal(90m, buyer.GetProperty("totalCost").GetDecimal());   // 10 units x unit cost 10 x (1 - 10%)
        Assert.Equal(JsonValueKind.Null, production.GetProperty("totalCost").ValueKind);
        Assert.Equal(10m, production.GetProperty("requestedQuantity").GetDecimal());
    }

    // ---- import-time ERP validation ---------------------------------------------------------------------------------------

    private static StringContent Corrections(string json) => new(json);

    private MultipartFormDataContent WithCorrections(MultipartFormDataContent upload, string json)
    {
        upload.Add(Corrections(json), "corrections");
        return upload;
    }

    [Fact]
    public async Task Import_checks_products_against_the_erp_when_asked_and_offers_suggestions()
    {
        _factory.Erp.Items.Clear();
        _factory.Erp.Items["SKU-1"] = new ErpItemInfo("SKU-1", "Tee", "123", "X6", 12.99m, true);
        _factory.Erp.Items["SKU-1B"] = new ErpItemInfo("SKU-1B", "Tee B", "124", "X6", 12.99m, true);

        var unchecked_ = await _factory.ClientAs("Buyer").PostAsync("/orders/import", StocklineApiFactory.RichTemplateUpload("ERPCHK-0"));
        var checkedImport = await _factory.ClientAs("Buyer").PostAsync("/orders/import?validateErp=true", StocklineApiFactory.RichTemplateUpload("ERPCHK-1"));

        Assert.Equal(HttpStatusCode.Created, unchecked_.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, checkedImport.StatusCode);
        var issues = (await checkedImport.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("issues").EnumerateArray().ToList();
        var missing = Assert.Single(issues, i => i.GetProperty("code").GetString() == "ERP_ITEM_NOT_FOUND" && i.GetProperty("row").GetInt32() == 4);
        Assert.Equal(["SKU-1", "SKU-1B"], missing.GetProperty("suggestions").EnumerateArray().Select(x => x.GetString()));
        Assert.False((await (await _factory.ClientAs("Buyer").GetAsync("/orders/exists?code=ERPCHK-1")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("exists").GetBoolean());
    }

    [Fact]
    public async Task Corrections_sent_with_the_file_fix_codes_before_the_erp_check()
    {
        _factory.Erp.Items.Clear();
        _factory.Erp.Items["SKU-1"] = new ErpItemInfo("SKU-1", "Tee", "123", "X6", null, true);
        _factory.Erp.Items["SKU-2-FIXED"] = new ErpItemInfo("SKU-2-FIXED", "Hoodie", "124", "X2", null, true);

        var response = await _factory.ClientAs("Buyer").PostAsync(
            "/orders/import?validateErp=true",
            WithCorrections(StocklineApiFactory.RichTemplateUpload("ERPFIX-1"), """{"sku-2":"SKU-2-FIXED"}"""));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("orderId").GetGuid();
        var order = await (await _factory.ClientAs("Buyer").GetAsync($"/orders/{id}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["SKU-1", "SKU-2-FIXED"], order.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("productCode").GetString()));
    }

    [Fact]
    public async Task Malformed_corrections_and_an_unreachable_erp_are_handled()
    {
        var badJson = await _factory.ClientAs("Buyer").PostAsync(
            "/orders/import", WithCorrections(StocklineApiFactory.RichTemplateUpload("ERPBAD-1"), "not json"));
        var notAnObject = await _factory.ClientAs("Buyer").PostAsync(
            "/orders/import", WithCorrections(StocklineApiFactory.RichTemplateUpload("ERPBAD-2"), "[1,2]"));

        _factory.Erp.CatalogError = "The ERP is unreachable.";
        try
        {
            var lenient = await _factory.ClientAs("Buyer").PostAsync("/orders/import?validateErp=true", StocklineApiFactory.RichTemplateUpload("ERPDOWN-1"));
            var strict = await _factory.ClientAs("Buyer").PostAsync("/orders/import?validateErp=true&requireErp=true", StocklineApiFactory.RichTemplateUpload("ERPDOWN-2"));

            Assert.Equal(HttpStatusCode.BadRequest, badJson.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, notAnObject.StatusCode);
            Assert.Equal(HttpStatusCode.Created, lenient.StatusCode);
            Assert.Contains((await lenient.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("warnings").EnumerateArray(), w => w.GetProperty("code").GetString() == "ERP_UNAVAILABLE");
            Assert.Equal(HttpStatusCode.UnprocessableEntity, strict.StatusCode);
        }
        finally
        {
            _factory.Erp.CatalogError = null;
        }
    }

    [Fact]
    public async Task Product_information_can_be_looked_up_in_the_erp()
    {
        _factory.Erp.Items.Clear();
        _factory.Erp.Items["SKU-1"] = new ErpItemInfo("SKU-1", "Tee", "123", "X6", 12.99m, true);

        var response = await _factory.ClientAs("Buyer").GetAsync("/erp/items?codes=SKU-1,NOPE,sku-1");
        var items = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();

        Assert.Equal(["SKU-1", "NOPE"], items.Select(i => i.GetProperty("code").GetString()));
        Assert.Equal((true, "X6", 12.99m), (items[0].GetProperty("found").GetBoolean(), items[0].GetProperty("pack").GetString(), items[0].GetProperty("retailPrice").GetDecimal()));
        Assert.False(items[1].GetProperty("found").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await _factory.ClientAs("Buyer").GetAsync("/erp/items")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _factory.ClientAs("Buyer").GetAsync($"/erp/items?codes={string.Join(',', Enumerable.Range(0, 101))}")).StatusCode);
        _factory.Erp.CatalogError = "down";
        try
        {
            Assert.Equal(HttpStatusCode.BadGateway, (await _factory.ClientAs("Buyer").GetAsync("/erp/items?codes=SKU-1")).StatusCode);
        }
        finally
        {
            _factory.Erp.CatalogError = null;
        }
    }
}

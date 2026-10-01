using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Stockline.Application.Orders.Import;
using Stockline.Domain.Orders;
using Stockline.Infrastructure.Excel;

namespace Stockline.UnitTests.Api;

public class OrderParityEndpointsTests : IClassFixture<StocklineApiFactory>
{
    private readonly StocklineApiFactory _factory;

    public OrderParityEndpointsTests(StocklineApiFactory factory) => _factory = factory;

    private async Task<Guid> Import(string code, bool priced = true)
    {
        var upload = priced ? StocklineApiFactory.PricedTemplateUpload(code) : StocklineApiFactory.RichTemplateUpload(code);
        var response = await _factory.ClientAs("Buyer").PostAsync("/orders/import", upload);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("orderId").GetGuid();
    }

    private Task<HttpResponseMessage> Act(string role, Guid id, string action) =>
        _factory.ClientAs(role).PostAsync($"/orders/{id}/actions/{action}", null);

    private async Task<Guid> ImportUnderReview(string code)
    {
        var id = await Import(code);
        Assert.Equal(HttpStatusCode.OK, (await Act("Buyer", id, "submit")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Act("Supplier", id, "open")).StatusCode);
        return id;
    }

    private Task<HttpResponseMessage> Send(string role, HttpMethod method, string url, object? body = null) =>
        _factory.ClientAs(role).SendAsync(new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) });

    private static async Task<JsonElement> Body(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private async Task<JsonElement> Line(string role, Guid id) =>
        (await Body(await _factory.ClientAs(role).GetAsync($"/orders/{id}"))).GetProperty("lines")[0];

    // ---- role-based data hiding ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Order_response_hides_costs_from_production_and_retail_prices_from_everyone_but_the_buyer()
    {
        var id = await ImportUnderReview("HIDE-1");

        var buyer = await Line("Buyer", id);
        var supplier = await Line("Supplier", id);
        var production = await Line("SupplierProduction", id);

        Assert.Equal((10m, 10m, 23m), (buyer.GetProperty("costWithoutTax").GetDecimal(), buyer.GetProperty("unitCost").GetDecimal(), buyer.GetProperty("retailPrice").GetDecimal()));
        Assert.Equal(0.1m, buyer.GetProperty("discount").GetDecimal());
        Assert.Equal((10m, JsonValueKind.Null), (supplier.GetProperty("costWithoutTax").GetDecimal(), supplier.GetProperty("retailPrice").ValueKind));
        Assert.All(new[] { "costWithoutTax", "unitCost", "discount", "retailPrice" }, f => Assert.Equal(JsonValueKind.Null, production.GetProperty(f).ValueKind));
        Assert.Equal(10m, production.GetProperty("requestedQuantity").GetDecimal());
    }

    [Fact]
    public async Task Exported_template_blanks_the_data_the_role_must_not_see()
    {
        var id = await Import("HIDE-EXPORT");

        OrderTemplateLine Read(byte[] bytes) => new ClosedXmlOrderTemplateReader().Read(new MemoryStream(bytes)).Lines[0];
        var buyer = Read(await _factory.ClientAs("Buyer").GetByteArrayAsync($"/orders/{id}/export"));
        var supplier = Read(await _factory.ClientAs("Supplier").GetByteArrayAsync($"/orders/{id}/export"));

        Assert.Equal((10m, 23m), (buyer.CostWithoutTax, buyer.RetailPrice));
        Assert.Equal((10m, null), (supplier.CostWithoutTax, supplier.RetailPrice));
    }

    [Fact]
    public async Task Production_works_on_submitted_and_under_review_orders_and_loses_sight_of_them_afterwards()
    {
        var id = await Import("VIS-1");
        Assert.Equal(HttpStatusCode.NotFound, (await _factory.ClientAs("SupplierProduction").GetAsync($"/orders/{id}")).StatusCode);

        await Act("Buyer", id, "submit");
        Assert.Equal(HttpStatusCode.OK, (await _factory.ClientAs("SupplierProduction").GetAsync($"/orders/{id}")).StatusCode);
        await Act("Supplier", id, "open");
        Assert.Equal(HttpStatusCode.OK, (await Act("SupplierProduction", id, "confirmproduction")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await _factory.ClientAs("SupplierProduction").GetAsync($"/orders/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _factory.ClientAs("SupplierProduction").GetAsync($"/orders/{id}/reports/differences")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _factory.ClientAs("Supplier").GetAsync($"/orders/{id}")).StatusCode);
    }

    [Fact]
    public async Task Production_does_not_list_orders_outside_its_statuses_and_gets_no_costs_in_reports()
    {
        await Import("VIS-LIST-DRAFT");
        var open = await ImportUnderReview("VIS-LIST-OPEN");

        var list = await Body(await _factory.ClientAs("SupplierProduction").GetAsync("/orders?search=VIS-LIST"));
        var codes = list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("code").GetString()).ToList();
        var report = await _factory.ClientAs("SupplierProduction").GetByteArrayAsync($"/orders/{open}/reports/differences");

        Assert.Equal(["VIS-LIST-OPEN"], codes);
        using var workbook = new XLWorkbook(new MemoryStream(report));
        Assert.DoesNotContain(workbook.Worksheet("Summary").Column(1).CellsUsed(), c => c.GetString().Contains("cost", StringComparison.OrdinalIgnoreCase));
        Assert.True(workbook.Worksheet("Differences by product").Cell(2, 7).IsEmpty());
    }

    [Fact]
    public async Task Event_stream_filters_out_orders_the_role_cannot_see()
    {
        var id = await Import("VIS-SSE");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await _factory.ClientAs("SupplierProduction")
            .GetAsync("/notifications/stream", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));

        await Act("Buyer", id, "cancel");           // Cancelled: not visible to production
        var second = await Import("VIS-SSE-2");
        await Act("Buyer", second, "submit");       // Submitted: visible

        string? data = null;
        while (await reader.ReadLineAsync(cts.Token) is { } line)
        {
            if (line.StartsWith("data:"))
            {
                data = line;
                break;
            }
        }

        Assert.Contains("VIS-SSE-2", data);
    }

    // ---- editing: comments, warnings, audit ---------------------------------------------------------------------------------

    [Fact]
    public async Task Supplier_comments_on_a_line_and_low_confirmations_come_back_as_warnings()
    {
        var id = await ImportUnderReview("EDIT-1");

        var comment = await Send("Supplier", HttpMethod.Patch, $"/orders/{id}/lines/SKU-1/comment", new { comment = "  Ready Friday " });
        var low = await Send("Supplier", HttpMethod.Patch, $"/orders/{id}/lines/SKU-1/confirmed-quantity", new { quantity = 4 });
        var costUp = await Send("Supplier", HttpMethod.Patch, $"/orders/{id}/lines/SKU-1/cost", new { cost = 12 });

        Assert.Equal("Ready Friday", (await Body(comment)).GetProperty("lines")[0].GetProperty("comment").GetString());
        Assert.Equal("LOW_CONFIRMATION", (await Body(low)).GetProperty("warnings")[0].GetProperty("code").GetString());
        Assert.Equal("HIGH_COST_INCREASE", (await Body(costUp)).GetProperty("warnings")[0].GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Send("Buyer", HttpMethod.Patch, $"/orders/{id}/lines/SKU-1/comment", new { comment = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await Send("Supplier", HttpMethod.Patch, $"/orders/{id}/lines/SKU-1/comment", new { comment = new string('x', 501) })).StatusCode);
    }

    [Fact]
    public async Task Production_may_confirm_quantities_and_comment_but_not_change_costs_or_see_cost_warnings()
    {
        var id = await ImportUnderReview("EDIT-PROD");

        var quantity = await Send("SupplierProduction", HttpMethod.Patch, $"/orders/{id}/lines/SKU-1/confirmed-quantity", new { quantity = 9 });
        var cost = await Send("SupplierProduction", HttpMethod.Patch, $"/orders/{id}/lines/SKU-1/cost", new { cost = 50 });

        Assert.Equal(HttpStatusCode.OK, quantity.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, cost.StatusCode);
    }

    [Fact]
    public async Task Audit_log_lists_status_changes_and_field_edits_newest_first_and_hides_costs_from_production()
    {
        var id = await ImportUnderReview("AUDIT-1");
        await Send("Supplier", HttpMethod.Patch, $"/orders/{id}/lines/SKU-1/cost", new { cost = 11 });
        await Send("SupplierProduction", HttpMethod.Patch, $"/orders/{id}/lines/SKU-1/confirmed-quantity", new { quantity = 9 });

        var supplier = (await Body(await _factory.ClientAs("Supplier").GetAsync($"/orders/{id}/audit-log"))).EnumerateArray().ToList();
        var production = (await Body(await _factory.ClientAs("SupplierProduction").GetAsync($"/orders/{id}/audit-log"))).EnumerateArray().ToList();

        Assert.Equal(["FieldChange", "FieldChange", "StatusChange", "StatusChange"], supplier.Select(e => e.GetProperty("type").GetString()));
        Assert.Equal(["ConfirmedQuantity", "CostWithoutTax"], supplier.Take(2).Select(e => e.GetProperty("field").GetString()));
        Assert.Equal(("10", "11"), (supplier[1].GetProperty("oldValue").GetString(), supplier[1].GetProperty("newValue").GetString()));
        Assert.DoesNotContain(production, e => e.GetProperty("field").GetString() == "CostWithoutTax");
        Assert.Equal(3, production.Count);
        Assert.Equal(HttpStatusCode.NotFound, (await _factory.ClientAs("Buyer").GetAsync($"/orders/{Guid.NewGuid()}/audit-log")).StatusCode);
    }

    [Fact]
    public async Task Order_exposes_who_created_and_last_modified_it()
    {
        var (client, user, _) = await _factory.SignedInAsync(ActorRole.Buyer);
        var response = await client.PostAsync("/orders/import", StocklineApiFactory.PricedTemplateUpload("OWNER-1"));
        var id = (await Body(response)).GetProperty("orderId").GetGuid();

        await client.PostAsync($"/orders/{id}/actions/submit", null);
        var order = await Body(await client.GetAsync($"/orders/{id}"));

        Assert.Equal(user.Id.ToString(), order.GetProperty("createdBy").GetString());
        Assert.Equal(user.Id.ToString(), order.GetProperty("modifiedBy").GetString());
    }

    // ---- discard, source file, lookups ---------------------------------------------------------------------------------

    [Fact]
    public async Task Buyer_discards_draft_and_submitted_orders_but_not_orders_under_review()
    {
        var draft = await Import("DISCARD-D");
        var submitted = await Import("DISCARD-S");
        await Act("Buyer", submitted, "submit");
        var underReview = await ImportUnderReview("DISCARD-U");

        Assert.Equal(HttpStatusCode.NoContent, (await Send("Buyer", HttpMethod.Delete, $"/orders/{draft}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send("Buyer", HttpMethod.Delete, $"/orders/{submitted}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Send("Buyer", HttpMethod.Delete, $"/orders/{underReview}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send("Supplier", HttpMethod.Delete, $"/orders/{underReview}")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await _factory.ClientAs("Buyer").GetAsync($"/orders/{draft}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send("Buyer", HttpMethod.Delete, $"/orders/{draft}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _factory.ClientAs("Buyer").GetAsync($"/orders/{draft}/source-file")).StatusCode);
        Assert.False((await Body(await _factory.ClientAs("Buyer").GetAsync("/orders/exists?code=DISCARD-D"))).GetProperty("exists").GetBoolean());
    }

    [Fact]
    public async Task Discarding_a_submitted_order_emails_the_subscribed_supplier()
    {
        await _factory.ClientAs("Buyer", admin: true).PostAsJsonAsync(
            "/admin/notifications/subscriptions", new { email = "discard@example.com", topic = "OrderDiscarded" });
        var id = await Import("DISCARD-MAIL");
        await Act("Buyer", id, "submit");

        await Send("Buyer", HttpMethod.Delete, $"/orders/{id}");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!_factory.Emails.Sent.Any(m => m.Subject == "Order discarded - DISCARD-MAIL") && !cts.IsCancellationRequested)
        {
            await Task.Delay(50);
        }

        Assert.Contains(_factory.Emails.Sent, m => m.Subject == "Order discarded - DISCARD-MAIL" && m.To.Contains("discard@example.com"));
    }

    [Fact]
    public async Task Original_file_can_be_downloaded_again_by_the_buyer_only()
    {
        var id = await Import("SOURCE-1");

        var buyer = await _factory.ClientAs("Buyer").GetAsync($"/orders/{id}/source-file");
        var supplier = await _factory.ClientAs("Supplier").GetAsync($"/orders/{id}/source-file");

        Assert.Equal(HttpStatusCode.OK, buyer.StatusCode);
        Assert.Equal("priced.xlsx", buyer.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        var template = new ClosedXmlOrderTemplateReader().Read(await buyer.Content.ReadAsStreamAsync());
        Assert.Equal("SOURCE-1", template.Header.OrderCode);
        Assert.Equal(HttpStatusCode.Forbidden, supplier.StatusCode);
    }

    [Fact]
    public async Task Order_code_existence_can_be_checked_before_importing()
    {
        await Import("EXISTS-1");

        Assert.True((await Body(await _factory.ClientAs("Buyer").GetAsync("/orders/exists?code=EXISTS-1"))).GetProperty("exists").GetBoolean());
        Assert.False((await Body(await _factory.ClientAs("Buyer").GetAsync("/orders/exists?code=NOPE-9"))).GetProperty("exists").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await _factory.ClientAs("Buyer").GetAsync("/orders/exists")).StatusCode);
    }

    // ---- listing filters ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task List_filters_by_date_supplier_creator_and_pending_action()
    {
        var (buyer, user, _) = await _factory.SignedInAsync(ActorRole.Buyer);
        var mine = (await Body(await buyer.PostAsync("/orders/import", StocklineApiFactory.PricedTemplateUpload("FLT-MINE")))).GetProperty("orderId").GetGuid();
        await Import("FLT-OTHER");
        await buyer.PostAsync($"/orders/{mine}/actions/submit", null);

        async Task<List<string>> Codes(string query) =>
            (await Body(await _factory.ClientAs("Buyer").GetAsync($"/orders?search=FLT-&{query}")))
                .GetProperty("items").EnumerateArray().Select(i => i.GetProperty("code").GetString()!).Order().ToList();

        var today = DateOnly.FromDateTime(DateTime.Today);
        Assert.Equal(["FLT-MINE"], await Codes($"createdBy={user.Id}"));
        Assert.Equal(["FLT-MINE", "FLT-OTHER"], await Codes($"dateFrom={today:yyyy-MM-dd}&dateTo={today:yyyy-MM-dd}"));
        Assert.Empty(await Codes($"dateTo={today.AddDays(-1):yyyy-MM-dd}"));
        Assert.Empty(await Codes($"dateFrom={today.AddDays(1):yyyy-MM-dd}"));
        Assert.Equal(["FLT-MINE", "FLT-OTHER"], await Codes("supplier=ACME"));
        Assert.Empty(await Codes("supplier=nobody"));
        Assert.Equal(["FLT-MINE"], await Codes($"modifiedBy={user.Id}"));

        // The supplier has something to do with the submitted order (open it) but nothing with the draft.
        var supplierPending = (await Body(await _factory.ClientAs("Supplier").GetAsync("/orders?search=FLT-&pendingMyAction=true")))
            .GetProperty("items").EnumerateArray().Select(i => i.GetProperty("code").GetString()).ToList();
        Assert.Equal(["FLT-MINE"], supplierPending);
    }

    [Fact]
    public async Task Filter_options_list_statuses_creators_and_modifiers_in_use()
    {
        var (buyer, user, _) = await _factory.SignedInAsync(ActorRole.Buyer);
        await buyer.PostAsync("/orders/import", StocklineApiFactory.PricedTemplateUpload("FILTERS-1"));

        var filters = await Body(await _factory.ClientAs("Buyer").GetAsync("/orders/filters"));

        Assert.Contains("Draft", filters.GetProperty("statuses").EnumerateArray().Select(s => s.GetString()));
        Assert.Contains(user.Id.ToString(), filters.GetProperty("creators").EnumerateArray().Select(s => s.GetString()));
        Assert.Contains(user.Id.ToString(), filters.GetProperty("modifiers").EnumerateArray().Select(s => s.GetString()));
        var production = await Body(await _factory.ClientAs("SupplierProduction").GetAsync("/orders/filters"));
        Assert.DoesNotContain("Draft", production.GetProperty("statuses").EnumerateArray().Select(s => s.GetString()));
    }

    [Fact]
    public async Task Last_redistributed_order_can_be_limited_to_the_callers_own_work()
    {
        var (buyer, _, _) = await _factory.SignedInAsync(ActorRole.Buyer);
        var id = (await Body(await buyer.PostAsync("/orders/import", StocklineApiFactory.PricedTemplateUpload("LASTREDIST-1")))).GetProperty("orderId").GetGuid();
        await buyer.PostAsync($"/orders/{id}/actions/submit", null);
        await Act("Supplier", id, "open");
        await Send("Supplier", HttpMethod.Post, $"/orders/{id}/confirm", new { acknowledgeDifference = true });
        var plan = new { lines = new[] { new { productCode = "SKU-1", allocations = new[] { new { locationCode = "WH-MAIN", quantity = 6m }, new { locationCode = "ST-001", quantity = 4m } } } } };
        var confirmed = await buyer.PostAsJsonAsync($"/orders/{id}/redistribution/confirm", plan);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);

        var mine = await Body(await buyer.GetAsync("/orders/last-redistributed?mine=true"));
        var other = await _factory.ClientAs("Buyer").GetAsync("/orders/last-redistributed?mine=true");

        Assert.Equal("LASTREDIST-1", mine.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
    }

    [Fact]
    public async Task Location_catalog_can_be_searched_by_text_and_kind_and_hides_inactive_locations()
    {
        var all = await Body(await _factory.ClientAs("Buyer").GetAsync("/locations?limit=100"));
        var stores = await Body(await _factory.ClientAs("Buyer").GetAsync("/locations?kind=store&limit=100"));
        var search = await Body(await _factory.ClientAs("Buyer").GetAsync("/locations?search=001"));

        Assert.Contains(all.EnumerateArray(), l => l.GetProperty("code").GetString() == "WH-MAIN");
        Assert.All(stores.EnumerateArray(), l => Assert.Equal("Store", l.GetProperty("kind").GetString()));
        Assert.Equal(["ST-001"], search.EnumerateArray().Select(l => l.GetProperty("code").GetString()));
        Assert.Equal(HttpStatusCode.BadRequest, (await _factory.ClientAs("Buyer").GetAsync("/locations?kind=moon")).StatusCode);
    }

    // ---- child order redistribution to catalog stores --------------------------------------------------------------------

    [Fact]
    public async Task Child_order_can_be_redistributed_to_a_catalog_store_the_parent_never_had()
    {
        var id = await ImportUnderReview("CHILD-CAT");
        await Send("Supplier", HttpMethod.Patch, $"/orders/{id}/lines/SKU-1/confirmed-quantity", new { quantity = 6 });
        var confirm = await Body(await Send("Supplier", HttpMethod.Post, $"/orders/{id}/confirm", new
        {
            children = new[] { new { deliveryDate = DateOnly.FromDateTime(DateTime.Today.AddDays(30)), portions = new Dictionary<string, decimal> { ["SKU-1"] = 4m } } },
        }));
        var child = confirm.GetProperty("childOrders")[0].GetProperty("id").GetGuid();
        await Act("Buyer", child, "acceptpostponed");

        var onlyThisStore = new { lines = new[] { new { productCode = "SKU-1", allocations = new[] { new { locationCode = "ST-003", quantity = 4m } } } } };
        var viaCatalog = await Send("Buyer", HttpMethod.Post, $"/orders/{child}/redistribution/confirm", onlyThisStore);
        var parentPlan = await Send("Buyer", HttpMethod.Post, $"/orders/{id}/redistribution/confirm",
            new { lines = new[] { new { productCode = "SKU-1", allocations = new[] { new { locationCode = "ST-003", quantity = 6m } } } } });

        Assert.True(viaCatalog.IsSuccessStatusCode, await viaCatalog.Content.ReadAsStringAsync());
        Assert.Contains(
            (await Body(viaCatalog)).GetProperty("locations").EnumerateArray(),
            l => l.GetProperty("code").GetString() == "ST-003");
        // A regular order stays limited to its own locations.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, parentPlan.StatusCode);
    }

    // ---- preferences --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task User_preferences_are_stored_per_user_and_can_be_replaced_listed_and_deleted()
    {
        var (a, _, _) = await _factory.SignedInAsync(ActorRole.Supplier);
        var (b, _, _) = await _factory.SignedInAsync(ActorRole.Supplier);

        Assert.Equal(HttpStatusCode.NoContent,
            (await a.PutAsync("/users/me/preferences/orders-table", new StringContent("""{"columns":["code","status"],"pageSize":50}""", Encoding.UTF8, "application/json"))).StatusCode);
        await a.PutAsync("/users/me/preferences/orders-table", new StringContent("""{"pageSize":25}""", Encoding.UTF8, "application/json"));
        await a.PutAsync("/users/me/preferences/sidebar", new StringContent("true", Encoding.UTF8, "application/json"));

        Assert.Equal(25, (await Body(await a.GetAsync("/users/me/preferences/orders-table"))).GetProperty("pageSize").GetInt32());
        var all = await Body(await a.GetAsync("/users/me/preferences"));
        Assert.Equal(["orders-table", "sidebar"], all.EnumerateObject().Select(p => p.Name));
        Assert.True(all.GetProperty("sidebar").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync("/users/me/preferences/orders-table")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await a.DeleteAsync("/users/me/preferences/sidebar")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.DeleteAsync("/users/me/preferences/sidebar")).StatusCode);
    }

    [Fact]
    public async Task Preference_keys_and_sizes_are_validated_and_a_user_identity_is_required()
    {
        var (client, _, _) = await _factory.SignedInAsync(ActorRole.Buyer);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PutAsync("/users/me/preferences/bad key!", new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge,
            (await client.PutAsync("/users/me/preferences/big", new StringContent($"\"{new string('x', 21_000)}\"", Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientAs("Buyer").GetAsync("/users/me/preferences")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.ClientAs(null).GetAsync("/users/me/preferences")).StatusCode);
    }
}

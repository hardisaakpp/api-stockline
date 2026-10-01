using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Stockline.UnitTests.Api;

public class OrderWorkflowEndpointsTests : IClassFixture<StocklineApiFactory>
{
    private readonly StocklineApiFactory _factory;

    public OrderWorkflowEndpointsTests(StocklineApiFactory factory) => _factory = factory;

    private async Task<Guid> ImportUnderReview(string code)
    {
        var import = await _factory.ClientAs("Buyer").PostAsync("/orders/import", StocklineApiFactory.RichTemplateUpload(code));
        Assert.Equal(HttpStatusCode.Created, import.StatusCode);
        var id = (await import.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("orderId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await Act("Buyer", id, "submit")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Act("Supplier", id, "open")).StatusCode);
        return id;
    }

    private Task<HttpResponseMessage> Act(string role, Guid id, string action) =>
        _factory.ClientAs(role).PostAsync($"/orders/{id}/actions/{action}", null);

    private Task<HttpResponseMessage> Send(string role, HttpMethod method, string url, object? body = null) =>
        _factory.ClientAs(role).SendAsync(new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) });

    private async Task<JsonElement> Get(Guid id) =>
        await (await _factory.ClientAs("Buyer").GetAsync($"/orders/{id}")).Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<JsonElement> Body(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private static object Plan(params (string Product, (string Location, decimal Quantity)[] Allocations)[] lines) => new
    {
        lines = lines.Select(l => new
        {
            productCode = l.Product,
            allocations = l.Allocations.Select(a => new { locationCode = a.Location, quantity = a.Quantity }),
        }),
    };

    [Fact]
    public async Task Imported_order_keeps_every_template_column_as_an_order_location()
    {
        var id = await ImportUnderReview("WF-LOC");

        var order = await Get(id);

        Assert.Equal(
            ["ST-001", "ST-002", "ST-003", "WH-MAIN"],
            order.GetProperty("locations").EnumerateArray().Select(l => l.GetProperty("code").GetString()!).Order());
        var store = order.GetProperty("locations").EnumerateArray().First(l => l.GetProperty("code").GetString() == "ST-001");
        Assert.Equal("WH-MAIN", store.GetProperty("supplyingWarehouseCode").GetString());
    }

    [Fact]
    public async Task Supplier_lowers_a_quantity_and_the_difference_must_be_resolved_before_confirming()
    {
        var id = await ImportUnderReview("WF-DIFF");

        var edit = await Send("Supplier", HttpMethod.Patch, $"/orders/{id}/lines/SKU-1/confirmed-quantity", new { quantity = 3 });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);

        var difference = await Body(await _factory.ClientAs("Supplier").GetAsync($"/orders/{id}/difference"));
        Assert.True(difference.GetProperty("hasDifference").GetBoolean());
        Assert.Equal(1m, difference.GetProperty("lines")[0].GetProperty("difference").GetDecimal());

        var blocked = await Send("Supplier", HttpMethod.Post, $"/orders/{id}/confirm", new { acknowledgeDifference = false });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.StatusCode);
        Assert.Equal(
            "DIFFERENCE_NOT_ACKNOWLEDGED",
            (await Body(blocked)).GetProperty("issues")[0].GetProperty("code").GetString());

        var acknowledged = await Send("Supplier", HttpMethod.Post, $"/orders/{id}/confirm", new { acknowledgeDifference = true });
        Assert.Equal(HttpStatusCode.OK, acknowledged.StatusCode);
        var parent = await Get(id);
        Assert.Equal("SupplierConfirmed", parent.GetProperty("status").GetString());
        Assert.Equal(3m, parent.GetProperty("lines")[0].GetProperty("confirmedQuantity").GetDecimal());
        Assert.Equal(4m, parent.GetProperty("lines")[0].GetProperty("requestedQuantity").GetDecimal());
    }

    [Fact]
    public async Task Line_edits_are_restricted_by_role_state_and_range()
    {
        var id = await ImportUnderReview("WF-EDIT");

        Assert.Equal(HttpStatusCode.Forbidden,
            (await Send("Buyer", HttpMethod.Patch, $"/orders/{id}/lines/SKU-1/confirmed-quantity", new { quantity = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await Send("Supplier", HttpMethod.Patch, $"/orders/{id}/lines/SKU-1/confirmed-quantity", new { quantity = 99 })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await Send("Supplier", HttpMethod.Patch, $"/orders/{id}/lines/NOPE/cost", new { cost = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Send("SupplierProduction", HttpMethod.Patch, $"/orders/{id}/lines/SKU-2/cost", new { cost = 18 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await Send("Supplier", HttpMethod.Patch, $"/orders/{id}/lines/SKU-2/cost", new { cost = 18 })).StatusCode);
        await Send("Supplier", HttpMethod.Post, $"/orders/{id}/confirm", new { acknowledgeDifference = false });

        Assert.Equal(HttpStatusCode.Conflict,
            (await Send("Supplier", HttpMethod.Patch, $"/orders/{id}/lines/SKU-1/cost", new { cost = 2 })).StatusCode);
    }

    [Fact]
    public async Task Production_difference_is_split_into_child_orders_that_the_buyer_redistributes()
    {
        var id = await ImportUnderReview("WF-CHILD");
        await Send("Supplier", HttpMethod.Patch, $"/orders/{id}/lines/SKU-1/confirmed-quantity", new { quantity = 1 });

        var badSplit = await Send("Supplier", HttpMethod.Post, $"/orders/{id}/confirm", new
        {
            children = new[] { new { deliveryDate = "2026-11-01", portions = new Dictionary<string, decimal> { ["SKU-1"] = 2m } } },
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badSplit.StatusCode);

        var confirm = await Send("Supplier", HttpMethod.Post, $"/orders/{id}/confirm", new
        {
            children = new[]
            {
                new { deliveryDate = "2026-11-01", portions = new Dictionary<string, decimal> { ["SKU-1"] = 2m } },
                new { deliveryDate = "2026-12-01", portions = new Dictionary<string, decimal> { ["SKU-1"] = 1m } },
            },
        });
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        var created = (await Body(confirm)).GetProperty("childOrders").EnumerateArray().ToList();
        Assert.Equal(["WF-CHILD.01", "WF-CHILD.02"], created.Select(c => c.GetProperty("code").GetString()!));

        var childId = created[0].GetProperty("id").GetGuid();
        var child = await Get(childId);
        Assert.Equal("ProductionPostponed", child.GetProperty("status").GetString());
        Assert.Equal(id, child.GetProperty("parentOrderId").GetGuid());
        Assert.Equal(2m, child.GetProperty("lines")[0].GetProperty("confirmedQuantity").GetDecimal());
        Assert.Empty(child.GetProperty("lines")[0].GetProperty("allocations").EnumerateArray());

        var parentAfter = await Get(id);
        Assert.Equal(4, parentAfter.GetProperty("locations").GetArrayLength());
        Assert.Equal(4, child.GetProperty("locations").GetArrayLength());
        var parentRedistribution = await Send("Buyer", HttpMethod.Post, $"/orders/{id}/redistribution/confirm",
            Plan(("SKU-1", [("WH-MAIN", 1m)]), ("SKU-2", [("ST-001", 3m)])));
        Assert.Equal(HttpStatusCode.OK, parentRedistribution.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await Act("Buyer", childId, "accept-postponed")).StatusCode);
        var redistribute = await Send("Buyer", HttpMethod.Post, $"/orders/{childId}/redistribution/confirm",
            Plan(("SKU-1", [("ST-003", 2m)])));
        Assert.Equal(HttpStatusCode.OK, redistribute.StatusCode);
        var done = await Body(redistribute);
        Assert.Equal("RedistributionConfirmed", done.GetProperty("status").GetString());
        Assert.Equal(1, done.GetProperty("redistributionVersion").GetInt32());
        Assert.Equal("ST-003", done.GetProperty("lines")[0].GetProperty("allocations")[0].GetProperty("locationCode").GetString());
    }

    [Fact]
    public async Task Redistribution_is_validated_saved_as_draft_and_confirmed_by_the_buyer()
    {
        var id = await ImportUnderReview("WF-REDIS");
        Assert.Equal(HttpStatusCode.OK, (await Send("Supplier", HttpMethod.Post, $"/orders/{id}/confirm", new { })).StatusCode);

        var tooMuch = await Send("Buyer", HttpMethod.Post, $"/orders/{id}/redistribution/confirm",
            Plan(("SKU-1", [("ST-001", 5m)])));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooMuch.StatusCode);
        Assert.Equal("TOTAL_MISMATCH", (await Body(tooMuch)).GetProperty("issues")[0].GetProperty("code").GetString());

        Assert.Equal(HttpStatusCode.Forbidden,
            (await Send("Supplier", HttpMethod.Post, $"/orders/{id}/redistribution/confirm", Plan())).StatusCode);

        var draft = await Send("Buyer", HttpMethod.Put, $"/orders/{id}/redistribution/draft", Plan(("SKU-1", [("ST-003", 1m)])));
        Assert.Equal(HttpStatusCode.OK, draft.StatusCode);
        var view = await Body(await _factory.ClientAs("Buyer").GetAsync($"/orders/{id}/redistribution"));
        Assert.Equal("ST-003", view.GetProperty("draft")[0].GetProperty("locationCode").GetString());
        Assert.Equal(4m, view.GetProperty("lines")[0].GetProperty("confirmedQuantity").GetDecimal());

        var ok = await Send("Buyer", HttpMethod.Post, $"/orders/{id}/redistribution/confirm",
            Plan(("SKU-1", [("WH-MAIN", 1m), ("ST-001", 1m), ("ST-003", 2m)])));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var after = await Body(await _factory.ClientAs("Buyer").GetAsync($"/orders/{id}/redistribution"));
        Assert.Equal("RedistributionConfirmed", after.GetProperty("status").GetString());
        Assert.Empty(after.GetProperty("draft").EnumerateArray());

        Assert.Equal(HttpStatusCode.Conflict,
            (await Send("Buyer", HttpMethod.Put, $"/orders/{id}/redistribution/draft", Plan())).StatusCode);
    }
}

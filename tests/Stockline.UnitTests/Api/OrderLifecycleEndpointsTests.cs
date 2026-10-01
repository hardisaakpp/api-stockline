using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Stockline.UnitTests.Api;

public class OrderLifecycleEndpointsTests : IClassFixture<StocklineApiFactory>
{
    private readonly StocklineApiFactory _factory;

    public OrderLifecycleEndpointsTests(StocklineApiFactory factory) => _factory = factory;

    private async Task<Guid> ImportOrder(string code)
    {
        var response = await _factory.ClientAs("Buyer").PostAsync("/orders/import", StocklineApiFactory.TemplateUpload(code));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("orderId").GetGuid();
    }

    private Task<HttpResponseMessage> Act(string role, Guid id, string action) =>
        _factory.ClientAs(role).PostAsync($"/orders/{id}/actions/{action}", null);

    private static async Task<string> StatusOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString()!;

    [Fact]
    public async Task Order_goes_from_draft_to_redistribution_confirmed_with_each_role_acting()
    {
        var id = await ImportOrder("LIFE-1");

        Assert.Equal("Submitted", await StatusOf(await Act("Buyer", id, "submit")));
        Assert.Equal("UnderReview", await StatusOf(await Act("Supplier", id, "open")));
        Assert.Equal("ProductionConfirmed", await StatusOf(await Act("SupplierProduction", id, "confirm-production")));
        Assert.Equal("SupplierConfirmed", await StatusOf(await Act("Supplier", id, "confirm-by-supplier")));
        Assert.Equal("RedistributionConfirmed", await StatusOf(await Act("Buyer", id, "confirm-redistribution")));
        Assert.Equal("Sent", await StatusOf(await Act("System", id, "mark-sent")));

        var order = await (await _factory.ClientAs("Buyer").GetAsync($"/orders/{id}")).Content.ReadFromJsonAsync<JsonElement>();
        var history = order.GetProperty("history").EnumerateArray().ToList();
        Assert.Equal(6, history.Count);
        Assert.Equal("Submit", history[0].GetProperty("action").GetString());
        Assert.Equal("Buyer", history[0].GetProperty("actor").GetString());
        Assert.Equal("user-1", history[0].GetProperty("userId").GetString());
        Assert.Equal("System", history[5].GetProperty("actor").GetString());
    }

    [Fact]
    public async Task Action_by_the_wrong_role_is_forbidden_and_changes_nothing()
    {
        var id = await ImportOrder("LIFE-403");

        var response = await Act("Supplier", id, "submit");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var order = await (await _factory.ClientAs("Buyer").GetAsync($"/orders/{id}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Draft", order.GetProperty("status").GetString());
        Assert.Empty(order.GetProperty("history").EnumerateArray());
    }

    [Fact]
    public async Task Action_not_allowed_in_the_current_status_is_a_conflict()
    {
        var id = await ImportOrder("LIFE-409");

        var response = await Act("Buyer", id, "confirm-redistribution");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Draft", problem.GetProperty("currentStatus").GetString());
    }

    [Fact]
    public async Task Unknown_action_lists_the_valid_ones()
    {
        var id = await ImportOrder("LIFE-400");

        var response = await Act("Buyer", id, "teleport");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var valid = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("validActions");
        Assert.Contains(valid.EnumerateArray(), a => a.GetString() == "Submit");
    }

    [Fact]
    public async Task Unknown_order_is_not_found() =>
        Assert.Equal(HttpStatusCode.NotFound, (await Act("Buyer", Guid.NewGuid(), "submit")).StatusCode);

    [Fact]
    public async Task A_token_without_a_known_role_is_forbidden()
    {
        var id = await ImportOrder("LIFE-NOROLE");

        Assert.Equal(HttpStatusCode.Forbidden, (await Act("Wizard", id, "submit")).StatusCode);
    }

    [Fact]
    public async Task Available_actions_depend_on_the_status_and_the_callers_role()
    {
        var id = await ImportOrder("LIFE-ACTIONS");

        var buyer = await (await _factory.ClientAs("Buyer").GetAsync($"/orders/{id}/actions")).Content.ReadFromJsonAsync<JsonElement>();
        var supplier = await (await _factory.ClientAs("Supplier").GetAsync($"/orders/{id}/actions")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Draft", buyer.GetProperty("status").GetString());
        Assert.Equal(
            ["Cancel", "Submit"],
            buyer.GetProperty("actions").EnumerateArray().Select(a => a.GetString()!).Order());
        Assert.Empty(supplier.GetProperty("actions").EnumerateArray());
    }

    [Fact]
    public async Task Lifecycle_endpoints_require_authentication()
    {
        var response = await _factory.ClientAs(null).PostAsync($"/orders/{Guid.NewGuid()}/actions/submit", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

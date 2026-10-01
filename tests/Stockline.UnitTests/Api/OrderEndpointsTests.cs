using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Stockline.UnitTests.Api;

public class OrderEndpointsTests : IClassFixture<StocklineApiFactory>
{
    private readonly StocklineApiFactory _factory;

    public OrderEndpointsTests(StocklineApiFactory factory) => _factory = factory;

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>())!;

    [Fact]
    public async Task Health_is_public()
    {
        var response = await _factory.ClientAs(null).GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Import_requires_authentication()
    {
        var response = await _factory.ClientAs(null).PostAsync("/orders/import", StocklineApiFactory.TemplateUpload("API-401"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Import_is_forbidden_for_non_buyers()
    {
        var response = await _factory.ClientAs("Supplier").PostAsync("/orders/import", StocklineApiFactory.TemplateUpload("API-403"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Buyer_imports_a_template_and_any_user_can_read_the_order()
    {
        var import = await _factory.ClientAs("Buyer").PostAsync("/orders/import", StocklineApiFactory.TemplateUpload("API-OK"));

        Assert.Equal(HttpStatusCode.Created, import.StatusCode);
        var location = import.Headers.Location!.ToString();
        Assert.StartsWith("/orders/", location);

        var get = await _factory.ClientAs("Supplier").GetAsync(location);

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var order = await Json(get);
        Assert.Equal("Draft", order.GetProperty("status").GetString());
        Assert.Equal("API-OK", order.GetProperty("header").GetProperty("code").GetString());
        var line = order.GetProperty("lines")[0];
        Assert.Equal("SKU-1", line.GetProperty("productCode").GetString());
        Assert.Equal(3.5m, line.GetProperty("totalQuantity").GetDecimal());
        Assert.Equal(1.5m, line.GetProperty("storeQuantity").GetDecimal());
    }

    [Fact]
    public async Task Importing_the_same_order_code_twice_returns_conflict()
    {
        var client = _factory.ClientAs("Buyer");
        await client.PostAsync("/orders/import", StocklineApiFactory.TemplateUpload("API-DUP"));

        var second = await client.PostAsync("/orders/import", StocklineApiFactory.TemplateUpload("API-DUP"));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Unknown_location_returns_validation_errors()
    {
        var response = await _factory.ClientAs("Buyer").PostAsync(
            "/orders/import", StocklineApiFactory.TemplateUpload("API-422", locationCode: "ST-404"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var issues = (await Json(response)).GetProperty("issues");
        Assert.Contains(issues.EnumerateArray(), i => i.GetProperty("code").GetString() == "UNKNOWN_LOCATION");
    }

    [Fact]
    public async Task Inactive_location_is_not_accepted()
    {
        var response = await _factory.ClientAs("Buyer").PostAsync(
            "/orders/import", StocklineApiFactory.TemplateUpload("API-INACTIVE", locationCode: "ST-099"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Garbage_with_an_xlsx_name_returns_bad_request()
    {
        var response = await _factory.ClientAs("Buyer").PostAsync(
            "/orders/import", StocklineApiFactory.Upload([1, 2, 3, 4], "order.xlsx"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Other_extensions_are_rejected()
    {
        var response = await _factory.ClientAs("Buyer").PostAsync(
            "/orders/import", StocklineApiFactory.Upload([1, 2, 3, 4], "order.csv"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_order_returns_not_found()
    {
        var response = await _factory.ClientAs("Buyer").GetAsync($"/orders/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

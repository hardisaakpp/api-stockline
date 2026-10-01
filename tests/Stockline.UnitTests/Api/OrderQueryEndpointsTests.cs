using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Stockline.UnitTests.Api;

public class OrderQueryEndpointsTests : IClassFixture<StocklineApiFactory>
{
    private readonly StocklineApiFactory _factory;

    public OrderQueryEndpointsTests(StocklineApiFactory factory) => _factory = factory;

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>())!;

    private async Task Import(string code) =>
        Assert.Equal(HttpStatusCode.Created, (await _factory.ClientAs("Buyer").PostAsync("/orders/import", StocklineApiFactory.TemplateUpload(code))).StatusCode);

    private static List<string?> Codes(JsonElement page) =>
        [.. page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("code").GetString())];

    [Fact]
    public async Task Listing_requires_authentication()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.ClientAs(null).GetAsync("/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.ClientAs(null).GetAsync("/dashboard/summary")).StatusCode);
    }

    [Fact]
    public async Task Listing_returns_summaries_with_line_totals()
    {
        await Import("LIST-SUMMARY");

        var page = await Json(await _factory.ClientAs("Supplier").GetAsync("/orders?search=LIST-SUMMARY"));

        var item = page.GetProperty("items")[0];
        Assert.Equal("LIST-SUMMARY", item.GetProperty("code").GetString());
        Assert.Equal("ACME SUPPLIER", item.GetProperty("supplierName").GetString());
        Assert.Equal("Draft", item.GetProperty("status").GetString());
        Assert.Equal(1, item.GetProperty("lineCount").GetInt32());
        Assert.Equal(1, page.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Listing_paginates_and_sorts()
    {
        foreach (var code in new[] { "PAGE-A", "PAGE-B", "PAGE-C" })
        {
            await Import(code);
        }

        var client = _factory.ClientAs("Buyer");
        var first = await Json(await client.GetAsync("/orders?search=PAGE-&sort=code&pageSize=2&page=1"));
        var second = await Json(await client.GetAsync("/orders?search=PAGE-&sort=code&pageSize=2&page=2"));
        var descending = await Json(await client.GetAsync("/orders?search=PAGE-&sort=-code&pageSize=10"));

        Assert.Equal(["PAGE-A", "PAGE-B"], Codes(first));
        Assert.Equal(["PAGE-C"], Codes(second));
        Assert.Equal(3, first.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, first.GetProperty("totalPages").GetInt32());
        Assert.Equal(["PAGE-C", "PAGE-B", "PAGE-A"], Codes(descending));
    }

    [Fact]
    public async Task Listing_filters_by_status_and_clamps_the_page_size()
    {
        await Import("STATUS-ONE");
        var client = _factory.ClientAs("Buyer");

        var drafts = await Json(await client.GetAsync("/orders?status=draft&search=STATUS-ONE"));
        var completed = await Json(await client.GetAsync("/orders?status=Completed&search=STATUS-ONE"));
        var clamped = await Json(await client.GetAsync("/orders?pageSize=100000"));

        Assert.Equal(1, drafts.GetProperty("totalCount").GetInt32());
        Assert.Equal(0, completed.GetProperty("totalCount").GetInt32());
        Assert.Equal(100, clamped.GetProperty("pageSize").GetInt32());
    }

    [Theory]
    [InlineData("/orders?status=Nonsense")]
    [InlineData("/orders?sort=bogus")]
    public async Task Listing_rejects_unknown_filters(string url)
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _factory.ClientAs("Buyer").GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task Dashboard_summarizes_orders_for_the_actor()
    {
        await Import("DASH-1");
        await Import("DASH-2");

        var summary = await Json(await _factory.ClientAs("Buyer").GetAsync("/dashboard/summary"));

        Assert.True(summary.GetProperty("totalOrders").GetInt32() >= 2);
        Assert.True(summary.GetProperty("openOrders").GetInt32() >= 2);
        Assert.True(summary.GetProperty("awaitingMyAction").GetInt32() >= 2);
        var draft = summary.GetProperty("byStatus").EnumerateArray().First(s => s.GetProperty("status").GetString() == "Draft");
        Assert.True(draft.GetProperty("count").GetInt32() >= 2);
        Assert.Equal(30, summary.GetProperty("last30Days").GetArrayLength());
        Assert.InRange(summary.GetProperty("recent").GetArrayLength(), 1, 5);
    }

    [Fact]
    public async Task Dashboard_awaiting_action_depends_on_the_role()
    {
        await Import("DASH-ROLE");

        var buyer = await Json(await _factory.ClientAs("Buyer").GetAsync("/dashboard/summary"));
        var supplier = await Json(await _factory.ClientAs("Supplier").GetAsync("/dashboard/summary"));

        // Drafts are the buyer's to submit; nothing in Draft is actionable by the supplier.
        Assert.True(buyer.GetProperty("awaitingMyAction").GetInt32() > supplier.GetProperty("awaitingMyAction").GetInt32());
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Stockline.Application.Orders.Import;
using Stockline.Infrastructure.Excel;

namespace Stockline.UnitTests.Api;

public class OrderNotificationAndExportEndpointsTests : IClassFixture<StocklineApiFactory>
{
    private readonly StocklineApiFactory _factory;

    public OrderNotificationAndExportEndpointsTests(StocklineApiFactory factory) => _factory = factory;

    private async Task<Guid> Import(string code)
    {
        var response = await _factory.ClientAs("Buyer").PostAsync("/orders/import", StocklineApiFactory.RichTemplateUpload(code));
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("orderId").GetGuid();
    }

    private static async Task<JsonElement> NextEvent(StreamReader reader, CancellationToken ct)
    {
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.StartsWith("data:"))
            {
                return JsonDocument.Parse(line["data:".Length..].Trim()).RootElement.Clone();
            }
        }

        throw new InvalidOperationException("The stream ended before an event arrived.");
    }

    [Fact]
    public async Task Stream_requires_authentication() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.ClientAs(null).GetAsync("/notifications/stream")).StatusCode);

    [Fact]
    public async Task Order_changes_are_pushed_to_subscribers_and_can_be_filtered_by_order()
    {
        var watched = await Import("SSE-A");
        var other = await Import("SSE-B");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = _factory.ClientAs("Supplier");
        using var response = await client.GetAsync($"/notifications/stream?orderId={watched}", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));

        await _factory.ClientAs("Buyer").PostAsync($"/orders/{other}/actions/submit", null);
        await _factory.ClientAs("Buyer").PostAsync($"/orders/{watched}/actions/submit", null);

        var received = await NextEvent(reader, cts.Token);
        Assert.Equal(watched, received.GetProperty("orderId").GetGuid());
        Assert.Equal(("Submit", "Submitted", "user-1"),
            (received.GetProperty("type").GetString(), received.GetProperty("status").GetString(), received.GetProperty("userId").GetString()));
    }

    [Fact]
    public async Task Order_can_be_exported_as_an_excel_file_that_reads_back()
    {
        var id = await Import("EXPORT-1");

        var response = await _factory.ClientAs("Supplier").GetAsync($"/orders/{id}/export");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("order-EXPORT-1.xlsx", response.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        var template = new ClosedXmlOrderTemplateReader().Read(await response.Content.ReadAsStreamAsync());
        Assert.Equal("EXPORT-1", template.Header.OrderCode);
        Assert.Equal(["SKU-1", "SKU-2"], template.Lines.Select(l => l.ProductCode));
        Assert.Equal(["WH-MAIN", "ST-001", "ST-002", "ST-003"], template.Locations!.Select(l => l.Code));
    }

    [Fact]
    public async Task Export_of_unknown_or_anonymous_requests_is_refused()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _factory.ClientAs("Buyer").GetAsync($"/orders/{Guid.NewGuid()}/export")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.ClientAs(null).GetAsync($"/orders/{Guid.NewGuid()}/export")).StatusCode);
    }

    [Theory]
    [InlineData("margin")]
    [InlineData("differences")]
    [InlineData("transfers")]
    public async Task Reports_are_exported_as_excel_files(string report)
    {
        var id = await Import($"REPORT-{report}");

        var response = await _factory.ClientAs("Buyer").GetAsync($"/orders/{id}/reports/{report}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"{report}-REPORT-{report}.xlsx", response.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        using var workbook = new ClosedXML.Excel.XLWorkbook(await response.Content.ReadAsStreamAsync());
        Assert.NotEmpty(workbook.Worksheets);
    }

    [Fact]
    public async Task Margin_report_is_buyer_only_but_the_other_reports_are_open_to_the_supplier()
    {
        var id = await Import("REPORT-ROLES");

        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientAs("Supplier").GetAsync($"/orders/{id}/reports/margin")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _factory.ClientAs("Supplier").GetAsync($"/orders/{id}/reports/differences")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _factory.ClientAs("Supplier").GetAsync($"/orders/{id}/reports/transfers")).StatusCode);
    }

    [Fact]
    public async Task Reports_of_unknown_orders_or_anonymous_requests_are_refused()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _factory.ClientAs("Buyer").GetAsync($"/orders/{Guid.NewGuid()}/reports/margin")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.ClientAs(null).GetAsync($"/orders/{Guid.NewGuid()}/reports/transfers")).StatusCode);
    }
}

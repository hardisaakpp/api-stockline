using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Stockline.Application.Erp;

namespace Stockline.UnitTests.Api;

public class OrderErpEndpointsTests : IClassFixture<StocklineApiFactory>
{
    private readonly StocklineApiFactory _factory;

    public OrderErpEndpointsTests(StocklineApiFactory factory)
    {
        _factory = factory;
        factory.Erp.Override = null;
        factory.Erp.Calls.Clear();
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
        var redistribution = await _factory.ClientAs("Buyer").PostAsJsonAsync($"/orders/{id}/redistribution/confirm", plan);
        Assert.Equal(HttpStatusCode.OK, redistribution.StatusCode);
        return id;
    }

    private async Task<JsonElement> Submission(Guid id) =>
        await (await _factory.ClientAs("Buyer").GetAsync($"/orders/{id}/erp-submission")).Content.ReadFromJsonAsync<JsonElement>();

    private async Task<JsonElement> WaitForOrderStatus(Guid id, string status, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        JsonElement last;
        do
        {
            last = await Submission(id);
            if (last.GetProperty("orderStatus").GetString() == status)
            {
                return last;
            }

            await Task.Delay(50);
        }
        while (DateTime.UtcNow < deadline);

        throw new Xunit.Sdk.XunitException($"Order did not reach {status}; it is {last.GetProperty("orderStatus").GetString()}.");
    }

    private int Calls(string prefix)
    {
        lock (_factory.Erp.Calls)
        {
            return _factory.Erp.Calls.Count(c => c.StartsWith(prefix));
        }
    }

    [Fact]
    public async Task Submission_is_processed_in_the_background_and_ends_with_the_order_sent()
    {
        var id = await ReadyForErp("ERP-OK");

        var request = await _factory.ClientAs("Buyer").PostAsync($"/orders/{id}/erp-submission", null);
        Assert.Equal(HttpStatusCode.Accepted, request.StatusCode);

        var done = await WaitForOrderStatus(id, "Sent");
        var documents = done.GetProperty("documents").EnumerateArray().ToList();
        Assert.Equal(["PurchaseOrder", "TransferRequest", "TransferRequest"], documents.Select(d => d.GetProperty("kind").GetString()!));
        Assert.Equal(["ST-001", "ST-002"], documents.Skip(1).Select(d => d.GetProperty("destinationLocation").GetString()!));
        Assert.All(documents, d =>
        {
            Assert.Equal("Sent", d.GetProperty("status").GetString());
            Assert.True(d.GetProperty("docNum").GetInt64() > 0);
        });
        Assert.Equal(7m, documents[0].GetProperty("lines").EnumerateArray().Sum(l => l.GetProperty("quantity").GetDecimal()));

        var order = await (await _factory.ClientAs("Buyer").GetAsync($"/orders/{id}")).Content.ReadFromJsonAsync<JsonElement>();
        var last = order.GetProperty("history").EnumerateArray().Last();
        Assert.Equal(("MarkSent", "System"), (last.GetProperty("action").GetString(), last.GetProperty("actor").GetString()));
    }

    [Fact]
    public async Task Only_a_buyer_can_request_and_only_for_orders_that_are_ready()
    {
        var id = await ReadyForErp("ERP-GUARD");

        Assert.Equal(HttpStatusCode.Conflict, (await _factory.ClientAs("Supplier").PostAsync($"/orders/{id}/erp-submission", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _factory.ClientAs("Buyer").PostAsync($"/orders/{Guid.NewGuid()}/erp-submission", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.ClientAs(null).PostAsync($"/orders/{id}/erp-submission", null)).StatusCode);

        var draft = await _factory.ClientAs("Buyer").PostAsync("/orders/import", StocklineApiFactory.RichTemplateUpload("ERP-DRAFT"));
        var draftId = (await draft.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("orderId").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await _factory.ClientAs("Buyer").PostAsync($"/orders/{draftId}/erp-submission", null)).StatusCode);
    }

    [Fact]
    public async Task Transient_failures_are_retried_automatically_until_the_erp_recovers()
    {
        var id = await ReadyForErp("ERP-TRANSIENT");
        var remainingFailures = 1;
        _factory.Erp.Override = call => call.StartsWith("PO") && Interlocked.Decrement(ref remainingFailures) >= 0
            ? ErpResult.Failure("ERP busy", isTransient: true)
            : null;

        await _factory.ClientAs("Buyer").PostAsync($"/orders/{id}/erp-submission", null);

        var done = await WaitForOrderStatus(id, "Sent");
        Assert.Equal(2, Calls("PO"));
        Assert.Equal(2, done.GetProperty("documents")[0].GetProperty("attempts").GetInt32());
    }

    [Fact]
    public async Task Permanent_failures_wait_for_the_buyer_instead_of_being_retried_automatically()
    {
        var id = await ReadyForErp("ERP-PERMANENT");
        _factory.Erp.Override = call => call.StartsWith("PO") ? ErpResult.Failure("Item 'SKU-1' does not exist", isTransient: false) : null;

        await _factory.ClientAs("Buyer").PostAsync($"/orders/{id}/erp-submission", null);
        var failed = await WaitForOrderStatus(id, "ErpSubmissionFailed");
        Assert.Equal("Item 'SKU-1' does not exist", failed.GetProperty("documents")[0].GetProperty("lastError").GetString());

        await Task.Delay(2500);
        Assert.Equal(1, Calls("PO"));

        _factory.Erp.Override = null;
        await _factory.ClientAs("Buyer").PostAsync($"/orders/{id}/erp-submission", null);
        await WaitForOrderStatus(id, "Sent");
        Assert.Equal(2, Calls("PO"));
    }

    [Fact]
    public async Task A_failed_transfer_leaves_the_order_logistics_pending_with_the_error_visible()
    {
        var id = await ReadyForErp("ERP-LOGISTICS");
        _factory.Erp.Override = call => call.EndsWith("ST-002") ? ErpResult.Failure("Stock too low", isTransient: false) : null;

        await _factory.ClientAs("Buyer").PostAsync($"/orders/{id}/erp-submission", null);

        var pending = await WaitForOrderStatus(id, "LogisticsPending");
        var failedTransfer = pending.GetProperty("documents").EnumerateArray().Single(d => d.GetProperty("status").GetString() == "Failed");
        Assert.Equal(("ST-002", "Stock too low"), (failedTransfer.GetProperty("destinationLocation").GetString(), failedTransfer.GetProperty("lastError").GetString()));
    }
}

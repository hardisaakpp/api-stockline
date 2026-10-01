using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Stockline.UnitTests.Api;

public class EmailNotificationEndpointsTests : IClassFixture<StocklineApiFactory>
{
    private readonly StocklineApiFactory _factory;

    public EmailNotificationEndpointsTests(StocklineApiFactory factory) => _factory = factory;

    private HttpClient Admin() => _factory.ClientAs("Buyer", admin: true);

    private async Task<Guid> Subscribe(string email, string topic)
    {
        var response = await Admin().PostAsJsonAsync("/admin/notifications/subscriptions", new { email, topic });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Topic_catalog_lists_every_email_with_its_audience()
    {
        var topics = await Admin().GetFromJsonAsync<JsonElement>("/admin/notifications/topics");

        Assert.Equal(9, topics.GetArrayLength());
        Assert.Contains(topics.EnumerateArray(), t => t.GetProperty("topic").GetString() == "ErpSubmissionFailed"
                                                      && t.GetProperty("audience").GetString() == "Technical");
    }

    [Fact]
    public async Task Subscriptions_can_be_created_listed_and_deleted_and_creation_is_idempotent()
    {
        var id = await Subscribe("ops@example.com", "OrderRejected");
        var again = await Admin().PostAsJsonAsync("/admin/notifications/subscriptions", new { email = "OPS@example.com", topic = "OrderRejected" });

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var list = await Admin().GetFromJsonAsync<JsonElement>("/admin/notifications/subscriptions");
        Assert.Single(list.EnumerateArray(), s => s.GetProperty("email").GetString() == "ops@example.com");
        Assert.Equal(HttpStatusCode.NoContent, (await Admin().DeleteAsync($"/admin/notifications/subscriptions/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Admin().DeleteAsync($"/admin/notifications/subscriptions/{id}")).StatusCode);
    }

    [Theory]
    [InlineData("a@x.com", "NoSuchTopic")]
    [InlineData("nope", "OrderRejected")]
    [InlineData("", "OrderRejected")]
    public async Task Invalid_subscriptions_are_rejected(string email, string topic) =>
        Assert.Equal(HttpStatusCode.BadRequest,
            (await Admin().PostAsJsonAsync("/admin/notifications/subscriptions", new { email, topic })).StatusCode);

    [Fact]
    public async Task Subscription_management_is_admin_only()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientAs("Buyer").GetAsync("/admin/notifications/subscriptions")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.ClientAs(null).GetAsync("/admin/notifications/topics")).StatusCode);
    }

    [Fact]
    public async Task Submitting_an_order_emails_the_subscribed_supplier_contacts()
    {
        await Subscribe("supplier-mail@example.com", "OrderSubmitted");
        var response = await _factory.ClientAs("Buyer").PostAsync("/orders/import", StocklineApiFactory.RichTemplateUpload("MAIL-1"));
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("orderId").GetGuid();

        await _factory.ClientAs("Buyer").PostAsync($"/orders/{id}/actions/submit", null);

        var message = await WaitForMail("MAIL-1");
        Assert.Contains("supplier-mail@example.com", message.To);
        Assert.Equal("Order submitted - MAIL-1", message.Subject);
    }

    private async Task<Stockline.Application.Notifications.EmailMessage> WaitForMail(string orderCode)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!cts.IsCancellationRequested)
        {
            if (_factory.Emails.Sent.FirstOrDefault(m => m.Subject.EndsWith(orderCode)) is { } found)
            {
                return found;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"No email for {orderCode} arrived.");
    }
}

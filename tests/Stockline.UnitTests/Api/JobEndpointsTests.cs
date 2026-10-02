using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Stockline.Domain.Orders;

namespace Stockline.UnitTests.Api;

public class JobEndpointsTests(StocklineApiFactory factory) : IClassFixture<StocklineApiFactory>
{
    private static async Task<JsonElement> Body(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<JsonElement> WaitForFinish(HttpClient client, Guid id)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            var job = await Body(await client.GetAsync($"/jobs/{id}", cts.Token));
            if (job.GetProperty("status").GetString() is "Succeeded" or "Failed")
            {
                return job;
            }

            await Task.Delay(50, cts.Token);
        }
    }

    [Fact]
    public async Task An_async_import_answers_at_once_and_creates_the_order_in_the_background()
    {
        var (buyer, _, _) = await factory.SignedInAsync(ActorRole.Buyer);
        var code = $"ASYNC-{Guid.NewGuid():N}"[..14];

        var accepted = await buyer.PostAsync("/orders/import/async", StocklineApiFactory.TemplateUpload(code));

        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var id = (await Body(accepted)).GetProperty("id").GetGuid();
        Assert.Equal($"/jobs/{id}", accepted.Headers.Location!.OriginalString);

        var job = await WaitForFinish(buyer, id);
        Assert.Equal("Succeeded", job.GetProperty("status").GetString());
        Assert.Equal(100, job.GetProperty("progress").GetInt32());
        var result = job.GetProperty("result");
        Assert.Equal("Imported", result.GetProperty("outcome").GetString());
        var orderId = result.GetProperty("orderId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await buyer.GetAsync($"/orders/{orderId}")).StatusCode);
    }

    [Fact]
    public async Task A_template_with_errors_finishes_the_job_with_the_issues_and_creates_nothing()
    {
        var (buyer, _, _) = await factory.SignedInAsync(ActorRole.Buyer);

        var accepted = await buyer.PostAsync("/orders/import/async", StocklineApiFactory.TemplateUpload($"BAD-{Guid.NewGuid():N}"[..12], "ST-NOPE"));
        var job = await WaitForFinish(buyer, (await Body(accepted)).GetProperty("id").GetGuid());

        Assert.Equal("Succeeded", job.GetProperty("status").GetString());
        var result = job.GetProperty("result");
        Assert.Equal("ValidationFailed", result.GetProperty("outcome").GetString());
        Assert.Contains(result.GetProperty("issues").EnumerateArray(), i => i.GetProperty("code").GetString() == "UNKNOWN_LOCATION");
        Assert.Equal(JsonValueKind.Null, result.GetProperty("orderId").ValueKind);
    }

    [Fact]
    public async Task A_duplicate_order_code_is_reported_in_the_job_result()
    {
        var (buyer, _, _) = await factory.SignedInAsync(ActorRole.Buyer);
        var code = $"DUP-{Guid.NewGuid():N}"[..12];
        Assert.Equal(HttpStatusCode.Created, (await buyer.PostAsync("/orders/import", StocklineApiFactory.TemplateUpload(code))).StatusCode);

        var accepted = await buyer.PostAsync("/orders/import/async", StocklineApiFactory.TemplateUpload(code));
        var job = await WaitForFinish(buyer, (await Body(accepted)).GetProperty("id").GetGuid());

        Assert.Equal("DuplicateOrderCode", job.GetProperty("result").GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Only_the_owner_and_admins_can_see_a_job()
    {
        var (owner, _, _) = await factory.SignedInAsync(ActorRole.Buyer);
        var (other, _, _) = await factory.SignedInAsync(ActorRole.Buyer);
        var (admin, _, _) = await factory.SignedInAsync(ActorRole.Buyer, isAdmin: true);
        var accepted = await owner.PostAsync("/orders/import/async", StocklineApiFactory.TemplateUpload($"OWN-{Guid.NewGuid():N}"[..12]));
        var id = (await Body(accepted)).GetProperty("id").GetGuid();
        await WaitForFinish(owner, id);

        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/jobs/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/jobs/{id}")).StatusCode);
        Assert.DoesNotContain(
            (await Body(await other.GetAsync("/jobs"))).EnumerateArray(), j => j.GetProperty("id").GetGuid() == id);
        Assert.Contains(
            (await Body(await owner.GetAsync("/jobs?kind=OrderImport"))).EnumerateArray(), j => j.GetProperty("id").GetGuid() == id);
    }

    [Fact]
    public async Task Async_import_is_buyer_only_and_validates_the_upload()
    {
        var (supplier, _, _) = await factory.SignedInAsync(ActorRole.Supplier);
        var (buyer, _, _) = await factory.SignedInAsync(ActorRole.Buyer);

        Assert.Equal(HttpStatusCode.Forbidden, (await supplier.PostAsync("/orders/import/async", StocklineApiFactory.TemplateUpload("X-1"))).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await buyer.PostAsync("/orders/import/async", StocklineApiFactory.Upload([1, 2, 3], "order.txt"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await buyer.PostAsync("/orders/import/async", new MultipartFormDataContent())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync($"/jobs/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await buyer.GetAsync("/jobs?kind=Nope")).StatusCode);
    }

    [Fact]
    public async Task An_unreadable_file_ends_as_an_invalid_format_result_not_a_crash()
    {
        var (buyer, _, _) = await factory.SignedInAsync(ActorRole.Buyer);

        var accepted = await buyer.PostAsync("/orders/import/async", StocklineApiFactory.Upload([1, 2, 3, 4], "broken.xlsx"));
        var job = await WaitForFinish(buyer, (await Body(accepted)).GetProperty("id").GetGuid());

        Assert.Equal("InvalidFormat", job.GetProperty("result").GetProperty("outcome").GetString());
    }
}

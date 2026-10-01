using Microsoft.Extensions.Logging.Abstractions;
using Stockline.Application.Notifications;
using Stockline.Domain.Notifications;
using Stockline.UnitTests.Support;

namespace Stockline.UnitTests.Orders;

public class EmailNotificationTests
{
    private static OrderEvent Event(string type, string status = "Submitted", string code = "PO-1") =>
        new(Guid.NewGuid(), code, type, status, new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero), "user-1");

    [Theory]
    [InlineData("Submit", "Submitted", EmailTopic.OrderSubmitted)]
    [InlineData("Cancel", "Cancelled", EmailTopic.OrderCancelledByBuyer)]
    [InlineData("Reject", "Rejected", EmailTopic.OrderRejected)]
    [InlineData("CancelBySupplier", "CancelledBySupplier", EmailTopic.OrderCancelledBySupplier)]
    [InlineData("ConfirmProduction", "ProductionConfirmed", EmailTopic.ProductionConfirmed)]
    [InlineData("ConfirmRedistribution", "RedistributionConfirmed", EmailTopic.RedistributionConfirmed)]
    [InlineData("ErpSubmission", "Sent", EmailTopic.ReadyForDispatch)]
    [InlineData("ErpSubmission", "LogisticsPending", EmailTopic.ReadyForDispatch)]
    [InlineData("ErpSubmission", "ErpSubmissionFailed", EmailTopic.ErpSubmissionFailed)]
    public void Events_map_to_the_topic_that_notifies_their_audience(string type, string status, EmailTopic expected) =>
        Assert.Equal(expected, EmailTopics.For(Event(type, status)));

    [Theory]
    [InlineData("Open", "UnderReview")]
    [InlineData("OrderImported", "Draft")]
    [InlineData("ChildOrderCreated", "ProductionPostponed")]
    [InlineData("ErpSubmission", "RedistributionConfirmed")]
    public void Other_events_send_no_email(string type, string status) =>
        Assert.Null(EmailTopics.For(Event(type, status)));

    [Fact]
    public void Every_topic_has_a_catalog_entry_and_every_entry_a_distinct_topic()
    {
        Assert.Equal(Enum.GetValues<EmailTopic>().OrderBy(t => t), EmailTopics.All.Select(t => t.Topic).OrderBy(t => t));
        Assert.Equal(EmailAudience.Technical, EmailTopics.Info(EmailTopic.ErpSubmissionFailed).Audience);
        Assert.Equal(EmailAudience.SupplierProduction, EmailTopics.Info(EmailTopic.OrderSubmitted).Audience);
        Assert.Equal(EmailAudience.Supplier, EmailTopics.Info(EmailTopic.ProductionConfirmed).Audience);
        Assert.Equal(EmailAudience.Buyer, EmailTopics.Info(EmailTopic.OrderCancelledBySupplier).Audience);
    }

    [Fact]
    public void Email_content_is_html_encoded_and_only_links_http_urls()
    {
        var evt = Event("Submit") with { OrderCode = "<script>alert(1)</script>" };

        var safe = OrderEmailBuilder.Build(EmailTopic.OrderSubmitted, evt, "https://app.example/orders/42");
        var unsafeLink = OrderEmailBuilder.Build(EmailTopic.OrderSubmitted, evt, "javascript:alert(1)");

        Assert.DoesNotContain("<script>", safe.HtmlBody);
        Assert.Contains("&lt;script&gt;", safe.HtmlBody);
        Assert.Contains("href=\"https://app.example/orders/42\"", safe.HtmlBody);
        Assert.DoesNotContain("href=", unsafeLink.HtmlBody);
        Assert.Equal("Order submitted - <script>alert(1)</script>", safe.Subject);
        Assert.Contains("Triggered by: user-1", safe.TextBody);
    }

    private sealed class FakeSubscriptions(params string[] recipients) : IEmailSubscriptionRepository
    {
        public Task<IReadOnlyList<EmailSubscription>> ListAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> RecipientsAsync(EmailTopic topic, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(recipients);

        public Task<(EmailSubscription Subscription, bool Created)> AddAsync(string email, EmailTopic topic, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<bool> RemoveAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Notifier_sends_one_email_to_all_subscribers_with_the_order_link()
    {
        var sender = new FakeEmailSender();
        var notifier = new OrderEmailNotifier(new FakeSubscriptions("a@x.com", "b@x.com"), sender, NullLogger<OrderEmailNotifier>.Instance);
        var evt = Event("Submit");

        var sent = await notifier.NotifyAsync(evt, "https://app/orders/{id}", CancellationToken.None);

        Assert.True(sent);
        var message = Assert.Single(sender.Sent);
        Assert.Equal(["a@x.com", "b@x.com"], message.To);
        Assert.Contains($"https://app/orders/{evt.OrderId}", message.HtmlBody);
    }

    [Fact]
    public async Task Notifier_skips_events_without_a_topic_and_topics_without_subscribers()
    {
        var sender = new FakeEmailSender();

        Assert.False(await new OrderEmailNotifier(new FakeSubscriptions("a@x.com"), sender, NullLogger<OrderEmailNotifier>.Instance)
            .NotifyAsync(Event("Open", "UnderReview"), null, CancellationToken.None));
        Assert.False(await new OrderEmailNotifier(new FakeSubscriptions(), sender, NullLogger<OrderEmailNotifier>.Instance)
            .NotifyAsync(Event("Submit"), null, CancellationToken.None));
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task Notifier_swallows_sender_failures_because_email_is_best_effort()
    {
        var sender = new FakeEmailSender { FailWith = new InvalidOperationException("smtp down") };
        var notifier = new OrderEmailNotifier(new FakeSubscriptions("a@x.com"), sender, NullLogger<OrderEmailNotifier>.Instance);

        Assert.False(await notifier.NotifyAsync(Event("Submit"), null, CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    public void Subscription_requires_a_valid_email(string email) =>
        Assert.Throws<Stockline.Domain.DomainException>(() => new EmailSubscription(Guid.NewGuid(), email, EmailTopic.OrderSubmitted));

    [Fact]
    public void Subscription_email_is_normalized() =>
        Assert.Equal("a@x.com", new EmailSubscription(Guid.NewGuid(), "  A@X.com ", EmailTopic.OrderSubmitted).Email);
}

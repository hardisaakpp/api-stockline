using Stockline.Domain.Notifications;
using Stockline.Infrastructure.Persistence;

namespace Stockline.UnitTests.Persistence;

public class EmailSubscriptionPersistenceTests
{
    [Fact]
    public async Task Adding_the_same_subscription_twice_keeps_one_row_and_reports_it_was_not_created()
    {
        using var db = new SqliteDb();
        await using var context = db.NewContext();
        var repository = new EfEmailSubscriptionRepository(context);

        var first = await repository.AddAsync("A@x.com", EmailTopic.OrderSubmitted, default);
        var second = await repository.AddAsync("a@x.com", EmailTopic.OrderSubmitted, default);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.Subscription.Id, second.Subscription.Id);
        Assert.Single(await repository.ListAsync(default));
    }

    [Fact]
    public async Task Recipients_are_resolved_per_topic_and_can_be_removed()
    {
        using var db = new SqliteDb();
        await using var context = db.NewContext();
        var repository = new EfEmailSubscriptionRepository(context);
        var b = await repository.AddAsync("b@x.com", EmailTopic.OrderSubmitted, default);
        await repository.AddAsync("a@x.com", EmailTopic.OrderSubmitted, default);
        await repository.AddAsync("c@x.com", EmailTopic.OrderRejected, default);

        Assert.Equal(["a@x.com", "b@x.com"], await repository.RecipientsAsync(EmailTopic.OrderSubmitted, default));
        Assert.True(await repository.RemoveAsync(b.Subscription.Id, default));
        Assert.False(await repository.RemoveAsync(b.Subscription.Id, default));
        Assert.Equal(["a@x.com"], await repository.RecipientsAsync(EmailTopic.OrderSubmitted, default));
    }
}

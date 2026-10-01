using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stockline.Application.Notifications;
using Stockline.Domain.Notifications;

namespace Stockline.Infrastructure.Persistence;

internal sealed class EmailSubscriptionConfiguration : IEntityTypeConfiguration<EmailSubscription>
{
    public void Configure(EntityTypeBuilder<EmailSubscription> builder)
    {
        builder.ToTable("email_subscriptions");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.Email).HasMaxLength(200).IsRequired();
        builder.Property(s => s.Topic).HasConversion<string>().HasMaxLength(60);
        builder.HasIndex(s => new { s.Email, s.Topic }).IsUnique();
    }
}

public sealed class EfEmailSubscriptionRepository(StocklineDbContext db) : IEmailSubscriptionRepository
{
    public async Task<IReadOnlyList<EmailSubscription>> ListAsync(CancellationToken ct) =>
        (await db.EmailSubscriptions.AsNoTracking().ToListAsync(ct))
            .OrderBy(s => s.Email, StringComparer.Ordinal).ThenBy(s => s.Topic).ToList();

    public async Task<IReadOnlyList<string>> RecipientsAsync(EmailTopic topic, CancellationToken ct) =>
        await db.EmailSubscriptions.AsNoTracking().Where(s => s.Topic == topic).Select(s => s.Email).OrderBy(e => e).ToListAsync(ct);

    public async Task<(EmailSubscription Subscription, bool Created)> AddAsync(string email, EmailTopic topic, CancellationToken ct)
    {
        var subscription = new EmailSubscription(Guid.CreateVersion7(), email, topic);
        var existing = await db.EmailSubscriptions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Email == subscription.Email && s.Topic == topic, ct);
        if (existing is not null)
        {
            return (existing, false);
        }

        db.EmailSubscriptions.Add(subscription);
        await db.SaveChangesAsync(ct);
        return (subscription, true);
    }

    public async Task<bool> RemoveAsync(Guid id, CancellationToken ct) =>
        await db.EmailSubscriptions.Where(s => s.Id == id).ExecuteDeleteAsync(ct) > 0;
}

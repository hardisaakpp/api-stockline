using Stockline.Application.Orders.Import;

namespace Stockline.UnitTests.Support;

/// <summary>A clock frozen shortly after the dates the fixtures use, so date-based rules never rot with the calendar.</summary>
public sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public static readonly DateTimeOffset FixtureNow = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => now;

    public static OrderTemplateValidator Validator() => new(new FixedTimeProvider(FixtureNow));
}

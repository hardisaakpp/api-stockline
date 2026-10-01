using Stockline.Domain.Orders;

namespace Stockline.UnitTests.Orders;

public class PackTests
{
    [Theory]
    [InlineData("X1", 1)]
    [InlineData("x6", 6)]
    [InlineData(" X12 ", 12)]
    public void Parses_valid_packs(string text, int units)
    {
        Assert.True(Pack.TryParse(text, out var pack));
        Assert.Equal(units, pack.Units);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("X")]
    [InlineData("X0")]
    [InlineData("6")]
    [InlineData("X-3")]
    [InlineData("XA")]
    public void Rejects_invalid_packs(string? text) => Assert.False(Pack.TryParse(text, out _));

    [Theory]
    [InlineData("X1", 11.2, 0.9333)]
    [InlineData("X6", 5.6, 2.8)]
    [InlineData("X12", 2.1, 2.1)]
    public void Unit_cost_is_cost_divided_by_dozen_over_pack(string text, double cost, double expected)
    {
        Pack.TryParse(text, out var pack);

        Assert.Equal((decimal)expected, Math.Round(pack.ToUnitCost((decimal)cost), 4));
    }
}

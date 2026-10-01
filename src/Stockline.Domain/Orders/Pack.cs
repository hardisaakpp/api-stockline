using System.Globalization;

namespace Stockline.Domain.Orders;

public readonly record struct Pack(int Units)
{
    private const int UnitsPerDozen = 12;

    public static bool TryParse(string? value, out Pack pack)
    {
        pack = default;
        var text = value?.Trim();
        if (text is not { Length: > 1 } || char.ToUpperInvariant(text[0]) != 'X')
        {
            return false;
        }

        if (!int.TryParse(text.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var units) || units <= 0)
        {
            return false;
        }

        pack = new Pack(units);
        return true;
    }

    public decimal ToUnitCost(decimal costWithoutTax) => costWithoutTax / (UnitsPerDozen / (decimal)Units);

    public override string ToString() => $"X{Units}";
}

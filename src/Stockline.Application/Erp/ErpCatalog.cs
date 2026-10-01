namespace Stockline.Application.Erp;

/// <summary>What the ERP knows about one product.</summary>
/// <param name="Pack">The pack text the ERP keeps for the product (e.g. X6), if any.</param>
/// <param name="RetailPrice">The ERP's retail price (PVP), if any.</param>
public sealed record ErpItemInfo(
    string Code, string? Name, string? Barcode, string? Pack, decimal? RetailPrice, bool IsActive);

/// <summary>Result of looking products up; <see cref="Error"/> is set when the ERP could not be queried.</summary>
public sealed record ErpItemLookup(IReadOnlyDictionary<string, ErpItemInfo> Found, string? Error = null)
{
    public bool Succeeded => Error is null;
}

/// <summary>Read-only master data of the ERP (products), used to validate templates before an order exists.</summary>
public interface IErpCatalog
{
    /// <summary>Looks products up by exact code; codes the ERP does not know are simply absent from the result.</summary>
    Task<ErpItemLookup> GetItemsAsync(IReadOnlyCollection<string> codes, CancellationToken ct);

    /// <summary>Active products whose code starts like <paramref name="code"/>, to offer as corrections.</summary>
    Task<IReadOnlyList<ErpItemInfo>> SuggestItemsAsync(string code, int limit, CancellationToken ct);
}

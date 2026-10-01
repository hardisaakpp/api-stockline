namespace Stockline.Infrastructure.Erp;

public sealed class ErpOptions
{
    public string BaseUrl { get; set; } = "http://localhost:50001/b1s/v1/";

    public string CompanyDb { get; set; } = "DEMO";

    public string UserName { get; set; } = "manager";

    public string Password { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>User-defined field holding a product's pack (e.g. X6).</summary>
    public string PackField { get; set; } = "U_IXX_PACK";

    /// <summary>Number of the price list that holds the retail price (PVP).</summary>
    public int RetailPriceList { get; set; } = 1;
}

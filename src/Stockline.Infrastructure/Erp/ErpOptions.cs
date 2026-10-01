namespace Stockline.Infrastructure.Erp;

public sealed class ErpOptions
{
    public string BaseUrl { get; set; } = "http://localhost:50001/b1s/v1/";

    public string CompanyDb { get; set; } = "DEMO";

    public string UserName { get; set; } = "manager";

    public string Password { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 30;
}

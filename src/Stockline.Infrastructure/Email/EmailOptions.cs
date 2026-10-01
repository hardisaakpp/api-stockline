namespace Stockline.Infrastructure.Email;

public sealed class EmailOptions
{
    /// <summary>SMTP host. When empty, emails are only written to the log (development default).</summary>
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 587;

    public bool UseStartTls { get; set; } = true;

    public string? User { get; set; }

    public string? Password { get; set; }

    public string From { get; set; } = "stockline@localhost";

    public string FromName { get; set; } = "Stockline";

    /// <summary>Link template placed in emails; <c>{id}</c> is replaced by the order id. Empty omits the link.</summary>
    public string? OrderUrlTemplate { get; set; }
}

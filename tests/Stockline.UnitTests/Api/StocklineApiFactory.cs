using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Claims;
using System.Text;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Stockline.Application.Erp;
using Stockline.Application.Identity;
using Stockline.Domain.Identity;
using Stockline.Domain.Orders;
using Stockline.Infrastructure.Identity;
using Stockline.Infrastructure.Persistence;
using Stockline.UnitTests.Support;

namespace Stockline.UnitTests.Api;

public sealed class StocklineApiFactory : WebApplicationFactory<Program>
{
    private const string JwtKey = "test-signing-key-test-signing-key-0123456789";
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"stockline-test-{Guid.NewGuid():N}.db");

    public FakeErpGateway Erp { get; } = new();

    public FakeEmailSender Emails { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Jwt:Key", JwtKey);
        builder.UseSetting("Jwt:Issuer", "stockline");
        builder.UseSetting("Jwt:Audience", "stockline-api");
        builder.UseSetting("ConnectionStrings:Default", "unused");
        builder.UseSetting("ErpBackground:RetryIntervalSeconds", "1");
        builder.UseSetting("Jobs:PollIntervalMilliseconds", "50");
        builder.UseSetting("RateLimiting:LoginPerMinute", "10000");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<StocklineDbContext>>();
            foreach (var descriptor in services
                         .Where(d => d.ServiceType.IsGenericType
                                     && d.ServiceType.Name.StartsWith("IDbContextOptionsConfiguration")
                                     && d.ServiceType.GenericTypeArguments[0] == typeof(StocklineDbContext))
                         .ToList())
            {
                services.Remove(descriptor);
            }

            services.RemoveAll<IPasswordHasher>();
            services.AddSingleton<IPasswordHasher>(new Pbkdf2PasswordHasher(iterations: 1000));
            services.RemoveAll<Stockline.Application.Notifications.IEmailSender>();
            services.AddSingleton<Stockline.Application.Notifications.IEmailSender>(Emails);
            services.RemoveAll<IErpGateway>();
            services.AddSingleton<IErpGateway>(Erp);
            services.AddDbContext<StocklineDbContext>(options => options.UseSqlite($"Data Source={_dbPath}"));
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StocklineDbContext>();
        db.Database.EnsureCreated();
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        return host;
    }

    public const string UserPassword = "Test-Pass-2026!";

    /// <summary>Creates a real user in the test database and returns a client signed in through <c>/auth/login</c>.</summary>
    public async Task<(HttpClient Client, User User, JsonElement Session)> SignedInAsync(
        ActorRole role, bool isAdmin = false, string? email = null)
    {
        var user = SeedUser(role, isAdmin, email);
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/auth/login", new { user.Email, Password = UserPassword });
        response.EnsureSuccessStatusCode();
        var session = (await response.Content.ReadFromJsonAsync<JsonElement>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.GetProperty("accessToken").GetString());
        return (client, user, session);
    }

    public User SeedUser(ActorRole role, bool isAdmin = false, string? email = null, bool active = true)
    {
        using var scope = Services.CreateScope();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var db = scope.ServiceProvider.GetRequiredService<StocklineDbContext>();
        var user = User.Create(
            Guid.NewGuid(), email ?? $"{Guid.NewGuid():N}@stockline.test", "Test User", hasher.Hash(UserPassword), role, isAdmin,
            DateTimeOffset.UtcNow);
        if (!active)
        {
            user.Update(user.DisplayName, role, isAdmin, isActive: false);
        }

        db.Users.Add(user);
        db.SaveChanges();
        return user;
    }

    public HttpClient ClientAs(string? role, bool admin = false)
    {
        var client = CreateClient();
        if (role is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(role, admin));
        }

        return client;
    }

    private static string Token(string role, bool admin = false) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = "stockline",
        Audience = "stockline-api",
        Subject = new ClaimsIdentity([new Claim("sub", "user-1"), new Claim("role", role), .. admin ? [new Claim("is_admin", "true")] : Array.Empty<Claim>()]),
        Expires = DateTime.UtcNow.AddMinutes(10),
        SigningCredentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtKey)), SecurityAlgorithms.HmacSha256),
    });

    public static MultipartFormDataContent TemplateUpload(string orderCode, string locationCode = "ST-001", string fileName = "order.xlsx")
    {
        using var workbook = new XLWorkbook();
        var header = workbook.AddWorksheet("cabecera");
        header.Cell("A1").Value = "CAMPO";
        header.Cell("A2").Value = "CODIGO PEDIDO";
        header.Cell("B2").Value = orderCode;
        header.Cell("A3").Value = "FECHA DE PEDIDO";
        header.Cell("B3").Value = DateTime.Today;
        header.Cell("A4").Value = "NOMBRE PROVEEDOR";
        header.Cell("B4").Value = "ACME SUPPLIER";
        var detail = workbook.AddWorksheet("detalle");
        detail.Cell("A1").Value = "Codigo Producto";
        detail.Cell("B1").Value = "Pack";
        detail.Cell("C1").Value = "Bodega";
        detail.Cell("D1").Value = "Tienda";
        detail.Cell("C2").Value = "WH-MAIN";
        detail.Cell("D2").Value = locationCode;
        detail.Cell("A3").Value = "SKU-1";
        detail.Cell("B3").Value = "X6";
        detail.Cell("C3").Value = 2;
        detail.Cell("D3").Value = 1.5;
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return Upload(stream.ToArray(), fileName);
    }

    public static MultipartFormDataContent RichTemplateUpload(string orderCode)
    {
        using var workbook = new XLWorkbook();
        var header = workbook.AddWorksheet("cabecera");
        header.Cell("A1").Value = "CAMPO";
        header.Cell("A2").Value = "CODIGO PEDIDO";
        header.Cell("B2").Value = orderCode;
        header.Cell("A3").Value = "FECHA DE PEDIDO";
        header.Cell("B3").Value = DateTime.Today;
        header.Cell("A4").Value = "NOMBRE PROVEEDOR";
        header.Cell("B4").Value = "ACME SUPPLIER";
        var detail = workbook.AddWorksheet("detalle");
        string[] headers = ["Codigo Producto", "Pack", "Bodega", "Tienda 1", "Tienda 2", "Tienda 3"];
        string[] codes = [string.Empty, string.Empty, "WH-MAIN", "ST-001", "ST-002", "ST-003"];
        for (var c = 0; c < headers.Length; c++)
        {
            detail.Cell(1, c + 1).Value = headers[c];
            if (codes[c].Length > 0)
            {
                detail.Cell(2, c + 1).Value = codes[c];
            }
        }

        detail.Cell("A3").Value = "SKU-1";
        detail.Cell("B3").Value = "X6";
        detail.Cell("C3").Value = 2;
        detail.Cell("D3").Value = 1;
        detail.Cell("E3").Value = 1;
        detail.Cell("A4").Value = "SKU-2";
        detail.Cell("B4").Value = "X2";
        detail.Cell("D4").Value = 3;
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return Upload(stream.ToArray(), "order.xlsx");
    }

    /// <summary>Like <see cref="RichTemplateUpload"/> but with cost, discount and retail price columns.</summary>
    public static MultipartFormDataContent PricedTemplateUpload(string orderCode)
    {
        using var workbook = new XLWorkbook();
        var header = workbook.AddWorksheet("cabecera");
        header.Cell("A1").Value = "CAMPO";
        header.Cell("A2").Value = "CODIGO PEDIDO";
        header.Cell("B2").Value = orderCode;
        header.Cell("A3").Value = "FECHA DE PEDIDO";
        header.Cell("B3").Value = DateTime.Today;
        header.Cell("A4").Value = "NOMBRE PROVEEDOR";
        header.Cell("B4").Value = "ACME SUPPLIER";
        var detail = workbook.AddWorksheet("detalle");
        string[] headers = ["Codigo Producto", "Pack", "Costo sin IVA", "Descuento", "PVP", "Bodega", "Tienda 1"];
        string[] codes = [string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, "WH-MAIN", "ST-001"];
        for (var c = 0; c < headers.Length; c++)
        {
            detail.Cell(1, c + 1).Value = headers[c];
            if (codes[c].Length > 0)
            {
                detail.Cell(2, c + 1).Value = codes[c];
            }
        }

        detail.Cell("A3").Value = "SKU-1";
        detail.Cell("B3").Value = "X12";
        detail.Cell("C3").Value = 10;
        detail.Cell("D3").Value = 0.1;
        detail.Cell("E3").Value = 23;
        detail.Cell("F3").Value = 6;
        detail.Cell("G3").Value = 4;
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return Upload(stream.ToArray(), "priced.xlsx");
    }

    public static MultipartFormDataContent Upload(byte[] bytes, string fileName)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        content.Add(file, "file", fileName);
        return content;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            // Only this factory's pool: clearing every pool would pull connections out from under test classes still running.
            using (var own = new SqliteConnection($"Data Source={_dbPath}"))
            {
                SqliteConnection.ClearPool(own);
            }

            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                File.Delete(_dbPath + suffix);
            }
        }
    }
}

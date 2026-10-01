using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Stockline.Application.Erp;
using Stockline.Application.Identity;
using Stockline.Application.Orders.Queries;
using Stockline.Infrastructure.Identity;
using Stockline.Application.Orders;
using Stockline.Application.Orders.Import;
using Stockline.Infrastructure.Email;
using Stockline.Infrastructure.Erp;
using Stockline.Infrastructure.Excel;
using Stockline.Infrastructure.Notifications;
using Stockline.Infrastructure.Pdf;
using Stockline.Application.Notifications;
using Stockline.Infrastructure.Persistence;

namespace Stockline.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<StocklineDbContext>(options =>
            options.UseSqlServer(configuration.GetConnectionString("Default")));
        services.AddScoped<IOrderRepository, EfOrderRepository>();
        services.AddScoped<IErpDocumentRepository, EfErpDocumentRepository>();
        services.AddScoped<IErpQueries, EfErpQueries>();
        services.AddScoped<IOrderQueries, EfOrderQueries>();
        services.AddScoped<IUserRepository, EfUserRepository>();
        services.AddScoped<IUserPreferenceRepository, EfUserPreferenceRepository>();
        services.AddScoped<IOrderFileStore, EfOrderFileStore>();
        services.AddScoped<IRefreshTokenRepository, EfRefreshTokenRepository>();
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.Configure<ErpOptions>(configuration.GetSection("Erp"));
        services.AddSingleton<ErpSession>();
        services.AddHttpClient<IErpGateway, ServiceLayerErpGateway>((provider, client) =>
        {
            var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ErpOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl.EndsWith('/') ? options.BaseUrl : options.BaseUrl + "/");
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        });
        services.AddTransient<IErpCatalog>(sp => (IErpCatalog)sp.GetRequiredService<IErpGateway>());
        services.AddScoped<ILocationCatalogProvider, DbLocationCatalogProvider>();
        services.AddSingleton<IOrderTemplateReader, ClosedXmlOrderTemplateReader>();
        services.AddSingleton<IOrderExporter, ClosedXmlOrderExporter>();
        services.AddSingleton<Stockline.Application.Reports.IOrderReportExporter, ClosedXmlOrderReportExporter>();
        services.AddSingleton<OrderEventHub>();
        services.AddSingleton<IPurchaseOrderPdfRenderer, QuestPdfPurchaseOrderRenderer>();
        services.AddSingleton(new BuyerProfile(
            configuration["Company:Name"] ?? "Demo Buyer, Inc.", configuration["Company:TaxId"], configuration["Company:Address"]));
        services.AddSingleton<EmailOrderEvents>();
        services.AddSingleton<IOrderEvents>(sp => new CompositeOrderEvents(
            [sp.GetRequiredService<OrderEventHub>(), sp.GetRequiredService<EmailOrderEvents>()]));
        services.Configure<EmailOptions>(configuration.GetSection("Email"));
        services.AddScoped<IEmailSubscriptionRepository, EfEmailSubscriptionRepository>();
        services.AddSingleton<IEmailSender>(sp =>
            string.IsNullOrWhiteSpace(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<EmailOptions>>().Value.Host)
                ? ActivatorUtilities.CreateInstance<LoggingEmailSender>(sp)
                : ActivatorUtilities.CreateInstance<SmtpEmailSender>(sp));
        return services;
    }
}

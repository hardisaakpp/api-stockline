using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stockline.Application.Erp;
using Stockline.Application.Identity;
using Stockline.Application.Orders.Import;
using Stockline.Application.Orders.Lifecycle;
using Stockline.Application.Orders.Queries;
using Stockline.Application.Orders.Redistribution;
using Stockline.Application.Orders.Supplier;

namespace Stockline.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<OrderTemplateValidator>();
        services.AddScoped<ImportOrderHandler>();
        services.AddScoped<ApplyOrderActionHandler>();
        services.AddScoped<EditOrderLineHandler>();
        services.AddScoped<ConfirmBySupplierHandler>();
        services.AddScoped<RedistributionHandler>();
        services.AddScoped<SubmitOrderToErpHandler>();
        services.AddScoped<RequestErpSubmissionHandler>();
        services.AddScoped<DashboardHandler>();
        services.AddScoped<Notifications.OrderEmailNotifier>();
        services.AddScoped<SessionIssuer>();
        services.AddScoped<LoginHandler>();
        services.AddScoped<RefreshSessionHandler>();
        services.AddScoped<LogoutHandler>();
        services.AddScoped<UserAdminHandler>();
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }
}

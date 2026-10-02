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
        services.AddScoped<ErpTemplateChecker>();
        services.AddScoped<ImportOrderHandler>();
        services.AddScoped<ApplyOrderActionHandler>();
        services.AddScoped<DiscardOrderHandler>();
        services.AddSingleton<Jobs.JobSignal>();
        services.AddSingleton<Jobs.IJobSignal>(sp => sp.GetRequiredService<Jobs.JobSignal>());
        services.AddScoped<Erp.IErpSubmissionQueue, Jobs.JobErpSubmissionQueue>();
        services.AddScoped<Jobs.IJobHandler, Jobs.ErpSubmissionJobHandler>();
        services.AddScoped<Jobs.IJobHandler, Jobs.ImportJobHandler>();
        services.AddScoped<EditOrderLineHandler>();
        services.AddScoped<ConfirmBySupplierHandler>();
        services.AddScoped<RedistributionHandler>();
        services.AddScoped<SubmitOrderToErpHandler>();
        services.AddScoped<RequestErpSubmissionHandler>();
        services.AddScoped<CorrectProductCodeHandler>();
        services.AddScoped<DashboardHandler>();
        services.AddScoped<Notifications.OrderEmailNotifier>();
        services.AddScoped<PurchaseOrderAttachments>();
        services.AddScoped<SessionIssuer>();
        services.AddScoped<LoginHandler>();
        services.AddScoped<RefreshSessionHandler>();
        services.AddScoped<LogoutHandler>();
        services.AddScoped<UserAdminHandler>();
        services.AddScoped<UserPreferenceHandler>();
        services.AddScoped<Locations.LocationAdminHandler>();
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }
}

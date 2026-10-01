using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Stockline.Api.Admin;
using Stockline.Api.Auth;
using Stockline.Api.Background;
using Stockline.Api.Orders;
using Stockline.Api.Users;
using Stockline.Application;
using Stockline.Application.Identity;
using Stockline.Application.Erp;
using Stockline.Infrastructure;
using Stockline.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddJwtAuthentication(builder.Configuration);
builder.Services.AddSingleton<IAccessTokenIssuer, JwtAccessTokenIssuer>();
builder.Services.AddSingleton(new RefreshTokenOptions(
    TimeSpan.FromDays(builder.Configuration.GetValue("Jwt:RefreshTokenDays", 7))));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    var permits = builder.Configuration.GetValue("RateLimiting:LoginPerMinute", 10);
    options.AddPolicy(AuthEndpoints.LoginRateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = TimeSpan.FromMinutes(1) }));
});
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.Configure<ErpBackgroundOptions>(builder.Configuration.GetSection("ErpBackground"));
builder.Services.AddSingleton<ChannelErpSubmissionQueue>();
builder.Services.AddSingleton<IErpSubmissionQueue>(sp => sp.GetRequiredService<ChannelErpSubmissionQueue>());
builder.Services.AddHostedService<ErpSubmissionWorker>();
builder.Services.AddHostedService<ErpRetryWorker>();
builder.Services.AddHostedService<EmailNotificationWorker>();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();

    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<StocklineDbContext>().Database.Migrate();
    await DevelopmentSeeder.SeedAsync(scope.ServiceProvider, app.Configuration, app.Logger);
}

app.UseHttpsRedirection();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok", time = DateTimeOffset.UtcNow }))
    .WithName("Health");
app.MapAuthEndpoints();
app.MapAdminEndpoints();
app.MapPreferenceEndpoints();
app.MapOrderEndpoints();
app.MapErpEndpoints();
app.MapReportEndpoints();
app.MapNotificationEndpoints();

app.Run();

public partial class Program;

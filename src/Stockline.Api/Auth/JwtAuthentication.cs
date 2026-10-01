using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Stockline.Domain.Orders;

namespace Stockline.Api.Auth;

public static class JwtAuthentication
{
    public const string BuyerPolicy = "Buyer";
    public const string PartnerPolicy = "Partner";
    public const string AdminPolicy = "Admin";
    public const string AdminClaim = "is_admin";

    public static IServiceCollection AddJwtAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("Jwt");
        var key = section["Key"];
        if (string.IsNullOrWhiteSpace(key) || key.Length < 32)
        {
            throw new InvalidOperationException("Jwt:Key must be configured with at least 32 characters.");
        }

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = section["Issuer"],
                    ValidAudience = section["Audience"],
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
                    ValidateIssuerSigningKey = true,
                    NameClaimType = "sub",
                    RoleClaimType = "role",
                    ClockSkew = TimeSpan.FromMinutes(1),
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(BuyerPolicy, policy => policy.RequireRole(nameof(ActorRole.Buyer)))
            .AddPolicy(PartnerPolicy, policy => policy.RequireRole(nameof(ActorRole.Buyer), nameof(ActorRole.Supplier)))
            .AddPolicy(AdminPolicy, policy => policy.RequireClaim(AdminClaim, "true"));

        return services;
    }
}

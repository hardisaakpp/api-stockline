using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Stockline.Application.Identity;
using Stockline.Domain.Identity;

namespace Stockline.Api.Auth;

public sealed class JwtAccessTokenIssuer(IConfiguration configuration) : IAccessTokenIssuer
{
    private readonly string _issuer = configuration["Jwt:Issuer"] ?? string.Empty;
    private readonly string _audience = configuration["Jwt:Audience"] ?? string.Empty;
    private readonly SigningCredentials _credentials = new(
        new SymmetricSecurityKey(Encoding.UTF8.GetBytes(configuration["Jwt:Key"] ?? string.Empty)),
        SecurityAlgorithms.HmacSha256);
    private readonly TimeSpan _lifetime = TimeSpan.FromMinutes(configuration.GetValue("Jwt:AccessTokenMinutes", 15));

    public AccessToken Issue(User user, DateTimeOffset now)
    {
        var expiresAt = now + _lifetime;
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _issuer,
            Audience = _audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            Subject = new ClaimsIdentity(
            [
                new Claim("sub", user.Id.ToString()),
                new Claim("name", user.DisplayName),
                new Claim("email", user.Email),
                new Claim("role", user.Role.ToString()),
                new Claim(JwtAuthentication.AdminClaim, user.IsAdmin ? "true" : "false"),
            ]),
            SigningCredentials = _credentials,
        });
        return new AccessToken(token, expiresAt);
    }
}

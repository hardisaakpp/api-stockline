using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Stockline.Application.Identity;

namespace Stockline.Api.Auth;

public sealed record LoginRequest(string? Email, string? Password);

public sealed record RefreshRequest(string? RefreshToken);

public static class AuthEndpoints
{
    public const string LoginRateLimitPolicy = "login";

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/auth").WithTags("Auth");

        group.MapPost("/login", LoginAsync).RequireRateLimiting(LoginRateLimitPolicy).WithName("Login");
        group.MapPost("/refresh", RefreshAsync).WithName("RefreshSession");
        group.MapPost("/logout", LogoutAsync).WithName("Logout");
        group.MapGet("/me", MeAsync).RequireAuthorization().WithName("CurrentUser");
        return app;
    }

    private static async Task<IResult> LoginAsync(LoginRequest request, LoginHandler handler, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrEmpty(request.Password))
        {
            return Results.Problem(statusCode: 400, title: "Email and password are required.");
        }

        var session = await handler.HandleAsync(request.Email, request.Password, ct);
        return session is null
            ? Results.Problem(statusCode: 401, title: "Invalid email or password.")
            : Results.Ok(session);
    }

    private static async Task<IResult> RefreshAsync(RefreshRequest request, RefreshSessionHandler handler, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return Results.Problem(statusCode: 400, title: "A refresh token is required.");
        }

        var session = await handler.HandleAsync(request.RefreshToken, ct);
        return session is null
            ? Results.Problem(statusCode: 401, title: "The session is no longer valid. Sign in again.")
            : Results.Ok(session);
    }

    private static async Task<IResult> LogoutAsync(RefreshRequest request, LogoutHandler handler, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            await handler.HandleAsync(request.RefreshToken, ct);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> MeAsync(ClaimsPrincipal principal, IUserRepository users, CancellationToken ct)
    {
        if (!Guid.TryParse(principal.FindFirstValue("sub"), out var id) || await users.GetAsync(id, ct) is not { IsActive: true } user)
        {
            return Results.Problem(statusCode: 401, title: "The user no longer exists or is disabled.");
        }

        return Results.Ok(UserSummary.From(user));
    }
}

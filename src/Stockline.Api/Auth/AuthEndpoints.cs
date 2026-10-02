using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Stockline.Application.Identity;

namespace Stockline.Api.Auth;

/// <param name="Email">The email, or for directory sign-in the account name (sAMAccountName), user principal name or email.</param>
/// <param name="Provider"><c>local</c> (the default) or <c>directory</c>.</param>
public sealed record LoginRequest(string? Email, string? Password, string? Provider = null);

public sealed record AuthProviders(bool Directory);

public sealed record RefreshRequest(string? RefreshToken);

public static class AuthEndpoints
{
    public const string LoginRateLimitPolicy = "login";

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/auth").WithTags("Auth");

        group.MapPost("/login", LoginAsync).RequireRateLimiting(LoginRateLimitPolicy).WithName("Login");
        group.MapGet("/providers", (IDirectoryAuthenticator directory) => Results.Ok(new AuthProviders(directory.IsEnabled)))
            .WithName("AuthProviders");
        group.MapPost("/refresh", RefreshAsync).WithName("RefreshSession");
        group.MapPost("/logout", LogoutAsync).WithName("Logout");
        group.MapGet("/me", MeAsync).RequireAuthorization().WithName("CurrentUser");
        return app;
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request, LoginHandler local, DirectoryLoginHandler directory, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrEmpty(request.Password))
        {
            return Results.Problem(statusCode: 400, title: "Email and password are required.");
        }

        if (string.Equals(request.Provider, "directory", StringComparison.OrdinalIgnoreCase))
        {
            return ToResult(await directory.HandleAsync(request.Email, request.Password, ct));
        }

        if (request.Provider is not null && !string.Equals(request.Provider, "local", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Problem(statusCode: 400, title: "Unknown sign-in provider.");
        }

        var session = await local.HandleAsync(request.Email, request.Password, ct);
        return session is null
            ? Results.Problem(statusCode: 401, title: "Invalid email or password.")
            : Results.Ok(session);
    }

    private static IResult ToResult(DirectoryLoginResult result) => result.Outcome switch
    {
        DirectoryLoginOutcome.Succeeded => Results.Ok(result.Session),
        DirectoryLoginOutcome.InvalidCredentials => Results.Problem(statusCode: 401, title: "Invalid username or password."),
        DirectoryLoginOutcome.NotAuthorized => Results.Problem(
            statusCode: 403, title: "Your account is not authorized to use Stockline. Ask an administrator."),
        DirectoryLoginOutcome.EmailConflict => Results.Problem(
            statusCode: 409, title: "A local account already uses the email of this directory account. Ask an administrator."),
        DirectoryLoginOutcome.NotEnabled => Results.Problem(statusCode: 400, title: "Directory sign-in is not enabled."),
        _ => Results.Problem(statusCode: 503, title: "The directory is not reachable right now. Try again later."),
    };

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

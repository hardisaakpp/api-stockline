using System.Security.Claims;
using System.Text.Json;
using Stockline.Application.Identity;

namespace Stockline.Api.Users;

public static class PreferenceEndpoints
{
    public static IEndpointRouteBuilder MapPreferenceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/users/me/preferences").RequireAuthorization().WithTags("Preferences");

        group.MapGet(string.Empty, ListAsync).WithName("ListPreferences");
        group.MapGet("/{key}", GetAsync).WithName("GetPreference");
        group.MapPut("/{key}", SetAsync).WithName("SetPreference");
        group.MapDelete("/{key}", DeleteAsync).WithName("DeletePreference");
        return app;
    }

    private static Guid? UserId(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue("sub"), out var id) ? id : null;

    private static IResult NoUser() => Results.Problem(statusCode: 403, title: "The token does not identify a user.");

    private static async Task<IResult> ListAsync(ClaimsPrincipal user, IUserPreferenceRepository preferences, CancellationToken ct) =>
        UserId(user) is { } id
            ? Results.Ok((await preferences.ListAsync(id, ct)).ToDictionary(p => p.Key, p => JsonDocument.Parse(p.Json).RootElement.Clone()))
            : NoUser();

    private static async Task<IResult> GetAsync(string key, ClaimsPrincipal user, IUserPreferenceRepository preferences, CancellationToken ct)
    {
        if (UserId(user) is not { } id)
        {
            return NoUser();
        }

        var preference = await preferences.GetAsync(id, key, ct);
        return preference is null ? Results.NotFound() : Results.Content(preference.Json, "application/json");
    }

    private static async Task<IResult> SetAsync(
        string key, JsonElement value, ClaimsPrincipal user, UserPreferenceHandler handler, CancellationToken ct)
    {
        if (UserId(user) is not { } id)
        {
            return NoUser();
        }

        return await handler.SetAsync(id, key, value, ct) switch
        {
            PreferenceOutcome.Saved => Results.NoContent(),
            PreferenceOutcome.InvalidKey => Results.Problem(
                statusCode: 400, title: "The key must be 1-100 characters: letters, digits, '-', '_' or '.'."),
            PreferenceOutcome.TooLarge => Results.Problem(statusCode: 413, title: "The preference is too large."),
            _ => Results.Problem(statusCode: 400, title: "The preference is not valid JSON."),
        };
    }

    private static async Task<IResult> DeleteAsync(string key, ClaimsPrincipal user, IUserPreferenceRepository preferences, CancellationToken ct) =>
        UserId(user) is { } id
            ? await preferences.DeleteAsync(id, key, ct) ? Results.NoContent() : Results.NotFound()
            : NoUser();
}

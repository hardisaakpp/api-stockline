using Stockline.Api.Auth;
using Stockline.Application.Identity;
using Stockline.Application.Locations;
using Stockline.Application.Notifications;
using Stockline.Domain;
using Stockline.Domain.Locations;
using Stockline.Domain.Notifications;
using Stockline.Domain.Identity;
using Stockline.Domain.Orders;

namespace Stockline.Api.Admin;

public sealed record CreateUserRequest(string? Email, string? DisplayName, string? Password, string? Role, bool IsAdmin);

public sealed record UpdateUserRequest(string? DisplayName, string? Role, bool IsAdmin, bool IsActive);

public sealed record SubscribeRequest(string? Email, string? Topic);

public sealed record CreateLocationRequest(string? Code, string? Name, string? Kind, string? SupplyingWarehouseCode, bool? IsActive);

public sealed record UpdateLocationRequest(string? Name, string? SupplyingWarehouseCode, bool IsActive);

public sealed record ResetPasswordRequest(string? NewPassword);

public sealed record RolePermissions(string Role, IReadOnlyList<string> Permissions);

public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin").RequireAuthorization(JwtAuthentication.AdminPolicy).WithTags("Admin");

        group.MapGet("/users", async (UserAdminHandler handler, CancellationToken ct) =>
            Results.Ok(await handler.ListAsync(ct))).WithName("ListUsers");
        group.MapPost("/users", CreateAsync).WithName("CreateUser");
        group.MapPatch("/users/{id:guid}", UpdateAsync).WithName("UpdateUser");
        group.MapPost("/users/{id:guid}/reset-password", ResetPasswordAsync).WithName("ResetUserPassword");
        group.MapGet("/roles", () => Results.Ok(new
        {
            roles = Permissions.AssignableRoles.Select(r => new RolePermissions(r.ToString(), Permissions.For(r, isAdmin: false))),
            adminPermissions = new[] { Permissions.AdminUsers },
            allPermissions = Permissions.All,
        })).WithName("ListRoles");

        group.MapGet("/notifications/topics", () => Results.Ok(EmailTopics.All.Select(t => new
        {
            topic = t.Topic.ToString(),
            audience = t.Audience.ToString(),
            subject = t.Subject,
            description = t.Description,
        }))).WithName("ListEmailTopics");
        group.MapGet("/notifications/subscriptions", async (IEmailSubscriptionRepository repository, CancellationToken ct) =>
            Results.Ok((await repository.ListAsync(ct)).Select(ToResponse))).WithName("ListEmailSubscriptions");
        group.MapGet("/notifications/topics/{topic}/preview", PreviewAsync).WithName("PreviewEmailTopic");
        group.MapPost("/notifications/subscriptions", SubscribeAsync).WithName("CreateEmailSubscription");
        group.MapDelete("/notifications/subscriptions/{id:guid}", async (Guid id, IEmailSubscriptionRepository repository, CancellationToken ct) =>
            await repository.RemoveAsync(id, ct) ? Results.NoContent() : Results.NotFound()).WithName("DeleteEmailSubscription");

        group.MapGet("/locations", async (ILocationRepository repository, CancellationToken ct) =>
            Results.Ok((await repository.ListAsync(ct)).Select(LocationResponse))).WithName("AdminListLocations");
        group.MapPost("/locations", CreateLocationAsync).WithName("AdminCreateLocation");
        group.MapPatch("/locations/{code}", UpdateLocationAsync).WithName("AdminUpdateLocation");
        return app;
    }

    private static object LocationResponse(Location l) =>
        new { l.Code, l.Name, kind = l.Kind.ToString(), l.SupplyingWarehouseCode, l.IsActive };

    private static async Task<IResult> CreateLocationAsync(CreateLocationRequest request, LocationAdminHandler handler, CancellationToken ct)
    {
        if (!Enum.TryParse<LocationKind>(request.Kind, ignoreCase: true, out var kind) || !Enum.IsDefined(kind))
        {
            return Results.Problem(
                statusCode: 400,
                title: $"Unknown kind '{request.Kind}'.",
                extensions: new Dictionary<string, object?> { ["validKinds"] = Enum.GetNames<LocationKind>() });
        }

        var result = await handler.CreateAsync(
            request.Code ?? string.Empty, request.Name ?? string.Empty, kind, request.SupplyingWarehouseCode, request.IsActive ?? true, ct);
        return result.Outcome == LocationAdminOutcome.Succeeded
            ? Results.Created($"/admin/locations/{result.Location!.Code}", LocationResponse(result.Location))
            : LocationProblem(result);
    }

    private static async Task<IResult> UpdateLocationAsync(
        string code, UpdateLocationRequest request, LocationAdminHandler handler, CancellationToken ct)
    {
        var result = await handler.UpdateAsync(code, request.Name ?? string.Empty, request.SupplyingWarehouseCode, request.IsActive, ct);
        return result.Outcome == LocationAdminOutcome.Succeeded ? Results.Ok(LocationResponse(result.Location!)) : LocationProblem(result);
    }

    private static IResult LocationProblem(LocationAdminResult result) => result.Outcome switch
    {
        LocationAdminOutcome.NotFound => Results.NotFound(),
        LocationAdminOutcome.Conflict => Results.Problem(statusCode: 409, title: result.Message),
        _ => Results.Problem(statusCode: 400, title: result.Message),
    };

    private static IResult PreviewAsync(string topic)
    {
        if (!Enum.TryParse<EmailTopic>(topic, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
        {
            return Results.Problem(
                statusCode: 400,
                title: $"Unknown topic '{topic}'.",
                extensions: new Dictionary<string, object?> { ["validTopics"] = Enum.GetNames<EmailTopic>() });
        }

        var sample = new OrderEvent(
            Guid.Empty, "PO-2026-0001", "Sample", "Submitted", new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero), "user@example.com");
        var content = OrderEmailBuilder.Build(parsed, sample, "https://app.example/orders/{id}");
        return Results.Ok(new { subject = content.Subject, html = content.HtmlBody, text = content.TextBody });
    }

    private static object ToResponse(EmailSubscription s) => new { s.Id, s.Email, topic = s.Topic.ToString() };

    private static async Task<IResult> SubscribeAsync(SubscribeRequest request, IEmailSubscriptionRepository repository, CancellationToken ct)
    {
        if (!Enum.TryParse<EmailTopic>(request.Topic, ignoreCase: true, out var topic) || !Enum.IsDefined(topic))
        {
            return Results.Problem(
                statusCode: 400,
                title: $"Unknown topic '{request.Topic}'.",
                extensions: new Dictionary<string, object?> { ["validTopics"] = Enum.GetNames<EmailTopic>() });
        }

        try
        {
            var (subscription, created) = await repository.AddAsync(request.Email ?? string.Empty, topic, ct);
            return created
                ? Results.Created($"/admin/notifications/subscriptions/{subscription.Id}", ToResponse(subscription))
                : Results.Ok(ToResponse(subscription));
        }
        catch (DomainException ex)
        {
            return Results.Problem(statusCode: 400, title: ex.Message);
        }
    }

    private static async Task<IResult> CreateAsync(CreateUserRequest request, UserAdminHandler handler, CancellationToken ct)
    {
        if (!TryRole(request.Role, out var role))
        {
            return InvalidRole();
        }

        var result = await handler.CreateAsync(
            new CreateUserCommand(request.Email ?? string.Empty, request.DisplayName ?? string.Empty, request.Password ?? string.Empty, role, request.IsAdmin), ct);
        return result.Outcome == UserAdminOutcome.Succeeded
            ? Results.Created($"/admin/users/{result.User!.Id}", result.User)
            : ToProblem(result);
    }

    private static async Task<IResult> UpdateAsync(Guid id, UpdateUserRequest request, UserAdminHandler handler, CancellationToken ct)
    {
        if (!TryRole(request.Role, out var role))
        {
            return InvalidRole();
        }

        var result = await handler.UpdateAsync(
            id, new UpdateUserCommand(request.DisplayName ?? string.Empty, role, request.IsAdmin, request.IsActive), ct);
        return result.Outcome == UserAdminOutcome.Succeeded ? Results.Ok(result.User) : ToProblem(result);
    }

    private static async Task<IResult> ResetPasswordAsync(Guid id, ResetPasswordRequest request, UserAdminHandler handler, CancellationToken ct)
    {
        var result = await handler.ResetPasswordAsync(id, request.NewPassword ?? string.Empty, ct);
        return result.Outcome == UserAdminOutcome.Succeeded ? Results.NoContent() : ToProblem(result);
    }

    private static bool TryRole(string? text, out ActorRole role) =>
        Enum.TryParse(text, ignoreCase: true, out role) && Permissions.AssignableRoles.Contains(role);

    private static IResult InvalidRole() => Results.Problem(
        statusCode: 400,
        title: "The role is not valid.",
        extensions: new Dictionary<string, object?> { ["validRoles"] = Permissions.AssignableRoles.Select(r => r.ToString()) });

    private static IResult ToProblem(UserAdminResult result) => result.Outcome switch
    {
        UserAdminOutcome.NotFound => Results.NotFound(),
        UserAdminOutcome.EmailTaken => Results.Problem(statusCode: 409, title: result.Message),
        UserAdminOutcome.LastAdmin => Results.Problem(statusCode: 409, title: result.Message),
        _ => Results.Problem(statusCode: 400, title: result.Message),
    };
}

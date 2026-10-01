using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Stockline.Domain.Orders;

namespace Stockline.UnitTests.Api;

public class AdminEndpointsTests : IClassFixture<StocklineApiFactory>
{
    private readonly StocklineApiFactory _factory;

    public AdminEndpointsTests(StocklineApiFactory factory) => _factory = factory;

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>())!;

    private static object NewUser(string email, string role = "Supplier", string password = "Brand-New-Pass-1") =>
        new { email, displayName = "New Person", password, role, isAdmin = false };

    [Fact]
    public async Task Admin_endpoints_require_authentication_and_the_admin_claim()
    {
        var (nonAdmin, _, _) = await _factory.SignedInAsync(ActorRole.Buyer);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.ClientAs(null).GetAsync("/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await nonAdmin.GetAsync("/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await nonAdmin.PostAsJsonAsync("/admin/users", NewUser("x@stockline.test"))).StatusCode);
    }

    [Fact]
    public async Task Admin_creates_a_user_who_can_then_sign_in()
    {
        var (admin, _, _) = await _factory.SignedInAsync(ActorRole.Buyer, isAdmin: true);

        var created = await admin.PostAsJsonAsync("/admin/users", NewUser("Created.User@Stockline.test"));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await Json(created);
        Assert.Equal("created.user@stockline.test", body.GetProperty("email").GetString());
        Assert.DoesNotContain("password", body.GetRawText(), StringComparison.OrdinalIgnoreCase);
        var login = await _factory.ClientAs(null).PostAsJsonAsync("/auth/login", new { email = "created.user@stockline.test", password = "Brand-New-Pass-1" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task Creating_a_user_validates_email_password_role_and_uniqueness()
    {
        var (admin, existing, _) = await _factory.SignedInAsync(ActorRole.Buyer, isAdmin: true);

        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/admin/users", NewUser(existing.Email))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/admin/users", NewUser("not-an-email"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/admin/users", NewUser("short@stockline.test", password: "short"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/admin/users", NewUser("system@stockline.test", role: "System"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/admin/users", NewUser("bogus@stockline.test", role: "Wizard"))).StatusCode);
    }

    [Fact]
    public async Task Admin_lists_users_without_secrets()
    {
        var (admin, _, _) = await _factory.SignedInAsync(ActorRole.Buyer, isAdmin: true);
        _factory.SeedUser(ActorRole.Supplier);

        var response = await admin.GetAsync("/admin/users");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var users = await Json(response);
        Assert.True(users.GetArrayLength() >= 2);
        Assert.DoesNotContain("hash", users.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Admin_updates_role_and_the_new_permissions_apply()
    {
        var (admin, _, _) = await _factory.SignedInAsync(ActorRole.Buyer, isAdmin: true);
        var target = _factory.SeedUser(ActorRole.Supplier);

        var response = await admin.PatchAsJsonAsync($"/admin/users/{target.Id}",
            new { displayName = "Promoted", role = "Buyer", isAdmin = false, isActive = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal("Buyer", body.GetProperty("role").GetString());
        Assert.Contains("orders.import", body.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
    }

    [Fact]
    public async Task Updating_an_unknown_user_is_404()
    {
        var (admin, _, _) = await _factory.SignedInAsync(ActorRole.Buyer, isAdmin: true);

        var response = await admin.PatchAsJsonAsync($"/admin/users/{Guid.NewGuid()}",
            new { displayName = "Ghost", role = "Buyer", isAdmin = false, isActive = true });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Deactivating_a_user_blocks_login_and_kills_their_refresh_tokens()
    {
        var (admin, _, _) = await _factory.SignedInAsync(ActorRole.Buyer, isAdmin: true);
        var (_, target, session) = await _factory.SignedInAsync(ActorRole.Supplier);

        var update = await admin.PatchAsJsonAsync($"/admin/users/{target.Id}",
            new { displayName = target.DisplayName, role = "Supplier", isAdmin = false, isActive = false });

        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var anonymous = _factory.ClientAs(null);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync("/auth/login", new { email = target.Email, password = StocklineApiFactory.UserPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync("/auth/refresh", new { refreshToken = session.GetProperty("refreshToken").GetString() })).StatusCode);
    }

    [Fact]
    public async Task The_last_active_admin_cannot_be_demoted_or_disabled()
    {
        // A fresh factory guarantees this admin is the only one.
        await using var factory = new StocklineApiFactory();
        var (admin, self, _) = await factory.SignedInAsync(ActorRole.Buyer, isAdmin: true);

        var demote = await admin.PatchAsJsonAsync($"/admin/users/{self.Id}",
            new { displayName = self.DisplayName, role = "Buyer", isAdmin = false, isActive = true });
        var disable = await admin.PatchAsJsonAsync($"/admin/users/{self.Id}",
            new { displayName = self.DisplayName, role = "Buyer", isAdmin = true, isActive = false });

        Assert.Equal(HttpStatusCode.Conflict, demote.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, disable.StatusCode);
    }

    [Fact]
    public async Task Resetting_a_password_replaces_it_and_revokes_sessions()
    {
        var (admin, _, _) = await _factory.SignedInAsync(ActorRole.Buyer, isAdmin: true);
        var (_, target, session) = await _factory.SignedInAsync(ActorRole.Supplier);

        var reset = await admin.PostAsJsonAsync($"/admin/users/{target.Id}/reset-password", new { newPassword = "A-Different-Pass-9" });

        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        var anonymous = _factory.ClientAs(null);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync("/auth/login", new { email = target.Email, password = StocklineApiFactory.UserPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await anonymous.PostAsJsonAsync("/auth/login", new { email = target.Email, password = "A-Different-Pass-9" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync("/auth/refresh", new { refreshToken = session.GetProperty("refreshToken").GetString() })).StatusCode);
    }

    [Fact]
    public async Task Reset_password_enforces_the_policy_and_the_user_must_exist()
    {
        var (admin, _, _) = await _factory.SignedInAsync(ActorRole.Buyer, isAdmin: true);
        var target = _factory.SeedUser(ActorRole.Supplier);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/admin/users/{target.Id}/reset-password", new { newPassword = "short" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync($"/admin/users/{Guid.NewGuid()}/reset-password", new { newPassword = "Long-Enough-Pass-1" })).StatusCode);
    }

    [Fact]
    public async Task Roles_endpoint_describes_the_permission_matrix()
    {
        var (admin, _, _) = await _factory.SignedInAsync(ActorRole.Buyer, isAdmin: true);

        var body = await Json(await admin.GetAsync("/admin/roles"));

        var roles = body.GetProperty("roles").EnumerateArray().Select(r => r.GetProperty("role").GetString()).ToList();
        Assert.Equal(["Buyer", "Supplier", "SupplierProduction"], roles);
        Assert.DoesNotContain("System", roles);
        Assert.Contains("admin.users", body.GetProperty("allPermissions").EnumerateArray().Select(p => p.GetString()));
    }
}

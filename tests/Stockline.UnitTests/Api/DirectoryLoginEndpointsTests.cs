using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Stockline.Application.Identity;
using Stockline.Domain.Identity;
using Stockline.Domain.Orders;
using Stockline.Infrastructure.Persistence;

namespace Stockline.UnitTests.Api;

public class DirectoryLoginEndpointsTests : IClassFixture<StocklineApiFactory>
{
    private const string Password = "Directory-Pass-1!";
    private readonly StocklineApiFactory _factory;

    public DirectoryLoginEndpointsTests(StocklineApiFactory factory) => _factory = factory;

    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..14];

    private Task<HttpResponseMessage> SignIn(string username, string password) =>
        _factory.ClientAs(null).PostAsJsonAsync("/auth/login", new { email = username, password, provider = "directory" });

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>())!;

    private T WithDb<T>(Func<StocklineDbContext, T> read)
    {
        using var scope = _factory.Services.CreateScope();
        return read(scope.ServiceProvider.GetRequiredService<StocklineDbContext>());
    }

    [Fact]
    public async Task The_providers_endpoint_tells_the_client_whether_to_offer_directory_sign_in()
    {
        var response = await _factory.ClientAs(null).GetAsync("/auth/providers");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await Json(response)).GetProperty("directory").GetBoolean());
    }

    [Fact]
    public async Task A_directory_user_in_a_role_group_signs_in_and_is_created_locally_with_that_role()
    {
        var account = Unique("ana");
        _factory.Directory.Add(account, Password, $"{account}@stockline.test", "Ana Directory", "sl-buyers");

        var response = await SignIn(account, Password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var session = await Json(response);
        var user = session.GetProperty("user");
        Assert.Equal("Buyer", user.GetProperty("role").GetString());
        Assert.Equal("Directory", user.GetProperty("provider").GetString());
        Assert.Equal("Ana Directory", user.GetProperty("displayName").GetString());
        Assert.False(user.GetProperty("isAdmin").GetBoolean());
        Assert.False(string.IsNullOrEmpty(session.GetProperty("accessToken").GetString()));

        var stored = WithDb(db => db.Users.Single(u => u.ExternalId == account));
        Assert.Equal(User.UnusablePasswordHash, stored.PasswordHash);
    }

    [Fact]
    public async Task The_token_works_against_the_rest_of_the_api()
    {
        var account = Unique("bea");
        _factory.Directory.Add(account, Password, $"{account}@stockline.test", "Bea Supplier", "sl-suppliers");

        var session = await Json(await SignIn(account, Password));
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.GetProperty("accessToken").GetString());

        var me = await client.GetAsync("/auth/me");

        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal("Supplier", (await Json(me)).GetProperty("role").GetString());
    }

    [Fact]
    public async Task Administrator_groups_add_the_administrator_permission()
    {
        var account = Unique("root");
        _factory.Directory.Add(account, Password, $"{account}@stockline.test", "Root", "sl-buyers", "sl-admins");

        var user = (await Json(await SignIn(account, Password))).GetProperty("user");

        Assert.True(user.GetProperty("isAdmin").GetBoolean());
        Assert.Contains(user.GetProperty("permissions").EnumerateArray(), p => p.GetString() == "admin.users");
    }

    [Fact]
    public async Task Each_sign_in_refreshes_the_role_from_the_directory_without_duplicating_the_user()
    {
        var account = Unique("cris");
        _factory.Directory.Add(account, Password, $"{account}@stockline.test", "Cris", "sl-production");
        await SignIn(account, Password);

        _factory.Directory.SetGroups(account, "sl-suppliers", "sl-admins");
        var user = (await Json(await SignIn(account, Password))).GetProperty("user");

        Assert.Equal("Supplier", user.GetProperty("role").GetString());
        Assert.True(user.GetProperty("isAdmin").GetBoolean());
        Assert.Equal(1, WithDb(db => db.Users.Count(u => u.ExternalId == account)));
    }

    [Fact]
    public async Task The_same_person_signing_in_for_the_first_time_twice_at_once_gets_one_account_and_two_sessions()
    {
        var account = Unique("race");
        _factory.Directory.Add(account, Password, $"{account}@stockline.test", "Race Runner", "sl-buyers");

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => SignIn(account, Password)));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        Assert.Equal(1, WithDb(db => db.Users.Count(u => u.ExternalId == account)));
    }

    [Fact]
    public async Task A_wrong_password_and_an_unknown_account_get_the_same_answer()
    {
        var account = Unique("dan");
        _factory.Directory.Add(account, Password, $"{account}@stockline.test", "Dan", "sl-buyers");

        var wrong = await SignIn(account, "nope");
        var unknown = await SignIn("nobody", Password);

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal((await Json(wrong)).GetProperty("title").GetString(), (await Json(unknown)).GetProperty("title").GetString());
    }

    [Fact]
    public async Task Someone_the_directory_knows_but_no_group_admits_is_refused_and_not_created()
    {
        var account = Unique("eve");
        _factory.Directory.Add(account, Password, $"{account}@stockline.test", "Eve", "finance");

        var response = await SignIn(account, Password);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, WithDb(db => db.Users.Count(u => u.ExternalId == account)));
    }

    [Fact]
    public async Task An_account_an_administrator_disabled_here_cannot_sign_in_even_though_the_directory_allows_it()
    {
        var account = Unique("fay");
        _factory.Directory.Add(account, Password, $"{account}@stockline.test", "Fay", "sl-buyers");
        await SignIn(account, Password);
        WithDb(db =>
        {
            db.Users.Single(u => u.ExternalId == account).SetActive(false);
            return db.SaveChanges();
        });

        Assert.Equal(HttpStatusCode.Forbidden, (await SignIn(account, Password)).StatusCode);
    }

    [Fact]
    public async Task A_directory_email_that_belongs_to_a_local_account_is_refused()
    {
        var local = _factory.SeedUser(ActorRole.Buyer);
        var account = Unique("gus");
        _factory.Directory.Add(account, Password, local.Email, "Gus", "sl-buyers");

        Assert.Equal(HttpStatusCode.Conflict, (await SignIn(account, Password)).StatusCode);
    }

    [Fact]
    public async Task An_unreachable_directory_is_reported_as_unavailable_not_as_wrong_credentials()
    {
        var account = Unique("hal");
        _factory.Directory.Add(account, Password, $"{account}@stockline.test", "Hal", "sl-buyers");
        _factory.Directory.Unreachable = true;
        try
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await SignIn(account, Password)).StatusCode);
        }
        finally
        {
            _factory.Directory.Unreachable = false;
        }
    }

    [Fact]
    public async Task A_directory_account_cannot_sign_in_locally_with_any_password()
    {
        var account = Unique("ivy");
        _factory.Directory.Add(account, Password, $"{account}@stockline.test", "Ivy", "sl-buyers");
        await SignIn(account, Password);
        var email = WithDb(db => db.Users.Single(u => u.ExternalId == account).Email);

        var local = await _factory.ClientAs(null).PostAsJsonAsync("/auth/login", new { email, password = Password });
        var hash = await _factory.ClientAs(null).PostAsJsonAsync("/auth/login", new { email, password = User.UnusablePasswordHash });

        Assert.Equal(HttpStatusCode.Unauthorized, local.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, hash.StatusCode);
    }

    [Fact]
    public async Task Directory_sign_in_is_refused_when_it_is_not_enabled()
    {
        _factory.Directory.IsEnabled = false;
        try
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await SignIn("anyone", Password)).StatusCode);
            Assert.False((await Json(await _factory.ClientAs(null).GetAsync("/auth/providers"))).GetProperty("directory").GetBoolean());
        }
        finally
        {
            _factory.Directory.IsEnabled = true;
        }
    }

    [Fact]
    public async Task An_unknown_provider_is_a_bad_request()
    {
        var response = await _factory.ClientAs(null)
            .PostAsJsonAsync("/auth/login", new { email = "a@b.co", password = "x", provider = "kerberos" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Administrators_cannot_change_what_the_directory_owns_but_can_deactivate_the_account()
    {
        var account = Unique("jon");
        _factory.Directory.Add(account, Password, $"{account}@stockline.test", "Jon", "sl-buyers");
        await SignIn(account, Password);
        var id = WithDb(db => db.Users.Single(u => u.ExternalId == account).Id);
        var (admin, _, _) = await _factory.SignedInAsync(ActorRole.Buyer, isAdmin: true);

        var roleChange = await admin.PatchAsJsonAsync($"/admin/users/{id}",
            new { displayName = "Jon", role = "Supplier", isAdmin = false, isActive = true });
        var reset = await admin.PostAsJsonAsync($"/admin/users/{id}/reset-password", new { newPassword = "A-new-password-1!" });
        var deactivate = await admin.PatchAsJsonAsync($"/admin/users/{id}",
            new { displayName = "Jon", role = "Buyer", isAdmin = false, isActive = false });

        Assert.Equal(HttpStatusCode.Conflict, roleChange.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, reset.StatusCode);
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);
        Assert.False(WithDb(db => db.Users.Single(u => u.Id == id).IsActive));
    }
}

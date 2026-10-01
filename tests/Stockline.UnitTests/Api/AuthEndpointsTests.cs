using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Stockline.Domain.Orders;

namespace Stockline.UnitTests.Api;

public class AuthEndpointsTests : IClassFixture<StocklineApiFactory>
{
    private readonly StocklineApiFactory _factory;

    public AuthEndpointsTests(StocklineApiFactory factory) => _factory = factory;

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>())!;

    private Task<HttpResponseMessage> Login(string email, string password) =>
        _factory.ClientAs(null).PostAsJsonAsync("/auth/login", new { email, password });

    [Fact]
    public async Task Login_returns_a_session_with_the_user_and_permissions()
    {
        var user = _factory.SeedUser(ActorRole.Buyer);

        var response = await Login(user.Email.ToUpperInvariant(), StocklineApiFactory.UserPassword);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var session = await Json(response);
        Assert.False(string.IsNullOrEmpty(session.GetProperty("accessToken").GetString()));
        Assert.False(string.IsNullOrEmpty(session.GetProperty("refreshToken").GetString()));
        var summary = session.GetProperty("user");
        Assert.Equal(user.Email, summary.GetProperty("email").GetString());
        Assert.Equal("Buyer", summary.GetProperty("role").GetString());
        var permissions = summary.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.Contains("orders.import", permissions);
        Assert.DoesNotContain("admin.users", permissions);
        Assert.DoesNotContain("passwordHash", summary.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Login_rejects_a_wrong_password_and_an_unknown_email_with_the_same_answer()
    {
        var user = _factory.SeedUser(ActorRole.Buyer);

        var wrongPassword = await Login(user.Email, "not-the-password");
        var unknownEmail = await Login("nobody@stockline.test", StocklineApiFactory.UserPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownEmail.StatusCode);
        Assert.Equal((await Json(wrongPassword)).GetProperty("title").GetString(), (await Json(unknownEmail)).GetProperty("title").GetString());
    }

    [Fact]
    public async Task Login_rejects_a_disabled_account()
    {
        var user = _factory.SeedUser(ActorRole.Buyer, active: false);

        var response = await Login(user.Email, StocklineApiFactory.UserPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("", "x")]
    [InlineData("a@b.co", "")]
    public async Task Login_requires_both_fields(string email, string password)
    {
        var response = await Login(email, password);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_access_token_works_on_protected_endpoints_and_me_returns_the_user()
    {
        var (client, user, _) = await _factory.SignedInAsync(ActorRole.Supplier);

        var me = await client.GetAsync("/auth/me");

        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal(user.Email, (await Json(me)).GetProperty("email").GetString());
    }

    [Fact]
    public async Task Me_requires_authentication_and_a_real_user()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.ClientAs(null).GetAsync("/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.ClientAs("Buyer").GetAsync("/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Refresh_rotates_the_token_and_the_old_one_stops_working()
    {
        var (client, _, session) = await _factory.SignedInAsync(ActorRole.Buyer);
        var first = session.GetProperty("refreshToken").GetString();

        var refreshed = await client.PostAsJsonAsync("/auth/refresh", new { refreshToken = first });

        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var second = (await Json(refreshed)).GetProperty("refreshToken").GetString();
        Assert.NotEqual(first, second);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/auth/refresh", new { refreshToken = second })).StatusCode);
    }

    [Fact]
    public async Task Reusing_a_rotated_refresh_token_revokes_the_whole_family()
    {
        var (client, _, session) = await _factory.SignedInAsync(ActorRole.Buyer);
        var stolen = session.GetProperty("refreshToken").GetString();
        var rotated = await Json(await client.PostAsJsonAsync("/auth/refresh", new { refreshToken = stolen }));

        var replay = await client.PostAsJsonAsync("/auth/refresh", new { refreshToken = stolen });
        var legitimate = await client.PostAsJsonAsync("/auth/refresh", new { refreshToken = rotated.GetProperty("refreshToken").GetString() });

        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, legitimate.StatusCode);
    }

    [Fact]
    public async Task Refresh_rejects_unknown_and_missing_tokens()
    {
        var client = _factory.ClientAs(null);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/auth/refresh", new { refreshToken = "nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/auth/refresh", new { refreshToken = "" })).StatusCode);
    }

    [Fact]
    public async Task Logout_revokes_the_refresh_token()
    {
        var (client, _, session) = await _factory.SignedInAsync(ActorRole.Buyer);
        var token = session.GetProperty("refreshToken").GetString();

        var logout = await client.PostAsJsonAsync("/auth/logout", new { refreshToken = token });
        var refresh = await client.PostAsJsonAsync("/auth/refresh", new { refreshToken = token });

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task A_tampered_access_token_is_rejected()
    {
        var (client, _, session) = await _factory.SignedInAsync(ActorRole.Buyer);
        var token = session.GetProperty("accessToken").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token[..^3] + "abc");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/auth/me")).StatusCode);
    }
}

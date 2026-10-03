using Stockline.Domain;
using Stockline.Domain.Identity;
using Stockline.Domain.Orders;
using Stockline.Infrastructure.Identity;

namespace Stockline.UnitTests.Identity;

public class IdentityDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(ActorRole.Buyer, false, new[] { "orders.view", "orders.import", "orders.act", "erp.submit", "dashboard.view", "careers.use" })]
    [InlineData(ActorRole.Supplier, false, new[] { "orders.view", "orders.act", "dashboard.view", "careers.use" })]
    [InlineData(ActorRole.SupplierProduction, false, new[] { "orders.view", "orders.act", "dashboard.view", "careers.use" })]
    [InlineData(ActorRole.Supplier, true, new[] { "orders.view", "orders.act", "dashboard.view", "careers.use", "admin.users" })]
    public void Permissions_follow_the_role_and_admin_flag(ActorRole role, bool admin, string[] expected) =>
        Assert.Equal(expected, Permissions.For(role, admin));

    [Fact]
    public void The_system_role_has_no_permissions_and_is_not_assignable()
    {
        Assert.Empty(Permissions.For(ActorRole.System, false));
        Assert.DoesNotContain(ActorRole.System, Permissions.AssignableRoles);
    }

    [Fact]
    public void Create_normalizes_the_email_and_trims_the_name()
    {
        var user = User.Create(Guid.NewGuid(), "  Ana@Stockline.TEST ", "  Ana  ", "hash", ActorRole.Buyer, false, Now);

        Assert.Equal("ana@stockline.test", user.Email);
        Assert.Equal("Ana", user.DisplayName);
        Assert.True(user.IsActive);
    }

    [Theory]
    [InlineData("")]
    [InlineData("plain")]
    [InlineData("@x.com")]
    [InlineData("a@b")]
    [InlineData("a b@c.com")]
    public void Create_rejects_implausible_emails(string email) =>
        Assert.Throws<DomainException>(() => User.Create(Guid.NewGuid(), email, "Ana", "hash", ActorRole.Buyer, false, Now));

    [Fact]
    public void Create_and_update_reject_blank_names_and_the_system_role()
    {
        Assert.Throws<DomainException>(() => User.Create(Guid.NewGuid(), "a@b.co", " ", "hash", ActorRole.Buyer, false, Now));
        Assert.Throws<DomainException>(() => User.Create(Guid.NewGuid(), "a@b.co", "Ana", "hash", ActorRole.System, false, Now));
        var user = User.Create(Guid.NewGuid(), "a@b.co", "Ana", "hash", ActorRole.Buyer, false, Now);
        Assert.Throws<DomainException>(() => user.Update("Ana", ActorRole.System, false, true));
    }

    [Fact]
    public void A_refresh_token_is_active_until_it_expires_or_is_revoked()
    {
        var token = new RefreshToken(Guid.NewGuid(), Guid.NewGuid(), "hash", Now, Now.AddDays(1));

        Assert.True(token.IsActive(Now));
        Assert.False(token.IsActive(Now.AddDays(1)));
        token.Revoke(Now.AddMinutes(1));
        token.Revoke(Now.AddMinutes(2));
        Assert.False(token.IsActive(Now));
        Assert.Equal(Now.AddMinutes(1), token.RevokedAt);
    }

    [Fact]
    public void The_hasher_verifies_the_right_password_only_and_salts_each_hash()
    {
        var hasher = new Pbkdf2PasswordHasher(iterations: 1000);

        var first = hasher.Hash("correct horse");
        var second = hasher.Hash("correct horse");

        Assert.NotEqual(first, second);
        Assert.True(hasher.Verify("correct horse", first));
        Assert.False(hasher.Verify("wrong horse", first));
        Assert.True(hasher.Verify("anything", hasher.DummyHash) is false);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("v1.abc.AAAA.AAAA")]
    [InlineData("v1.1000.not-base64!.AAAA")]
    [InlineData("v2.1000.AAAA.AAAA")]
    public void The_hasher_treats_malformed_hashes_as_a_mismatch(string stored) =>
        Assert.False(new Pbkdf2PasswordHasher(iterations: 1000).Verify("x", stored));
}

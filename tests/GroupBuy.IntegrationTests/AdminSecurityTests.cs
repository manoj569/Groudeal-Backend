using System.Net;
using System.Net.Http.Json;
using GroupBuy.Application;
using GroupBuy.Domain;
using GroupBuy.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GroupBuy.IntegrationTests;
public partial class BackendTests
{
    private static void AssertSecretAbsent(IEnumerable<string?> values, string secret)
    {
        if (values.Any(value => value?.Contains(secret, StringComparison.Ordinal) == true)) Assert.Fail("A synthetic security secret appeared in persisted output.");
    }
    private static async Task<(UserResponse User, TokenResponse Challenge, string Key)> StartEnrollment(ApiFactory f, HttpClient client)
    {
        var user = await Register(client, "mfa-admin"); await Role(f, user.Id, "Admin");
        var token = await Read<TokenResponse>(await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(user.Email!, "Strong!Password123")));
        Assert.Null(token.AccessToken); Assert.True(token.EnrollmentRequired);
        var enrollment = await Read<MfaEnrollmentResponse>(await client.PostAsJsonAsync("/api/v1/auth/mfa/enrollment", new MfaChallengeRequest(token.MfaChallenge!)));
        return (user, token, enrollment.SharedKey);
    }
    private static async Task<TokenResponse> PasswordChallenge(HttpClient client, UserResponse user) => await Read<TokenResponse>(await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(user.Email!, "Strong!Password123")));

    [Fact]
    [Trait("Sprint5", "Security")]
    public async Task Admin_provisioning_is_explicit_audited_idempotent_and_revokes_stale_tokens()
    {
        using var disabled = new ApiFactory(fixture.Connection); using var ordinary = disabled.CreateClient(); var user = await Register(ordinary, "operator-fixture"); await Login(ordinary, user);
        await using (var scope = disabled.Services.CreateAsyncScope())
        {
            var error = await Assert.ThrowsAsync<BusinessException>(() => scope.ServiceProvider.GetRequiredService<AdminProvisioner>().Execute("provision", user.Id, "qa-operator", "TEST-001", default)); Assert.Equal(403, error.StatusCode);
        }
        using var f = new ApiFactory(fixture.Connection, overrides: new() { ["AdminProvisioning:Enabled"] = "true" }); using var client = f.CreateClient();
        for (var i = 0; i < 2; i++) { await using var scope = f.Services.CreateAsyncScope(); await scope.ServiceProvider.GetRequiredService<AdminProvisioner>().Execute("provision", user.Id, "qa-operator", "TEST-001", default); }
        Assert.Equal(HttpStatusCode.Unauthorized, (await ordinary.GetAsync("/api/v1/auth/me")).StatusCode);
        var token = await PasswordChallenge(client, user); Assert.Null(token.AccessToken); Assert.True(token.EnrollmentRequired);
        var full = await CompleteMfa(client, user, token); client.DefaultRequestHeaders.Authorization = new("Bearer", full.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/admin/dashboard/summary")).StatusCode);
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>(); Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.EntityId == user.Id && a.Action == "AdminProvisioned"));
            await db.UsersProfile.Where(u => u.Id == user.Id).ExecuteUpdateAsync(x => x.SetProperty(u => u.Status, AccountStatus.Suspended));
            await scope.ServiceProvider.GetRequiredService<AdminProvisioner>().Execute("revoke", user.Id, "qa-operator", "TEST-002", default);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/admin/dashboard/summary")).StatusCode);
    }
    [Fact]
    [Trait("Sprint5", "Security")]
    public async Task Admin_enrollment_encrypts_secrets_and_issues_full_token_only_after_TOTP()
    {
        using var f = new ApiFactory(fixture.Connection); using var client = f.CreateClient(); var x = await StartEnrollment(f, client);
        client.DefaultRequestHeaders.Authorization = new("Bearer", x.Challenge.MfaChallenge);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/admin/audit-logs")).StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        var request = new MfaVerifyRequest(x.Challenge.MfaChallenge!, await UnusedTotp(f, x.User.Id, x.Key));
        var full = await Read<MfaTokenResponse>(await client.PostAsJsonAsync("/api/v1/auth/mfa/verify", request)); Assert.Equal(10, full.RecoveryCodes.Count);
        client.DefaultRequestHeaders.Authorization = new("Bearer", full.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/admin/audit-logs")).StatusCode);
        await using var scope = f.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
        var values = await db.UserTokens.Where(t => t.UserId == x.User.Id).Select(t => t.Value).ToListAsync();
        AssertSecretAbsent(values, x.Key); AssertSecretAbsent(values, full.RecoveryCodes[0]);
        var audit = string.Join(" ", await db.AuditLogs.Where(a => a.EntityId == x.User.Id).Select(a => a.SafeMetadataJson).ToListAsync());
        AssertSecretAbsent([audit], x.Key); AssertSecretAbsent([audit], full.RecoveryCodes[0]);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/mfa/verify", request)).StatusCode);
        var next = await PasswordChallenge(client, x.User); Assert.False(next.EnrollmentRequired);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/mfa/enrollment", new MfaChallengeRequest(next.MfaChallenge!))).StatusCode);
        // Accepted enrollment code cannot authenticate a fresh challenge in the same TOTP window.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/mfa/verify", request with { Challenge = next.MfaChallenge! })).StatusCode);
    }
    [Fact]
    [Trait("Sprint5", "Security")]
    public async Task Mfa_challenges_expire_are_context_bound_and_cannot_be_used_as_bearer_tokens()
    {
        using var f = new ApiFactory(fixture.Connection); using var client = f.CreateClient(); var x = await StartEnrollment(f, client);
        var next = await PasswordChallenge(client, x.User);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/mfa/verify", new MfaVerifyRequest(x.Challenge.MfaChallenge!, await UnusedTotp(f, x.User.Id, x.Key)))).StatusCode);
        await using (var scope = f.Services.CreateAsyncScope()) await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().MfaChallenges.Where(c => c.TokenHash == SecurityTokens.Hash(next.MfaChallenge!)).ExecuteUpdateAsync(s => s.SetProperty(c => c.ExpiresAtUtc, DateTimeOffset.UtcNow.AddMinutes(-1)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/mfa/enrollment", new MfaChallengeRequest(next.MfaChallenge!))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/mfa/verify", new MfaVerifyRequest(new string('x', 43), "123456"))).StatusCode);
    }
    [Fact]
    [Trait("Sprint5", "Security")]
    public async Task Failed_MFA_persists_account_lockout_across_password_challenges()
    {
        using var f = new ApiFactory(fixture.Connection); using var client = f.CreateClient(); var x = await StartEnrollment(f, client);
        for (var i = 0; i < 5; i++)
        {
            var token = i == 0 ? x.Challenge : await PasswordChallenge(client, x.User);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/mfa/verify", new MfaVerifyRequest(token.MfaChallenge!, "invalid"))).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(x.User.Email!, "Strong!Password123"))).StatusCode);
        await using var scope = f.Services.CreateAsyncScope(); var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityAccount>>(); Assert.True(await users.IsLockedOutAsync((await users.FindByIdAsync(x.User.Id.ToString()))!));
    }
    [Fact]
    [Trait("Sprint5", "Security")]
    public async Task Recovery_codes_are_encrypted_single_use_and_revoke_existing_sessions()
    {
        using var f = new ApiFactory(fixture.Connection); using var client = f.CreateClient(); var x = await StartEnrollment(f, client);
        var enrolled = await Read<MfaTokenResponse>(await client.PostAsJsonAsync("/api/v1/auth/mfa/verify", new MfaVerifyRequest(x.Challenge.MfaChallenge!, await UnusedTotp(f, x.User.Id, x.Key))));
        using var old = f.CreateClient(); old.DefaultRequestHeaders.Authorization = new("Bearer", enrolled.AccessToken);
        var next = await PasswordChallenge(client, x.User); var request = new MfaVerifyRequest(next.MfaChallenge!, enrolled.RecoveryCodes[0], true);
        var raced = await Task.WhenAll(client.PostAsJsonAsync("/api/v1/auth/mfa/verify", request), client.PostAsJsonAsync("/api/v1/auth/mfa/verify", request));
        Assert.Single(raced, r => r.StatusCode == HttpStatusCode.OK); Assert.Single(raced, r => r.StatusCode == HttpStatusCode.Unauthorized);
        Assert.Equal(HttpStatusCode.Unauthorized, (await old.GetAsync("/api/v1/admin/audit-logs")).StatusCode);
        next = await PasswordChallenge(client, x.User);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/mfa/verify", request with { Challenge = next.MfaChallenge! })).StatusCode);
    }
    [Fact]
    [Trait("Sprint5", "Security")]
    public async Task MFA_reset_has_no_HTTP_bypass_and_requires_audited_operator_permission()
    {
        using var f = new ApiFactory(fixture.Connection, overrides: new() { ["AdminProvisioning:Enabled"] = "true" }); using var client = f.CreateClient(); var x = await StartEnrollment(f, client);
        var enrolled = await Read<MfaTokenResponse>(await client.PostAsJsonAsync("/api/v1/auth/mfa/verify", new MfaVerifyRequest(x.Challenge.MfaChallenge!, await UnusedTotp(f, x.User.Id, x.Key))));
        client.DefaultRequestHeaders.Authorization = new("Bearer", enrolled.AccessToken);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/v1/admin/mfa/reset", null)).StatusCode);
        await using (var scope = f.Services.CreateAsyncScope()) {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityAccount>>(); var account = (await users.FindByIdAsync(x.User.Id.ToString()))!;
            SecurityTokens.Check(await users.SetLockoutEndDateAsync(account, DateTimeOffset.UtcNow.AddMinutes(15)));
            await scope.ServiceProvider.GetRequiredService<AdminProvisioner>().Execute("reset-mfa", x.User.Id, "qa-reviewer", "TEST-RESET", default);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/admin/audit-logs")).StatusCode);
        await using (var scope = f.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>(); var version = await db.Users.Where(u => u.Id == x.User.Id).Select(u => u.TokenVersion).SingleAsync();
            await scope.ServiceProvider.GetRequiredService<AdminProvisioner>().Execute("reset-mfa", x.User.Id, "qa-reviewer", "TEST-RESET", default);
            Assert.Equal(version, await db.Users.Where(u => u.Id == x.User.Id).Select(u => u.TokenVersion).SingleAsync());
            Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.EntityId == x.User.Id && a.Action == "AdminMfaReset"));
        }
        var next = await PasswordChallenge(client, x.User); Assert.True(next.EnrollmentRequired);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/mfa/verify", new MfaVerifyRequest(next.MfaChallenge!, enrolled.RecoveryCodes[0], true))).StatusCode);
        var enrollment = await Read<MfaEnrollmentResponse>(await client.PostAsJsonAsync("/api/v1/auth/mfa/enrollment", new MfaChallengeRequest(next.MfaChallenge!))); Assert.False(x.Key == enrollment.SharedKey);
    }
    [Fact]
    [Trait("Sprint5", "Security")]
    public async Task MFA_account_version_and_role_revocation_invalidate_password_challenges()
    {
        using var f = new ApiFactory(fixture.Connection); using var client = f.CreateClient(); var x = await StartEnrollment(f, client);
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityAccount>>(); var account = (await users.FindByIdAsync(x.User.Id.ToString()))!; SecurityTokens.Check(await users.RemoveFromRoleAsync(account, "Admin"));
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/mfa/enrollment", new MfaChallengeRequest(x.Challenge.MfaChallenge!))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/mfa/verify", new MfaVerifyRequest(x.Challenge.MfaChallenge!, await UnusedTotp(f, x.User.Id, x.Key)))).StatusCode);
    }
}


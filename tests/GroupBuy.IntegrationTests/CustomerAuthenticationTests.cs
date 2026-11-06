using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GroupBuy.Application;
using GroupBuy.Domain;
using GroupBuy.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GroupBuy.IntegrationTests;

// Delivery capture exists only in the test assembly; neither API nor application logs codes.
public sealed class CustomerAuthFactory(string connection) : Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>
{
    public readonly CaptureDelivery Delivery = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string,string?> {
            ["ConnectionStrings:Database"] = connection, ["Jwt:SigningKey"] = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
            ["Jwt:Issuer"] = "customer-tests", ["Jwt:Audience"] = "customer-tests", ["Authentication:EmailOtp:Enabled"] = "true", ["Authentication:MobileOtp:Enabled"] = "true",
            ["Authentication:Google:Enabled"] = "true", ["Authentication:Google:ClientId"] = "test-client",
            ["Authentication:Consent:TermsVersion"] = "test-terms-v1", ["Authentication:Consent:PrivacyVersion"] = "test-privacy-v1" }));
        builder.ConfigureServices(s => {
            s.RemoveAll<IOtpDeliveryProvider>(); s.AddSingleton<IOtpDeliveryProvider>(Delivery);
            s.RemoveAll<IGoogleCredentialValidator>(); s.AddSingleton<IGoogleCredentialValidator, FakeGoogle>();
            foreach (var worker in s.Where(d => d.ImplementationType == typeof(GroupBuy.API.ExpiryWorker)).ToArray()) s.Remove(worker);
        });
    }
    public sealed class CaptureDelivery : IOtpDeliveryProvider
    {
        public ConcurrentDictionary<string,string> Codes { get; } = new();
        public bool Supports(string channel) => true;
        public Task Deliver(string channel, string destination, string code, CancellationToken ct) { Codes[channel + destination] = code; return Task.CompletedTask; }
    }
    private sealed class FakeGoogle : IGoogleCredentialValidator
    {
        public Task<GoogleIdentity> Validate(string credential, CancellationToken ct) =>
            Task.FromResult(new GoogleIdentity(credential, credential + "@gmail.com", "Google Customer"));
    }
}

public sealed class CustomerAuthenticationTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private static string Destination(string channel) => channel == "email" ? Guid.NewGuid().ToString("N") + "@example.invalid" : "+91" + System.Security.Cryptography.RandomNumberGenerator.GetInt32(100000000, 999999999).ToString() + "0";
    private static async Task<T> Read<T>(HttpResponseMessage response)
    { Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}"); return (await response.Content.ReadFromJsonAsync<T>())!; }
    private static async Task<(ChallengeResponse Challenge, string Code)> Request(CustomerAuthFactory f, HttpClient client, string channel, string destination, bool link = false)
    {
        var response = await client.PostAsJsonAsync(link ? $"/api/v1/me/link/{channel}/request" : $"/api/v1/auth/{channel}/request-otp", new OtpRequest(destination));
        var challenge = await Read<ChallengeResponse>(response);
        var code = f.Delivery.Codes[channel + CustomerIdentityNormalization.Normalize(channel, destination)];
        Assert.DoesNotContain(code, await response.Content.ReadAsStringAsync()); return (challenge, code);
    }
    private static async Task<CustomerLogin> Login(CustomerAuthFactory f, HttpClient client, string channel)
    {
        var c = await Request(f, client, channel, Destination(channel));
        var login = await Read<CustomerLogin>(await client.PostAsJsonAsync($"/api/v1/auth/{channel}/verify-otp", new OtpVerification(c.Challenge.ChallengeId, c.Code)));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Session.AccessToken); return login;
    }
    [Theory]
    [InlineData("email")]
    [InlineData("mobile")]
    public async Task Otp_lifecycle_profile_consent_authorization_logout(string channel)
    {
        using var f = new CustomerAuthFactory(fixture.Connection); using var client = f.CreateClient();
        var login = await Login(f, client, channel); Assert.False(login.User.ProfileCompleted); Assert.True(login.User.ConsentRequired);
        var p = await Read<CustomerProfile>(await client.PatchAsJsonAsync("/api/v1/me/profile", new ProfileCompletion("Customer"))); Assert.True(p.ProfileCompleted);
        p = await Read<CustomerProfile>(await client.PostAsJsonAsync("/api/v1/me/consent", new ConsentAcceptance("test-terms-v1", "test-privacy-v1"))); Assert.False(p.ConsentRequired);
        await Read<CustomerProfile>(await client.PostAsJsonAsync("/api/v1/me/consent", new ConsentAcceptance("test-terms-v1", "test-privacy-v1")));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/admin/sellers/pending")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/seller/products")).StatusCode);
        await using (var scope = f.Services.CreateAsyncScope())
        { var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>(); Assert.Equal(2, await db.UserConsents.CountAsync(x => x.UserId == login.User.Id)); }
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/v1/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/me")).StatusCode);
    }
    [Theory]
    [InlineData("email")]
    [InlineData("mobile")]
    public async Task Otp_wrong_expired_replay_attempt_limit_and_throttle(string channel)
    {
        using var f = new CustomerAuthFactory(fixture.Connection); using var client = f.CreateClient();
        var destination = Destination(channel); var c = await Request(f, client, channel, destination);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync($"/api/v1/auth/{channel}/request-otp", new OtpRequest(destination))).StatusCode);
        var wrong = c.Code == "000000" ? "000001" : "000000";
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync($"/api/v1/auth/{channel}/verify-otp", new OtpVerification(c.Challenge.ChallengeId, wrong))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync($"/api/v1/auth/{channel}/verify-otp", new OtpVerification(c.Challenge.ChallengeId, c.Code))).StatusCode);
        c = await Request(f, client, channel, Destination(channel));
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
            var stored = await db.CustomerChallenges.SingleAsync(x => x.Id == c.Challenge.ChallengeId); Assert.NotEqual(c.Code, stored.CodeHash);
            stored.ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1); await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync($"/api/v1/auth/{channel}/verify-otp", new OtpVerification(c.Challenge.ChallengeId, c.Code))).StatusCode);
        c = await Request(f, client, channel, Destination(channel));
        await Read<CustomerLogin>(await client.PostAsJsonAsync($"/api/v1/auth/{channel}/verify-otp", new OtpVerification(c.Challenge.ChallengeId, c.Code)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync($"/api/v1/auth/{channel}/verify-otp", new OtpVerification(c.Challenge.ChallengeId, c.Code))).StatusCode);
    }
    [Theory]
    [InlineData("email")]
    [InlineData("mobile")]
    public async Task Linking_requires_actor_and_purpose_and_rejects_takeover(string channel)
    {
        using var f = new CustomerAuthFactory(fixture.Connection); using var a = f.CreateClient(); using var b = f.CreateClient(); using var anon = f.CreateClient();
        await Login(f, a, channel == "email" ? "mobile" : "email"); await Login(f, b, channel == "email" ? "mobile" : "email");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync($"/api/v1/me/link/{channel}/request", new OtpRequest(Destination(channel)))).StatusCode);
        var destination = Destination(channel); var c = await Request(f, a, channel, destination, true);
        Assert.Equal(HttpStatusCode.Unauthorized, (await b.PostAsJsonAsync($"/api/v1/me/link/{channel}/verify", new OtpVerification(c.Challenge.ChallengeId, c.Code))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync($"/api/v1/auth/{channel}/verify-otp", new OtpVerification(c.Challenge.ChallengeId, c.Code))).StatusCode);
        await Read<CustomerProfile>(await a.PostAsJsonAsync($"/api/v1/me/link/{channel}/verify", new OtpVerification(c.Challenge.ChallengeId, c.Code)));
        // Age only the resend metadata to obtain a fresh independent proof for the other account.
        await using (var scope = f.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().CustomerChallenges.Where(x => x.Id == c.Challenge.ChallengeId).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAtUtc, DateTimeOffset.UtcNow.AddMinutes(-2)));
        c = await Request(f, b, channel, destination, true);
        Assert.Equal(HttpStatusCode.Conflict, (await b.PostAsJsonAsync($"/api/v1/me/link/{channel}/verify", new OtpVerification(c.Challenge.ChallengeId, c.Code))).StatusCode);
    }
    [Fact]
    public async Task Google_subject_existing_login_new_account_conflict_and_duplicate_race()
    {
        using var f = new CustomerAuthFactory(fixture.Connection); using var a = f.CreateClient(); using var b = f.CreateClient();
        var credential = Guid.NewGuid().ToString("N");
        var results = await Task.WhenAll(a.PostAsJsonAsync("/api/v1/auth/google", new GoogleCredential(credential)), b.PostAsJsonAsync("/api/v1/auth/google", new GoogleCredential(credential)));
        var first = await Read<CustomerLogin>(results[0]); var second = await Read<CustomerLogin>(results[1]); Assert.Equal(first.User.Id, second.User.Id); Assert.True(first.User.ProfileCompleted);
        await Login(f, b, "mobile");
        Assert.Equal(HttpStatusCode.Conflict, (await b.PostAsJsonAsync("/api/v1/me/link/google", new GoogleCredential(credential))).StatusCode);
        var ownGoogle = Guid.NewGuid().ToString("N"); await Read<CustomerProfile>(await b.PostAsJsonAsync("/api/v1/me/link/google", new GoogleCredential(ownGoogle)));
        var ownLogin = await Read<CustomerLogin>(await a.PostAsJsonAsync("/api/v1/auth/google", new GoogleCredential(ownGoogle))); Assert.NotEqual(first.User.Id, ownLogin.User.Id);
    }
    [Theory]
    [InlineData("email")]
    [InlineData("mobile")]
    public async Task Concurrent_consumption_issues_one_session_and_same_identity_reauthenticates(string channel)
    {
        using var f = new CustomerAuthFactory(fixture.Connection); using var a = f.CreateClient(); using var b = f.CreateClient();
        var destination = Destination(channel); var c = await Request(f, a, channel, destination);
        var responses = await Task.WhenAll(a.PostAsJsonAsync($"/api/v1/auth/{channel}/verify-otp", new OtpVerification(c.Challenge.ChallengeId, c.Code)),
            b.PostAsJsonAsync($"/api/v1/auth/{channel}/verify-otp", new OtpVerification(c.Challenge.ChallengeId, c.Code)));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK); Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Unauthorized);
        var first = await Read<CustomerLogin>(responses.Single(x => x.IsSuccessStatusCode));
        await using (var scope = f.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().CustomerChallenges.Where(x => x.Id == c.Challenge.ChallengeId).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAtUtc, DateTimeOffset.UtcNow.AddMinutes(-2)));
        c = await Request(f, a, channel, channel == "email" ? destination.ToUpperInvariant() : destination.Insert(3, " "));
        var second = await Read<CustomerLogin>(await a.PostAsJsonAsync($"/api/v1/auth/{channel}/verify-otp", new OtpVerification(c.Challenge.ChallengeId, c.Code)));
        Assert.Equal(first.User.Id, second.User.Id);
    }
    [Fact]
    public async Task Google_email_collision_does_not_merge_and_legacy_unconfirmed_email_cannot_be_taken()
    {
        using var f = new CustomerAuthFactory(fixture.Connection); using var client = f.CreateClient();
        var subject = Guid.NewGuid().ToString("N");
        var user = await Read<UserResponse>(await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(subject + "@gmail.com", "Strong!Password123", "Legacy")));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/v1/auth/google", new GoogleCredential(subject))).StatusCode);
        var c = await Request(f, client, "email", subject + "@gmail.com");
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/v1/auth/email/verify-otp", new OtpVerification(c.Challenge.ChallengeId, c.Code))).StatusCode);
        await using var scope = f.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
        Assert.Equal(1, await db.Users.CountAsync(x => x.NormalizedEmail == (subject + "@gmail.com").ToUpperInvariant()));
        Assert.False(await db.UserLogins.AnyAsync(x => x.UserId == user.Id));
    }
    [Fact]
    public async Task Link_challenge_is_revoked_by_logout_and_rejects_channel_confusion()
    {
        using var f = new CustomerAuthFactory(fixture.Connection); using var client = f.CreateClient();
        await Login(f, client, "mobile"); var c = await Request(f, client, "email", Destination("email"), true);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/me/link/mobile/verify", new OtpVerification(c.Challenge.ChallengeId, c.Code))).StatusCode);
        await client.PostAsync("/api/v1/auth/logout", null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/me/link/email/verify", new OtpVerification(c.Challenge.ChallengeId, c.Code))).StatusCode);
    }
    [Fact]
    public async Task Strict_bodies_reject_role_injection_and_external_return_destinations()
    {
        using var f = new CustomerAuthFactory(fixture.Connection); using var client = f.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/auth/google", new { credential = "test", returnUrl = "https://evil.invalid", roles = new[] { "Admin" } })).StatusCode);
    }
    [Theory]
    [InlineData("Authentication:EmailOtp:Enabled")]
    [InlineData("Authentication:MobileOtp:Enabled")]
    [InlineData("Authentication:Google:Enabled")]
    public void Unconfigured_provider_activation_fails_closed(string key)
    {
        using var f = new ApiFactory(fixture.Connection, overrides: new() { [key] = "true" });
        Assert.Throws<InvalidOperationException>(() => f.CreateClient());
    }
    [Fact]
    public async Task Concurrent_google_linking_cannot_steal_subject_and_passwordless_cannot_bypass_privileged_auth()
    {
        using var f = new CustomerAuthFactory(fixture.Connection); using var a = f.CreateClient(); using var b = f.CreateClient();
        var first = await Login(f, a, "mobile"); var second = await Login(f, b, "mobile");
        var subject = Guid.NewGuid().ToString("N");
        var responses = await Task.WhenAll(a.PostAsJsonAsync("/api/v1/me/link/google", new GoogleCredential(subject)), b.PostAsJsonAsync("/api/v1/me/link/google", new GoogleCredential(subject)));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK); Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict);
        var winner = await Read<CustomerProfile>(responses.Single(x => x.IsSuccessStatusCode));
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<IdentityAccount>>();
            var account = await manager.FindByIdAsync(winner.Id.ToString());
            Assert.True((await manager.AddToRoleAsync(account!, "Admin")).Succeeded);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await a.PostAsJsonAsync("/api/v1/auth/google", new GoogleCredential(subject))).StatusCode);
        await using var check = f.Services.CreateAsyncScope(); var db = check.ServiceProvider.GetRequiredService<MarketplaceDb>();
        Assert.Equal(1, await db.UserLogins.CountAsync(x => x.LoginProvider == "Google" && x.ProviderKey == subject));
        Assert.NotEqual(first.User.Id, second.User.Id);
    }
    [Fact]
    public async Task Canonically_equivalent_unicode_email_cannot_bypass_destination_cooldown()
    {
        using var f = new CustomerAuthFactory(fixture.Connection); using var client = f.CreateClient();
        var prefix = Guid.NewGuid().ToString("N");
        await Request(f, client, "email", prefix + "te\u0301st@example.invalid");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync("/api/v1/auth/email/request-otp", new OtpRequest(prefix + "tést@example.invalid"))).StatusCode);
    }
}

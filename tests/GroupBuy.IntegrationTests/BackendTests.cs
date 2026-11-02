using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using GroupBuy.Application;
using GroupBuy.Domain;
using GroupBuy.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace GroupBuy.IntegrationTests;
public sealed class DatabaseFixture : IAsyncLifetime
{
    private PostgreSqlContainer? container;
    private string? adminConnection;
    private readonly string name = "groupbuy_test_" + Guid.NewGuid().ToString("N");
    public string Connection { get; private set; } = "";
    public async Task InitializeAsync()
    {
        adminConnection = Environment.GetEnvironmentVariable("GROUPBUY_TEST_POSTGRES");
        if (adminConnection is null)
        {
            container = new PostgreSqlBuilder().WithImage("postgres:17-alpine").Build(); await container.StartAsync(); adminConnection = container.GetConnectionString();
        }
        await using var connection = new NpgsqlConnection(adminConnection); await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE {name}", connection); await command.ExecuteNonQueryAsync();
        Connection = new NpgsqlConnectionStringBuilder(adminConnection) { Database = name }.ConnectionString;
        await using var db = new MarketplaceDb(new DbContextOptionsBuilder<MarketplaceDb>().UseNpgsql(Connection).Options); await db.Database.MigrateAsync();
    }
    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        if (adminConnection is not null) { await using var c = new NpgsqlConnection(adminConnection); await c.OpenAsync(); await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)", c); await cmd.ExecuteNonQueryAsync(); }
        if (container is not null) await container.DisposeAsync();
    }
}
public sealed class ApiFactory(string connection, string environment = "Development", Dictionary<string, string?>? overrides = null) : WebApplicationFactory<Program>
{
    internal static readonly System.Runtime.CompilerServices.ConditionalWeakTable<HttpClient, ApiFactory> ClientFactories = new();
    public new HttpClient CreateClient() { var client = base.CreateClient(); ClientFactories.Add(client, this); return client; }
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Database"] = connection, ["Jwt:SigningKey"] = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48)), ["Jwt:Issuer"] = "groupbuy-tests", ["Jwt:Audience"] = "groupbuy-tests", ["Demo:Password"] = "Test!" + Guid.NewGuid().ToString("N") + "A1" }.Concat(overrides ?? new()).GroupBy(p => p.Key).Select(g => g.Last())));
    }
}
public partial class BackendTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static async Task<T> Read<T>(HttpResponseMessage response) { var body = await response.Content.ReadAsStringAsync(); Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {body}"); return JsonSerializer.Deserialize<T>(body, Json)!; }
    private static async Task<UserResponse> Register(HttpClient client, string prefix) => await Read<UserResponse>(await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest($"{prefix}-{Guid.NewGuid():N}@example.invalid", "Strong!Password123", prefix)));
    private static async Task Login(HttpClient client, UserResponse user)
    {
        var t = await Read<TokenResponse>(await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(user.Email!, "Strong!Password123"))); var access = t.AccessToken ?? (await CompleteMfa(client, user, t)).AccessToken; client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access);
    }
    private static async Task Role(ApiFactory factory, Guid userId, string role)
    {
        await using var scope = factory.Services.CreateAsyncScope(); var manager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityAccount>>(); var user = await manager.FindByIdAsync(userId.ToString()); Assert.True((await manager.AddToRoleAsync(user!, role)).Succeeded);
        if (role == "Admin") { user!.MfaEnrollmentAllowedUntilUtc = DateTimeOffset.UtcNow.AddMinutes(15); Assert.True((await manager.UpdateAsync(user)).Succeeded); }
    }
    private static SellerRequest SellerRequest(string name) => new(name, SellerType.Grocery, "Integration seller", "shop@example.invalid", "+919876543210", "12345678901234", new("Pickup Road", null, "Area", "City", "State", "110001", null, null));
    private async Task<(ApiFactory Factory, HttpClient Admin, HttpClient Seller, HttpClient Customer, Guid Campaign, Guid CustomerId)> Setup(decimal target = 10)
    {
        var f = new ApiFactory(fixture.Connection); var admin = f.CreateClient(); var seller = f.CreateClient(); var customer = f.CreateClient();
        var a = await Register(admin, "admin"); await Role(f, a.Id, "Admin"); await Login(admin, a);
        var s = await Register(seller, "seller"); await Login(seller, s); var c = await Register(customer, "customer"); await Login(customer, c);
        var profile = await Read<SellerResponse>(await seller.PostAsJsonAsync("/api/v1/seller/profile", SellerRequest("Test Shop")));
        var unapproved = await seller.PostAsJsonAsync("/api/v1/seller/group-buys", new CampaignRequest(Guid.NewGuid(), Guid.NewGuid(), "Test", "", 100, 80, target, 1, target, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1)));
        Assert.Equal(HttpStatusCode.Forbidden, unapproved.StatusCode);
        await Read<SellerReviewResponse>(await admin.PostAsJsonAsync($"/api/v1/admin/sellers/{profile.Id}/approve", new ReviewRequest(null))); await Login(seller, s);
        var product = await Read<ProductResponse>(await seller.PostAsJsonAsync("/api/v1/seller/products", new ProductRequest(CategorySeed.All[1].Id, "Wheat", "wheat", "Test", ProductType.Physical, Guid.NewGuid().ToString("N"), "1 kg", UnitOfMeasure.Kilogram, 1, "{}")));
        await Read<InventoryResponse>(await seller.PutAsJsonAsync($"/api/v1/seller/inventory/{product.Variants[0].Id}", new InventoryRequest(target, 0)));
        var campaign = await Read<CampaignResponse>(await seller.PostAsJsonAsync("/api/v1/seller/group-buys", new CampaignRequest(product.Variants[0].Id, profile.PickupLocations[0].Id, "Test wheat", "Test", 100, 80, target, 1, target, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1))));
        await Read<CampaignResponse>(await seller.PostAsync($"/api/v1/seller/group-buys/{campaign.Id}/submit", null));
        await Read<CampaignResponse>(await admin.PostAsJsonAsync($"/api/v1/admin/group-buys/{campaign.Id}/approve", new ReviewRequest(null)));
        return (f, admin, seller, customer, campaign.Id, c.Id);
    }
    [Fact]
    public async Task Jwt_rejects_expiry_wrong_issuer_audience_signature_and_algorithm()
    {
        using var f = new ApiFactory(fixture.Connection); using var client = f.CreateClient();
        var user = await Register(client, "jwt-validation");
        var config = f.Services.GetRequiredService<IConfiguration>();
        var claims = new[] { new System.Security.Claims.Claim("sub", user.Id.ToString()), new System.Security.Claims.Claim("ver", "0"), new System.Security.Claims.Claim("role", "Customer") };
        foreach (var fault in new[] { "expired", "issuer", "audience", "signature", "algorithm" })
        {
            var key = fault == "signature" ? new string('x', 64) : config["Jwt:SigningKey"]!;
            var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
                fault == "issuer" ? "untrusted" : config["Jwt:Issuer"], fault == "audience" ? "untrusted" : config["Jwt:Audience"], claims,
                DateTime.UtcNow.AddHours(-2), fault == "expired" ? DateTime.UtcNow.AddMinutes(-1) : DateTime.UtcNow.AddMinutes(5),
                new Microsoft.IdentityModel.Tokens.SigningCredentials(new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(key)),
                    fault == "algorithm" ? Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha384 : Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256));
            client.DefaultRequestHeaders.Authorization = new("Bearer", new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token));
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
        }
    }
    [Fact]
    public async Task Removed_roles_and_suspended_accounts_reject_existing_tokens()
    {
        using var f = new ApiFactory(fixture.Connection); using var client = f.CreateClient();
        var user = await Register(client, "revocation"); await Role(f, user.Id, "Admin"); await Login(client, user);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/admin/sellers/pending")).StatusCode);
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityAccount>>();
            var account = await manager.FindByIdAsync(user.Id.ToString());
            Assert.True((await manager.RemoveFromRoleAsync(account!, "Admin")).Succeeded);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/admin/sellers/pending")).StatusCode);
        await Login(client, user);
        await using (var scope = f.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().UsersProfile.Where(u => u.Id == user.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, AccountStatus.Suspended));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(user.Email!, "Strong!Password123"))).StatusCode);
    }
    [Fact]
    public async Task Seller_lock_refreshes_approval_loaded_before_concurrent_profile_change()
    {
        var x = await Setup(); using var f = x.Factory; using var admin = x.Admin; using var seller = x.Seller; using var customer = x.Customer;
        await using var scope = f.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
        var campaign = await db.Campaigns.AsNoTracking().SingleAsync(c => c.Id == x.Campaign);
        // Reproduce the tracking state created by OwnedSeller before waiting for its row lock.
        var staleSeller = await db.Sellers.SingleAsync(s => s.Id == campaign.SellerId);
        await Read<SellerResponse>(await seller.PutAsJsonAsync("/api/v1/seller/profile", SellerRequest("Review required")));
        Assert.Equal(VerificationStatus.Approved, staleSeller.VerificationStatus);
        var service = scope.ServiceProvider.GetRequiredService<IMarketplace>();
        var error = await Assert.ThrowsAsync<BusinessException>(() => service.SaveProduct(new(staleSeller.OwnerUserId, false, "approval-race"), null,
            new(CategorySeed.All[1].Id, "Blocked", "blocked", "", ProductType.Physical, Guid.NewGuid().ToString("N"), "1 kg", UnitOfMeasure.Kilogram, 1, "{}"), default));
        Assert.Equal(403, error.StatusCode);
        Assert.Equal(VerificationStatus.Pending, staleSeller.VerificationStatus);
        Assert.False(await db.Products.AnyAsync(p => p.SellerId == staleSeller.Id && p.Name == "Blocked"));
    }
    [Fact]
    public async Task Concurrent_cancellation_and_confirmation_preserve_capacity_and_reservation_state()
    {
        var x = await Setup(2); using var f = x.Factory; using var admin = x.Admin; using var seller = x.Seller; using var customer = x.Customer;
        var r = await Read<ReservationResponse>(await customer.PostAsJsonAsync($"/api/v1/group-buys/{x.Campaign}/reservations", new ReservationRequest(2, "confirmation-race")));
        var results = await Task.WhenAll(customer.PostAsync($"/api/v1/me/reservations/{r.Id}/cancel", null),
            seller.PostAsJsonAsync($"/api/v1/seller/group-buys/{x.Campaign}/transition", new TransitionRequest(CampaignStatus.AwaitingSellerConfirmation)));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
        await using var scope = f.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
        var c = await db.Campaigns.SingleAsync(c => c.Id == x.Campaign); var stored = await db.Reservations.SingleAsync(v => v.Id == r.Id);
        Assert.Equal(stored.Status == ReservationStatus.Active ? 2 : 0, c.ReservedQuantity);
        Assert.Equal(stored.Status == ReservationStatus.Active ? CampaignStatus.AwaitingSellerConfirmation : CampaignStatus.Open, c.Status);
    }
    [Fact]
    public async Task Failed_fulfillment_expires_commitments_once_and_is_terminal()
    {
        var x = await Setup(2); using var f = x.Factory; using var admin = x.Admin; using var seller = x.Seller; using var customer = x.Customer;
        await Read<ReservationResponse>(await customer.PostAsJsonAsync($"/api/v1/group-buys/{x.Campaign}/reservations", new ReservationRequest(2, "failed-fulfillment")));
        foreach (var status in new[] { CampaignStatus.AwaitingSellerConfirmation, CampaignStatus.Confirmed, CampaignStatus.FulfillmentInProgress, CampaignStatus.Failed })
            await Read<CampaignResponse>(await seller.PostAsJsonAsync($"/api/v1/seller/group-buys/{x.Campaign}/transition", new TransitionRequest(status)));
        Assert.Equal(HttpStatusCode.Conflict, (await seller.PostAsJsonAsync($"/api/v1/seller/group-buys/{x.Campaign}/transition", new TransitionRequest(CampaignStatus.Completed))).StatusCode);
        await using var scope = f.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
        Assert.Equal(0, (await db.Campaigns.SingleAsync(c => c.Id == x.Campaign)).ReservedQuantity);
        Assert.Equal(ReservationStatus.Expired, (await db.Reservations.SingleAsync(r => r.CampaignId == x.Campaign)).Status);
        Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.EntityId == x.Campaign && a.Action == "CampaignFailed"));
        Assert.Contains("Seller reported fulfillment failure", (await db.AuditLogs.SingleAsync(a => a.EntityId == x.Campaign && a.Action == "CampaignFailed")).SafeMetadataJson);
    }
    [Fact]
    public async Task Authentication_categories_authorization_and_logout()
    {
        using var f = new ApiFactory(fixture.Connection); using var client = f.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/auth/register", new { email = "test@example.invalid", password = "Strong!Password123", displayName = "Test", roles = new[] { "Admin" } })).StatusCode);
        var user = await Register(client, "auth"); Assert.Equal(new[] { "Customer" }, user.Roles);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(user.Email!, "incorrect-password"))).StatusCode);
        await Login(client, user); Assert.Equal(user.Id, (await Read<UserResponse>(await client.GetAsync("/api/v1/auth/me"))).Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/admin/sellers/pending")).StatusCode);
        var categories = await Read<PageResponse<CategoryResponse>>(await client.GetAsync("/api/v1/categories")); Assert.Equal(7, categories.Total); Assert.DoesNotContain(categories.Items, x => x.Slug == "vehicles");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/categories/vehicles")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/products?pageSize=101")).StatusCode);
        var openapi = await client.GetStringAsync("/openapi/v1.json"); Assert.Contains("/api/v1/group-buys/{id}/reservations", openapi);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/v1/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
    }
    [Fact]
    public async Task Concurrent_reservations_never_overbook_and_preserve_server_prices()
    {
        var x = await Setup(); using var f = x.Factory; using var admin = x.Admin; using var seller = x.Seller; using var customer = x.Customer;
        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => customer.PostAsJsonAsync($"/api/v1/group-buys/{x.Campaign}/reservations", new ReservationRequest(1, $"concurrent-{i:0000}"))));
        Assert.Equal(10, responses.Count(r => r.StatusCode == HttpStatusCode.Created)); Assert.Equal(10, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        var c = await Read<CampaignResponse>(await customer.GetAsync($"/api/v1/group-buys/{x.Campaign}")); Assert.Equal(10, c.ReservedQuantity); Assert.Equal(100, c.Progress); Assert.Equal(CampaignStatus.AwaitingSellerConfirmation, c.Status);
        var reservations = await Read<PageResponse<ReservationResponse>>(await customer.GetAsync("/api/v1/me/reservations")); Assert.All(reservations.Items, r => { Assert.Equal(80, r.UnitPriceSnapshot); Assert.Equal(80, r.TotalPriceSnapshot); });
        await using var scope = f.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>(); Assert.Equal(10, await db.Reservations.Where(r => r.CampaignId == x.Campaign).SumAsync(r => r.Quantity));
    }
    [Fact]
    public async Task Idempotency_cancellation_and_ownership()
    {
        var x = await Setup(); using var f = x.Factory; using var admin = x.Admin; using var seller = x.Seller; using var customer = x.Customer;
        var request = new ReservationRequest(2, "idempotency-test");
        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => customer.PostAsJsonAsync($"/api/v1/group-buys/{x.Campaign}/reservations", request)));
        var reservations = new List<ReservationResponse>(); foreach (var response in responses) reservations.Add(await Read<ReservationResponse>(response)); Assert.Single(reservations.Select(r => r.Id).Distinct());
        Assert.Equal(HttpStatusCode.Conflict, (await customer.PostAsJsonAsync($"/api/v1/group-buys/{x.Campaign}/reservations", request with { Quantity = 3 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await customer.PostAsJsonAsync($"/api/v1/group-buys/{x.Campaign}/reservations", new { quantity = 1, idempotencyKey = "hacked-price", unitPriceSnapshot = 1 })).StatusCode);
        using var other = f.CreateClient(); var u = await Register(other, "other"); await Login(other, u);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/v1/me/reservations/{reservations[0].Id}/cancel", null)).StatusCode);
        var profile = await Read<SellerResponse>(await other.PostAsJsonAsync("/api/v1/seller/profile", SellerRequest("Other"))); await Read<SellerReviewResponse>(await admin.PostAsJsonAsync($"/api/v1/admin/sellers/{profile.Id}/approve", new ReviewRequest(null))); await Login(other, u);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/v1/seller/group-buys/{x.Campaign}/submit", null)).StatusCode);
        var cancelled = await Read<ReservationResponse>(await customer.PostAsync($"/api/v1/me/reservations/{reservations[0].Id}/cancel", null)); Assert.Equal(ReservationStatus.Cancelled, cancelled.Status);
        await Read<ReservationResponse>(await customer.PostAsync($"/api/v1/me/reservations/{cancelled.Id}/cancel", null));
        Assert.Equal(0, (await Read<CampaignResponse>(await customer.GetAsync($"/api/v1/group-buys/{x.Campaign}"))).ReservedQuantity);
    }
    [Fact]
    public async Task Expiry_is_idempotent_and_expired_campaign_rejects_join()
    {
        var x = await Setup(); using var f = x.Factory; using var admin = x.Admin; using var seller = x.Seller; using var customer = x.Customer;
        await Read<ReservationResponse>(await customer.PostAsJsonAsync($"/api/v1/group-buys/{x.Campaign}/reservations", new ReservationRequest(2, "expiry-reservation")));
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>(); await db.Campaigns.Where(c => c.Id == x.Campaign).ExecuteUpdateAsync(s => s.SetProperty(c => c.ExpiresAtUtc, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }
        Assert.Equal(HttpStatusCode.Conflict, (await customer.PostAsJsonAsync($"/api/v1/group-buys/{x.Campaign}/reservations", new ReservationRequest(1, "expired-new-join"))).StatusCode);
        for (var i = 0; i < 2; i++) { await using var scope = f.Services.CreateAsyncScope(); await scope.ServiceProvider.GetRequiredService<ICampaignExpiryProcessor>().Process(default); }
        var campaign = await Read<CampaignResponse>(await customer.GetAsync($"/api/v1/group-buys/{x.Campaign}")); Assert.Equal(CampaignStatus.Expired, campaign.Status); Assert.Equal(0, campaign.ReservedQuantity);
        var reservations = await Read<PageResponse<ReservationResponse>>(await customer.GetAsync("/api/v1/me/reservations")); Assert.All(reservations.Items, r => Assert.Equal(ReservationStatus.Expired, r.Status));
        await using var verify = f.Services.CreateAsyncScope(); var db2 = verify.ServiceProvider.GetRequiredService<MarketplaceDb>(); Assert.Equal(1, await db2.AuditLogs.CountAsync(a => a.EntityId == x.Campaign && a.Action == "CampaignExpired"));
    }
    [Fact]
    public async Task Full_campaign_requires_confirmation_and_completion_is_terminal()
    {
        var x = await Setup(2); using var f = x.Factory; using var admin = x.Admin; using var seller = x.Seller; using var customer = x.Customer;
        var reservation = await Read<ReservationResponse>(await customer.PostAsJsonAsync($"/api/v1/group-buys/{x.Campaign}/reservations", new ReservationRequest(2, "full-campaign-key")));
        Assert.Equal(HttpStatusCode.Conflict, (await seller.PostAsJsonAsync($"/api/v1/seller/group-buys/{x.Campaign}/transition", new TransitionRequest(CampaignStatus.Completed))).StatusCode);
        foreach (var state in new[] { CampaignStatus.AwaitingSellerConfirmation, CampaignStatus.Confirmed, CampaignStatus.FulfillmentInProgress })
            Assert.Equal(state, (await Read<CampaignResponse>(await seller.PostAsJsonAsync($"/api/v1/seller/group-buys/{x.Campaign}/transition", new TransitionRequest(state)))).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await seller.PostAsJsonAsync($"/api/v1/seller/group-buys/{x.Campaign}/transition", new TransitionRequest(CampaignStatus.Completed))).StatusCode);
        var pickup = (await Read<PageResponse<PickupDetailResponse>>(await customer.GetAsync("/api/v1/me/pickups"))).Items.Single();
        await Read<PickupDetailResponse>(await seller.PostAsync($"/api/v1/seller/pickups/{pickup.Id}/ready", null));
        var code = await Read<IssuedPickupCode>(await customer.PostAsync($"/api/v1/me/pickups/{pickup.Id}/code", null));
        await Read<PickupDetailResponse>(await seller.PostAsJsonAsync($"/api/v1/seller/pickups/{pickup.Id}/collect", new PickupCodeRequest(code.Code)));
        Assert.Equal(HttpStatusCode.Conflict, (await customer.PostAsync($"/api/v1/me/reservations/{reservation.Id}/cancel", null)).StatusCode);
        await using var scope = f.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
        await db.Campaigns.Where(c => c.Id == x.Campaign).ExecuteUpdateAsync(s => s.SetProperty(c => c.ExpiresAtUtc, DateTimeOffset.UtcNow.AddMinutes(-1)));
        await scope.ServiceProvider.GetRequiredService<ICampaignExpiryProcessor>().Process(default);
        Assert.Equal(CampaignStatus.Completed, (await Read<CampaignResponse>(await customer.GetAsync($"/api/v1/group-buys/{x.Campaign}"))).Status);
        Assert.Equal(ReservationStatus.Completed, (await Read<PageResponse<ReservationResponse>>(await customer.GetAsync("/api/v1/me/reservations"))).Items.Single().Status);
    }
    [Fact]
    public async Task Database_constraints_reject_over_capacity_and_negative_reservations()
    {
        var x = await Setup(); using var f = x.Factory; using var admin = x.Admin; using var seller = x.Seller; using var customer = x.Customer;
        await using var scope = f.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Campaigns.Where(c => c.Id == x.Campaign).ExecuteUpdateAsync(s => s.SetProperty(c => c.ReservedQuantity, 11)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        db.Reservations.Add(new GroupBuyReservation { CampaignId = x.Campaign, CustomerUserId = x.CustomerId, Quantity = -1, UnitPriceSnapshot = 80, TotalPriceSnapshot = -80, IdempotencyKey = "invalid-direct-db" });
        var update = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()); Assert.Equal(PostgresErrorCodes.CheckViolation, ((PostgresException)update.InnerException!).SqlState);
    }
    [Fact]
    public async Task Demo_seed_is_explicit_transactional_and_repeatable()
    {
        using var f = new ApiFactory(fixture.Connection); using var client = f.CreateClient();
        await using var scope = f.Services.CreateAsyncScope();
        await DemoSeed.Run(scope.ServiceProvider); await DemoSeed.Run(scope.ServiceProvider);
        var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
        Assert.Equal(3, await db.Sellers.CountAsync(s => s.BusinessName.StartsWith("FICTIONAL")));
        Assert.Equal(15, await db.Products.CountAsync(p => p.Description == "Fictional demo product"));
        Assert.Equal(15, await db.Reservations.CountAsync(r => r.IdempotencyKey.StartsWith("demo-")));
        Assert.False(await db.UsersProfile.AnyAsync(u => u.Email!.EndsWith("FICTIONAL-DEMO.INVALID") && (u.PhoneNumberVerified || u.EmailVerified)));
    }
    [Fact]
    public async Task Seller_profile_changes_suspend_publication_until_reapproved()
    {
        var x = await Setup(); using var f = x.Factory; using var admin = x.Admin; using var seller = x.Seller; using var customer = x.Customer;
        var updated = await Read<SellerResponse>(await seller.PutAsJsonAsync("/api/v1/seller/profile", SellerRequest("Updated Shop")));
        Assert.Equal(VerificationStatus.Pending, updated.VerificationStatus);
        Assert.Equal(HttpStatusCode.Conflict, (await customer.PostAsJsonAsync($"/api/v1/group-buys/{x.Campaign}/reservations", new ReservationRequest(1, "pending-seller-key"))).StatusCode);
        await Read<SellerReviewResponse>(await admin.PostAsJsonAsync($"/api/v1/admin/sellers/{updated.Id}/approve", new ReviewRequest(null)));
        await Read<ReservationResponse>(await customer.PostAsJsonAsync($"/api/v1/group-buys/{x.Campaign}/reservations", new ReservationRequest(1, "reapproved-seller-key")));
    }
}

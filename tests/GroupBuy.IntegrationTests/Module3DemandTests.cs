using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using static GroupBuy.IntegrationTests.BackendTests;
using GroupBuy.Application;
using GroupBuy.Domain;
using GroupBuy.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace GroupBuy.IntegrationTests;

public sealed class Module3DemandTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(); Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {body}"); return JsonSerializer.Deserialize<T>(body, Json)!;
    }
    private sealed record M3Data(ApiFactory Factory, string Area, Guid Product, Guid ProductCategory, Guid ServiceCategory);
    private async Task<M3Data> M3Setup(bool configured = true, bool pilot = false, bool multiple = false)
    {
        var area = "demand-" + Guid.NewGuid().ToString("N");
        var settings = new Dictionary<string, string?>()
        {
            ["Pilot:Enabled"] = pilot.ToString(),
            ["Payments:Enabled"] = "false",
            ["Pilot:ServiceAreas:0:Id"] = configured ? area : null,
            ["Pilot:ServiceAreas:0:Name"] = configured ? "Synthetic Module 3 area" : null,
            ["Pilot:ServiceAreas:0:PostalCodes:0"] = configured ? "900001" : null,
            ["Cors:Origins:0"] = "https://groudeal-frontend.vercel.app"
        };
        if (multiple) { settings["Pilot:ServiceAreas:1:Id"] = area + "-other"; settings["Pilot:ServiceAreas:1:Name"] = "Other synthetic area"; settings["Pilot:ServiceAreas:1:PostalCodes:0"] = "900002"; }
        var f = new ApiFactory(fixture.Connection, overrides: settings, disableWorker: true);
        using var start = f.CreateClientWithEnvironmentVariable("Cors__Origins__0", "https://groudeal-frontend.vercel.app");
        await using var scope = f.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
        var owner = Guid.NewGuid(); db.Users.Add(new() { Id = owner, UserName = owner.ToString(), NormalizedUserName = owner.ToString().ToUpperInvariant() });
        db.UsersProfile.Add(new() { Id = owner, DisplayName = "PRIVATE_MODULE3_OWNER", Status = AccountStatus.Active });
        var seller = new Seller { OwnerUserId = owner, BusinessName = "Synthetic shop", VerificationStatus = VerificationStatus.Approved, Status = ResourceStatus.Active, ContactEmail = "PRIVATE_MODULE3_CONTACT" };
        var category = new Category { Name = "Synthetic services", Slug = area, IsActive = true };
        var product = new Product { SellerId = seller.Id, CategoryId = CategorySeed.All[2].Id, Name = "Synthetic rice", Slug = area, Status = ResourceStatus.Active };
        db.AddRange(seller, category, product, new SellerLocation { SellerId = seller.Id, PostalCode = "900001", AddressLine1 = "PRIVATE_MODULE3_ADDRESS", IsActive = true, IsPickupEnabled = true },
            new ProductVariant { SellerId = seller.Id, ProductId = product.Id, SKU = area, VariantName = "1 kg", UnitQuantity = 1, UnitOfMeasure = UnitOfMeasure.Kilogram });
        await db.SaveChangesAsync(); return new(f, area, product.Id, product.CategoryId, category.Id);
    }
    private static CreateDemandRequest M3Product(M3Data d) => new("Client display title", d.Area, d.ProductCategory, DemandType.Product, d.Product, 10, "kg");
    private static CreateDemandRequest M3Service(M3Data d, string title = "Home solar installation") => new(title, d.Area, d.ServiceCategory, DemandType.Service, Requirement: "Assess a roof installation");
    private static async Task<(HttpClient Client, Guid Id)> M3Customer(ApiFactory f)
    {
        await using var scope = f.Services.CreateAsyncScope(); var identity = scope.ServiceProvider.GetRequiredService<IIdentityService>();
        var email = Guid.NewGuid().ToString("N") + "@example.invalid"; const string password = "Synthetic!Password123";
        var user = await identity.Register(new(email, password, "PRIVATE_MODULE3_CUSTOMER"), default); var token = await identity.Login(new(email, password), default);
        var client = f.CreateClient(); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken); return (client, user.Id);
    }
    private static async Task<long[]> M3SideEffects(MarketplaceDb db) => [await db.PaymentOrders.LongCountAsync(), await db.Reservations.LongCountAsync(), await db.Campaigns.LongCountAsync(), await db.Inventories.LongCountAsync(), await db.PaymentRefunds.LongCountAsync()];
    [Fact]
    public async Task Module3_product_service_custom_demands_are_free_and_preserve_legacy_contract()
    {
        var d = await M3Setup(); using var f = d.Factory; var (client, id) = await M3Customer(f); using (client)
        {
            await using var scope = f.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>(); var before = await M3SideEffects(db);
            var catalogName = new string('x', 99) + "\U0001F33E";
            await db.Products.Where(p => p.Id == d.Product).ExecuteUpdateAsync(s => s.SetProperty(p => p.Name, catalogName));
            var product = await Read<DemandMembership>(await client.PostAsJsonAsync("/api/v1/discovery/demands", M3Product(d) with { Title = "" }));
            Assert.Equal(new string('x', 99), product.Demand!.Title); Assert.Equal(catalogName, product.Demand.Product!.Name); Assert.Equal(d.Product, product.Demand.Product!.Id); Assert.Equal(10, product.Demand.Membership!.Quantity);
            var service = await Read<DemandMembership>(await client.PostAsJsonAsync("/api/v1/discovery/demands", M3Service(d)));
            Assert.Equal(DemandType.Service, service.Demand!.DemandType); Assert.Null(service.Demand.Membership!.Quantity); Assert.Null(service.Demand.Membership.Unit);
            Assert.Null(service.Demand.Progress.TargetInterest); Assert.Null(service.Demand.Progress.ProgressPercentage);
            var custom = await Read<DemandMembership>(await client.PostAsJsonAsync("/api/v1/discovery/demands", M3Product(d) with { ProductId = null, Title = "Organic rice in packs" }));
            Assert.Null(custom.Demand!.Product); Assert.True(custom.Demand.IsFree); Assert.False(custom.Demand.GuaranteesFulfillment);
            var legacy = await Read<DemandMembership>(await client.PostAsJsonAsync("/api/v1/discovery/demands", new CreateDemandRequest("Legacy public request", d.Area)));
            Assert.True(legacy.Joined); Assert.Equal(before, await M3SideEffects(db));
            Assert.Equal(1, await db.Interests.CountAsync(i => i.CustomerId == id && i.ProductId == d.Product));
        }
    }
    [Theory]
    [InlineData("category")]
    [InlineData("product")]
    [InlineData("mismatch")]
    [InlineData("area")]
    [InlineData("quantity")]
    [InlineData("unit")]
    [InlineData("type")]
    [InlineData("details")]
    public async Task Module3_creation_rejects_invalid_input_without_writes(string invalid)
    {
        var d = await M3Setup(); using var f = d.Factory; var (client, _) = await M3Customer(f); using (client)
        {
            var r = M3Product(d); r = invalid switch { "category" => r with { CategoryId = Guid.NewGuid() }, "product" => r with { ProductId = Guid.NewGuid() }, "mismatch" => r with { CategoryId = d.ServiceCategory }, "area" => r with { ServiceArea = "unsupported" }, "quantity" => r with { Quantity = -1 }, "unit" => r with { Unit = "l" }, "type" => r with { DemandType = (DemandType)99 }, _ => r with { Details = "<script>unsafe</script>" } };
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/discovery/demands", r)).StatusCode);
            await using var scope = f.Services.CreateAsyncScope(); Assert.False(await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().DiscoveryDemands.AnyAsync(x => x.ServiceArea == d.Area));
        }
    }
    [Fact]
    public async Task Module3_unconfigured_area_and_unauthenticated_requests_fail_closed()
    {
        var d = await M3Setup(false); using var f = d.Factory; using var anon = f.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/v1/discovery/demands", M3Service(d))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/discovery/demands/mine")).StatusCode);
        var (client, _) = await M3Customer(f); using (client)
        {
            var response = await client.PostAsJsonAsync("/api/v1/discovery/demands", M3Service(d)); Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); Assert.Contains("service_area_unconfigured", await response.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync("/api/v1/discovery/demands/similar", M3Service(d))).StatusCode);
        }
    }
    [Fact]
    public async Task Module3_review_is_read_only_normalized_and_finds_exact_similarity()
    {
        var d = await M3Setup(); using var f = d.Factory; var (client, _) = await M3Customer(f); using (client)
        {
            await using var scope = f.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
            var r = M3Service(d) with { Title = "  Home   solar installation  ", Details = "Public specification" }; var audits = await db.AuditLogs.CountAsync();
            var preview = await Read<DemandPreview>(await client.PostAsJsonAsync("/api/v1/discovery/demands/similar", r)); Assert.False(preview.SimilarDemandFound); Assert.Equal("Home solar installation", preview.Normalized.Title);
            Assert.Equal(audits, await db.AuditLogs.CountAsync()); Assert.False(await db.DiscoveryDemands.AnyAsync(x => x.ServiceArea == d.Area));
            var created = await Read<DemandMembership>(await client.PostAsJsonAsync("/api/v1/discovery/demands", r));
            preview = await Read<DemandPreview>(await client.PostAsJsonAsync("/api/v1/discovery/demands/similar", r with { Title = "HOME SOLAR INSTALLATION" })); Assert.True(preview.SimilarDemandFound); Assert.Equal(created.DemandId, preview.Demand!.DemandId); Assert.True(preview.Demand.Membership!.Joined);
            Assert.False((await Read<DemandPreview>(await client.PostAsJsonAsync("/api/v1/discovery/demands/similar", r with { CategoryId = d.ProductCategory }))).SimilarDemandFound);
            var actor = new Actor((await Read<UserResponse>(await client.GetAsync("/api/v1/auth/me"))).Id, true, "module3-close"); await scope.ServiceProvider.GetRequiredService<IDiscovery>().CloseDemand(actor, created.DemandId, default);
            Assert.False((await Read<DemandPreview>(await client.PostAsJsonAsync("/api/v1/discovery/demands/similar", r))).SimilarDemandFound);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/v1/discovery/demands", r)).StatusCode);
        }
    }
    [Fact]
    public async Task Module3_twenty_simultaneous_customers_create_one_canonical_aggregate()
    {
        var d = await M3Setup(); using var f = d.Factory; var customers = new List<(HttpClient Client, Guid Id)>();
        try
        {
            for (var i = 0; i < 20; i++) customers.Add(await M3Customer(f));
            var replies = await Task.WhenAll(customers.Select(c => c.Client.PostAsJsonAsync("/api/v1/discovery/demands", M3Service(d))));
            var memberships = new List<DemandMembership>(); foreach (var response in replies) memberships.Add(await Read<DemandMembership>(response)); var id = Assert.Single(memberships.Select(m => m.DemandId).Distinct());
            using var anon = f.CreateClient(); Assert.Equal(20, (await Read<DemandDetail>(await anon.GetAsync($"/api/v1/discovery/demands/{id}"))).Progress.CurrentInterest);
            await using var scope = f.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>(); Assert.Equal(1, await db.DiscoveryDemands.CountAsync(x => x.ServiceArea == d.Area)); Assert.Equal(20, await db.Interests.CountAsync(i => i.DemandId == id));
        }
        finally { foreach (var c in customers) c.Client.Dispose(); }
    }
    [Fact]
    public async Task Module3_twenty_joins_and_join_withdraw_races_do_not_inflate_counts()
    {
        var d = await M3Setup(); using var f = d.Factory; var (creator, _) = await M3Customer(f); using (creator)
        {
            var created = await Read<DemandMembership>(await creator.PostAsJsonAsync("/api/v1/discovery/demands", M3Service(d))); var (joiner, user) = await M3Customer(f); using (joiner)
            {
                var replies = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => joiner.PostAsync($"/api/v1/discovery/demands/{created.DemandId}/join", null))); foreach (var response in replies) Assert.True((await Read<DemandMembership>(response)).Joined);
                using var anon = f.CreateClient(); Assert.Equal(2, (await Read<DemandDetail>(await anon.GetAsync($"/api/v1/discovery/demands/{created.DemandId}"))).Progress.CurrentInterest);
                // A different caller avoids spending the rate-policy budget on test setup.
                var (racer, racerId) = await M3Customer(f); using (racer)
                {
                    var racing = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => racer.PostAsync($"/api/v1/discovery/demands/{created.DemandId}/{(i % 2 == 0 ? "join" : "withdraw")}", null))); foreach (var response in racing) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    await Read<DemandMembership>(await racer.PostAsync($"/api/v1/discovery/demands/{created.DemandId}/withdraw", null));
                    var again = await Read<DemandMembership>(await racer.PostAsync($"/api/v1/discovery/demands/{created.DemandId}/withdraw", null)); Assert.False(again.Joined); Assert.Equal(2, again.Demand!.Progress.CurrentInterest);
                    await using var scope = f.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>(); Assert.Equal(1, await db.Interests.CountAsync(i => i.CustomerId == racerId && i.DemandId == created.DemandId)); Assert.Equal(1, await db.Interests.CountAsync(i => i.CustomerId == user && i.DemandId == created.DemandId));
                }
            }
        }
    }
    [Fact]
    public async Task Module3_product_memberships_are_shared_with_legacy_interest_and_not_double_ranked()
    {
        var d = await M3Setup(pilot: true); using var f = d.Factory; var (client, id) = await M3Customer(f); using (client)
        {
            var catalogName = new string('x', 98) + "\U0001F33Eextra";
            await using (var setup = f.Services.CreateAsyncScope()) await setup.ServiceProvider.GetRequiredService<MarketplaceDb>().Products.Where(p => p.Id == d.Product).ExecuteUpdateAsync(s => s.SetProperty(p => p.Name, catalogName));
            await Read<InterestResponse>(await client.PostAsJsonAsync("/api/v1/mvp/customer/interests", new InterestRequest(d.Product, d.Area, 3)));
            var created = await Read<DemandMembership>(await client.PostAsJsonAsync("/api/v1/discovery/demands", M3Product(d)));
            Assert.Equal(new string('x', 98) + "\U0001F33E", created.Demand!.Title);
            await using var scope = f.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>(); Assert.Equal(1, await db.Interests.CountAsync(i => i.ProductId == d.Product && i.CustomerId == id));
            var popular = await Read<PageResponse<DemandCard>>(await client.GetAsync("/api/v1/discovery/demands/popular")); var card = Assert.Single(popular.Items, i => i.ProductId == d.Product); Assert.Equal(1, card.InterestedCustomers); Assert.DoesNotContain(popular.Items, x => x.DemandId == created.DemandId);
            await Read<DemandMembership>(await client.PostAsync($"/api/v1/discovery/demands/{created.DemandId}/withdraw", null));
            Assert.False((await Read<PageResponse<InterestResponse>>(await client.GetAsync("/api/v1/mvp/customer/interests"))).Items.Single(i => i.ProductId == d.Product).Active);
        }
    }
    [Fact]
    public async Task Module3_mine_filters_pagination_withdrawn_history_and_isolation_are_stable()
    {
        var d = await M3Setup(); using var f = d.Factory; var (owner, _) = await M3Customer(f); using (owner)
        {
            var first = await Read<DemandMembership>(await owner.PostAsJsonAsync("/api/v1/discovery/demands", M3Service(d))); await Read<DemandMembership>(await owner.PostAsJsonAsync("/api/v1/discovery/demands", M3Service(d, "House painting")));
            var (other, _) = await M3Customer(f); using (other)
            {
                await Read<DemandMembership>(await other.PostAsync($"/api/v1/discovery/demands/{first.DemandId}/join", null)); await Read<DemandMembership>(await other.PostAsync($"/api/v1/discovery/demands/{first.DemandId}/withdraw", null));
                var mine = await Read<PageResponse<MyDemand>>(await other.GetAsync("/api/v1/discovery/demands/mine")); var item = Assert.Single(mine.Items); Assert.False(item.Created); Assert.False(item.Participation.Joined); Assert.False(item.Participation.CanWithdraw); Assert.NotNull(item.Participation.JoinedAtUtc);
                var a = await Read<PageResponse<MyDemand>>(await owner.GetAsync("/api/v1/discovery/demands/mine?pageSize=1")); var b = await Read<PageResponse<MyDemand>>(await owner.GetAsync("/api/v1/discovery/demands/mine?pageSize=1&page=2")); Assert.Equal(2, a.Total); Assert.NotEqual(a.Items[0].Demand.DemandId, b.Items[0].Demand.DemandId); Assert.True(a.Items[0].Created);
                Assert.Empty((await Read<PageResponse<MyDemand>>(await owner.GetAsync("/api/v1/discovery/demands/mine?type=Product"))).Items);
                Assert.Equal(2, (await Read<PageResponse<MyDemand>>(await owner.GetAsync($"/api/v1/discovery/demands/mine?status=BuildingInterest&categoryId={d.ServiceCategory}"))).Total);
            }
        }
    }
    [Fact]
    public async Task Module3_public_detail_excludes_private_participation_and_other_customers()
    {
        var d = await M3Setup(); using var f = d.Factory; var (owner, ownerId) = await M3Customer(f); using (owner)
        {
            var r = M3Service(d) with { Details = "PRIVATE_REQUIREMENT_MARKER" }; var created = await Read<DemandMembership>(await owner.PostAsJsonAsync("/api/v1/discovery/demands", r)); using var anon = f.CreateClient();
            var json = await (await anon.GetAsync($"/api/v1/discovery/demands/{created.DemandId}")).Content.ReadAsStringAsync(); foreach (var marker in new[] { ownerId.ToString(), "PRIVATE_REQUIREMENT_MARKER", "PRIVATE_MODULE3", "customerId", "createdBy" }) Assert.DoesNotContain(marker, json, StringComparison.OrdinalIgnoreCase);
            Assert.Null((await Read<DemandDetail>(await anon.GetAsync($"/api/v1/discovery/demands/{created.DemandId}"))).Membership);
            var (other, _) = await M3Customer(f); using (other) { var detail = await Read<DemandDetail>(await other.GetAsync($"/api/v1/discovery/demands/{created.DemandId}")); Assert.False(detail.Membership!.Joined); Assert.Null(detail.Membership.Details); }
            Assert.Equal("PRIVATE_REQUIREMENT_MARKER", (await Read<DemandDetail>(await owner.GetAsync($"/api/v1/discovery/demands/{created.DemandId}"))).Membership!.Details);
            Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/v1/discovery/demands/{Guid.NewGuid()}")).StatusCode);
        }
    }
    [Fact]
    public async Task Module3_admin_mfa_controls_are_audited_private_and_terminal()
    {
        var d = await M3Setup(); using var f = d.Factory; var (customer, _) = await M3Customer(f); using (customer)
        {
            var created = await Read<DemandMembership>(await customer.PostAsJsonAsync("/api/v1/discovery/demands", M3Service(d))); var url = $"/api/v1/admin/discovery/demands/{created.DemandId}";
            Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync(url)).StatusCode); Assert.Equal(HttpStatusCode.Forbidden, (await customer.PostAsJsonAsync(url + "/moderate", new ModerateDemandRequest(DemandStatus.Cancelled, "Invalid local requirement"))).StatusCode);
            using var admin = f.CreateClient(); var user = await Register(admin, "module3-admin"); await Role(f, user.Id, "Admin"); await Login(admin, user);
            var detail = await Read<AdminDemandDetail>(await admin.GetAsync(url)); Assert.Null(detail.Demand.Membership);
            var listing = await Read<PageResponse<DemandDetail>>(await admin.GetAsync($"/api/v1/admin/discovery/demands?type=Service&categoryId={d.ServiceCategory}&serviceArea={d.Area}")); Assert.Single(listing.Items); Assert.Null(listing.Items[0].Membership);
            var history = await Read<PageResponse<DemandAudit>>(await admin.GetAsync(url + "/audit?pageSize=1")); Assert.Single(history.Items); Assert.True(history.Total >= 2);
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync(url + "/moderate", new ModerateDemandRequest(DemandStatus.Cancelled, "No longer an eligible public requirement"))).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await customer.PostAsync($"/api/v1/discovery/demands/{created.DemandId}/join", null)).StatusCode);
            var publicDetail = await Read<DemandDetail>(await customer.GetAsync($"/api/v1/discovery/demands/{created.DemandId}")); Assert.Equal(DemandStatus.Cancelled, publicDetail.Status);
            detail = await Read<AdminDemandDetail>(await admin.GetAsync(url)); Assert.Equal("No longer an eligible public requirement", detail.ClosureReason); Assert.Contains(detail.RecentAudit, a => a.Action == "DemandCancelled");
            Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync(url + "/moderate", new ModerateDemandRequest(DemandStatus.Closed, "Attempt to change terminal state"))).StatusCode);
            Assert.False((await Read<DemandMembership>(await customer.PostAsync($"/api/v1/discovery/demands/{created.DemandId}/withdraw", null))).Joined);
        }
    }
    [Fact]
    public async Task Module3_upgrade_from_Module2_preserves_free_and_product_interest_and_restart()
    {
        var name = "module3_upgrade_" + Guid.NewGuid().ToString("N"); await using var owner = new NpgsqlConnection(fixture.Connection); await owner.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {name}", owner)) await create.ExecuteNonQueryAsync();
        try
        {
            var connection = new NpgsqlConnectionStringBuilder(fixture.Connection) { Database = name, Pooling = false }.ConnectionString;
            var options = new DbContextOptionsBuilder<MarketplaceDb>().UseNpgsql(connection).Options;
            var user = Guid.NewGuid(); var productId = Guid.NewGuid(); var freeId = Guid.NewGuid(); var productInterest = Guid.NewGuid(); var freeInterest = Guid.NewGuid();
            await using (var db = new MarketplaceDb(options))
            {
                var migrator = db.GetService<IMigrator>(); await migrator.MigrateAsync("20261107060508_HomeDiscoveryDemands");
                db.Users.Add(new() { Id = user, UserName = user.ToString() }); db.UsersProfile.Add(new() { Id = user, DisplayName = "PRIVATE_UPGRADE_MARKER" }); var seller = new Seller { OwnerUserId = user };
                db.AddRange(seller, new Product { Id = productId, SellerId = seller.Id, CategoryId = CategorySeed.All[2].Id, Name = "Legacy product", Slug = "module3-legacy" }); await db.SaveChangesAsync();
                var now = DateTimeOffset.UtcNow;
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO discovery_demands (\"Id\",\"Title\",\"NormalizedTitle\",\"ServiceArea\",\"CategoryId\",\"IsOpen\",\"CreatedAt\",\"UpdatedAt\") VALUES ({freeId},{"Legacy free need"},{"legacy free need"},{"synthetic-upgrade"},{CategorySeed.All[2].Id},{true},{now},{now})");
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO customer_interests (\"Id\",\"CustomerId\",\"ProductId\",\"DemandId\",\"ServiceArea\",\"DesiredQuantity\",\"Active\",\"CreatedAt\",\"UpdatedAt\") VALUES ({productInterest},{user},{productId},NULL,{"synthetic-upgrade"},{4m},{true},{now},{now}),({freeInterest},{user},NULL,{freeId},{"synthetic-upgrade"},{1m},{true},{now},{now})");
                db.AuditLogs.Add(new() { ActorUserId = user, EntityType = nameof(DiscoveryDemand), EntityId = freeId, Action = "DiscoveryDemandCreated" }); await db.SaveChangesAsync();
                await migrator.MigrateAsync(); Assert.False(db.Database.HasPendingModelChanges());
                var product = await db.Interests.AsNoTracking().SingleAsync(i => i.Id == productInterest); Assert.Equal(productId, product.ProductId); Assert.NotNull(product.DemandId); Assert.Equal(4, product.DesiredQuantity); Assert.Null(product.RequestedQuantity);
                Assert.Equal(freeId, (await db.Interests.AsNoTracking().SingleAsync(i => i.Id == freeInterest)).DemandId); Assert.Equal(user, (await db.DiscoveryDemands.SingleAsync(d => d.Id == freeId)).CreatedBy);
                Assert.Throws<NotSupportedException>(() => migrator.GenerateScript(db.Database.GetMigrations().Last(), "20261107060508_HomeDiscoveryDemands"));
                Assert.Contains("groudeal_guard_interest_demand", migrator.GenerateScript("20261107060508_HomeDiscoveryDemands"));
            }
            await using (var restarted = new MarketplaceDb(options)) { Assert.Equal(2, await restarted.Interests.CountAsync()); Assert.Equal(2, await restarted.DiscoveryDemands.CountAsync()); Assert.Equal(4, (await restarted.Interests.SingleAsync(i => i.Id == productInterest)).DesiredQuantity); }
        }
        finally { await using var drop = new NpgsqlCommand($"DROP DATABASE {name} WITH (FORCE)", owner); await drop.ExecuteNonQueryAsync(); }
    }
    [Fact]
    public async Task Module3_database_rejects_wrong_target_area_negative_quantity_and_identity_rewrite()
    {
        var d = await M3Setup(); using var f = d.Factory; var (client, user) = await M3Customer(f); using (client)
        {
            var created = await Read<DemandMembership>(await client.PostAsJsonAsync("/api/v1/discovery/demands", M3Service(d)));
            await using var scope = f.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
            var statements = new[]{
                $"UPDATE customer_interests SET \"RequestedQuantity\"=-1,\"RequestedUnit\"='project' WHERE \"DemandId\"='{created.DemandId}'",
                $"UPDATE customer_interests SET \"ServiceArea\"='different' WHERE \"DemandId\"='{created.DemandId}'",
                $"UPDATE customer_interests SET \"ProductId\"='{d.Product}' WHERE \"DemandId\"='{created.DemandId}'",
                $"UPDATE discovery_demands SET \"NormalizedTitle\"='changed' WHERE \"Id\"='{created.DemandId}'"};
            foreach (var sql in statements) { await using var connection = new NpgsqlConnection(fixture.Connection); await connection.OpenAsync(); await using var command = new NpgsqlCommand(sql, connection); var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync()); Assert.Contains(error.SqlState, new[] { "23514", "23503" }); }
            Assert.Equal(1, (await Read<DemandDetail>(await client.GetAsync($"/api/v1/discovery/demands/{created.DemandId}"))).Progress.CurrentInterest);
        }
    }

    [Fact]
    public async Task Module3_similarity_and_creation_keep_type_category_and_area_separate()
    {
        var d = await M3Setup(multiple: true); using var f = d.Factory; var (client, _) = await M3Customer(f); using (client)
        {
            var first = await Read<DemandMembership>(await client.PostAsJsonAsync("/api/v1/discovery/demands", M3Service(d)));
            foreach (var r in new[] { M3Service(d) with { CategoryId = d.ProductCategory }, M3Service(d) with { ServiceArea = d.Area + "-other" }, new CreateDemandRequest("Home solar installation", d.Area, d.ServiceCategory, DemandType.Product, Quantity: 1, Unit: "piece") })
            {
                Assert.False((await Read<DemandPreview>(await client.PostAsJsonAsync("/api/v1/discovery/demands/similar", r))).SimilarDemandFound);
                var created = await Read<DemandMembership>(await client.PostAsJsonAsync("/api/v1/discovery/demands", r)); Assert.NotEqual(first.DemandId, created.DemandId);
            }
            await using var scope = f.Services.CreateAsyncScope(); var rows = await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().DiscoveryDemands.Where(x => x.ServiceArea == d.Area || x.ServiceArea == d.Area + "-other").ToListAsync(); Assert.Equal(4, rows.Count); Assert.All(rows, x => Assert.False(x.LegacyIdentity));
        }
    }
    [Fact]
    public async Task Module3_twenty_withdrawals_are_idempotent_and_preserve_membership_history()
    {
        var d = await M3Setup(); using var f = d.Factory; var (client, id) = await M3Customer(f); using (client)
        {
            var first = await Read<DemandMembership>(await client.PostAsJsonAsync("/api/v1/discovery/demands", M3Service(d)));
            var replies = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => client.PostAsync($"/api/v1/discovery/demands/{first.DemandId}/withdraw", null)));
            foreach (var response in replies) { var result = await Read<DemandMembership>(response); Assert.False(result.Joined); Assert.Equal(0, result.Demand!.Progress.CurrentInterest); }
            await using var scope = f.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>(); Assert.Equal(1, await db.Interests.CountAsync(i => i.CustomerId == id && i.DemandId == first.DemandId)); Assert.Equal(1, await db.DiscoveryDemands.CountAsync(i => i.Id == first.DemandId));
        }
    }
    [Fact]
    public async Task Module3_actor_timestamp_and_counts_are_not_client_controlled()
    {
        var d = await M3Setup(); using var f = d.Factory; var (client, id) = await M3Customer(f); using (client)
        {
            var payload = new { title = "Public local requirement", serviceArea = d.Area, categoryId = d.ServiceCategory, demandType = "Service", customerId = Guid.NewGuid(), createdBy = Guid.NewGuid(), interestedCustomers = 9000, createdAtUtc = "1970-01-01T00:00:00Z" };
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/discovery/demands", payload)).StatusCode);
            await using var scope = f.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>(); Assert.False(await db.DiscoveryDemands.AnyAsync(x => x.ServiceArea == d.Area));
            var first = await Read<DemandMembership>(await client.PostAsJsonAsync("/api/v1/discovery/demands", M3Service(d))); Assert.Equal(1, first.Demand!.Progress.CurrentInterest); Assert.True(first.Demand.CreatedAtUtc > DateTimeOffset.UtcNow.AddMinutes(-1));
            Assert.Equal(id, (await db.DiscoveryDemands.SingleAsync(x => x.Id == first.DemandId)).CreatedBy);
        }
    }
    [Fact]
    public async Task Module3_customer_policy_rejects_seller_only_tokens()
    {
        var d = await M3Setup(); using var f = d.Factory; var (client, id) = await M3Customer(f); using (client)
        {
            await using var scope = f.Services.CreateAsyncScope(); var manager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityAccount>>(); var user = (await manager.FindByIdAsync(id.ToString()))!; Assert.True((await manager.RemoveFromRoleAsync(user, "Customer")).Succeeded); Assert.True((await manager.AddToRoleAsync(user, "Seller")).Succeeded);
            var issuer = scope.ServiceProvider.GetRequiredService<IIdentityService>(); var token = await issuer.Login(new(user.Email!, "Synthetic!Password123"), default); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/discovery/demands", M3Service(d))).StatusCode); Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/discovery/demands/mine")).StatusCode);
        }
    }
    [Fact]
    public async Task Module3_preview_uses_existing_rate_limit_and_never_writes()
    {
        var d = await M3Setup(); using var f = d.Factory; var (client, _) = await M3Customer(f); using (client)
        {
            for (var i = 0; i < 30; i++) Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/discovery/demands/similar", M3Service(d))).StatusCode);
            var limited = await client.PostAsJsonAsync("/api/v1/discovery/demands/similar", M3Service(d)); Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode); Assert.Contains("rate_limited", await limited.Content.ReadAsStringAsync());
            await using var scope = f.Services.CreateAsyncScope(); Assert.False(await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().DiscoveryDemands.AnyAsync(demand => demand.ServiceArea == d.Area));
        }
    }
    [Fact]
    public async Task Module3_withdrawal_remains_available_when_area_configuration_is_removed()
    {
        var d = await M3Setup(); using var f = d.Factory; var (client, _) = await M3Customer(f); using (client)
        {
            var first = await Read<DemandMembership>(await client.PostAsJsonAsync("/api/v1/discovery/demands", M3Service(d)));
            await using var scope = f.Services.CreateAsyncScope(); scope.ServiceProvider.GetRequiredService<IConfiguration>()["Pilot:ServiceAreas:0:Name"] = null;
            var withdrawn = await Read<DemandMembership>(await client.PostAsync($"/api/v1/discovery/demands/{first.DemandId}/withdraw", null)); Assert.False(withdrawn.Joined); Assert.Null(withdrawn.Demand);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsync($"/api/v1/discovery/demands/{first.DemandId}/join", null)).StatusCode);
            Assert.False((await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().Interests.SingleAsync(i => i.DemandId == first.DemandId)).Active);
        }
    }
    [Theory]
    [InlineData("product")]
    [InlineData("owner")]
    public async Task Module3_inactive_product_or_owner_blocks_creation_and_join(string target)
    {
        var d = await M3Setup(); using var f = d.Factory; var (client, _) = await M3Customer(f); using (client)
        {
            var first = await Read<DemandMembership>(await client.PostAsJsonAsync("/api/v1/discovery/demands", M3Product(d)));
            await using var scope = f.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
            if (target == "product") await db.Products.Where(p => p.Id == d.Product).ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, ResourceStatus.Inactive));
            else { var owner = await (from p in db.Products join s in db.Sellers on p.SellerId equals s.Id where p.Id == d.Product select s.OwnerUserId).SingleAsync(); await db.UsersProfile.Where(p => p.Id == owner).ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, AccountStatus.Suspended)); }
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/discovery/demands", M3Product(d))).StatusCode); Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/v1/discovery/demands/{first.DemandId}/join", null)).StatusCode);
        }
    }
    [Fact]
    public async Task Module3_runtime_role_supports_new_details_and_units_without_new_privileges()
    {
        var d = await M3Setup(); using var f = d.Factory; var (runtimeCustomer, user) = await M3Customer(f); using var runtimeClient = runtimeCustomer; var role = "module3_runtime_" + Guid.NewGuid().ToString("N"); var password = DatabaseFixture.CreateTestRuntimePassword();
        await using var owner = new NpgsqlConnection(fixture.Connection); await owner.OpenAsync(); await DatabaseFixture.CreateRuntimeLoginRoleAsync(owner, role, password);
        try
        {
            var sql = (await File.ReadAllTextAsync(Path.Combine(DatabaseFixture.FindRepositoryRoot(), "scripts", "grant-runtime.sql"))).Replace(":'app_role'", "@role"); await using (var grant = new NpgsqlCommand(sql, owner)) { grant.Parameters.AddWithValue("role", role); await grant.ExecuteNonQueryAsync(); }
            var runtime = DatabaseFixture.CreateRuntimeConnectionSettings(fixture.Connection, role, password).ConnectionString; await using var db = new MarketplaceDb(new DbContextOptionsBuilder<MarketplaceDb>().UseNpgsql(runtime).Options);
            await using var scope = f.Services.CreateAsyncScope(); var config = scope.ServiceProvider.GetRequiredService<IConfiguration>(); var areas = new DiscoveryAreas(config, scope.ServiceProvider.GetRequiredService<IHostEnvironment>()); var queries = new DiscoveryQueries(db, config, TimeProvider.System, scope.ServiceProvider.GetRequiredService<IPaymentGateway>(), areas); var service = new DiscoveryService(db, areas, queries, TimeProvider.System);
            var created = await service.CreateDemand(new(user, false, "runtime-demand"), M3Product(d), default); Assert.Equal(10, created.Demand!.Membership!.Quantity); Assert.Equal(1, (await service.MyDemands(new(user, false, "runtime-mine"), new(), new(), default)).Total);
            foreach (var statement in new[] { "CREATE TABLE demand_runtime_forbidden(id int)", "DELETE FROM discovery_demands WHERE false", "DELETE FROM customer_interests WHERE false" }) { await using var limited = new NpgsqlConnection(runtime); await limited.OpenAsync(); await using var command = new NpgsqlCommand(statement, limited); Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState); }
        }
        finally { await using var cleanup = new NpgsqlCommand($"DROP OWNED BY {role};DROP ROLE {role};", owner); await cleanup.ExecuteNonQueryAsync(); }
    }

    private sealed class M3ReaderCounter : DbCommandInterceptor
    {
        public int Count;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Count); return ValueTask.FromResult(result); }
    }
    [Fact]
    public async Task Module3_mine_query_batches_are_constant_as_page_grows()
    {
        var d = await M3Setup(); using var f = d.Factory; var (client, user) = await M3Customer(f); using (client)
        {
            await Read<DemandMembership>(await client.PostAsJsonAsync("/api/v1/discovery/demands", M3Service(d)));
            var counter = new M3ReaderCounter(); await using var db = new MarketplaceDb(new DbContextOptionsBuilder<MarketplaceDb>().UseNpgsql(fixture.Connection).AddInterceptors(counter).Options);
            await using var scope = f.Services.CreateAsyncScope(); var config = scope.ServiceProvider.GetRequiredService<IConfiguration>(); var areas = new DiscoveryAreas(config, scope.ServiceProvider.GetRequiredService<IHostEnvironment>()); var queries = new DiscoveryQueries(db, config, TimeProvider.System, scope.ServiceProvider.GetRequiredService<IPaymentGateway>(), areas); var service = new DiscoveryService(db, areas, queries, TimeProvider.System);
            Assert.Single((await service.MyDemands(new(user, false, "batch"), new(), new(), default)).Items); var one = counter.Count;
            for (var i = 0; i < 9; i++) await Read<DemandMembership>(await client.PostAsJsonAsync("/api/v1/discovery/demands", M3Service(d, "Distinct public need " + i)));
            counter.Count = 0; Assert.Equal(10, (await service.MyDemands(new(user, false, "batch"), new(), new(), default)).Items.Count); Assert.Equal(one, counter.Count); Assert.InRange(counter.Count, 1, 7);
        }
    }

}

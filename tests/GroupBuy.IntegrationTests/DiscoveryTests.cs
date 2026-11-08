using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using GroupBuy.Application;
using GroupBuy.Domain;
using GroupBuy.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using System.Data.Common;
using Npgsql;

namespace GroupBuy.IntegrationTests;

public sealed class DiscoveryTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private static int sequence;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private const string Frontend = "https://groudeal-frontend.vercel.app";
    private sealed record Dataset(ApiFactory Factory, string Area, string Postal, Guid Product, Guid Seller, Guid Location, Guid Customer);
    private async Task<Dataset> Setup(bool payments = true)
    {
        var area = "synthetic-" + Guid.NewGuid().ToString("N"); var postal = (900000 + Interlocked.Increment(ref sequence)).ToString();
        var f = new ApiFactory(fixture.Connection, overrides: new()
        {
            ["Pilot:Enabled"] = "true",
            ["Pilot:ServiceAreas:0:Id"] = area,
            ["Pilot:ServiceAreas:0:Name"] = "Synthetic locality",
            ["Pilot:ServiceAreas:0:PostalCodes:0"] = postal,
            ["Pilot:DeliveryFeeMinor"] = "0",
            ["Payments:Enabled"] = payments.ToString(),
            ["Payments:Provider"] = payments ? "Sandbox" : null,
            ["Payments:SandboxEnabled"] = payments.ToString(),
            ["Payments:SandboxWebhookSecret"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            ["Cors:Origins:0"] = Frontend
        }, disableWorker: true);
        using var client = f.CreateClientWithEnvironmentVariable("Cors__Origins__0", Frontend);
        await using var scope = f.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
        var owner = Guid.NewGuid(); var customer = Guid.NewGuid();
        foreach (var id in new[] { owner, customer })
        {
            db.Users.Add(new() { Id = id, UserName = id.ToString(), NormalizedUserName = id.ToString().ToUpperInvariant() });
            db.UsersProfile.Add(new() { Id = id, DisplayName = "PRIVATE_CUSTOMER_MARKER", Status = AccountStatus.Active });
        }
        var seller = new Seller
        {
            OwnerUserId = owner,
            BusinessName = "Public Synthetic Shop",
            VerificationStatus = VerificationStatus.Approved,
            ContactEmail = "PRIVATE_CONTACT_MARKER",
            ContactPhone = "PRIVATE_PHONE_MARKER"
        };
        var location = new SellerLocation { SellerId = seller.Id, PostalCode = postal, AddressLine1 = "PRIVATE_ADDRESS_MARKER" };
        var product = new Product { SellerId = seller.Id, CategoryId = CategorySeed.All[2].Id, Name = "Rice " + area, Slug = area };
        db.AddRange(seller, location, product);
        var variant = new ProductVariant { ProductId = product.Id, SellerId = seller.Id, SKU = Guid.NewGuid().ToString("N"), VariantName = "1 kg", UnitQuantity = 1 };
        db.Add(variant); await db.SaveChangesAsync();
        return new(f, area, postal, product.Id, seller.Id, location.Id, customer);
    }

    private static async Task<T> Read<T>(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.Equal(expected, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }
    private async Task<Guid> Campaign(Dataset data, string title = "Rice group deal", CampaignStatus status = CampaignStatus.Open, int publishedMinutesAgo = 0)
    {
        await using var scope = data.Factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
        var v = new ProductVariant { ProductId = data.Product, SellerId = data.Seller, SKU = Guid.NewGuid().ToString("N"), UnitQuantity = 1, VariantName = "1 kg" };
        db.Variants.Add(v); db.Inventories.Add(new() { SellerId = data.Seller, ProductVariantId = v.Id, AvailableQuantity = 100 });
        var c = new GroupBuyCampaign
        {
            ProductVariantId = v.Id,
            SellerId = data.Seller,
            SellerLocationId = data.Location,
            Title = title,
            Status = status,
            UsesTierPricing = true,
            ServiceArea = data.Area,
            TargetQuantity = 100,
            CommittedQuantity = 100,
            MinimumReservationQuantity = 1,
            MaximumReservationQuantity = 100,
            ReferenceUnitPrice = 100,
            GroupUnitPrice = 80,
            StartsAtUtc = DateTimeOffset.UtcNow.AddHours(-2),
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(2)
        };
        db.Campaigns.Add(c);
        db.PriceTiers.AddRange(new CampaignPriceTier { CampaignId = c.Id, Quantity = 10, UnitPrice = 80 }, new CampaignPriceTier { CampaignId = c.Id, Quantity = 20, UnitPrice = 60 });
        db.AuditLogs.Add(new() { EntityId = c.Id, EntityType = nameof(GroupBuyCampaign), Action = "CampaignApproved", TimestampUtc = DateTimeOffset.UtcNow.AddMinutes(-publishedMinutesAgo) });
        await db.SaveChangesAsync(); return c.Id;
    }
    private async Task Paid(Dataset data, Guid campaign, decimal quantity, PaymentState state = PaymentState.Captured, ReservationStatus status = ReservationStatus.Active, bool refund = false)
    {
        await using var scope = data.Factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
        var reservation = new GroupBuyReservation
        {
            CampaignId = campaign,
            CustomerUserId = data.Customer,
            Quantity = quantity,
            UnitPriceSnapshot = 80,
            TotalPriceSnapshot = quantity * 80,
            Status = status,
            IdempotencyKey = Guid.NewGuid().ToString("N")
        };
        var order = new PaymentOrder
        {
            CampaignId = campaign,
            CustomerId = data.Customer,
            ReservationId = reservation.Id,
            Quantity = quantity,
            MaximumUnitPrice = 80,
            MaximumAmountMinor = (long)(quantity * 8000),
            State = state,
            CapturedAtUtc = state == PaymentState.Captured ? DateTimeOffset.UtcNow : null,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            Provider = "synthetic"
        };
        db.AddRange(reservation, order);
        if (refund) db.PaymentRefunds.Add(new() { PaymentOrderId = order.Id, AmountMinor = 100, Reason = "synthetic", State = RefundState.Pending });
        var c = await db.Campaigns.SingleAsync(c => c.Id == campaign); c.ReservedQuantity += quantity;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Anonymous_home_is_bounded_deterministic_and_exposes_no_private_fields()
    {
        var d = await Setup(); using var f = d.Factory; using var client = f.CreateClient();
        for (var i = 0; i < 8; i++) await Campaign(d, publishedMinutesAgo: i);
        var response = await client.GetAsync("/api/v1/discovery/home");
        if (response.StatusCode == HttpStatusCode.InternalServerError)
        {
            // Synthetic local fixture only: surface the query failure without weakening production error redaction.
            await using var diagnosticScope = f.Services.CreateAsyncScope();
            await diagnosticScope.ServiceProvider.GetRequiredService<IDiscovery>().Home(new(), default);
        }
        var first = await Read<DiscoveryHome>(response);
        Assert.Equal(6, first.NewDeals.Count); Assert.Equal(6, first.NearYou.Count); Assert.Equal(6, first.TrendingDeals.Count);
        Assert.True(first.Location.Supported); Assert.Contains(first.Categories, c => c.Slug == "grocery");
        Assert.DoesNotContain(first.Categories, c => c.Slug == "electronics");
        var second = await Read<DiscoveryHome>(await client.GetAsync("/api/v1/discovery/home"));
        Assert.Equal(first.NewDeals.Select(c => c.Id), second.NewDeals.Select(c => c.Id));
        var text = JsonSerializer.Serialize(first); Assert.DoesNotContain("PRIVATE_", text); Assert.DoesNotContain("AddressCiphertext", text);
    }

    [Theory]
    [InlineData(CampaignStatus.Draft)]
    [InlineData(CampaignStatus.PendingApproval)]
    [InlineData(CampaignStatus.Cancelled)]
    [InlineData(CampaignStatus.Expired)]
    [InlineData(CampaignStatus.Failed)]
    [InlineData(CampaignStatus.Successful)]
    public async Task Ineligible_campaign_status_is_not_a_deal(CampaignStatus status)
    {
        var d = await Setup(); using var f = d.Factory; using var client = f.CreateClient(); await Campaign(d, status: status);
        Assert.Empty((await Read<PageResponse<DealCard>>(await client.GetAsync("/api/v1/discovery/deals/new"))).Items);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("seller")]
    [InlineData("owner")]
    [InlineData("product")]
    [InlineData("variant")]
    [InlineData("location")]
    [InlineData("category")]
    public async Task Resource_and_time_eligibility_is_shared_across_all_discovery_paths(string resource)
    {
        var d = await Setup(); using var f = d.Factory; using var client = f.CreateClient(); var id = await Campaign(d);
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>(); var c = await db.Campaigns.SingleAsync(c => c.Id == id);
            if (resource == "expired") c.ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
            if (resource == "future") { c.StartsAtUtc = DateTimeOffset.UtcNow.AddHours(1); c.ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(3); }
            if (resource == "seller") (await db.Sellers.SingleAsync(s => s.Id == d.Seller)).VerificationStatus = VerificationStatus.Rejected;
            if (resource == "owner") { var owner = await db.Sellers.Where(s => s.Id == d.Seller).Select(s => s.OwnerUserId).SingleAsync(); (await db.UsersProfile.SingleAsync(u => u.Id == owner)).Status = AccountStatus.Suspended; }
            if (resource == "product") (await db.Products.SingleAsync(p => p.Id == d.Product)).Status = ResourceStatus.Inactive;
            if (resource == "variant") (await db.Variants.SingleAsync(v => v.Id == c.ProductVariantId)).IsActive = false;
            if (resource == "location") (await db.Locations.SingleAsync(l => l.Id == d.Location)).IsActive = false;
            // Use a private category rather than changing shared seeded taxonomy.
            if (resource == "category") { var cat = new Category { Name = "Inactive", Slug = Guid.NewGuid().ToString("N"), IsActive = false }; db.Categories.Add(cat); (await db.Products.SingleAsync(p => p.Id == d.Product)).CategoryId = cat.Id; }
            await db.SaveChangesAsync();
        }
        foreach (var route in new[] { "new", "trending", "near-you" }) Assert.Empty((await Read<PageResponse<DealCard>>(await client.GetAsync("/api/v1/discovery/deals/" + route))).Items);
        Assert.Empty((await Read<DiscoverySearch>(await client.GetAsync("/api/v1/discovery/search?q=rice"))).Deals.Items);
        Assert.Empty((await Read<CategoryDiscovery>(await client.GetAsync("/api/v1/categories/rice/discovery"))).Deals.Items);
    }

    [Fact]
    public async Task Paid_progress_prices_and_trending_exclude_failed_cancelled_expired_and_refund_orders()
    {
        var d = await Setup(); using var f = d.Factory; using var client = f.CreateClient();
        var first = await Campaign(d, "Older paid rice", publishedMinutesAgo: 20); var second = await Campaign(d, "New rice", publishedMinutesAgo: 1);
        await Paid(d, first, 5); await Paid(d, second, 1);
        await Paid(d, first, 2, PaymentState.Failed); await Paid(d, first, 2, status: ReservationStatus.Cancelled);
        await Paid(d, first, 2, status: ReservationStatus.Expired); await Paid(d, first, 2, refund: true);
        await Paid(d, first, 2, PaymentState.Refunded); await Paid(d, first, 2, PaymentState.Authorized);
        var trending = await Read<PageResponse<DealCard>>(await client.GetAsync("/api/v1/discovery/deals/trending"));
        Assert.Equal(first, trending.Items[0].Id); var card = trending.Items[0];
        Assert.Equal(5, card.PaidQuantity); Assert.Equal(80, card.EffectiveUnitPrice); Assert.Equal(20, card.DiscountPercent);
        Assert.Equal(50, card.ProgressPercent); Assert.Equal(10, card.NextTierQuantity); Assert.Equal(80, card.NextTierUnitPrice);
        var newest = await Read<PageResponse<DealCard>>(await client.GetAsync("/api/v1/discovery/deals/new")); Assert.Equal(second, newest.Items[0].Id);
        await Paid(d, first, 15);
        card = (await Read<PageResponse<DealCard>>(await client.GetAsync("/api/v1/discovery/deals/trending"))).Items[0];
        Assert.Equal(60, card.EffectiveUnitPrice); Assert.Null(card.NextTierQuantity); Assert.Equal(100, card.ProgressPercent);
    }

    [Fact]
    public async Task Disabled_payments_do_not_make_products_purchasable()
    {
        var d = await Setup(payments: false); using var f = d.Factory; using var client = f.CreateClient(); await Campaign(d);
        var response = await Read<DiscoverySearch>(await client.GetAsync("/api/v1/discovery/search?q=rice"));
        Assert.Empty(response.Deals.Items); Assert.False(Assert.Single(response.Products.Items).Purchasable);
        Assert.False(response.NoResults); Assert.NotNull(response.CreateDemand);
    }

    [Fact]
    public async Task Area_selection_is_configured_normalized_and_unsupported_areas_are_empty()
    {
        var d = await Setup(); using var f = d.Factory; using var client = f.CreateClient(); await Campaign(d);
        Assert.True((await Read<DiscoveryLocation>(await client.GetAsync("/api/v1/discovery/service-area?postalCode=%20" + d.Postal + "%20"))).Supported);
        var unsupported = await Read<DiscoveryHome>(await client.GetAsync("/api/v1/discovery/home?postalCode=899999"));
        Assert.Equal("unsupported", unsupported.Location.State); Assert.Empty(unsupported.NewDeals); Assert.Empty(unsupported.Categories);
        Assert.Empty((await Read<PageResponse<DealCard>>(await client.GetAsync("/api/v1/discovery/deals/near-you?serviceArea=wrong"))).Items);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/discovery/service-area?postalCode=123")).StatusCode);
    }

    [Fact]
    public async Task Search_distinguishes_types_normalizes_query_and_never_creates_demand_automatically()
    {
        var d = await Setup(); using var f = d.Factory; using var client = f.CreateClient(); await Campaign(d);
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>(); db.Interests.Add(new() { CustomerId = d.Customer, ProductId = d.Product, ServiceArea = d.Area, DesiredQuantity = 2 }); await db.SaveChangesAsync();
        }
        var found = await Read<DiscoverySearch>(await client.GetAsync("/api/v1/discovery/search?q=%20RiCe%20"));
        Assert.Equal("RiCe", found.Query); Assert.Equal("deal", Assert.Single(found.Deals.Items).Type); Assert.Equal("demand", Assert.Single(found.Demands.Items).Type);
        Assert.Equal("product", Assert.Single(found.Products.Items).Type); Assert.Equal("category", Assert.Single(found.Categories.Items).Type);
        var empty = await Read<DiscoverySearch>(await client.GetAsync("/api/v1/discovery/search?q=unmet%20need"));
        Assert.True(empty.NoResults); Assert.Equal("unmet need", empty.CreateDemand!.Query); Assert.False(empty.CreateDemand.CreatesAutomatically);
        await using var check = f.Services.CreateAsyncScope(); Assert.Equal(0, await check.ServiceProvider.GetRequiredService<MarketplaceDb>().DiscoveryDemands.CountAsync(demand => demand.ServiceArea == d.Area));
    }

    [Theory]
    [InlineData("%25_%27%5C")]
    [InlineData("%27%20OR%201%3D1--")]
    public async Task Search_special_characters_are_literal_safe_parameters(string encoded)
    {
        var d = await Setup(); using var f = d.Factory; using var client = f.CreateClient(); await Campaign(d);
        var result = await Read<DiscoverySearch>(await client.GetAsync("/api/v1/discovery/search?q=" + encoded)); Assert.True(result.NoResults);
    }

    [Theory]
    [InlineData("/api/v1/discovery/search?q=r")]
    [InlineData("/api/v1/discovery/search?q=rice&page=0")]
    [InlineData("/api/v1/discovery/search?q=rice&pageSize=101")]
    [InlineData("/api/v1/discovery/deals/new?pageSize=0")]
    [InlineData("/api/v1/discovery/demands/popular?page=-1")]
    [InlineData("/api/v1/discovery/search/suggestions?q=r")]
    [InlineData("/api/v1/discovery/search/suggestions?q=rice&limit=11")]
    public async Task Invalid_search_pagination_and_suggestion_limits_are_rejected(string route)
    {
        var d = await Setup(); using var f = d.Factory; using var client = f.CreateClient(); Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(route)).StatusCode);
    }

    [Fact]
    public async Task Suggestions_and_see_all_pages_are_bounded_and_stable()
    {
        var d = await Setup(); using var f = d.Factory; using var client = f.CreateClient();
        for (var i = 0; i < 4; i++) await Campaign(d, "Rice " + i);
        var suggestions = await Read<DiscoverySuggestions>(await client.GetAsync("/api/v1/discovery/search/suggestions?q=ric&limit=2"));
        Assert.Equal(2, suggestions.Items.Count); Assert.DoesNotContain("PRIVATE_", JsonSerializer.Serialize(suggestions));
        Assert.Equal(suggestions.Items, (await Read<DiscoverySuggestions>(await client.GetAsync("/api/v1/discovery/search/suggestions?q=ric&limit=2"))).Items);
        var first = await Read<PageResponse<DealCard>>(await client.GetAsync("/api/v1/discovery/deals/new?pageSize=2"));
        var second = await Read<PageResponse<DealCard>>(await client.GetAsync("/api/v1/discovery/deals/new?page=2&pageSize=2"));
        Assert.Equal(4, first.Total); Assert.Empty(first.Items.Select(c => c.Id).Intersect(second.Items.Select(c => c.Id)));
    }

    [Theory]
    [InlineData("electronics")]
    [InlineData("unknown-category")]
    public async Task Inactive_or_unknown_category_is_not_discoverable(string slug)
    {
        var d = await Setup(); using var f = d.Factory; using var client = f.CreateClient(); Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/categories/" + slug + "/discovery")).StatusCode);
    }

    private static async Task Login(HttpClient client)
    {
        var email = Guid.NewGuid().ToString("N") + "@example.invalid";
        await Read<UserResponse>(await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(email, "Synthetic!Password123", "Private customer")), HttpStatusCode.Created);
        var token = await Read<TokenResponse>(await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, "Synthetic!Password123")));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
    }

    [Fact]
    public async Task Free_text_demand_creation_is_explicit_unique_private_and_survives_new_request_scopes()
    {
        var d = await Setup(); using var f = d.Factory; using var client = f.CreateClient();
        var request = new CreateDemandRequest("  Need   oats  ", d.Area);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/discovery/demands", request)).StatusCode);
        await Login(client);
        var created = await Read<DemandMembership>(await client.PostAsJsonAsync("/api/v1/discovery/demands", request));
        Assert.True((await Read<DemandMembership>(await client.GetAsync($"/api/v1/discovery/demands/{created.DemandId}/membership"))).Joined);
        var again = await Read<DemandMembership>(await client.PostAsJsonAsync("/api/v1/discovery/demands", request with { Title = "NEED OATS" })); Assert.Equal(created.DemandId, again.DemandId);
        await Read<DemandMembership>(await client.PostAsync($"/api/v1/discovery/demands/{created.DemandId}/join", null));
        var popular = await Read<PageResponse<DemandCard>>(await client.GetAsync("/api/v1/discovery/demands/popular"));
        Assert.Equal(1, Assert.Single(popular.Items).InterestedCustomers); Assert.True(popular.Items[0].IsFree); Assert.False(popular.Items[0].GuaranteesFulfillment);
        Assert.DoesNotContain("Private customer", JsonSerializer.Serialize(popular));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/api/v1/admin/discovery/demands/{created.DemandId}/close", null)).StatusCode);
        using var second = f.CreateClient(); await Login(second);
        Assert.False((await Read<DemandMembership>(await second.GetAsync($"/api/v1/discovery/demands/{created.DemandId}/membership"))).Joined);
        await Read<DemandMembership>(await second.PostAsync($"/api/v1/discovery/demands/{created.DemandId}/join", null));
        Assert.Equal(2, Assert.Single((await Read<PageResponse<DemandCard>>(await client.GetAsync("/api/v1/discovery/demands/popular"))).Items).InterestedCustomers);
        await Read<DemandMembership>(await client.PostAsync($"/api/v1/discovery/demands/{created.DemandId}/withdraw", null));
        Assert.False((await Read<DemandMembership>(await client.GetAsync($"/api/v1/discovery/demands/{created.DemandId}/membership"))).Joined);
        Assert.Equal(1, Assert.Single((await Read<PageResponse<DemandCard>>(await client.GetAsync("/api/v1/discovery/demands/popular"))).Items).InterestedCustomers);
    }

    [Fact]
    public async Task CORS_stays_restricted_for_public_discovery()
    {
        var d = await Setup(); using var f = d.Factory; using var client = f.CreateClient();
        foreach (var origin in new[] { Frontend, "https://unapproved.invalid" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/discovery/home"); request.Headers.Add("Origin", origin);
            using var response = await client.SendAsync(request); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(origin == Frontend, response.Headers.Contains("Access-Control-Allow-Origin"));
        }
    }

    [Fact]
    public async Task Category_detail_uses_actual_children_and_empty_active_metadata_is_not_inventory()
    {
        var d = await Setup(); using var f = d.Factory; using var client = f.CreateClient(); await Campaign(d);
        var root = await Read<CategoryDiscovery>(await client.GetAsync("/api/v1/categories/grocery/discovery"));
        Assert.True(root.Category.IsDiscoverable); Assert.Contains(root.Subcategories, c => c.Slug == "rice"); Assert.Single(root.Deals.Items);
        var empty = await Read<CategoryDiscovery>(await client.GetAsync("/api/v1/categories/wheat-flour/discovery"));
        Assert.False(empty.Category.IsDiscoverable); Assert.Empty(empty.Deals.Items); Assert.Empty(empty.Demands.Items);
    }

    [Fact]
    public async Task Concurrent_canonical_creation_and_duplicate_join_cannot_inflate_free_demand()
    {
        var d = await Setup(); using var f = d.Factory; using var first = f.CreateClient(); using var second = f.CreateClient();
        await Login(first); await Login(second);
        var request = new CreateDemandRequest("Community oats", d.Area);
        var replies = await Task.WhenAll(first.PostAsJsonAsync("/api/v1/discovery/demands", request), second.PostAsJsonAsync("/api/v1/discovery/demands", request));
        var a = await Read<DemandMembership>(replies[0]); var b = await Read<DemandMembership>(replies[1]); Assert.Equal(a.DemandId, b.DemandId);
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => first.PostAsync($"/api/v1/discovery/demands/{a.DemandId}/join", null)));
        var card = Assert.Single((await Read<PageResponse<DemandCard>>(await first.GetAsync("/api/v1/discovery/demands/popular"))).Items);
        Assert.Equal(2, card.InterestedCustomers);
        await using var scope = f.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
        Assert.Equal(1, await db.DiscoveryDemands.CountAsync(demand => demand.ServiceArea == d.Area));
        Assert.Equal(2, await db.Interests.CountAsync(i => i.DemandId == a.DemandId));
    }

    [Fact]
    public async Task Closed_demands_disappear_prevent_rejoins_and_allow_withdrawal()
    {
        var d = await Setup(); using var f = d.Factory; using var client = f.CreateClient(); await Login(client);
        var request = new CreateDemandRequest("Local pulses", d.Area);
        var created = await Read<DemandMembership>(await client.PostAsJsonAsync("/api/v1/discovery/demands", request));
        await using (var scope = f.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IDiscovery>().CloseDemand(new(d.Customer, true, "synthetic-admin"), created.DemandId, default);
        Assert.Empty((await Read<PageResponse<DemandCard>>(await client.GetAsync("/api/v1/discovery/demands/popular"))).Items);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/v1/discovery/demands/{created.DemandId}/join", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/v1/discovery/demands", request)).StatusCode);
        Assert.False((await Read<DemandMembership>(await client.PostAsync($"/api/v1/discovery/demands/{created.DemandId}/withdraw", null))).Joined);
    }

    [Fact]
    public async Task Product_demand_uniqueness_inactive_interest_and_suspended_accounts_are_respected()
    {
        var d = await Setup(); using var f = d.Factory; using var client = f.CreateClient();
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
            db.Interests.Add(new() { CustomerId = d.Customer, ProductId = d.Product, ServiceArea = d.Area, DesiredQuantity = 1 }); await db.SaveChangesAsync();
            db.Interests.Add(new() { CustomerId = d.Customer, ProductId = d.Product, ServiceArea = d.Area, DesiredQuantity = 1 });
            Assert.Equal("23505", (await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync())).InnerException is PostgresException e ? e.SqlState : null);
        }
        Assert.Equal(1, Assert.Single((await Read<PageResponse<DemandCard>>(await client.GetAsync("/api/v1/discovery/demands/popular"))).Items).InterestedCustomers);
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>(); (await db.UsersProfile.SingleAsync(u => u.Id == d.Customer)).Status = AccountStatus.Suspended; await db.SaveChangesAsync();
        }
        Assert.Empty((await Read<PageResponse<DemandCard>>(await client.GetAsync("/api/v1/discovery/demands/popular"))).Items);
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>(); (await db.UsersProfile.SingleAsync(u => u.Id == d.Customer)).Status = AccountStatus.Active;
            (await db.Interests.SingleAsync(i => i.CustomerId == d.Customer && i.ProductId == d.Product)).Active = false; await db.SaveChangesAsync();
        }
        Assert.Empty((await Read<PageResponse<DemandCard>>(await client.GetAsync("/api/v1/discovery/demands/popular"))).Items);
    }

    [Theory]
    [InlineData("call 9999999999", false)]
    [InlineData("contact@example.invalid", false)]
    [InlineData("<b>rice</b>", false)]
    [InlineData("Rice", true)]
    public async Task Demand_creation_validates_public_content_and_area(string title, bool wrongArea)
    {
        var d = await Setup(); using var f = d.Factory; using var client = f.CreateClient(); await Login(client);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/discovery/demands", new CreateDemandRequest(title, wrongArea ? "unsupported" : d.Area))).StatusCode);
    }

    [Fact]
    public async Task Missing_service_area_fails_closed_without_changing_health()
    {
        using var f = new ApiFactory(fixture.Connection, disableWorker: true); using var client = f.CreateClient();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/v1/discovery/home")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/v1/discovery/service-area")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
    }

    private sealed class QueryCounter : DbCommandInterceptor
    {
        public List<(string Sql, NpgsqlParameter[] Parameters)> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add((command.CommandText, command.Parameters.Cast<NpgsqlParameter>().Select(p => p.Clone()).ToArray()));
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task Home_uses_constant_bounded_SQL_batches_and_Postgres_plan_has_a_limit()
    {
        var d = await Setup(); using var f = d.Factory; await Campaign(d);
        var counter = new QueryCounter();
        await using var db = new MarketplaceDb(new DbContextOptionsBuilder<MarketplaceDb>().UseNpgsql(fixture.Connection).AddInterceptors(counter).Options);
        await using var scope = f.Services.CreateAsyncScope(); var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var areas = new DiscoveryAreas(config, scope.ServiceProvider.GetRequiredService<IHostEnvironment>());
        var queries = new DiscoveryQueries(db, config, TimeProvider.System, scope.ServiceProvider.GetRequiredService<IPaymentGateway>(), areas);
        var service = new DiscoveryService(db, areas, queries, TimeProvider.System);
        await service.Home(new(), default); var smallCount = counter.Commands.Count; counter.Commands.Clear();
        for (var i = 0; i < 7; i++) await Campaign(d);
        var home = await service.Home(new(), default);
        Assert.Equal(6, home.NewDeals.Count); Assert.Equal(smallCount, counter.Commands.Count); Assert.InRange(counter.Commands.Count, 1, 10);
        var selected = counter.Commands.First(c => c.Sql.Contains("group_buy_campaigns") && c.Sql.Contains("ORDER BY") && c.Sql.Contains("LIMIT"));
        await using var connection = new NpgsqlConnection(fixture.Connection); await connection.OpenAsync();
        await using var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + selected.Sql, connection);
        explain.Parameters.AddRange(selected.Parameters);
        var plan = (await explain.ExecuteScalarAsync())!.ToString()!; Assert.Contains("Limit", plan);
        await File.WriteAllTextAsync(Path.Combine(DatabaseFixture.FindRepositoryRoot(), ".local", "discovery-query-plan.json"), plan);
    }

    [Fact]
    public async Task Runtime_role_can_create_and_join_demand_without_DDL_or_DELETE_permission()
    {
        var d = await Setup(); using var f = d.Factory; var role = "discovery_runtime_" + Guid.NewGuid().ToString("N"); var password = DatabaseFixture.CreateTestRuntimePassword();
        await using var owner = new NpgsqlConnection(fixture.Connection); await owner.OpenAsync();
        await DatabaseFixture.CreateRuntimeLoginRoleAsync(owner, role, password);
        try
        {
            var sql = (await File.ReadAllTextAsync(Path.Combine(DatabaseFixture.FindRepositoryRoot(), "scripts", "grant-runtime.sql"))).Replace(":'app_role'", "@role");
            await using (var grant = new NpgsqlCommand(sql, owner)) { grant.Parameters.AddWithValue("role", role); await grant.ExecuteNonQueryAsync(); }
            var runtime = DatabaseFixture.CreateRuntimeConnectionSettings(fixture.Connection, role, password).ConnectionString;
            await using var db = new MarketplaceDb(new DbContextOptionsBuilder<MarketplaceDb>().UseNpgsql(runtime).Options);
            await using var scope = f.Services.CreateAsyncScope(); var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
            var areas = new DiscoveryAreas(config, scope.ServiceProvider.GetRequiredService<IHostEnvironment>());
            var queries = new DiscoveryQueries(db, config, TimeProvider.System, scope.ServiceProvider.GetRequiredService<IPaymentGateway>(), areas);
            var service = new DiscoveryService(db, areas, queries, TimeProvider.System);
            var created = await service.CreateDemand(new(d.Customer, false, "synthetic"), new("Runtime oats", d.Area), default); Assert.True(created.Joined);
            Assert.Equal(1, (await service.Demands(new(), new(), default)).Total);
            foreach (var statement in new[] { "DELETE FROM discovery_demands WHERE false", "CREATE TABLE discovery_forbidden(id int)" })
            {
                await using var limited = new NpgsqlConnection(runtime); await limited.OpenAsync(); await using var command = new NpgsqlCommand(statement, limited);
                Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
            }
        }
        finally { await using var cleanup = new NpgsqlCommand($"DROP OWNED BY {role}; DROP ROLE {role};", owner); await cleanup.ExecuteNonQueryAsync(); }
    }

    [Fact]
    public async Task Upgrade_preserves_legacy_product_interest_and_is_forward_only()
    {
        var name = "discovery_upgrade_" + Guid.NewGuid().ToString("N");
        await using var owner = new NpgsqlConnection(fixture.Connection); await owner.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {name}", owner)) await create.ExecuteNonQueryAsync();
        try
        {
            var connection = new NpgsqlConnectionStringBuilder(fixture.Connection) { Database = name, Pooling = false }.ConnectionString;
            await using var db = new MarketplaceDb(new DbContextOptionsBuilder<MarketplaceDb>().UseNpgsql(connection).Options);
            var migrator = db.GetService<IMigrator>(); await migrator.MigrateAsync("20261106155504_PersistDataProtectionKeys");
            var user = Guid.NewGuid(); db.Users.Add(new() { Id = user, UserName = user.ToString() }); db.UsersProfile.Add(new() { Id = user, DisplayName = "Synthetic" });
            var seller = new Seller { OwnerUserId = user }; db.Sellers.Add(seller);
            var product = new Product { SellerId = seller.Id, CategoryId = CategorySeed.All[2].Id, Name = "Legacy rice", Slug = "legacy" }; db.Products.Add(product); await db.SaveChangesAsync();
            var interest = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO customer_interests (\"Id\",\"CustomerId\",\"ProductId\",\"ServiceArea\",\"DesiredQuantity\",\"Active\",\"CreatedAt\",\"UpdatedAt\") VALUES ({interest},{user},{product.Id},{"synthetic"},{2m},{true},{now},{now})");
            await migrator.MigrateAsync("20261107060508_HomeDiscoveryDemands");
            var preserved = await db.Interests.AsNoTracking().Where(i => i.Id == interest).Select(i => new { i.ProductId, i.DemandId, i.DesiredQuantity }).SingleAsync(); Assert.Equal(product.Id, preserved.ProductId); Assert.Null(preserved.DemandId); Assert.Equal(2, preserved.DesiredQuantity);
            await migrator.MigrateAsync();
            var upgraded = await db.Interests.AsNoTracking().SingleAsync(i => i.Id == interest); Assert.Equal(product.Id, upgraded.ProductId); Assert.NotNull(upgraded.DemandId); Assert.Equal(2, upgraded.DesiredQuantity);
            await Assert.ThrowsAsync<NotSupportedException>(() => migrator.MigrateAsync("20261106155504_PersistDataProtectionKeys"));
            Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync());
        }
        finally { await using var drop = new NpgsqlCommand($"DROP DATABASE {name} WITH (FORCE)", owner); await drop.ExecuteNonQueryAsync(); }
    }
}

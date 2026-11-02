using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GroupBuy.Application;
using GroupBuy.Domain;
using GroupBuy.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GroupBuy.IntegrationTests;

public partial class BackendTests
{
    private static async Task<ProductResponse> UnusedProduct(HttpClient seller) => await Read<ProductResponse>(await seller.PostAsJsonAsync("/api/v1/seller/products", new ProductRequest(CategorySeed.All[1].Id, "Status product", Guid.NewGuid().ToString("N"), "Private catalog test", ProductType.Physical, Guid.NewGuid().ToString("N"), "One piece", UnitOfMeasure.Piece, 1, "{}")));

    [Fact]
    public async Task Private_profile_is_available_during_onboarding_and_never_added_to_public_dtos()
    {
        using var f = new ApiFactory(fixture.Connection); using var owner = f.CreateClient(); using var anonymous = f.CreateClient(); using var other = f.CreateClient(); using var admin = f.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/seller/profile")).StatusCode);
        var user = await Register(owner, "private-profile"); await Login(owner, user);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync("/api/v1/seller/profile")).StatusCode);
        var request = SellerRequest("Private shop") with { Location = new("Private road", "Suite 2", "Area", "Pune", "MH", "411001", 18.52m, 73.85m) };
        var seller = await Read<SellerResponse>(await owner.PostAsJsonAsync("/api/v1/seller/profile", request));
        var profile = await Read<SellerPrivateProfileResponse>(await owner.GetAsync("/api/v1/seller/profile"));
        Assert.Equal(seller.Id, profile.Id); Assert.Equal(VerificationStatus.Pending, profile.VerificationStatus); Assert.Equal(request.ContactEmail, profile.ContactEmail); Assert.Equal(request.ContactPhone, profile.ContactPhone); Assert.Equal(request.FssaiRegistrationNumber, profile.FssaiRegistrationNumber); Assert.Null(profile.ReviewedAt);
        var location = Assert.Single(profile.PickupLocations); Assert.Equal(18.52m, location.Latitude); Assert.Equal(73.85m, location.Longitude);
        var stranger = await Register(other, "profile-stranger"); await Login(other, stranger);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync("/api/v1/seller/profile")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/v1/sellers/{seller.Id}")).StatusCode);
        var operatorUser = await Register(admin, "profile-admin"); await Role(f, operatorUser.Id, "Admin"); await Login(admin, operatorUser);
        await Read<SellerReviewResponse>(await admin.PostAsJsonAsync($"/api/v1/admin/sellers/{seller.Id}/approve", new ReviewRequest(null)));
        profile = await Read<SellerPrivateProfileResponse>(await owner.GetAsync("/api/v1/seller/profile")); Assert.Equal(VerificationStatus.Approved, profile.VerificationStatus); Assert.NotNull(profile.ReviewedAt);
        var publicResponse = await anonymous.GetAsync($"/api/v1/sellers/{seller.Id}"); Assert.Equal(HttpStatusCode.OK, publicResponse.StatusCode);
        using var json = JsonDocument.Parse(await publicResponse.Content.ReadAsStringAsync());
        foreach (var field in new[] { "contactEmail", "contactPhone", "fssaiRegistrationNumber", "ownerUserId", "reviewerUserId", "reviewedAt" }) Assert.False(json.RootElement.TryGetProperty(field, out _));
        Assert.False(json.RootElement.GetProperty("pickupLocations")[0].TryGetProperty("latitude", out _));
        // This token still has Customer only; private read must continue to work after rejection.
        await Read<SellerResponse>(await owner.PutAsJsonAsync("/api/v1/seller/profile", request));
        await Read<SellerReviewResponse>(await admin.PostAsJsonAsync($"/api/v1/admin/sellers/{seller.Id}/reject", new ReviewRequest("Review again")));
        Assert.Equal(VerificationStatus.Rejected, (await Read<SellerPrivateProfileResponse>(await owner.GetAsync("/api/v1/seller/profile"))).VerificationStatus);
    }

    [Fact]
    public async Task Product_status_is_reversible_idempotent_and_inactive_products_remain_owner_visible()
    {
        var x = await Setup(); using var f = x.Factory; using var admin = x.Admin; using var seller = x.Seller; using var customer = x.Customer;
        var p = await UnusedProduct(seller);
        for (var i = 0; i < 2; i++) Assert.Equal(ResourceStatus.Inactive, (await Read<SellerProductResponse>(await seller.PutAsJsonAsync($"/api/v1/seller/products/{p.Id}/status", new ProductStatusRequest(ResourceStatus.Inactive)))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await customer.GetAsync($"/api/v1/products/{p.Id}")).StatusCode);
        var owned = await Read<PageResponse<SellerProductResponse>>(await seller.GetAsync("/api/v1/seller/products?status=Inactive")); Assert.Equal(p.Id, Assert.Single(owned.Items).Id);
        Assert.Equal(ResourceStatus.Active, (await Read<SellerProductResponse>(await seller.PutAsJsonAsync($"/api/v1/seller/products/{p.Id}/status", new ProductStatusRequest(ResourceStatus.Active)))).Status);
        Assert.Equal(HttpStatusCode.OK, (await customer.GetAsync($"/api/v1/products/{p.Id}")).StatusCode);
        await using var scope = f.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
        Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.EntityId == p.Id && a.Action == "ProductDeactivated")); Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.EntityId == p.Id && a.Action == "ProductActivated"));
    }

    [Fact]
    public async Task Invalid_product_status_requests_and_ineligible_activation_are_rejected()
    {
        var x = await Setup(); using var f = x.Factory; using var admin = x.Admin; using var seller = x.Seller; using var customer = x.Customer;
        var p = await UnusedProduct(seller); var path = $"/api/v1/seller/products/{p.Id}/status";
        foreach (var body in new object[] { new { status = "Deleted" }, new { status = 999 }, new { status = (string?)null }, new { }, new { status = "Inactive", sellerId = Guid.NewGuid() } })
            Assert.Equal(HttpStatusCode.BadRequest, (await seller.PutAsJsonAsync(path, body)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await seller.GetAsync("/api/v1/seller/products?status=999")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await seller.GetAsync("/api/v1/seller/products?pageSize=101")).StatusCode);
        await Read<SellerProductResponse>(await seller.PutAsJsonAsync(path, new ProductStatusRequest(ResourceStatus.Inactive)));
        await using (var scope = f.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().Products.Where(row => row.Id == p.Id).ExecuteUpdateAsync(s => s.SetProperty(row => row.CategoryId, CategorySeed.All[7].Id));
        Assert.Equal(HttpStatusCode.Conflict, (await seller.PutAsJsonAsync(path, new ProductStatusRequest(ResourceStatus.Active))).StatusCode);
        Assert.Equal(ResourceStatus.Inactive, Assert.Single((await Read<PageResponse<SellerProductResponse>>(await seller.GetAsync("/api/v1/seller/products?status=Inactive"))).Items).Status);
    }

    [Theory]
    [InlineData(CampaignStatus.PendingApproval)] [InlineData(CampaignStatus.Scheduled)] [InlineData(CampaignStatus.Open)]
    [InlineData(CampaignStatus.TargetReached)] [InlineData(CampaignStatus.AwaitingSellerConfirmation)] [InlineData(CampaignStatus.Confirmed)] [InlineData(CampaignStatus.FulfillmentInProgress)]
    public async Task Product_deactivation_rejects_live_campaigns_even_without_legacy_stock_holds(CampaignStatus status)
    {
        var x = await Setup(); using var f = x.Factory; using var admin = x.Admin; using var seller = x.Seller; using var customer = x.Customer;
        var campaign = await StoredCampaign(f, x.Campaign);
        await using (var scope = f.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().Campaigns.Where(c => c.Id == x.Campaign).ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, status).SetProperty(c => c.CommittedQuantity, 0));
        var product = (await Read<PageResponse<SellerProductResponse>>(await seller.GetAsync("/api/v1/seller/products"))).Items.Single(p => p.Variants.Any(v => v.Id == campaign.ProductVariantId));
        Assert.Equal(HttpStatusCode.Conflict, (await seller.PutAsJsonAsync($"/api/v1/seller/products/{product.Id}/status", new ProductStatusRequest(ResourceStatus.Inactive))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await customer.GetAsync($"/api/v1/products/{product.Id}")).StatusCode);
    }

    [Fact]
    public async Task Product_status_cannot_bypass_outstanding_holds_pickups_or_current_seller_approval()
    {
        var x = await Setup(2); using var f = x.Factory; using var admin = x.Admin; using var seller = x.Seller; using var customer = x.Customer;
        await ConfirmAndReady(seller, customer, x.Campaign);
        var c = await StoredCampaign(f, x.Campaign);
        var product = (await Read<PageResponse<SellerProductResponse>>(await seller.GetAsync("/api/v1/seller/products"))).Items.Single(p => p.Variants.Any(v => v.Id == c.ProductVariantId));
        var path = $"/api/v1/seller/products/{product.Id}/status";
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
            // Fail closed on inconsistent legacy/operator data; terminal status alone cannot erase backing.
            await db.Campaigns.Where(row => row.Id == c.Id).ExecuteUpdateAsync(s => s.SetProperty(row => row.Status, CampaignStatus.Failed));
        }
        Assert.Equal(HttpStatusCode.Conflict, (await seller.PutAsJsonAsync(path, new ProductStatusRequest(ResourceStatus.Inactive))).StatusCode);
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
            await db.Campaigns.Where(row => row.Id == c.Id).ExecuteUpdateAsync(s => s.SetProperty(row => row.CommittedQuantity, 0));
            await db.Reservations.Where(row => row.CampaignId == c.Id).ExecuteUpdateAsync(s => s.SetProperty(row => row.Status, ReservationStatus.Expired));
        }
        Assert.Equal(HttpStatusCode.Conflict, (await seller.PutAsJsonAsync(path, new ProductStatusRequest(ResourceStatus.Inactive))).StatusCode);
        await Read<SellerResponse>(await seller.PutAsJsonAsync("/api/v1/seller/profile", SellerRequest("Approval reset")));
        Assert.Equal(HttpStatusCode.Forbidden, (await seller.PutAsJsonAsync(path, new ProductStatusRequest(ResourceStatus.Inactive))).StatusCode);
    }

    [Fact]
    public async Task Product_deactivation_and_campaign_submission_share_the_inventory_lock_boundary()
    {
        var x = await Setup(); using var f = x.Factory; using var admin = x.Admin; using var seller = x.Seller; using var customer = x.Customer;
        var p = await UnusedProduct(seller); var variant = p.Variants[0].Id;
        await Read<InventoryResponse>(await seller.PutAsJsonAsync($"/api/v1/seller/inventory/{variant}", new InventoryRequest(2, 0)));
        var source = await StoredCampaign(f, x.Campaign);
        var draft = await Read<CampaignResponse>(await seller.PostAsJsonAsync("/api/v1/seller/group-buys", new CampaignRequest(variant, source.SellerLocationId, "Status race", "", 100, 80, 2, 1, 2, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1))));
        var results = await Task.WhenAll(seller.PutAsJsonAsync($"/api/v1/seller/products/{p.Id}/status", new ProductStatusRequest(ResourceStatus.Inactive)), seller.PostAsync($"/api/v1/seller/group-buys/{draft.Id}/submit", null));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK); Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
        var current = await StoredCampaign(f, draft.Id);
        await using var scope = f.Services.CreateAsyncScope(); var stored = await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().Products.SingleAsync(row => row.Id == p.Id);
        Assert.Equal(stored.Status == ResourceStatus.Active ? CampaignStatus.PendingApproval : CampaignStatus.Draft, current.Status);
        Assert.Equal(stored.Status == ResourceStatus.Active ? 2 : 0, current.CommittedQuantity);
    }

    [Fact]
    public async Task Cross_seller_product_and_pickup_access_is_denied_and_pickup_secrets_are_never_serialized()
    {
        var x = await Setup(2); using var f = x.Factory; using var admin = x.Admin; using var seller = x.Seller; using var customer = x.Customer; using var anonymous = f.CreateClient();
        var p = await UnusedProduct(seller); var pickup = await ConfirmAndReady(seller, customer, x.Campaign);
        var code = await Read<IssuedPickupCode>(await customer.PostAsync($"/api/v1/me/pickups/{pickup.Id}/code", null));
        var path = $"/api/v1/seller/pickups/{pickup.Id}";
        var response = await seller.GetAsync(path); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(); using var json = JsonDocument.Parse(body);
        var allowed = new[] { "id", "reservationId", "campaignId", "status", "quantity", "totalPriceSnapshot", "instructions", "deadlineUtc", "paymentPolicy", "nextAction" };
        Assert.Equal(allowed.Order(), json.RootElement.EnumerateObject().Select(field => field.Name).Order());
        Assert.Equal(pickup.Id, JsonSerializer.Deserialize<PickupDetailResponse>(body, Json)!.Id); Assert.DoesNotContain(code.Code, body);
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().Pickups.SingleAsync(row => row.Id == pickup.Id);
            Assert.NotNull(stored.CodeHash); Assert.DoesNotContain(stored.CodeHash, body); Assert.DoesNotContain(x.CustomerId.ToString(), body);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode); Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await seller.GetAsync($"/api/v1/seller/pickups/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync($"/api/v1/seller/products/{p.Id}/status", new ProductStatusRequest(ResourceStatus.Inactive))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.PutAsJsonAsync($"/api/v1/seller/products/{p.Id}/status", new ProductStatusRequest(ResourceStatus.Inactive))).StatusCode);
        using var other = f.CreateClient(); var user = await Register(other, "seller-management-isolation"); await Login(other, user);
        var profile = await Read<SellerResponse>(await other.PostAsJsonAsync("/api/v1/seller/profile", SellerRequest("Other private shop")));
        await Read<SellerReviewResponse>(await admin.PostAsJsonAsync($"/api/v1/admin/sellers/{profile.Id}/approve", new ReviewRequest(null))); await Login(other, user);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync($"/api/v1/seller/products/{p.Id}/status", new ProductStatusRequest(ResourceStatus.Inactive))).StatusCode);
        Assert.DoesNotContain((await Read<PageResponse<SellerProductResponse>>(await other.GetAsync("/api/v1/seller/products"))).Items, row => row.Id == p.Id);
        Assert.Equal(profile.Id, (await Read<SellerPrivateProfileResponse>(await other.GetAsync("/api/v1/seller/profile"))).Id);
    }
}

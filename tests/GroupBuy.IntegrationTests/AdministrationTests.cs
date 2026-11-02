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
    private static readonly string[] AdminReadPaths = ["/dashboard/summary", "/sellers", "/sellers/00000000-0000-0000-0000-000000000001", "/campaigns", "/campaigns/00000000-0000-0000-0000-000000000001", "/categories", "/products", "/reservations", "/pickups", "/audit-logs", "/operations/health"];
    [Fact]
    public async Task Administrative_reads_reject_guests_customers_and_sellers_and_service_forgery()
    {
        var x = await Setup(); using var f = x.Factory; using var admin = x.Admin; using var seller = x.Seller; using var customer = x.Customer; using var guest = f.CreateClient();
        foreach (var path in AdminReadPaths)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync("/api/v1/admin" + path)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync("/api/v1/admin" + path)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await seller.GetAsync("/api/v1/admin" + path)).StatusCode);
        }
        await using var scope = f.Services.CreateAsyncScope();
        var error = await Assert.ThrowsAsync<BusinessException>(() => scope.ServiceProvider.GetRequiredService<IAdministration>().AdminSummary(new(x.CustomerId, false, "unauthorized"), default));
        Assert.Equal(403, error.StatusCode);
    }
    [Fact]
    public async Task Admin_summary_and_governance_reads_include_real_inactive_private_records_without_pickup_secrets()
    {
        var x = await Setup(2); using var f = x.Factory; using var admin = x.Admin; using var seller = x.Seller; using var customer = x.Customer;
        var before = await Read<AdminSummaryResponse>(await admin.GetAsync("/api/v1/admin/dashboard/summary"));
        var pickup = await ConfirmAndReady(seller, customer, x.Campaign); var code = await Read<IssuedPickupCode>(await customer.PostAsync($"/api/v1/me/pickups/{pickup.Id}/code", null));
        var summary = await Read<AdminSummaryResponse>(await admin.GetAsync("/api/v1/admin/dashboard/summary")); Assert.Equal(before.TotalReservations + 1, summary.TotalReservations); Assert.Equal(before.AwaitingCollectionPickups + 1, summary.AwaitingCollectionPickups);
        var campaign = await Read<AdminCampaignResponse>(await admin.GetAsync($"/api/v1/admin/campaigns/{x.Campaign}")); Assert.Equal(2, campaign.Operation.CommittedQuantity); Assert.Equal("Wheat", campaign.ProductName); Assert.Equal(1, campaign.UnitQuantity); Assert.Equal(80, campaign.Operation.Campaign.GroupUnitPrice);
        var page = await Read<PageResponse<AdminCampaignResponse>>(await admin.GetAsync("/api/v1/admin/campaigns?status=FulfillmentInProgress&search=TEST%20WHEAT")); Assert.Contains(page.Items, row => row.Operation.Campaign.Id == x.Campaign);
        var categories = await Read<PageResponse<AdminCategoryResponse>>(await admin.GetAsync("/api/v1/admin/categories?active=false&search=fashion")); Assert.Contains(categories.Items, row => row.Slug == "fashion" && !row.IsActive);
        var unused = await UnusedProduct(seller); await Read<SellerProductResponse>(await seller.PutAsJsonAsync($"/api/v1/seller/products/{unused.Id}/status", new ProductStatusRequest(ResourceStatus.Inactive)));
        Assert.Contains((await Read<PageResponse<SellerProductResponse>>(await admin.GetAsync("/api/v1/admin/products?status=Inactive&search=Status"))).Items, row => row.Id == unused.Id);
        var reservations = await Read<PageResponse<AdminReservationResponse>>(await admin.GetAsync($"/api/v1/admin/reservations?campaignId={x.Campaign}&status=Active")); Assert.Single(reservations.Items);
        var response = await admin.GetAsync($"/api/v1/admin/pickups?campaignId={x.Campaign}&status=ReadyForPickup"); var body = await response.Content.ReadAsStringAsync();
        var pickups = await Read<PageResponse<AdminPickupResponse>>(response); Assert.Equal(pickup.Id, Assert.Single(pickups.Items).Id); Assert.DoesNotContain(code.Code, body); Assert.DoesNotContain("codeHash", body); Assert.DoesNotContain("customerUserId", body); Assert.DoesNotContain("failedAttempts", body);
        await using var scope = f.Services.CreateAsyncScope(); var hash = (await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().Pickups.SingleAsync(p => p.Id == pickup.Id)).CodeHash!; Assert.DoesNotContain(hash, body);
    }
    [Fact]
    public async Task Admin_seller_search_pagination_review_reasons_and_audit_filters_work()
    {
        var x = await Setup(); using var f = x.Factory; using var admin = x.Admin; using var seller = x.Seller; using var customer = x.Customer;
        var suffix = Guid.NewGuid().ToString("N");
        using var first = f.CreateClient(); using var second = f.CreateClient();
        // Arrange two onboarding profiles through existing application flows without consuming auth IP throttle.
        async Task<SellerResponse> Create(string name)
        {
            await using var scope = f.Services.CreateAsyncScope();
            var identity = scope.ServiceProvider.GetRequiredService<IIdentityService>(); var user = await identity.Register(new($"{Guid.NewGuid():N}@example.invalid", "Strong!Password123", name), default);
            return await scope.ServiceProvider.GetRequiredService<IMarketplace>().SaveSeller(new(user.Id, false, "admin-test"), SellerRequest(name), true, default);
        }
        var one = await Create("Review " + suffix + " one"); var two = await Create("Review " + suffix + " two");
        var path = $"/api/v1/admin/sellers?status=Pending&search={suffix}&pageSize=1";
        var page1 = await Read<PageResponse<AdminSellerResponse>>(await admin.GetAsync(path + "&page=1")); var page2 = await Read<PageResponse<AdminSellerResponse>>(await admin.GetAsync(path + "&page=2"));
        Assert.Equal(2, page1.Total); Assert.Single(page1.Items); Assert.NotEqual(page1.Items[0].Id, Assert.Single(page2.Items).Id);
        var detail = await Read<AdminSellerResponse>(await admin.GetAsync($"/api/v1/admin/sellers/{one.Id}")); Assert.Equal("12345678901234", detail.FssaiRegistrationNumber);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/v1/admin/sellers/{one.Id}/reject", new ReviewRequest(" "))).StatusCode);
        await Read<SellerReviewResponse>(await admin.PostAsJsonAsync($"/api/v1/admin/sellers/{one.Id}/reject", new ReviewRequest("Registration evidence incomplete")));
        await Read<SellerReviewResponse>(await admin.PostAsJsonAsync($"/api/v1/admin/sellers/{two.Id}/approve", new ReviewRequest("Evidence reviewed")));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/v1/admin/sellers/{two.Id}/approve", new ReviewRequest(null))).StatusCode);
        var logs = await Read<PageResponse<AdminAuditResponse>>(await admin.GetAsync($"/api/v1/admin/audit-logs?entityId={one.Id}&action=SellerRejected&entityType=Seller&pageSize=1"));
        var log = Assert.Single(logs.Items); Assert.Equal("Registration evidence incomplete", log.SafeMetadata["reason"]); Assert.NotNull(log.ActorUserId); Assert.NotNull(log.ActorDisplayName); Assert.NotEmpty(log.CorrelationId);
        Assert.Single((await Read<PageResponse<AdminAuditResponse>>(await admin.GetAsync($"/api/v1/admin/audit-logs?actorId={log.ActorUserId}&entityId={one.Id}&action=SellerRejected"))).Items);
    }
    [Fact]
    public async Task Admin_campaign_review_uses_existing_state_machine_and_retains_rejection_audit()
    {
        var x = await Setup(); using var f = x.Factory; using var admin = x.Admin; using var seller = x.Seller; using var customer = x.Customer;
        var p = await UnusedProduct(seller); var source = await StoredCampaign(f, x.Campaign);
        await Read<InventoryResponse>(await seller.PutAsJsonAsync($"/api/v1/seller/inventory/{p.Variants[0].Id}", new InventoryRequest(2, 0)));
        async Task<CampaignResponse> Draft() => await Read<CampaignResponse>(await seller.PostAsJsonAsync("/api/v1/seller/group-buys", new CampaignRequest(p.Variants[0].Id, source.SellerLocationId, "Admin publication", "", 100, 80, 1, 1, 1, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1))));
        var first = await Draft(); var second = await Draft();
        foreach (var c in new[] { first, second }) await Read<CampaignResponse>(await seller.PostAsync($"/api/v1/seller/group-buys/{c.Id}/submit", null));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/v1/admin/group-buys/{first.Id}/reject", new ReviewRequest(null))).StatusCode);
        await Read<CampaignResponse>(await admin.PostAsJsonAsync($"/api/v1/admin/group-buys/{first.Id}/reject", new ReviewRequest("Price evidence required")));
        var rejected = await Read<AdminCampaignResponse>(await admin.GetAsync($"/api/v1/admin/campaigns/{first.Id}")); Assert.Equal(CampaignStatus.Draft, rejected.Operation.Campaign.Status); Assert.Equal(0, rejected.Operation.CommittedQuantity);
        var approved = await Read<CampaignResponse>(await admin.PostAsJsonAsync($"/api/v1/admin/group-buys/{second.Id}/approve", new ReviewRequest("Inventory and price reviewed"))); Assert.Equal(CampaignStatus.Open, approved.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/v1/admin/group-buys/{second.Id}/approve", new ReviewRequest(null))).StatusCode);
        Assert.Equal("Price evidence required", Assert.Single((await Read<PageResponse<AdminAuditResponse>>(await admin.GetAsync($"/api/v1/admin/audit-logs?entityId={first.Id}&action=CampaignRejected"))).Items).SafeMetadata["reason"]);
    }
    [Fact]
    public async Task Admin_monitoring_and_audit_never_return_arbitrary_metadata_or_raw_worker_errors()
    {
        var x = await Setup(); using var f = x.Factory; using var admin = x.Admin; using var seller = x.Seller; using var customer = x.Customer;
        var entity = Guid.NewGuid();
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
            db.AuditLogs.Add(new AuditLog { Action = "SellerRejected", EntityType = "Seller", EntityId = entity, SafeMetadataJson = "{\"reason\":\"Reviewed\",\"accessToken\":\"sensitive-token\",\"pickupCode\":\"sensitive-code\"}" });
            await db.Campaigns.Where(c => c.Id == x.Campaign).ExecuteUpdateAsync(s => s.SetProperty(c => c.WorkerFailureCount, 2).SetProperty(c => c.WorkerRetryAtUtc, DateTimeOffset.UtcNow.AddHours(1)).SetProperty(c => c.WorkerErrorCode, "SECRET EXCEPTION STACK"));
            await db.SaveChangesAsync();
        }
        var auditBody = await admin.GetStringAsync($"/api/v1/admin/audit-logs?entityId={entity}"); Assert.DoesNotContain("sensitive-token", auditBody); Assert.DoesNotContain("sensitive-code", auditBody);
        var records = await Read<PageResponse<AdminCampaignResponse>>(await admin.GetAsync("/api/v1/admin/campaigns?failedOnly=true")); var row = records.Items.Single(r => r.Operation.Campaign.Id == x.Campaign); Assert.Equal("processing_failed", row.WorkerErrorCode); Assert.Equal(2, row.WorkerFailureCount);
        foreach (var path in new[] { "/sellers?status=999", "/products?pageSize=101", "/campaigns?status=999", "/pickups?status=999", "/reservations?status=999", "/audit-logs?fromUtc=2026-12-01T00:00:00Z&toUtc=2026-01-01T00:00:00Z", "/sellers?search=" + new string('x', 101) })
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/v1/admin" + path)).StatusCode);
    }
}

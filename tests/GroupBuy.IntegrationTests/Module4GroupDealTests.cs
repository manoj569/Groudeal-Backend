using System.Net;
using System.Net.Http.Json;
using GroupBuy.Application;
using GroupBuy.Domain;
using GroupBuy.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
namespace GroupBuy.IntegrationTests;
public partial class BackendTests
{
    [Theory]
    [InlineData(ProductType.Physical)] [InlineData(ProductType.Service)] [InlineData(ProductType.Membership)]
    public async Task Module4_demand_offer_projects_safe_seller_paid_progress_and_idempotent_checkout(ProductType type)
    {
        var x = await Setup(4, true, true, type, true);
        using var f = x.Factory; using var customer = x.Customer; using var seller = x.Seller; using var admin = x.Admin;
        var route = $"/api/v1/group-buys/{x.Campaign}/deal";
        var before = await Read<GroupDealDetail>(await customer.GetAsync(route));
        Assert.Equal((GroupDealType)type, before.Type); Assert.True(before.Seller.Verified); Assert.True(before.CanJoin);
        Assert.Null(before.Progress.CurrentTier); Assert.Equal(2, before.Progress.QuantityNeededForNextTier); Assert.Equal(0, before.CommittedMemberCount);
        Assert.NotNull(before.OriginatingDemandId); Assert.Null(before.ReconfirmedCount);
        await using (var interestScope = f.Services.CreateAsyncScope())
        {
            var interestDb = interestScope.ServiceProvider.GetRequiredService<MarketplaceDb>();
            interestDb.Interests.Add(new CustomerInterest { CustomerId = x.CustomerId, DemandId = before.OriginatingDemandId, ServiceArea = "synthetic-area", DesiredQuantity = 100 });
            await interestDb.SaveChangesAsync();
        }
        var interested = await Read<GroupDealDetail>(await customer.GetAsync(route)); Assert.Equal(1, interested.InterestedCount); Assert.Null(interested.Progress.CurrentTier); Assert.Equal(0, interested.PaidMemberCount);
        var request = Checkout(x.Campaign, 1);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => customer.PostAsJsonAsync("/api/v1/mvp/customer/orders", request)));
        var orders = new List<PaymentOrderResponse>(); foreach (var result in attempts) orders.Add(await Read<PaymentOrderResponse>(result));
        Assert.Single(orders.Select(o => o.Id).Distinct());
        Assert.Equal(HttpStatusCode.Conflict, (await customer.PostAsJsonAsync("/api/v1/mvp/customer/orders", Checkout(x.Campaign, 1))).StatusCode);
        await Read<PaymentOrderResponse>(await customer.PostAsJsonAsync($"/api/v1/mvp/customer/orders/{orders[0].Id}/sandbox?success=true", new { }));
        var paid = await Read<GroupDealDetail>(await customer.GetAsync(route));
        Assert.Equal(1, paid.CommittedMemberCount); Assert.Equal(1, paid.PaidMemberCount); Assert.Equal(1, paid.Progress.CommittedQuantity); Assert.Null(paid.Progress.CurrentTier);
        var all = await Read<PageResponse<GroupDealSummary>>(await customer.GetAsync($"/api/v1/group-buys/deals?serviceArea=synthetic-area&type={paid.Type}&pageSize=100"));
        Assert.Contains(all.Items, i => i.Id == x.Campaign);
        var opportunities = await seller.GetStringAsync("/api/v1/seller/demand-opportunities?serviceArea=synthetic-area");
        Assert.DoesNotContain(x.CustomerId.ToString(), opportunities); Assert.DoesNotContain("@example.invalid", opportunities);
        var json = await customer.GetStringAsync(route); Assert.DoesNotContain("contactEmail", json, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("ciphertext", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync("/api/v1/seller/demand-opportunities?serviceArea=synthetic-area")).StatusCode);
        await using var scope = f.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
        if (type != ProductType.Physical) Assert.False(await db.Inventories.AnyAsync(i => i.ProductVariantId == db.Campaigns.Where(c => c.Id == x.Campaign).Select(c => c.ProductVariantId).Single()));
        Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.EntityId == x.Campaign && a.Action == "DemandOfferLinked"));
        Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.EntityId == x.Campaign && a.Action == "DemandOfferPublished"));
    }

    [Fact]
    public async Task Module4_twenty_customers_compete_for_ten_slots_without_overselling()
    {
        var x = await Setup(10, true, true, ProductType.Service, true);
        using var f = x.Factory; using var customer = x.Customer; using var seller = x.Seller; using var admin = x.Admin;
        var clients = new List<HttpClient>();
        try
        {
            for (var i = 0; i < 20; i++)
            {
                var client = f.CreateClient(); clients.Add(client);
                await using var identityScope = f.Services.CreateAsyncScope(); var identity = identityScope.ServiceProvider.GetRequiredService<IIdentityService>();
                var email = Guid.NewGuid().ToString("N") + "@example.invalid"; const string password = "Synthetic!Password123";
                await identity.Register(new(email, password, "Synthetic concurrent customer"), default);
                var token = await identity.Login(new(email, password), default);
                client.DefaultRequestHeaders.Authorization = new("Bearer", token.AccessToken);
            }
            var results = await Task.WhenAll(clients.Select(client => client.PostAsJsonAsync("/api/v1/mvp/customer/orders", Checkout(x.Campaign, 1))));
            Assert.Equal(10, results.Count(r => r.IsSuccessStatusCode)); Assert.Equal(10, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
            for (var i = 0; i < results.Length; i++) if (results[i].IsSuccessStatusCode) { var order = await Read<PaymentOrderResponse>(results[i]); await Read<PaymentOrderResponse>(await clients[i].PostAsJsonAsync($"/api/v1/mvp/customer/orders/{order.Id}/sandbox?success=true", new { })); }
            var finalized = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => seller.PostAsync($"/api/v1/mvp/seller/campaigns/{x.Campaign}/finalize", null)));
            foreach (var result in finalized) Assert.Equal(CampaignStatus.Successful, (await Read<TierCampaignResponse>(result)).Campaign.Status);
            var detail = await Read<GroupDealDetail>(await customer.GetAsync($"/api/v1/group-buys/{x.Campaign}/deal"));
            Assert.Equal(10, detail.PaidMemberCount); Assert.Equal(10, detail.Progress.CommittedQuantity); Assert.Equal(60, detail.Progress.CurrentTier!.UnitPrice);
            await using var scope = f.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
            Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.EntityId == x.Campaign && a.Action == "TierCampaignFinalized"));
            Assert.Equal(10, await db.PaymentRefunds.CountAsync(r => db.PaymentOrders.Any(o => o.Id == r.PaymentOrderId && o.CampaignId == x.Campaign)));
        }
        finally { foreach (var client in clients) client.Dispose(); }
    }
    [Fact]
    public async Task Module4_admin_cancellation_is_audited_idempotent_and_refunds_captured_money()
    {
        var x = await Setup(4, true, true, ProductType.Service, true);
        using var f = x.Factory; using var customer = x.Customer; using var seller = x.Seller; using var admin = x.Admin;
        var order = await Read<PaymentOrderResponse>(await customer.PostAsJsonAsync("/api/v1/mvp/customer/orders", Checkout(x.Campaign, 1)));
        await Read<PaymentOrderResponse>(await customer.PostAsJsonAsync($"/api/v1/mvp/customer/orders/{order.Id}/sandbox?success=true", new { }));
        var route = $"/api/v1/admin/group-buys/{x.Campaign}/cancel";
        Assert.Equal(HttpStatusCode.Forbidden, (await seller.PostAsJsonAsync(route, new ReviewRequest("Seller cannot cancel as Admin"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(route, new ReviewRequest(null))).StatusCode);
        foreach (var i in Enumerable.Range(0, 2)) Assert.Equal(CampaignStatus.Cancelled, (await Read<CampaignResponse>(await admin.PostAsJsonAsync(route, new ReviewRequest("Synthetic cancellation reason")))).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await customer.PostAsJsonAsync("/api/v1/mvp/customer/orders", Checkout(x.Campaign, 1))).StatusCode);
        await using var scope = f.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
        Assert.Equal(1, await db.PaymentRefunds.CountAsync(r => r.PaymentOrderId == order.Id));
        Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.EntityId == x.Campaign && a.Action == "GroupDealCancelled"));
    }

    [Theory]
    [InlineData(ProductType.Service)] [InlineData(ProductType.Membership)]
    public async Task Module4_entitlement_redemption_uses_one_time_pass_without_stock(ProductType type)
    {
        var x = await Setup(2, true, true, type, true);
        using var f = x.Factory; using var customer = x.Customer; using var seller = x.Seller; using var admin = x.Admin;
        var order = await Read<PaymentOrderResponse>(await customer.PostAsJsonAsync("/api/v1/mvp/customer/orders", Checkout(x.Campaign, 1)));
        await Read<PaymentOrderResponse>(await customer.PostAsJsonAsync($"/api/v1/mvp/customer/orders/{order.Id}/sandbox?success=true", new { }));
        await using (var expiry = f.Services.CreateAsyncScope()) await expiry.ServiceProvider.GetRequiredService<MarketplaceDb>().Campaigns.Where(c => c.Id == x.Campaign).ExecuteUpdateAsync(s => s.SetProperty(c => c.ExpiresAtUtc, DateTimeOffset.UtcNow.AddSeconds(-1)));
        await Read<TierCampaignResponse>(await seller.PostAsync($"/api/v1/mvp/seller/campaigns/{x.Campaign}/finalize", null));
        order = await Read<PaymentOrderResponse>(await customer.GetAsync($"/api/v1/mvp/customer/orders/{order.Id}")); var id = order.FulfillmentId!.Value;
        await Read<FulfillmentResponse>(await customer.PostAsync($"/api/v1/mvp/customer/fulfillments/{id}/confirm-choice", null));
        foreach (var state in new[] { FulfillmentState.Preparing, FulfillmentState.ReadyForCollection }) await Read<FulfillmentResponse>(await seller.PostAsJsonAsync($"/api/v1/mvp/seller/fulfillments/{id}/status", new FulfillmentAction(state, null)));
        var pass = await Read<FulfillmentPass>(await customer.PostAsync($"/api/v1/mvp/customer/fulfillments/{id}/pass", null));
        await Read<FulfillmentResponse>(await seller.PostAsJsonAsync($"/api/v1/mvp/seller/fulfillments/{id}/verify", new SecretRequest(pass.BackupCode)));
        Assert.True((await Read<FulfillmentResponse>(await seller.PostAsync($"/api/v1/mvp/seller/fulfillments/{id}/redeem", null))).Redeemed);
        Assert.Equal(HttpStatusCode.Conflict, (await seller.PostAsync($"/api/v1/mvp/seller/fulfillments/{id}/redeem", null)).StatusCode);
        await using var check = f.Services.CreateAsyncScope(); var db = check.ServiceProvider.GetRequiredService<MarketplaceDb>(); var campaign = await db.Campaigns.SingleAsync(c => c.Id == x.Campaign);
        Assert.Equal(0, campaign.CommittedQuantity); Assert.Equal(1, campaign.FulfilledQuantity); Assert.False(await db.Inventories.AnyAsync(i => i.ProductVariantId == campaign.ProductVariantId));
        Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.Action == "SellerEntitlementRedeemed" && a.EntityId == id));
    }

    [Fact]
    public async Task Module4_admin_cancel_races_join_without_leaving_capacity_or_unqualified_reservations()
    {
        var x = await Setup(2, true, true, ProductType.Service, true);
        using var f = x.Factory; using var customer = x.Customer; using var seller = x.Seller; using var admin = x.Admin;
        var join = customer.PostAsJsonAsync("/api/v1/mvp/customer/orders", Checkout(x.Campaign, 1));
        var cancel = admin.PostAsJsonAsync($"/api/v1/admin/group-buys/{x.Campaign}/cancel", new ReviewRequest("Synthetic concurrent cancellation"));
        await Task.WhenAll(join, cancel); Assert.True((await cancel).IsSuccessStatusCode);
        Assert.True((await join).IsSuccessStatusCode || (await join).StatusCode == HttpStatusCode.Conflict);
        if ((await join).IsSuccessStatusCode)
        {
            var order = await Read<PaymentOrderResponse>((await join));
            if (order.ProviderOrderId is null) Assert.Equal(PaymentState.Expired, order.State);
            else
            {
                var late = await Read<PaymentOrderResponse>(await customer.PostAsJsonAsync($"/api/v1/mvp/customer/orders/{order.Id}/sandbox?success=true", new { }));
                Assert.Null(late.ReservationId); Assert.Equal(PaymentState.RefundPending, late.State);
            }
        }
        await using var scope = f.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>(); var campaign = await db.Campaigns.SingleAsync(c => c.Id == x.Campaign);
        Assert.Equal(CampaignStatus.Cancelled, campaign.Status); Assert.Equal(0, campaign.ReservedQuantity); Assert.Equal(0, campaign.CommittedQuantity);
        Assert.False(await db.Reservations.AnyAsync(r => r.CampaignId == x.Campaign && r.Status == ReservationStatus.Active));
    }

    [Fact]
    public async Task Module4_deadline_finalization_races_join_and_customer_cancellation_without_reopening()
    {
        var x = await Setup(2, true, true, ProductType.Service, true);
        using var f = x.Factory; using var customer = x.Customer; using var seller = x.Seller; using var admin = x.Admin;
        var order = await Read<PaymentOrderResponse>(await customer.PostAsJsonAsync("/api/v1/mvp/customer/orders", Checkout(x.Campaign, 1)));
        await Read<PaymentOrderResponse>(await customer.PostAsJsonAsync($"/api/v1/mvp/customer/orders/{order.Id}/sandbox?success=true", new { }));
        await using (var scope = f.Services.CreateAsyncScope()) await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().Campaigns.Where(c => c.Id == x.Campaign).ExecuteUpdateAsync(s => s.SetProperty(c => c.ExpiresAtUtc, DateTimeOffset.UtcNow));
        var finalize = seller.PostAsync($"/api/v1/mvp/seller/campaigns/{x.Campaign}/finalize", null);
        var cancel = customer.PostAsync($"/api/v1/mvp/customer/orders/{order.Id}/cancel", null);
        var join = customer.PostAsJsonAsync("/api/v1/mvp/customer/orders", Checkout(x.Campaign, 1));
        await Task.WhenAll(finalize, cancel, join);
        Assert.Equal(CampaignStatus.Successful, (await Read<TierCampaignResponse>((await finalize))).Campaign.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await cancel).StatusCode); Assert.Equal(HttpStatusCode.Conflict, (await join).StatusCode);
        var detail = await Read<GroupDealDetail>(await customer.GetAsync($"/api/v1/group-buys/{x.Campaign}/deal")); Assert.False(detail.CanJoin); Assert.Equal(1, detail.Progress.CommittedQuantity);
    }

    [Fact]
    public async Task Module4_multiple_seller_offers_share_only_the_demand_and_reject_cross_seller_edits()
    {
        var x = await Setup(2, true, true, ProductType.Service, true);
        using var f = x.Factory; using var customer = x.Customer; using var seller = x.Seller; using var admin = x.Admin; using var second = f.CreateClient();
        var original = await Read<GroupDealDetail>(await customer.GetAsync($"/api/v1/group-buys/{x.Campaign}/deal"));
        var owner = await Register(second, "other-offer-seller"); await Login(second, owner);
        var profile = await Read<SellerResponse>(await second.PostAsJsonAsync("/api/v1/seller/profile", SellerRequest("Other synthetic seller")));
        await Read<SellerReviewResponse>(await admin.PostAsJsonAsync($"/api/v1/admin/sellers/{profile.Id}/approve", new ReviewRequest(null))); await Login(second, owner);
        var product = await Read<ProductResponse>(await second.PostAsJsonAsync("/api/v1/seller/products", new ProductRequest(original.CategoryId, "Other service", "other-service", "Separate offer", ProductType.Service, Guid.NewGuid().ToString("N"), "One entitlement", UnitOfMeasure.Piece, 1, "{}")));
        Assert.Equal(HttpStatusCode.NotFound, (await seller.PutAsJsonAsync($"/api/v1/seller/inventory/{product.Variants[0].Id}", new InventoryRequest(0,0))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await second.PutAsJsonAsync($"/api/v1/seller/inventory/{product.Variants[0].Id}", new InventoryRequest(0,0))).StatusCode);
        var campaign = await Read<CampaignResponse>(await second.PostAsJsonAsync("/api/v1/seller/group-buys", new CampaignRequest(product.Variants[0].Id, profile.PickupLocations[0].Id, "Other service offer", "Separate commercial terms", 100, 90, 2, 1, 1, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1))));
        await Read<TierCampaignResponse>(await second.PutAsJsonAsync($"/api/v1/mvp/seller/campaigns/{campaign.Id}/tiers", new TierConfigurationRequest("synthetic-area", [new(1,90),new(2,70)], null)));
        var link = new LinkDemandOfferRequest(original.OriginatingDemandId!.Value, "Separate seller offer terms for redemption.");
        await Read<CampaignResponse>(await second.PutAsJsonAsync($"/api/v1/seller/group-buys/{campaign.Id}/demand-offer", link));
        Assert.Equal(HttpStatusCode.NotFound, (await seller.PutAsJsonAsync($"/api/v1/seller/group-buys/{campaign.Id}/demand-offer", link)).StatusCode);
        foreach (var i in Enumerable.Range(0,2)) Assert.Equal(CampaignStatus.PendingApproval, (await Read<CampaignResponse>(await second.PostAsync($"/api/v1/seller/group-buys/{campaign.Id}/submit", null))).Status);
        await Read<CampaignResponse>(await admin.PostAsJsonAsync($"/api/v1/admin/group-buys/{campaign.Id}/approve", new ReviewRequest(null)));
        var other = await Read<GroupDealDetail>(await customer.GetAsync($"/api/v1/group-buys/{campaign.Id}/deal"));
        var privateOffer = await Read<GroupDealOffer>(await second.GetAsync($"/api/v1/seller/group-buys/{campaign.Id}/offer"));
        Assert.Equal(link.Terms, privateOffer.Terms); Assert.Equal(link.DemandId, privateOffer.OriginatingDemandId);
        Assert.Equal(HttpStatusCode.NotFound, (await seller.GetAsync($"/api/v1/seller/group-buys/{campaign.Id}/offer")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync($"/api/v1/admin/group-buys/{campaign.Id}/offer")).StatusCode);
        Assert.Equal(link.DemandId, (await Read<GroupDealOffer>(await admin.GetAsync($"/api/v1/admin/group-buys/{campaign.Id}/offer"))).OriginatingDemandId);
        Assert.Equal(original.OriginatingDemandId, other.OriginatingDemandId); Assert.NotEqual(original.Id, other.Id); Assert.NotEqual(original.Tiers[0].UnitPrice, other.Tiers[0].UnitPrice);
        await using var scope = f.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
        Assert.Equal(2, await db.Campaigns.CountAsync(c => c.OriginatingDemandId == original.OriginatingDemandId));
        Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.EntityId == campaign.Id && a.Action == "CampaignSubmitted"));
    }

    [Fact]
    public async Task Module4_quantity_threshold_crossings_do_not_use_member_counts()
    {
        var x = await Setup(30, true, true, ProductType.Physical, true, [new(10,80),new(20,70),new(30,60)]);
        using var f = x.Factory; using var customer = x.Customer; using var seller = x.Seller; using var admin = x.Admin;
        var clients = new List<HttpClient>();
        try
        {
            var quantities = new[] { 9, 1, 9, 1, 9, 1 }; var totals = new[] { 9,10,19,20,29,30 }; decimal?[] prices = [null,80,80,70,70,60];
            for (var i = 0; i < quantities.Length; i++)
            {
                var client = f.CreateClient(); clients.Add(client);
                await using var scope = f.Services.CreateAsyncScope(); var identity = scope.ServiceProvider.GetRequiredService<IIdentityService>();
                var email = Guid.NewGuid().ToString("N") + "@example.invalid"; const string password = "Synthetic!Password123";
                await identity.Register(new(email,password,"Synthetic bulk customer"),default); var token = await identity.Login(new(email,password),default); client.DefaultRequestHeaders.Authorization = new("Bearer",token.AccessToken);
                var order = await Read<PaymentOrderResponse>(await client.PostAsJsonAsync("/api/v1/mvp/customer/orders",Checkout(x.Campaign,quantities[i])));
                await Read<PaymentOrderResponse>(await client.PostAsJsonAsync($"/api/v1/mvp/customer/orders/{order.Id}/sandbox?success=true",new { }));
                var detail = await Read<GroupDealDetail>(await client.GetAsync($"/api/v1/group-buys/{x.Campaign}/deal"));
                Assert.Equal(totals[i],detail.Progress.CommittedQuantity); Assert.Equal(prices[i],detail.Progress.CurrentTier?.UnitPrice); Assert.Equal(i+1,detail.PaidMemberCount);
            }
        }
        finally { foreach(var client in clients)client.Dispose(); }
    }

    [Theory]
    [InlineData(ProductType.Physical)] [InlineData(ProductType.Service)]
    public async Task Module4_demand_and_offer_counts_match_active_accounts_and_legacy_product_interests(ProductType type)
    {
        var x = await Setup(4,true,true,type,true,catalogDemand:type==ProductType.Physical);
        using var f = x.Factory; using var customer = x.Customer; using var seller = x.Seller; using var admin = x.Admin; using var anonymous = f.CreateClient();
        var before = await Read<GroupDealDetail>(await anonymous.GetAsync($"/api/v1/group-buys/{x.Campaign}/deal"));
        await using (var seed = f.Services.CreateAsyncScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<MarketplaceDb>(); var demand = await db.DiscoveryDemands.SingleAsync(d => d.Id == before.OriginatingDemandId);
            await db.SaveChangesAsync();
            db.Interests.Add(new CustomerInterest { CustomerId=x.CustomerId, ProductId=type==ProductType.Physical?before.ProductId:null, DemandId=type==ProductType.Physical?null:demand.Id, ServiceArea="synthetic-area",DesiredQuantity=100 }); await db.SaveChangesAsync();
        }
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var demand = await scope.ServiceProvider.GetRequiredService<IDiscovery>().DemandDetail(before.OriginatingDemandId!.Value,null,default);
            Assert.Equal(1,demand.Progress.CurrentInterest); Assert.Equal(1,demand.ActiveOfferCount); Assert.Equal("OffersAvailable",demand.SellerOpportunityState); Assert.NotNull(demand.GroupDealsRoute);
            var offers = await Read<PageResponse<GroupDealSummary>>(await anonymous.GetAsync(demand.GroupDealsRoute)); Assert.Single(offers.Items); Assert.Equal(x.Campaign,offers.Items[0].Id);
        }
        Assert.Equal(1,(await Read<GroupDealDetail>(await anonymous.GetAsync($"/api/v1/group-buys/{x.Campaign}/deal"))).InterestedCount);
        await using (var scope = f.Services.CreateAsyncScope()) await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().UsersProfile.Where(u => u.Id==x.CustomerId).ExecuteUpdateAsync(s => s.SetProperty(u=>u.Status,AccountStatus.Suspended));
        Assert.Equal(0,(await Read<GroupDealDetail>(await anonymous.GetAsync($"/api/v1/group-buys/{x.Campaign}/deal"))).InterestedCount);
        await using var check = f.Services.CreateAsyncScope(); Assert.Equal(0,(await check.ServiceProvider.GetRequiredService<IDiscovery>().DemandDetail(before.OriginatingDemandId!.Value,null,default)).Progress.CurrentInterest);
    }

}

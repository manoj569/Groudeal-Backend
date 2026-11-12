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
    private sealed record M7Fixture(ApiFactory Factory,HttpClient Admin,HttpClient Seller,HttpClient Customer,
        Guid Campaign,Guid Order,Guid Fulfillment) : IDisposable
    {
        public void Dispose(){Customer.Dispose();Seller.Dispose();Admin.Dispose();Factory.Dispose();}
    }
    private async Task<M7Fixture> M7Setup(ProductType type=ProductType.Physical,bool delivery=false,bool settle=true)
    {
        var x=await Setup(2,true,true,type,true);
        var quantity=type==ProductType.Physical ? 2 : 1;
        var request=Checkout(x.Campaign,quantity);
        if(delivery) request=request with { FulfillmentMode=OrderFulfillmentMode.FounderDelivery,
            Address=new("Synthetic Recipient","0000000000","Synthetic delivery address",null,"synthetic-area","900001") };
        var order=await Read<PaymentOrderResponse>(await x.Customer.PostAsJsonAsync("/api/v1/mvp/customer/orders",request));
        await Read<PaymentOrderResponse>(await x.Customer.PostAsJsonAsync($"/api/v1/mvp/customer/orders/{order.Id}/sandbox?success=true",new{}));
        if(type!=ProductType.Physical)
        {
            using var second=await CheckoutCustomer(x.Factory);
            var another=await Read<PaymentOrderResponse>(await second.PostAsJsonAsync("/api/v1/mvp/customer/orders",Checkout(x.Campaign,1)));
            await Read<PaymentOrderResponse>(await second.PostAsJsonAsync($"/api/v1/mvp/customer/orders/{another.Id}/sandbox?success=true",new{}));
        }
        await Read<TierCampaignResponse>(await x.Seller.PostAsync($"/api/v1/mvp/seller/campaigns/{x.Campaign}/finalize",null));
        if(settle){await using var scope=x.Factory.Services.CreateAsyncScope();await scope.ServiceProvider.GetRequiredService<IPhaseOne>().Process(default);}
        order=await Read<PaymentOrderResponse>(await x.Customer.GetAsync($"/api/v1/mvp/customer/orders/{order.Id}"));
        return new(x.Factory,x.Admin,x.Seller,x.Customer,x.Campaign,order.Id,order.FulfillmentId!.Value);
    }
    private static async Task<FulfillmentResponse> M7Move(M7Fixture x,FulfillmentState state,string? reason=null) =>
        await Read<FulfillmentResponse>(await (FulfillmentRules.DeliveryOperation(state)?x.Admin:x.Seller)
            .PostAsJsonAsync($"/api/v1/mvp/{(FulfillmentRules.DeliveryOperation(state)?"admin":"seller")}/fulfillments/{x.Fulfillment}/status",new FulfillmentAction(state,reason)));
    private static async Task M7Ready(M7Fixture x,bool delivery=false)
    {
        await Read<FulfillmentResponse>(await x.Customer.PostAsync($"/api/v1/mvp/customer/fulfillments/{x.Fulfillment}/confirm-choice",null));
        await M7Move(x,FulfillmentState.Preparing);await M7Move(x,FulfillmentState.ReadyForCollection);
        if(delivery){await M7Move(x,FulfillmentState.CollectedFromSeller);await M7Move(x,FulfillmentState.OutForDelivery);}
    }
    private static async Task<FulfillmentPass> M7Pass(M7Fixture x) =>
        await Read<FulfillmentPass>(await x.Customer.PostAsync($"/api/v1/mvp/customer/fulfillments/{x.Fulfillment}/pass",null));
    private static async Task M7Edit(M7Fixture x,Func<MarketplaceDb,Task> edit)
    {await using var scope=x.Factory.Services.CreateAsyncScope();await edit(scope.ServiceProvider.GetRequiredService<MarketplaceDb>());}
    private static async Task M7NoPhysicalPickup(M7Fixture x)
    {
        Guid pickup=default;
        await M7Edit(x,async db=>{pickup=await db.OrderFulfillments.Where(f=>f.Id==x.Fulfillment).Select(f=>f.PickupId).SingleAsync();});
        Assert.Empty((await Read<PageResponse<PickupDetailResponse>>(await x.Customer.GetAsync("/api/v1/me/pickups"))).Items);
        Assert.Empty((await Read<PageResponse<PickupDetailResponse>>(await x.Seller.GetAsync("/api/v1/seller/pickups"))).Items);
        Assert.Empty((await Read<PageResponse<AdminPickupResponse>>(await x.Admin.GetAsync($"/api/v1/admin/pickups?campaignId={x.Campaign}"))).Items);
        Assert.Equal(HttpStatusCode.NotFound,(await x.Customer.GetAsync($"/api/v1/me/pickups/{pickup}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await x.Seller.GetAsync($"/api/v1/seller/pickups/{pickup}")).StatusCode);
        var reservations=await Read<PageResponse<SellerReservationResponse>>(await x.Seller.GetAsync($"/api/v1/seller/campaigns/{x.Campaign}/reservations"));
        Assert.All(reservations.Items,r=>{Assert.Null(r.PickupId);Assert.Null(r.PickupStatus);});
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Module7_pickup_atomic_QR_or_backup_concurrent_completion(bool backup)
    {
        using var x=await M7Setup();
        Assert.Equal(HttpStatusCode.Conflict,(await x.Customer.PostAsync($"/api/v1/mvp/customer/fulfillments/{x.Fulfillment}/pass",null)).StatusCode);
        await M7Ready(x);
        var ready=await Read<MyOrderDetail>(await x.Customer.GetAsync($"/api/v1/me/orders/{x.Order}"));
        Assert.Equal(ParticipationStatus.Ready,ready.Order.Status);Assert.Equal("ready_for_pickup",ready.Order.Fulfillment.Status);
        var pass=await M7Pass(x);Assert.Matches("^[0-9]{6}$",pass.BackupCode);Assert.Matches("^[0-9A-F]{64}$",pass.OpaqueToken);
        var body=new SecretRequest(backup?pass.BackupCode:pass.OpaqueToken);
        var replies=await Task.WhenAll(Enumerable.Range(0,2).Select(_=>x.Seller.PostAsJsonAsync($"/api/v1/mvp/seller/fulfillments/{x.Fulfillment}/handover",body)));
        Assert.Single(replies,r=>r.StatusCode==HttpStatusCode.OK);Assert.Single(replies,r=>r.StatusCode==HttpStatusCode.Conflict);
        Assert.Null((await Read<FulfillmentResponse>(replies.Single(r=>r.StatusCode==HttpStatusCode.OK))).CollectedFromSellerAtUtc);
        var done=await Read<MyOrderDetail>(await x.Customer.GetAsync($"/api/v1/me/orders/{x.Order}"));Assert.Equal(ParticipationStatus.Completed,done.Order.Status);
        Assert.Equal(HttpStatusCode.Conflict,(await x.Seller.PostAsJsonAsync($"/api/v1/mvp/seller/fulfillments/{x.Fulfillment}/handover",body)).StatusCode);
        await M7Edit(x,async db=>{
            var f=await db.OrderFulfillments.SingleAsync(f=>f.Id==x.Fulfillment);var p=await db.Pickups.SingleAsync(p=>p.Id==f.PickupId);
            Assert.Null(f.TokenHash);Assert.Null(f.BackupCodeHash);Assert.Null(f.VerifiedBy);Assert.NotNull(f.RedeemedAtUtc);
            Assert.Equal(PickupStatus.Collected,p.Status);Assert.NotNull(p.CollectedByUserId);
            Assert.Equal(1,await db.AuditLogs.CountAsync(a=>a.EntityId==f.Id && a.Action=="FulfillmentRedeemed"));
            Assert.Equal(1,await db.Notifications.CountAsync(n=>n.EntityId==f.Id && n.Type==NotificationType.PilotFulfillmentCompleted));
            Assert.DoesNotContain(await db.AuditLogs.Where(a=>a.EntityId==f.Id).Select(a=>a.SafeMetadataJson).ToListAsync(),s=>s.Contains(pass.BackupCode) || s.Contains(pass.OpaqueToken));
        });
    }

    [Theory] [InlineData("campaign")] [InlineData("cancelled")] [InlineData("payment-failed")]
    [InlineData("full-refund")] [InlineData("refund-pending")] [InlineData("unfinalized")]
    public async Task Module7_commercial_invalidity_blocks_prepare_pass_verify_and_redeem(string invalid)
    {
        using var x=await M7Setup();await M7Ready(x);var pass=await M7Pass(x);
        await M7Edit(x,async db=>{
            if(invalid=="campaign")await db.Campaigns.Where(c=>c.Id==x.Campaign).ExecuteUpdateAsync(u=>u.SetProperty(c=>c.Status,CampaignStatus.Open));
            if(invalid=="unfinalized")await db.Campaigns.Where(c=>c.Id==x.Campaign).ExecuteUpdateAsync(u=>u.SetProperty(c=>c.FinalizedAtUtc,(DateTimeOffset?)null));
            var order=await db.PaymentOrders.SingleAsync(o=>o.Id==x.Order);
            if(invalid=="cancelled")await db.Reservations.Where(r=>r.Id==order.ReservationId).ExecuteUpdateAsync(u=>u.SetProperty(r=>r.Status,ReservationStatus.Cancelled));
            if(invalid=="payment-failed")order.State=PaymentState.Failed;
            if(invalid=="full-refund"){order.State=PaymentState.Refunded;order.RefundedMinor=order.MaximumAmountMinor;}
            if(invalid=="refund-pending")db.PaymentRefunds.Add(new(){PaymentOrderId=order.Id,AmountMinor=100,Reason="synthetic-test",State=RefundState.Pending});
            await db.SaveChangesAsync();
        });
        Assert.Equal(HttpStatusCode.Conflict,(await x.Seller.PostAsJsonAsync($"/api/v1/mvp/seller/fulfillments/{x.Fulfillment}/status",new FulfillmentAction(FulfillmentState.Preparing,null))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await x.Customer.PostAsync($"/api/v1/mvp/customer/fulfillments/{x.Fulfillment}/pass",null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await x.Seller.PostAsJsonAsync($"/api/v1/mvp/seller/fulfillments/{x.Fulfillment}/handover",new SecretRequest(pass.BackupCode))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await x.Seller.PostAsync($"/api/v1/mvp/seller/fulfillments/{x.Fulfillment}/redeem",null)).StatusCode);
        Assert.NotEqual(ParticipationStatus.Ready,(await Read<MyOrderDetail>(await x.Customer.GetAsync($"/api/v1/me/orders/{x.Order}"))).Order.Status);
    }

    [Fact]
    public async Task Module7_ready_duplicate_is_idempotent_and_customer_cannot_complete()
    {
        using var x=await M7Setup();await M7Ready(x);
        await M7Move(x,FulfillmentState.ReadyForCollection);
        Assert.Equal(HttpStatusCode.Forbidden,(await x.Customer.PostAsJsonAsync($"/api/v1/mvp/seller/fulfillments/{x.Fulfillment}/handover",new SecretRequest("123456"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await x.Customer.PostAsJsonAsync($"/api/v1/mvp/admin/fulfillments/{x.Fulfillment}/status",new FulfillmentAction(FulfillmentState.Delivered,null))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await x.Seller.PostAsJsonAsync($"/api/v1/mvp/seller/fulfillments/{x.Fulfillment}/status",new FulfillmentAction(FulfillmentState.Delivered,null))).StatusCode);
        await M7Edit(x,async db=>{
            Assert.Equal(1,await db.AuditLogs.CountAsync(a=>a.EntityId==x.Fulfillment && a.Action=="FulfillmentReadyForCollection"));
            Assert.Equal(1,await db.Notifications.CountAsync(n=>n.EntityId==x.Fulfillment && n.Type==NotificationType.PilotFulfillmentReady));
        });
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Module7_expiry_invalid_code_attempts_and_regeneration_preserve_lock(bool expiry)
    {
        using var x=await M7Setup();await M7Ready(x);var pass=await M7Pass(x);
        if(expiry)await M7Edit(x,async db=>{await db.OrderFulfillments.Where(f=>f.Id==x.Fulfillment).ExecuteUpdateAsync(u=>u.SetProperty(f=>f.SecretExpiresAtUtc,DateTimeOffset.UtcNow.AddSeconds(-1)));});
        var invalid=expiry?pass.BackupCode:"000000";
        for(var i=0;i<5;i++)Assert.Equal(HttpStatusCode.BadRequest,(await x.Seller.PostAsJsonAsync($"/api/v1/mvp/seller/fulfillments/{x.Fulfillment}/handover",new SecretRequest(invalid))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests,(await x.Seller.PostAsJsonAsync($"/api/v1/mvp/seller/fulfillments/{x.Fulfillment}/handover",new SecretRequest(pass.BackupCode))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests,(await x.Customer.PostAsync($"/api/v1/mvp/customer/fulfillments/{x.Fulfillment}/pass",null)).StatusCode);
        await M7Edit(x,async db=>{
            var f=await db.OrderFulfillments.SingleAsync(f=>f.Id==x.Fulfillment);Assert.Equal(5,f.FailedAttempts);Assert.NotNull(f.LockedUntilUtc);Assert.Null(f.RedeemedAtUtc);
        });
    }

    [Fact]
    public async Task Module7_delivery_retry_rotates_OTP_requires_admin_and_completes_once()
    {
        using var x=await M7Setup(delivery:true);
        await Read<FulfillmentResponse>(await x.Customer.PostAsync($"/api/v1/mvp/customer/fulfillments/{x.Fulfillment}/confirm-choice",null));
        await M7Move(x,FulfillmentState.Preparing);var ready=await M7Move(x,FulfillmentState.ReadyForCollection);
        Assert.Equal("ready_for_collection",ready.Status);Assert.Equal("founder_delivery",ready.Kind);Assert.Null(ready.DeliveryAddress);
        await M7NoPhysicalPickup(x);
        var card=(await Read<MyOrderDetail>(await x.Customer.GetAsync($"/api/v1/me/orders/{x.Order}"))).Order;
        Assert.NotEqual(ParticipationStatus.Ready,card.Status);
        Assert.Null(card.Fulfillment.PickupId);Assert.Null(card.Fulfillment.PickupStatus);
        Assert.Equal(HttpStatusCode.Conflict,(await x.Customer.PostAsync($"/api/v1/mvp/customer/fulfillments/{x.Fulfillment}/pass",null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await x.Seller.PostAsJsonAsync($"/api/v1/mvp/seller/fulfillments/{x.Fulfillment}/status",new FulfillmentAction(FulfillmentState.CollectedFromSeller,null))).StatusCode);
        await M7Move(x,FulfillmentState.CollectedFromSeller);await M7Move(x,FulfillmentState.CollectedFromSeller);
        await M7Move(x,FulfillmentState.OutForDelivery);var old=await M7Pass(x);
        await Read<FulfillmentResponse>(await x.Admin.PostAsJsonAsync($"/api/v1/mvp/admin/fulfillments/{x.Fulfillment}/verify",new SecretRequest(old.BackupCode)));
        Assert.Equal(HttpStatusCode.Forbidden,(await x.Seller.PostAsJsonAsync($"/api/v1/mvp/seller/fulfillments/{x.Fulfillment}/handover",new SecretRequest(old.BackupCode))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await x.Admin.PostAsJsonAsync($"/api/v1/mvp/admin/fulfillments/{x.Fulfillment}/status",new FulfillmentAction(FulfillmentState.DeliveryFailed,"private note must not leak"))).StatusCode);
        var failed=await M7Move(x,FulfillmentState.DeliveryFailed,"customer_unavailable");Assert.Equal("customer_unavailable",failed.ExceptionReason);
        await M7Move(x,FulfillmentState.RescheduleRequired);
        Assert.Equal(HttpStatusCode.Conflict,(await x.Admin.PostAsJsonAsync($"/api/v1/mvp/admin/fulfillments/{x.Fulfillment}/handover",new SecretRequest(old.BackupCode))).StatusCode);
        await M7Move(x,FulfillmentState.OutForDelivery);
        Assert.Equal(HttpStatusCode.Conflict,(await x.Admin.PostAsync($"/api/v1/mvp/admin/fulfillments/{x.Fulfillment}/redeem",null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await x.Admin.PostAsJsonAsync($"/api/v1/mvp/admin/fulfillments/{x.Fulfillment}/handover",new SecretRequest(old.BackupCode))).StatusCode);
        await M7Edit(x,async db=>{await db.OrderFulfillments.Where(f=>f.Id==x.Fulfillment).ExecuteUpdateAsync(u=>u.SetProperty(f=>f.SecretIssuedAtUtc,DateTimeOffset.UtcNow.AddMinutes(-2)));});
        var pass=await M7Pass(x);Assert.True(pass.Version>old.Version);
        var results=await Task.WhenAll(Enumerable.Range(0,2).Select(_=>x.Admin.PostAsJsonAsync($"/api/v1/mvp/admin/fulfillments/{x.Fulfillment}/handover",new SecretRequest(pass.BackupCode))));
        Assert.Single(results,r=>r.StatusCode==HttpStatusCode.OK);Assert.Single(results,r=>r.StatusCode==HttpStatusCode.Conflict);
        Assert.NotNull((await Read<FulfillmentResponse>(results.Single(r=>r.StatusCode==HttpStatusCode.OK))).CollectedFromSellerAtUtc);
        Assert.Equal(ParticipationStatus.Completed,(await Read<MyOrderDetail>(await x.Customer.GetAsync($"/api/v1/me/orders/{x.Order}"))).Order.Status);
        await M7Edit(x,async db=>{
            Assert.Equal(1,await db.AuditLogs.CountAsync(a=>a.EntityId==x.Fulfillment && a.Action=="SellerStockHandedOver"));
            Assert.Equal(2,await db.Notifications.CountAsync(n=>n.EntityId==x.Fulfillment && n.Type==NotificationType.PilotOutForDelivery));
            var o=await db.PaymentOrders.SingleAsync(o=>o.Id==x.Order);Assert.Equal(3000,o.DeliveryFeeMinor);
            Assert.DoesNotContain("Synthetic delivery address",o.AddressCiphertext);
        });
    }

    [Theory] [InlineData(ProductType.Service)] [InlineData(ProductType.Membership)]
    public async Task Module7_service_redemption_distinct_semantics_concurrent_and_no_delivery(ProductType type)
    {
        using var x=await M7Setup(type);await M7Ready(x);
        var detail=await Read<FulfillmentResponse>(await x.Customer.GetAsync($"/api/v1/mvp/customer/fulfillments/{x.Fulfillment}"));
        Assert.Equal("seller_redemption",detail.Kind);Assert.Equal("service_ready",detail.Status);Assert.Null(detail.DeliveryAddress);
        Assert.DoesNotContain("pickup",detail.Instructions!,StringComparison.OrdinalIgnoreCase);
        await M7NoPhysicalPickup(x);
        Assert.Equal(HttpStatusCode.Conflict,(await x.Admin.PostAsJsonAsync($"/api/v1/mvp/admin/fulfillments/{x.Fulfillment}/status",new FulfillmentAction(FulfillmentState.CollectedFromSeller,null))).StatusCode);
        var pass=await M7Pass(x);
        Assert.Equal(HttpStatusCode.BadRequest,(await x.Seller.PostAsJsonAsync($"/api/v1/mvp/seller/fulfillments/{x.Fulfillment}/handover",new SecretRequest("000000"))).StatusCode);
        var results=await Task.WhenAll(Enumerable.Range(0,2).Select(_=>x.Seller.PostAsJsonAsync($"/api/v1/mvp/seller/fulfillments/{x.Fulfillment}/handover",new SecretRequest(pass.BackupCode))));
        Assert.Single(results,r=>r.StatusCode==HttpStatusCode.OK);Assert.Single(results,r=>r.StatusCode==HttpStatusCode.Conflict);
        Assert.Null((await Read<FulfillmentResponse>(results.Single(r=>r.StatusCode==HttpStatusCode.OK))).CollectedFromSellerAtUtc);
        Assert.Equal(ParticipationStatus.Completed,(await Read<MyOrderDetail>(await x.Customer.GetAsync($"/api/v1/me/orders/{x.Order}"))).Order.Status);
        await M7Edit(x,async db=>{
            Assert.Equal(1,await db.AuditLogs.CountAsync(a=>a.EntityId==x.Fulfillment && a.Action=="SellerEntitlementRedeemed"));
            var c=await db.Campaigns.SingleAsync(c=>c.Id==x.Campaign);Assert.False(await db.Inventories.AnyAsync(i=>i.ProductVariantId==c.ProductVariantId));
        });
    }

    [Fact]
    public async Task Module7_customer_seller_IDOR_and_broad_projection_privacy()
    {
        using var x=await M7Setup(delivery:true);await M7Ready(x,true);var pass=await M7Pass(x);
        using var other=await CheckoutCustomer(x.Factory);
        Assert.Equal(HttpStatusCode.NotFound,(await other.GetAsync($"/api/v1/mvp/customer/fulfillments/{x.Fulfillment}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await other.PostAsync($"/api/v1/mvp/customer/fulfillments/{x.Fulfillment}/pass",null)).StatusCode);
        using var foreign=x.Factory.CreateClient();var user=await Register(foreign,"foreign-seller");await Login(foreign,user);
        var seller=await Read<SellerResponse>(await foreign.PostAsJsonAsync("/api/v1/seller/profile",SellerRequest("Foreign seller")));
        await Read<SellerReviewResponse>(await x.Admin.PostAsJsonAsync($"/api/v1/admin/sellers/{seller.Id}/approve",new ReviewRequest(null)));await Login(foreign,user);
        Assert.Equal(HttpStatusCode.NotFound,(await foreign.GetAsync($"/api/v1/mvp/seller/fulfillments/{x.Fulfillment}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await foreign.PostAsJsonAsync($"/api/v1/mvp/seller/fulfillments/{x.Fulfillment}/handover",new SecretRequest(pass.BackupCode))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await x.Seller.GetAsync("/api/v1/mvp/admin/fulfillments")).StatusCode);
        foreach(var path in new[]{"/api/v1/me/orders",$"/api/v1/me/orders/{x.Order}","/api/v1/mvp/customer/fulfillments","/api/v1/mvp/seller/fulfillments","/api/v1/mvp/admin/fulfillments"})
        {
            var client=path.Contains("/seller/")?x.Seller:path.Contains("/admin/")?x.Admin:x.Customer;
            var json=await client.GetStringAsync(path);Assert.DoesNotContain(pass.BackupCode,json);Assert.DoesNotContain(pass.OpaqueToken,json);
            Assert.DoesNotContain("tokenHash",json,StringComparison.OrdinalIgnoreCase);Assert.DoesNotContain("backupCodeHash",json,StringComparison.OrdinalIgnoreCase);
            if(!path.Contains("/admin/") && !path.Contains("/customer/"))Assert.DoesNotContain("Synthetic delivery address",json);
        }
    }

    [Fact]
    public async Task Module7_queue_filters_pagination_constant_batches_and_strict_JSON()
    {
        using var x=await M7Setup();await M7Ready(x);
        var page=await Read<PageResponse<FulfillmentResponse>>(await x.Seller.GetAsync($"/api/v1/mvp/seller/fulfillments?campaign={x.Campaign}&kind=seller_pickup&state=ReadyForCollection&pageSize=1"));
        Assert.Equal(x.Fulfillment,Assert.Single(page.Items).Id);Assert.Equal(1,page.Total);
        Assert.Empty((await Read<PageResponse<FulfillmentResponse>>(await x.Seller.GetAsync($"/api/v1/mvp/seller/fulfillments?campaign={x.Campaign}&kind=founder_delivery"))).Items);
        foreach(var query in new[]{"page=0","pageSize=101","state=99","kind=unknown"})Assert.Equal(HttpStatusCode.BadRequest,(await x.Seller.GetAsync("/api/v1/mvp/seller/fulfillments?"+query)).StatusCode);
        var body=new StringContent("{\"secret\":\"000000\",\"customerId\":\"forged\"}",System.Text.Encoding.UTF8,"application/json");
        Assert.Equal(HttpStatusCode.BadRequest,(await x.Seller.PostAsync($"/api/v1/mvp/seller/fulfillments/{x.Fulfillment}/handover",body)).StatusCode);
        // The interceptor measures the service SQL, independently of HTTP authentication queries.
        var counter=new OrdersCounter();
        await using var db=new MarketplaceDb(new DbContextOptionsBuilder<MarketplaceDb>().UseNpgsql(fixture.Connection).AddInterceptors(counter).Options);
        await using var scope=x.Factory.Services.CreateAsyncScope();
        var service=Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<Marketplace>(scope.ServiceProvider,db);
        var customerId=await db.PaymentOrders.Where(o=>o.Id==x.Order).Select(o=>o.CustomerId).SingleAsync();counter.Commands.Clear();
        await service.Fulfillments(new(customerId,false,"module7-query"),"customer",new(1,1),null,null,null,null,default);
        Assert.Equal(2,counter.Commands.Count);Assert.DoesNotContain(counter.Commands,s=>s.Sql.Contains("TokenHash") || s.Sql.Contains("BackupCodeHash"));
    }

    [Fact]
    public async Task Module7_completion_vs_cancellation_uses_existing_commercial_locks()
    {
        using var x=await M7Setup();await M7Ready(x);var pass=await M7Pass(x);
        var completion=x.Seller.PostAsJsonAsync($"/api/v1/mvp/seller/fulfillments/{x.Fulfillment}/handover",new SecretRequest(pass.BackupCode));
        var cancellation=x.Customer.PostAsync($"/api/v1/mvp/customer/orders/{x.Order}/cancel",null);
        await Task.WhenAll(completion,cancellation);
        Assert.Equal(HttpStatusCode.OK,(await completion).StatusCode);Assert.Equal(HttpStatusCode.Conflict,(await cancellation).StatusCode);
        Assert.Equal(ParticipationStatus.Completed,(await Read<MyOrderDetail>(await x.Customer.GetAsync($"/api/v1/me/orders/{x.Order}"))).Order.Status);
    }

    [Theory]
    [InlineData(ProductType.Service,false)] [InlineData(ProductType.Service,true)]
    [InlineData(ProductType.Membership,false)] [InlineData(ProductType.Membership,true)]
    public async Task Module7_cancelled_or_refunded_services_cannot_redeem(ProductType type,bool refunded)
    {
        using var x=await M7Setup(type);await M7Ready(x);var pass=await M7Pass(x);
        await M7Edit(x,async db=>{
            var o=await db.PaymentOrders.SingleAsync(o=>o.Id==x.Order);
            if(refunded){o.State=PaymentState.Refunded;o.RefundedMinor=o.MaximumAmountMinor;}
            else await db.Reservations.Where(r=>r.Id==o.ReservationId).ExecuteUpdateAsync(u=>u.SetProperty(r=>r.Status,ReservationStatus.Cancelled));
            await db.SaveChangesAsync();
        });
        Assert.Equal(HttpStatusCode.Conflict,(await x.Seller.PostAsJsonAsync($"/api/v1/mvp/seller/fulfillments/{x.Fulfillment}/handover",new SecretRequest(pass.BackupCode))).StatusCode);
    }

    [Fact]
    public async Task Module7_invalid_and_expired_QR_and_unconfirmed_campaign_reject()
    {
        using var x=await M7Setup();
        await M7Edit(x,async db=>{await db.Campaigns.Where(c=>c.Id==x.Campaign).ExecuteUpdateAsync(u=>u.SetProperty(c=>c.Status,CampaignStatus.Open));});
        Assert.Equal(HttpStatusCode.Conflict,(await x.Customer.PostAsync($"/api/v1/mvp/customer/fulfillments/{x.Fulfillment}/confirm-choice",null)).StatusCode);
        await M7Edit(x,async db=>{await db.Campaigns.Where(c=>c.Id==x.Campaign).ExecuteUpdateAsync(u=>u.SetProperty(c=>c.Status,CampaignStatus.Successful));});
        await M7Ready(x);var pass=await M7Pass(x);
        Assert.Equal(HttpStatusCode.BadRequest,(await x.Seller.PostAsJsonAsync($"/api/v1/mvp/seller/fulfillments/{x.Fulfillment}/handover",new SecretRequest(new string('A',64)))).StatusCode);
        await M7Edit(x,async db=>{await db.OrderFulfillments.Where(f=>f.Id==x.Fulfillment).ExecuteUpdateAsync(u=>u.SetProperty(f=>f.SecretExpiresAtUtc,DateTimeOffset.UtcNow.AddSeconds(-1)));});
        Assert.Equal(HttpStatusCode.BadRequest,(await x.Seller.PostAsJsonAsync($"/api/v1/mvp/seller/fulfillments/{x.Fulfillment}/handover",new SecretRequest(pass.OpaqueToken))).StatusCode);
    }
}

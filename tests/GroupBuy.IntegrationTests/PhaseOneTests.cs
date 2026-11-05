using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using GroupBuy.Application;
using GroupBuy.Domain;
using GroupBuy.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
namespace GroupBuy.IntegrationTests;
public partial class BackendTests
{
    private static PaymentOrderRequest Checkout(Guid campaign,decimal quantity,string? key=null)=>new(campaign,quantity,key??Guid.NewGuid().ToString("N"),OrderFulfillmentMode.SellerPickup,null);
    [Theory][InlineData("seller")][InlineData("customer")][InlineData("product")][InlineData("location")]
    public async Task Pilot_async_capture_refunds_when_checkout_participants_or_resources_become_unavailable(string resource)
    {
        var x=await Setup(2,true,disableWorker:true);using var f=x.Factory;using var customer=x.Customer;using var seller=x.Seller;using var admin=x.Admin;var order=await Read<PaymentOrderResponse>(await customer.PostAsJsonAsync("/api/v1/mvp/customer/orders",Checkout(x.Campaign,1)));
        await using var scope=f.Services.CreateAsyncScope();var db=scope.ServiceProvider.GetRequiredService<MarketplaceDb>();var c=await db.Campaigns.AsNoTracking().SingleAsync(c=>c.Id==x.Campaign);var s=await db.Sellers.AsNoTracking().SingleAsync(s=>s.Id==c.SellerId);
        if(resource is "seller" or "customer")await db.UsersProfile.Where(u=>u.Id==(resource=="seller"?s.OwnerUserId:x.CustomerId)).ExecuteUpdateAsync(update=>update.SetProperty(u=>u.Status,AccountStatus.Suspended));
        if(resource=="product"){var pid=await db.Variants.Where(v=>v.Id==c.ProductVariantId).Select(v=>v.ProductId).SingleAsync();await db.Products.Where(p=>p.Id==pid).ExecuteUpdateAsync(update=>update.SetProperty(p=>p.Status,ResourceStatus.Inactive));}
        if(resource=="location")await db.Locations.Where(l=>l.Id==c.SellerLocationId).ExecuteUpdateAsync(update=>update.SetProperty(l=>l.IsActive,false));
        var gateway=(SandboxPaymentGateway)scope.ServiceProvider.GetRequiredService<IPaymentGateway>();var e=await gateway.Simulate(order.ProviderOrderId!,"captured",default);var service=scope.ServiceProvider.GetRequiredService<IPhaseOne>();await service.Webhook(e.Raw,e.Signature,e.EventId,default);await service.Process(default);
        var result=await db.PaymentOrders.AsNoTracking().SingleAsync(o=>o.Id==order.Id);Assert.Null(result.ReservationId);Assert.Equal(PaymentState.Refunded,result.State);Assert.Equal(result.MaximumAmountMinor,result.RefundedMinor);Assert.Equal(0,(await db.Campaigns.AsNoTracking().SingleAsync(c=>c.Id==x.Campaign)).ReservedQuantity);
    }
    [Fact]
    public async Task Pilot_multiple_orders_for_one_customer_finalize_without_duplicate_notifications()
    {
        var x=await Setup(4,true,disableWorker:true);using var f=x.Factory;using var customer=x.Customer;using var seller=x.Seller;using var admin=x.Admin;
        for(var i=0;i<2;i++){var o=await Read<PaymentOrderResponse>(await customer.PostAsJsonAsync("/api/v1/mvp/customer/orders",Checkout(x.Campaign,2)));await Read<PaymentOrderResponse>(await customer.PostAsJsonAsync($"/api/v1/mvp/customer/orders/{o.Id}/sandbox?success=true",new{}));}
        await Read<TierCampaignResponse>(await seller.PostAsync($"/api/v1/mvp/seller/campaigns/{x.Campaign}/finalize",null));await using var scope=f.Services.CreateAsyncScope();var db=scope.ServiceProvider.GetRequiredService<MarketplaceDb>();Assert.Equal(2,await db.OrderFulfillments.CountAsync(f=>db.PaymentOrders.Any(o=>o.Id==f.PaymentOrderId && o.CampaignId==x.Campaign)));Assert.Equal(1,await db.Notifications.CountAsync(n=>n.RecipientUserId==x.CustomerId && n.EntityId==x.Campaign && n.Type==NotificationType.PilotCampaignFinalized));
        var metrics=await Read<PilotMetrics>(await admin.GetAsync("/api/v1/mvp/admin/metrics"));Assert.True(metrics.MinimumReached>=1);Assert.InRange(metrics.InterestConversion,0,1);
    }
    [Fact]
    public async Task Pilot_authorized_and_failed_payments_do_not_count_and_released_holds_cannot_reclaim_capacity()
    {
        var x=await Setup(4,true,disableWorker:true);using var f=x.Factory;using var customer=x.Customer;using var seller=x.Seller;using var admin=x.Admin;
        var order=await Read<PaymentOrderResponse>(await customer.PostAsJsonAsync("/api/v1/mvp/customer/orders",Checkout(x.Campaign,1)));
        await using(var scope=f.Services.CreateAsyncScope())
        {
            var gateway=(SandboxPaymentGateway)scope.ServiceProvider.GetRequiredService<IPaymentGateway>();var service=scope.ServiceProvider.GetRequiredService<IPhaseOne>();var e=await gateway.Simulate(order.ProviderOrderId!,"authorized",default);await service.Webhook(e.Raw,e.Signature,e.EventId,default);await service.Process(default);
        }
        order=await Read<PaymentOrderResponse>(await customer.GetAsync($"/api/v1/mvp/customer/orders/{order.Id}"));Assert.Equal(PaymentState.Authorized,order.State);Assert.Null(order.ReservationId);
        order=await Read<PaymentOrderResponse>(await customer.PostAsJsonAsync($"/api/v1/mvp/customer/orders/{order.Id}/sandbox?success=false",new{}));Assert.Equal(PaymentState.Failed,order.State);
        order=await Read<PaymentOrderResponse>(await customer.PostAsJsonAsync($"/api/v1/mvp/customer/orders/{order.Id}/sandbox?success=true",new{}));Assert.Null(order.ReservationId);Assert.Equal(PaymentState.RefundPending,order.State);
        await using var check=f.Services.CreateAsyncScope();var db=check.ServiceProvider.GetRequiredService<MarketplaceDb>();Assert.Equal(0,(await db.Campaigns.SingleAsync(c=>c.Id==x.Campaign)).ReservedQuantity);
        var expiring=await Read<PaymentOrderResponse>(await customer.PostAsJsonAsync("/api/v1/mvp/customer/orders",Checkout(x.Campaign,1)));await db.PaymentOrders.Where(o=>o.Id==expiring.Id).ExecuteUpdateAsync(s=>s.SetProperty(o=>o.ExpiresAtUtc,DateTimeOffset.UtcNow.AddSeconds(-1)));await check.ServiceProvider.GetRequiredService<IPhaseOne>().Process(default);expiring=await Read<PaymentOrderResponse>(await customer.GetAsync($"/api/v1/mvp/customer/orders/{expiring.Id}"));Assert.Equal(PaymentState.Expired,expiring.State);expiring=await Read<PaymentOrderResponse>(await customer.PostAsJsonAsync($"/api/v1/mvp/customer/orders/{expiring.Id}/sandbox?success=true",new{}));Assert.Null(expiring.ReservationId);
    }
    [Fact]
    public async Task Pilot_provider_refund_success_survives_local_crash_and_retry_without_double_refund()
    {
        var x=await Setup(4,true,disableWorker:true);using var f=x.Factory;using var customer=x.Customer;using var seller=x.Seller;using var admin=x.Admin;
        var order=await Read<PaymentOrderResponse>(await customer.PostAsJsonAsync("/api/v1/mvp/customer/orders",Checkout(x.Campaign,1)));await Read<PaymentOrderResponse>(await customer.PostAsJsonAsync($"/api/v1/mvp/customer/orders/{order.Id}/sandbox?success=true",new{}));Assert.Equal(HttpStatusCode.NoContent,(await customer.PostAsync($"/api/v1/mvp/customer/orders/{order.Id}/cancel",null)).StatusCode);
        await using(var scope=f.Services.CreateAsyncScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<MarketplaceDb>();var r=await db.PaymentRefunds.SingleAsync(r=>r.PaymentOrderId==order.Id);var o=await db.PaymentOrders.SingleAsync(o=>o.Id==order.Id);await scope.ServiceProvider.GetRequiredService<IPaymentGateway>().Refund(o.ProviderPaymentId!,r.Id,r.AmountMinor,default);
            // Simulated process loss: deliberately do not mark the local refund complete.
        }
        for(var i=0;i<2;i++){await using var scope=f.Services.CreateAsyncScope();await scope.ServiceProvider.GetRequiredService<IPhaseOne>().Process(default);}
        order=await Read<PaymentOrderResponse>(await customer.GetAsync($"/api/v1/mvp/customer/orders/{order.Id}"));Assert.Equal(order.MaximumAmountMinor,order.RefundedMinor);await using var check=f.Services.CreateAsyncScope();var ledger=check.ServiceProvider.GetRequiredService<MarketplaceDb>();Assert.Equal(1,await ledger.SandboxRefundReceipts.CountAsync(r=>r.LocalOrderId==order.Id));
    }
    [Fact]
    public async Task Pilot_one_extension_and_disclosed_near_target_acceptance_do_not_change_prices()
    {
        var x=await Setup(10,true,disableWorker:true);using var f=x.Factory;using var customer=x.Customer;using var seller=x.Seller;using var admin=x.Admin;
        var current=await Read<TierCampaignResponse>(await customer.GetAsync($"/api/v1/mvp/campaigns/{x.Campaign}"));var request=new RescueRequest("extend",current.Campaign.ExpiresAtUtc.AddHours(1),"Synthetic controlled one-time extension");var extended=await Read<TierCampaignResponse>(await seller.PostAsJsonAsync($"/api/v1/mvp/seller/campaigns/{x.Campaign}/rescue",request));Assert.Equal(1,extended.ExtensionCount);Assert.Equal(current.Tiers,extended.Tiers);
        Assert.Equal(HttpStatusCode.Conflict,(await seller.PostAsJsonAsync($"/api/v1/mvp/seller/campaigns/{x.Campaign}/rescue",request with{NewDeadlineUtc=extended.Campaign.ExpiresAtUtc.AddHours(1)})).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await seller.PostAsJsonAsync($"/api/v1/mvp/seller/campaigns/{x.Campaign}/rescue",new RescueRequest("accept-near-target",null,"No undisclosed threshold may be invented"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await seller.PutAsJsonAsync($"/api/v1/mvp/seller/campaigns/{x.Campaign}/tiers",new TierConfigurationRequest("synthetic-area",[new(1,1)],null))).StatusCode);
    }
    [Fact]
    public async Task Pilot_paid_checkout_refunds_best_tier_and_redeems_one_time_pass()
    {
        var x=await Setup(2,true,disableWorker:true);using var f=x.Factory;using var customer=x.Customer;using var seller=x.Seller;using var admin=x.Admin;
        var request=Checkout(x.Campaign,2);var order=await Read<PaymentOrderResponse>(await customer.PostAsJsonAsync("/api/v1/mvp/customer/orders",request));Assert.Equal(16000,order.MaximumAmountMinor);Assert.Null(order.ReservationId);Assert.Equal(0,order.DeliveryFeeMinor);
        var duplicate=await Read<PaymentOrderResponse>(await customer.PostAsJsonAsync("/api/v1/mvp/customer/orders",request));Assert.Equal(order.Id,duplicate.Id);
        Assert.Equal(HttpStatusCode.Conflict,(await customer.PostAsJsonAsync("/api/v1/mvp/customer/orders",request with{Quantity=1})).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await customer.PostAsJsonAsync($"/api/v1/group-buys/{x.Campaign}/reservations",new ReservationRequest(1,"legacy-bypass-key"))).StatusCode);
        var captures=await Task.WhenAll(Enumerable.Range(0,2).Select(_=>customer.PostAsJsonAsync($"/api/v1/mvp/customer/orders/{order.Id}/sandbox?success=true",new{})));var first=await Read<PaymentOrderResponse>(captures[0]);var second=await Read<PaymentOrderResponse>(captures[1]);Assert.Equal(first.ReservationId,second.ReservationId);order=first;Assert.NotNull(order.ReservationId);
        var finalized=await Read<TierCampaignResponse>(await seller.PostAsync($"/api/v1/mvp/seller/campaigns/{x.Campaign}/finalize",null));Assert.Equal(60,finalized.FinalUnitPrice);
        await using(var scope=f.Services.CreateAsyncScope())await scope.ServiceProvider.GetRequiredService<IPhaseOne>().Process(default);
        order=await Read<PaymentOrderResponse>(await customer.GetAsync($"/api/v1/mvp/customer/orders/{order.Id}"));Assert.Equal(4000,order.RefundedMinor);Assert.Equal(12000,order.FinalAmountMinor);
        var id=order.FulfillmentId!.Value;await Read<FulfillmentResponse>(await customer.PostAsync($"/api/v1/mvp/customer/fulfillments/{id}/confirm-choice",null));
        await using(var bypass=f.Services.CreateAsyncScope()){var p=await bypass.ServiceProvider.GetRequiredService<MarketplaceDb>().OrderFulfillments.Where(f=>f.Id==id).Select(f=>f.PickupId).SingleAsync();var details=await Read<PickupDetailResponse>(await customer.GetAsync($"/api/v1/me/pickups/{p}"));Assert.True(details.UsesTierPricing);Assert.Equal(PickupPaymentPolicy.VerifiedCapturedPayment,details.PaymentPolicy);Assert.Equal(HttpStatusCode.Conflict,(await customer.PostAsync($"/api/v1/me/pickups/{p}/code",null)).StatusCode);Assert.Equal(HttpStatusCode.Conflict,(await seller.PostAsync($"/api/v1/seller/pickups/{p}/ready",null)).StatusCode);}
        foreach(var state in new[]{FulfillmentState.Preparing,FulfillmentState.ReadyForCollection})await Read<FulfillmentResponse>(await seller.PostAsJsonAsync($"/api/v1/mvp/seller/fulfillments/{id}/status",new FulfillmentAction(state,null)));
        Assert.Equal(HttpStatusCode.Conflict,(await seller.PostAsync($"/api/v1/mvp/seller/fulfillments/{id}/redeem",null)).StatusCode);
        var pass=await Read<FulfillmentPass>(await customer.PostAsync($"/api/v1/mvp/customer/fulfillments/{id}/pass",null));Assert.Equal(64,pass.OpaqueToken.Length);Assert.True(pass.BackupCode.Length==6 && pass.BackupCode.All(char.IsAsciiDigit),"Backup code format invalid.");
        var visible=await seller.GetStringAsync($"/api/v1/mvp/seller/fulfillments/{id}");Assert.False(visible.Contains(pass.OpaqueToken,StringComparison.Ordinal),"Opaque pass leaked.");Assert.False(visible.Contains(pass.BackupCode,StringComparison.Ordinal),"Backup code leaked.");Assert.DoesNotContain("tokenHash",visible,StringComparison.OrdinalIgnoreCase);
        var verified=await Read<FulfillmentResponse>(await seller.PostAsJsonAsync("/api/v1/mvp/seller/fulfillments/verify",new SecretRequest(pass.OpaqueToken)));Assert.Equal(id,verified.Id);
        var handovers=await Task.WhenAll(Enumerable.Range(0,2).Select(_=>seller.PostAsync($"/api/v1/mvp/seller/fulfillments/{id}/redeem",null)));Assert.Single(handovers,r=>r.StatusCode==HttpStatusCode.OK);Assert.Single(handovers,r=>r.StatusCode==HttpStatusCode.Conflict);var done=await Read<FulfillmentResponse>(handovers.Single(r=>r.IsSuccessStatusCode));Assert.True(done.Redeemed);
        Assert.Equal(HttpStatusCode.Conflict,(await seller.PostAsync($"/api/v1/mvp/seller/fulfillments/{id}/redeem",null)).StatusCode);
    }
    [Fact]
    public async Task Pilot_holds_serialize_capacity_and_cancellation_releases_only_own_hold()
    {
        var x=await Setup(2,true,disableWorker:true);using var f=x.Factory;using var customer=x.Customer;using var seller=x.Seller;using var admin=x.Admin;
        var results=await Task.WhenAll(Enumerable.Range(0,5).Select(_=>customer.PostAsJsonAsync("/api/v1/mvp/customer/orders",Checkout(x.Campaign,1))));Assert.Equal(2,results.Count(r=>r.IsSuccessStatusCode));Assert.Equal(3,results.Count(r=>r.StatusCode==HttpStatusCode.Conflict));
        var held=await Read<PaymentOrderResponse>(results.First(r=>r.IsSuccessStatusCode));
        await using(var stale=f.Services.CreateAsyncScope()){var service=stale.ServiceProvider.GetRequiredService<IPhaseOne>();var unchanged=await service.Finalize(null,x.Campaign,default);Assert.Equal(CampaignStatus.Open,unchanged.Campaign.Status);Assert.Equal(0,(await stale.ServiceProvider.GetRequiredService<MarketplaceDb>().Campaigns.SingleAsync(c=>c.Id==x.Campaign)).WorkerFailureCount);}
        using var outsider=f.CreateClient();var other=await Register(outsider,"outsider");await Login(outsider,other);Assert.Equal(HttpStatusCode.NotFound,(await outsider.GetAsync($"/api/v1/mvp/customer/orders/{held.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,(await customer.PostAsync($"/api/v1/mvp/customer/orders/{held.Id}/cancel",null)).StatusCode);
        var late=await Read<PaymentOrderResponse>(await customer.PostAsJsonAsync($"/api/v1/mvp/customer/orders/{held.Id}/sandbox?success=true",new{}));Assert.Null(late.ReservationId);Assert.Equal(PaymentState.RefundPending,late.State);
        await using(var scope=f.Services.CreateAsyncScope())await scope.ServiceProvider.GetRequiredService<IPhaseOne>().Process(default);
        late=await Read<PaymentOrderResponse>(await customer.GetAsync($"/api/v1/mvp/customer/orders/{held.Id}"));Assert.Equal(late.MaximumAmountMinor,late.RefundedMinor);
    }
    [Fact]
    public async Task Pilot_interest_is_idempotent_and_seller_demand_contains_no_customer_identity()
    {
        var x=await Setup(4,true,disableWorker:true);using var f=x.Factory;using var customer=x.Customer;using var seller=x.Seller;using var admin=x.Admin;
        Guid productId;await using(var scope=f.Services.CreateAsyncScope()){var db=scope.ServiceProvider.GetRequiredService<MarketplaceDb>();productId=await (from c in db.Campaigns join v in db.Variants on c.ProductVariantId equals v.Id where c.Id==x.Campaign select v.ProductId).SingleAsync();}
        var request=new InterestRequest(productId,"synthetic-area",2);var a=await Read<InterestResponse>(await customer.PostAsJsonAsync("/api/v1/mvp/customer/interests",request));var b=await Read<InterestResponse>(await customer.PostAsJsonAsync("/api/v1/mvp/customer/interests",request));Assert.Equal(a.Id,b.Id);
        var demand=await Read<PageResponse<DemandResponse>>(await seller.GetAsync("/api/v1/mvp/seller/demand"));Assert.Contains(demand.Items,d=>d.ProductId==productId && d.Customers==1 && d.Quantity==2);
        var json=await seller.GetStringAsync("/api/v1/mvp/seller/demand");Assert.DoesNotContain(x.CustomerId.ToString(),json);Assert.DoesNotContain("@example.invalid",json);
        await Read<InterestResponse>(await customer.PostAsJsonAsync("/api/v1/mvp/customer/interests/withdraw",request));var after=await Read<PageResponse<DemandResponse>>(await seller.GetAsync("/api/v1/mvp/seller/demand"));Assert.DoesNotContain(after.Items,d=>d.ProductId==productId);
        await Read<InterestResponse>(await customer.PostAsJsonAsync("/api/v1/mvp/customer/interests",request));await using(var scope=f.Services.CreateAsyncScope())await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().Products.Where(p=>p.Id==productId).ExecuteUpdateAsync(s=>s.SetProperty(p=>p.Status,ResourceStatus.Inactive));var withdrawn=await Read<InterestResponse>(await customer.PostAsJsonAsync("/api/v1/mvp/customer/interests/withdraw",request));Assert.False(withdrawn.Active);
        Assert.Equal(HttpStatusCode.Forbidden,(await customer.GetAsync("/api/v1/mvp/seller/demand")).StatusCode);
    }
    [Fact]
    public async Task Pilot_failed_campaign_refunds_every_captured_paise_once()
    {
        var x=await Setup(4,true,disableWorker:true);using var f=x.Factory;using var customer=x.Customer;using var seller=x.Seller;using var admin=x.Admin;
        var order=await Read<PaymentOrderResponse>(await customer.PostAsJsonAsync("/api/v1/mvp/customer/orders",Checkout(x.Campaign,1)));
        await Read<PaymentOrderResponse>(await customer.PostAsJsonAsync($"/api/v1/mvp/customer/orders/{order.Id}/sandbox?success=true",new{}));
        await using(var scope=f.Services.CreateAsyncScope()){var db=scope.ServiceProvider.GetRequiredService<MarketplaceDb>();await db.Campaigns.Where(c=>c.Id==x.Campaign).ExecuteUpdateAsync(s=>s.SetProperty(c=>c.ExpiresAtUtc,DateTimeOffset.UtcNow.AddSeconds(-1)));}
        using(var paused=new ApiFactory(fixture.Connection,disableWorker:true)){using var start=paused.CreateClient();await using var scope=paused.Services.CreateAsyncScope();await scope.ServiceProvider.GetRequiredService<IPhaseOne>().Process(default);Assert.True(await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().PaymentRefunds.AnyAsync(r=>r.PaymentOrderId==order.Id));}
        var campaign=await Read<TierCampaignResponse>(await seller.PostAsync($"/api/v1/mvp/seller/campaigns/{x.Campaign}/finalize",null));Assert.Equal(CampaignStatus.Failed,campaign.Campaign.Status);Assert.Null(campaign.FinalUnitPrice);
        await Read<TierCampaignResponse>(await seller.PostAsync($"/api/v1/mvp/seller/campaigns/{x.Campaign}/finalize",null));
        for(var i=0;i<2;i++){await using var scope=f.Services.CreateAsyncScope();await scope.ServiceProvider.GetRequiredService<IPhaseOne>().Process(default);}
        order=await Read<PaymentOrderResponse>(await customer.GetAsync($"/api/v1/mvp/customer/orders/{order.Id}"));Assert.Equal(PaymentState.Refunded,order.State);Assert.Equal(order.MaximumAmountMinor,order.RefundedMinor);
        await using var check=f.Services.CreateAsyncScope();var database=check.ServiceProvider.GetRequiredService<MarketplaceDb>();Assert.Equal(1,await database.PaymentRefunds.CountAsync(r=>r.PaymentOrderId==order.Id));Assert.Equal(0,(await database.Campaigns.SingleAsync(c=>c.Id==x.Campaign)).CommittedQuantity);
    }
    [Fact]
    public async Task Pilot_delivery_is_server_priced_private_and_requires_founder_OTP_handover()
    {
        var x=await Setup(2,true,disableWorker:true);using var f=x.Factory;using var customer=x.Customer;using var seller=x.Seller;using var admin=x.Admin;
        var address=new DeliveryAddress("Synthetic Recipient","0000000000","Synthetic test address",null,"synthetic-area","TEST01");var request=Checkout(x.Campaign,2) with{FulfillmentMode=OrderFulfillmentMode.FounderDelivery,Address=address};
        Assert.Equal(HttpStatusCode.BadRequest,(await customer.PostAsJsonAsync("/api/v1/mvp/customer/orders",request with{Address=address with{PostalCode="OUTSIDE"}})).StatusCode);
        var order=await Read<PaymentOrderResponse>(await customer.PostAsJsonAsync("/api/v1/mvp/customer/orders",request));Assert.Equal(3000,order.DeliveryFeeMinor);Assert.Equal(19000,order.MaximumAmountMinor);
        await Read<PaymentOrderResponse>(await customer.PostAsJsonAsync($"/api/v1/mvp/customer/orders/{order.Id}/sandbox?success=true",new{}));await Read<TierCampaignResponse>(await seller.PostAsync($"/api/v1/mvp/seller/campaigns/{x.Campaign}/finalize",null));
        await using(var scope=f.Services.CreateAsyncScope())await scope.ServiceProvider.GetRequiredService<IPhaseOne>().Process(default);
        order=await Read<PaymentOrderResponse>(await customer.GetAsync($"/api/v1/mvp/customer/orders/{order.Id}"));var id=order.FulfillmentId!.Value;await Read<FulfillmentResponse>(await customer.PostAsync($"/api/v1/mvp/customer/fulfillments/{id}/confirm-choice",null));
        foreach(var state in new[]{FulfillmentState.Preparing,FulfillmentState.ReadyForCollection})await Read<FulfillmentResponse>(await seller.PostAsJsonAsync($"/api/v1/mvp/seller/fulfillments/{id}/status",new FulfillmentAction(state,null)));
        Assert.Equal(HttpStatusCode.Forbidden,(await seller.PostAsJsonAsync($"/api/v1/mvp/seller/fulfillments/{id}/status",new FulfillmentAction(FulfillmentState.CollectedFromSeller,null))).StatusCode);
        var privateSeller=await seller.GetStringAsync($"/api/v1/mvp/seller/fulfillments/{id}");Assert.DoesNotContain(address.Address,privateSeller);Assert.DoesNotContain(address.Phone,privateSeller);
        foreach(var state in new[]{FulfillmentState.CollectedFromSeller,FulfillmentState.OutForDelivery})await Read<FulfillmentResponse>(await admin.PostAsJsonAsync($"/api/v1/mvp/admin/fulfillments/{id}/status",new FulfillmentAction(state,null)));
        await using(var stockScope=f.Services.CreateAsyncScope()){var stockDb=stockScope.ServiceProvider.GetRequiredService<MarketplaceDb>();var c=await stockDb.Campaigns.SingleAsync(c=>c.Id==x.Campaign);Assert.Equal(0,c.CommittedQuantity);Assert.Equal(2,c.FulfilledQuantity);Assert.Equal(0,(await stockDb.Inventories.SingleAsync(i=>i.ProductVariantId==c.ProductVariantId)).AvailableQuantity);}
        var queue=await Read<PageResponse<FulfillmentResponse>>(await admin.GetAsync("/api/v1/mvp/admin/fulfillments?area=synthetic-area"));Assert.Contains(queue.Items,r=>r.Id==id && r.DeliveryAddress!.Address==address.Address);
        var pass=await Read<FulfillmentPass>(await customer.PostAsync($"/api/v1/mvp/customer/fulfillments/{id}/pass",null));
        for(var i=0;i<5;i++)Assert.Equal(HttpStatusCode.BadRequest,(await admin.PostAsJsonAsync($"/api/v1/mvp/admin/fulfillments/{id}/verify",new SecretRequest("invalid-synthetic-pass"))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests,(await admin.PostAsJsonAsync($"/api/v1/mvp/admin/fulfillments/{id}/verify",new SecretRequest(pass.BackupCode))).StatusCode);
        await using(var scope=f.Services.CreateAsyncScope())await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().OrderFulfillments.Where(r=>r.Id==id).ExecuteUpdateAsync(s=>s.SetProperty(r=>r.LockedUntilUtc,DateTimeOffset.UtcNow.AddSeconds(-1)));
        await Read<FulfillmentResponse>(await admin.PostAsJsonAsync($"/api/v1/mvp/admin/fulfillments/{id}/verify",new SecretRequest(pass.BackupCode)));
        var done=await Read<FulfillmentResponse>(await admin.PostAsync($"/api/v1/mvp/admin/fulfillments/{id}/redeem",null));Assert.Equal(FulfillmentState.Delivered,done.State);
        await using var check=f.Services.CreateAsyncScope();var dbCheck=check.ServiceProvider.GetRequiredService<MarketplaceDb>();var persisted=await dbCheck.PaymentOrders.SingleAsync(o=>o.Id==order.Id);Assert.DoesNotContain(address.Address,persisted.AddressCiphertext);Assert.DoesNotContain(address.Phone,persisted.AddressCiphertext);
    }
    [Fact]
    public async Task Pilot_webhook_replay_deduplicates_and_unsigned_payload_does_not_change_capacity()
    {
        var x=await Setup(4,true,disableWorker:true);using var f=x.Factory;using var customer=x.Customer;using var seller=x.Seller;using var admin=x.Admin;
        var order=await Read<PaymentOrderResponse>(await customer.PostAsJsonAsync("/api/v1/mvp/customer/orders",Checkout(x.Campaign,1)));
        await using var scope=f.Services.CreateAsyncScope();var service=scope.ServiceProvider.GetRequiredService<IPhaseOne>();var gateway=(SandboxPaymentGateway)scope.ServiceProvider.GetRequiredService<IPaymentGateway>();var e=await gateway.Simulate(order.ProviderOrderId!,"captured",default);
        await Assert.ThrowsAsync<BusinessException>(()=>service.Webhook(e.Raw,new string('0',64),e.EventId,default));await service.Webhook(e.Raw,e.Signature,e.EventId,default);await service.Webhook(e.Raw,e.Signature,e.EventId,default);
        var changed=e.Raw.Concat(new byte[]{32}).ToArray();var secret=scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()["Payments:SandboxWebhookSecret"]!;var signature=Convert.ToHexString(HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret),changed));var conflict=await Assert.ThrowsAsync<BusinessException>(()=>service.Webhook(changed,signature,e.EventId,default));Assert.Equal(409,conflict.StatusCode);
        await service.Process(default);await service.Process(default);var db=scope.ServiceProvider.GetRequiredService<MarketplaceDb>();Assert.Equal(1,await db.PaymentWebhooks.CountAsync(w=>w.EventId==e.EventId));Assert.Equal(1,await db.Reservations.CountAsync(r=>r.CampaignId==x.Campaign));Assert.Equal(1,(await db.Campaigns.SingleAsync(c=>c.Id==x.Campaign)).ReservedQuantity);
    }
}

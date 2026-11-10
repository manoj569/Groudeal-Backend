using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GroupBuy.Application;
using GroupBuy.Domain;
using GroupBuy.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
namespace GroupBuy.IntegrationTests;

public partial class BackendTests
{
    private static CheckoutQuoteRequest PickupQuote(decimal quantity = 1) => new(quantity,OrderFulfillmentMode.SellerPickup);
    private static async Task<HttpClient> CheckoutCustomer(ApiFactory f)
    {
        await using var scope = f.Services.CreateAsyncScope(); var identity = scope.ServiceProvider.GetRequiredService<IIdentityService>();
        var email = Guid.NewGuid().ToString("N") + "@example.invalid"; const string password = "Synthetic!Password123";
        await identity.Register(new(email,password,"Synthetic checkout customer"),default); var token = await identity.Login(new(email,password),default);
        var client = f.CreateClient(); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",token.AccessToken); return client;
    }
    private sealed class CheckoutClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }

    [Theory]
    [InlineData(ProductType.Physical)] [InlineData(ProductType.Service)] [InlineData(ProductType.Membership)]
    public async Task Module5_quote_is_authoritative_side_effect_free_and_confirmation_is_idempotent(ProductType type)
    {
        var x = await Setup(4,true,true,type,true); using var f=x.Factory; using var customer=x.Customer; using var seller=x.Seller; using var admin=x.Admin;
        var route=$"/api/v1/group-buys/{x.Campaign}/checkout";
        var quote = await Read<CheckoutQuoteResponse>(await customer.PostAsJsonAsync(route+"/quote",PickupQuote()));
        Assert.Equal((GroupDealType)type,quote.Package.Type); Assert.Null(quote.Progress.CurrentTier);
        Assert.Equal(2,quote.Progress.NextTier!.MinimumQuantity); Assert.Equal(2,quote.Progress.QuantityNeededForNextTier);
        Assert.Equal(80,quote.MaximumUnitPrice); Assert.Equal(8000,quote.TotalMinor); Assert.Equal(quote.TotalMinor,quote.MaximumAuthorizedAmountMinor); Assert.Equal("INR",quote.Currency);
        Assert.Equal(0,quote.DeliveryFeeMinor); Assert.True(quote.ExpiresAtUtc>quote.ServerTimeUtc);
        await using(var scope=f.Services.CreateAsyncScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<MarketplaceDb>(); Assert.False(await db.PaymentOrders.AnyAsync(o=>o.CampaignId==x.Campaign)); Assert.False(await db.Reservations.AnyAsync(r=>r.CampaignId==x.Campaign));
        }
        var request = new CheckoutConfirmRequest(quote.QuoteId);
        var responses = await Task.WhenAll(Enumerable.Range(0,3).Select(_=>customer.PostAsJsonAsync(route+"/confirm",request)));
        var statuses=new List<CheckoutStatusResponse>(); foreach(var response in responses)statuses.Add(await Read<CheckoutStatusResponse>(response));
        Assert.Single(statuses.Select(s=>s.Order.Id).Distinct()); Assert.All(statuses,s=>Assert.False(s.ParticipationConfirmed));
        var order=statuses[0].Order;
        await Read<PaymentOrderResponse>(await customer.PostAsJsonAsync($"/api/v1/mvp/customer/orders/{order.Id}/sandbox?success=true",new{}));
        var success=await Read<CheckoutStatusResponse>(await customer.GetAsync($"/api/v1/me/checkouts/{order.Id}")); Assert.True(success.ParticipationConfirmed); Assert.Equal("successful",success.Status);
        var recovered=await Read<CheckoutStatusResponse>(await customer.PostAsJsonAsync(route+"/recover",request)); Assert.Equal(order.Id,recovered.Order.Id); Assert.True(recovered.ParticipationConfirmed);
        f.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()["Payments:Enabled"]="false";
        var offlineRecovered=await Read<CheckoutStatusResponse>(await customer.PostAsJsonAsync(route+"/recover",request)); Assert.True(offlineRecovered.ParticipationConfirmed); Assert.False(offlineRecovered.RequiresOperator);
        await using var check=f.Services.CreateAsyncScope(); var ledger=check.ServiceProvider.GetRequiredService<MarketplaceDb>();
        Assert.Equal(1,await ledger.PaymentOrders.CountAsync(o=>o.CampaignId==x.Campaign)); Assert.Equal(1,await ledger.AuditLogs.CountAsync(a=>a.EntityId==order.Id&&a.Action=="CheckoutConfirmed"));
    }

    [Fact]
    public async Task Module5_quote_tokens_bind_customer_deal_and_selection_and_reject_commercial_JSON()
    {
        var x=await Setup(4,true,true,ProductType.Physical,true); using var f=x.Factory; using var customer=x.Customer; using var seller=x.Seller; using var admin=x.Admin; using var other=await CheckoutCustomer(f); using var anonymous=f.CreateClient();
        var route=$"/api/v1/group-buys/{x.Campaign}/checkout";
        Assert.Equal(HttpStatusCode.Unauthorized,(await anonymous.PostAsJsonAsync(route+"/quote",PickupQuote())).StatusCode);
        var quote=await Read<CheckoutQuoteResponse>(await customer.PostAsJsonAsync(route+"/quote",PickupQuote())); var request=new CheckoutConfirmRequest(quote.QuoteId);
        Assert.Equal(HttpStatusCode.NotFound,(await other.PostAsJsonAsync(route+"/confirm",request)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await customer.PostAsJsonAsync($"/api/v1/group-buys/{Guid.NewGuid()}/checkout/confirm",request)).StatusCode);
        var token=quote.QuoteId; var at=token.Length/2; var tampered=token[..at]+(token[at]=='A'?'B':'A')+token[(at+1)..];
        Assert.Equal(HttpStatusCode.NotFound,(await customer.PostAsJsonAsync(route+"/confirm",new CheckoutConfirmRequest(tampered))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await customer.PostAsJsonAsync(route+"/quote",new { quantity=1,fulfillmentMode="SellerPickup",unitPrice=1,deliveryFee=0,sellerId=Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await customer.PostAsJsonAsync(route+"/quote",PickupQuote() with {ProductVariantId=Guid.NewGuid()})).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await customer.PostAsJsonAsync($"/api/v1/group-buys/{Guid.NewGuid()}/checkout/quote",PickupQuote())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await customer.PostAsJsonAsync(route+"/confirm",new {quoteId=quote.QuoteId,paymentStatus="captured"})).StatusCode);
        var order=(await Read<CheckoutStatusResponse>(await customer.PostAsJsonAsync(route+"/confirm",request))).Order;
        Assert.Equal(HttpStatusCode.NotFound,(await other.GetAsync($"/api/v1/me/checkouts/{order.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await other.PostAsJsonAsync($"/api/v1/me/checkouts/{order.Id}/recover",new{})).StatusCode);
    }

    [Theory] [InlineData(0)] [InlineData(-1)] [InlineData(5)] [InlineData(1.0001)]
    public async Task Module5_invalid_quantity_never_allocates_capacity(decimal quantity)
    {
        var x=await Setup(4,true,true,ProductType.Physical,true); using var f=x.Factory; using var customer=x.Customer; using var seller=x.Seller; using var admin=x.Admin;
        Assert.Equal(HttpStatusCode.BadRequest,(await customer.PostAsJsonAsync($"/api/v1/group-buys/{x.Campaign}/checkout/quote",PickupQuote(quantity))).StatusCode);
    }

    [Fact]
    public async Task Module5_expired_quote_and_changed_tier_require_requote_but_existing_order_survives_quote_expiry()
    {
        var x=await Setup(4,true,true,ProductType.Physical,true); using var f=x.Factory; using var customer=x.Customer; using var seller=x.Seller; using var admin=x.Admin; using var other=await CheckoutCustomer(f);
        var route=$"/api/v1/group-buys/{x.Campaign}/checkout";
        var quote=await Read<CheckoutQuoteResponse>(await customer.PostAsJsonAsync(route+"/quote",PickupQuote()));
        await using(var scope=f.Services.CreateAsyncScope())
        {
            var checkout=ActivatorUtilities.CreateInstance<Marketplace>(scope.ServiceProvider,new CheckoutClock(quote.ExpiresAtUtc));
            var error=await Assert.ThrowsAsync<BusinessException>(()=>checkout.ConfirmCheckout(new(x.CustomerId,false,"expiry-test"),x.Campaign,new(quote.QuoteId),default)); Assert.Equal("checkout_quote_expired",error.Code);
        }
        var otherOrder=await Read<PaymentOrderResponse>(await other.PostAsJsonAsync("/api/v1/mvp/customer/orders",Checkout(x.Campaign,2)));
        await Read<PaymentOrderResponse>(await other.PostAsJsonAsync($"/api/v1/mvp/customer/orders/{otherOrder.Id}/sandbox?success=true",new{}));
        var stale=await customer.PostAsJsonAsync(route+"/confirm",new CheckoutConfirmRequest(quote.QuoteId)); Assert.Equal(HttpStatusCode.Conflict,stale.StatusCode); Assert.Contains("checkout_quote_expired",await stale.Content.ReadAsStringAsync());
        var fresh=await Read<CheckoutQuoteResponse>(await customer.PostAsJsonAsync(route+"/quote",PickupQuote())); Assert.Equal(2,fresh.Progress.CurrentTier!.MinimumQuantity); Assert.Equal(2,fresh.Progress.QuantityNeededForNextTier);
        var order=(await Read<CheckoutStatusResponse>(await customer.PostAsJsonAsync(route+"/confirm",new CheckoutConfirmRequest(fresh.QuoteId)))).Order;
        await using var check=f.Services.CreateAsyncScope(); var resumed=ActivatorUtilities.CreateInstance<Marketplace>(check.ServiceProvider,new CheckoutClock(fresh.ExpiresAtUtc.AddSeconds(1)));
        Assert.Equal(order.Id,(await resumed.ConfirmCheckout(new(x.CustomerId,false,"expired-retry"),x.Campaign,new(fresh.QuoteId),default)).Order.Id);
    }

    [Fact]
    public async Task Module5_final_capacity_quotes_do_not_hold_stock_and_confirmation_cannot_oversell()
    {
        var x=await Setup(1,true,true,ProductType.Physical,true); using var f=x.Factory; using var customer=x.Customer; using var seller=x.Seller; using var admin=x.Admin; using var other=await CheckoutCustomer(f);
        var route=$"/api/v1/group-buys/{x.Campaign}/checkout";
        var first=await Read<CheckoutQuoteResponse>(await customer.PostAsJsonAsync(route+"/quote",PickupQuote())); var second=await Read<CheckoutQuoteResponse>(await other.PostAsJsonAsync(route+"/quote",PickupQuote()));
        var results=await Task.WhenAll(customer.PostAsJsonAsync(route+"/confirm",new CheckoutConfirmRequest(first.QuoteId)),other.PostAsJsonAsync(route+"/confirm",new CheckoutConfirmRequest(second.QuoteId)));
        Assert.Single(results,r=>r.IsSuccessStatusCode); Assert.Single(results,r=>r.StatusCode==HttpStatusCode.Conflict);
        var failed=results.Single(r=>!r.IsSuccessStatusCode); Assert.Contains("capacity_unavailable",await failed.Content.ReadAsStringAsync());
        await using var scope=f.Services.CreateAsyncScope(); Assert.Equal(1,await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().PaymentOrders.CountAsync(o=>o.CampaignId==x.Campaign));
    }

    [Fact]
    public async Task Module5_delivery_quote_encrypts_address_uses_server_fee_and_revalidates_configuration()
    {
        var x=await Setup(4,true,true,ProductType.Physical,true); using var f=x.Factory; using var customer=x.Customer; using var seller=x.Seller; using var admin=x.Admin;
        var route=$"/api/v1/group-buys/{x.Campaign}/checkout";
        var address=new DeliveryAddress("Synthetic recipient","9000000000","Synthetic delivery address",null,"synthetic-area","900001");
        var request=new CheckoutQuoteRequest(1,OrderFulfillmentMode.FounderDelivery,address);
        Assert.Equal(HttpStatusCode.BadRequest,(await customer.PostAsJsonAsync(route+"/quote",request with {Address=address with {PostalCode="900002"}})).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await customer.PostAsJsonAsync(route+"/quote",new{quantity=1,fulfillmentMode="FounderDelivery",addressId=Guid.NewGuid()})).StatusCode);
        var quote=await Read<CheckoutQuoteResponse>(await customer.PostAsJsonAsync(route+"/quote",request)); Assert.Equal(3000,quote.DeliveryFeeMinor); Assert.Equal(11000,quote.MaximumAuthorizedAmountMinor); Assert.DoesNotContain(address.Address,quote.QuoteId);
        var config=f.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(); config["Pilot:DeliveryFeeMinor"]="4000";
        var stale=await customer.PostAsJsonAsync(route+"/confirm",new CheckoutConfirmRequest(quote.QuoteId)); Assert.Equal(HttpStatusCode.Conflict,stale.StatusCode); Assert.Contains("checkout_quote_expired",await stale.Content.ReadAsStringAsync());
        config["Pilot:DeliveryFeeMinor"]="3000";
        var confirmed=await Read<CheckoutStatusResponse>(await customer.PostAsJsonAsync(route+"/confirm",new CheckoutConfirmRequest(quote.QuoteId))); Assert.Equal(3000,confirmed.Order.DeliveryFeeMinor);
        await using var scope=f.Services.CreateAsyncScope(); var order=await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().PaymentOrders.SingleAsync(o=>o.Id==confirmed.Order.Id);
        Assert.DoesNotContain(address.Address,order.AddressCiphertext); Assert.Equal(x.CustomerId,order.CustomerId);
        Assert.Contains(address.Address,scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector("Groudeal.PhaseOne.DeliveryAddress.v1").Unprotect(order.AddressCiphertext));
    }

    [Fact]
    public async Task Module5_service_area_and_payment_configuration_fail_closed_and_unsupported_modes_reject()
    {
        var x=await Setup(4,true,true,ProductType.Service,true); using var f=x.Factory; using var customer=x.Customer; using var seller=x.Seller; using var admin=x.Admin;
        var route=$"/api/v1/group-buys/{x.Campaign}/checkout/quote";
        Assert.Equal(HttpStatusCode.BadRequest,(await customer.PostAsJsonAsync(route,new CheckoutQuoteRequest(1,OrderFulfillmentMode.FounderDelivery))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await customer.PostAsJsonAsync(route,new CheckoutQuoteRequest(1,(OrderFulfillmentMode)999))).StatusCode);
        var config=f.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(); config["Pilot:Enabled"]="false"; config["Pilot:ServiceAreas:0:Id"]=null;
        var unconfigured=await customer.PostAsJsonAsync(route,PickupQuote()); Assert.Equal(HttpStatusCode.ServiceUnavailable,unconfigured.StatusCode); Assert.Contains("service_area_unconfigured",await unconfigured.Content.ReadAsStringAsync());
        config["Pilot:ServiceAreas:0:Id"]="synthetic-area"; config["Pilot:Enabled"]="false";
        var disabled=await customer.PostAsJsonAsync(route,PickupQuote()); Assert.Equal(HttpStatusCode.ServiceUnavailable,disabled.StatusCode); Assert.Contains("payments_unavailable",await disabled.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Module5_unknown_payment_recovery_uses_original_provider_order_and_refunds_final_tier_once()
    {
        var x=await Setup(2,true,true,ProductType.Physical,true); using var f=x.Factory; using var customer=x.Customer; using var seller=x.Seller; using var admin=x.Admin;
        var route=$"/api/v1/group-buys/{x.Campaign}/checkout";
        var quote=await Read<CheckoutQuoteResponse>(await customer.PostAsJsonAsync(route+"/quote",PickupQuote(2)));
        var order=(await Read<CheckoutStatusResponse>(await customer.PostAsJsonAsync(route+"/confirm",new CheckoutConfirmRequest(quote.QuoteId)))).Order;
        await using(var scope=f.Services.CreateAsyncScope())
        {
            var gateway=(SandboxPaymentGateway)scope.ServiceProvider.GetRequiredService<IPaymentGateway>(); await gateway.Simulate(order.ProviderOrderId!,"captured",default);
        }
        var recovered=await Read<CheckoutStatusResponse>(await customer.PostAsJsonAsync($"/api/v1/me/checkouts/{order.Id}/recover",new{})); Assert.True(recovered.ParticipationConfirmed); Assert.Equal(order.ProviderOrderId,recovered.Order.ProviderOrderId);
        var again=await Read<CheckoutStatusResponse>(await customer.PostAsJsonAsync(route+"/recover",new CheckoutConfirmRequest(quote.QuoteId))); Assert.Equal(order.Id,again.Order.Id);
        await Read<PaymentOrderResponse>(await customer.PostAsJsonAsync($"/api/v1/mvp/customer/orders/{order.Id}/sandbox?success=true",new{}));
        await Read<TierCampaignResponse>(await seller.PostAsync($"/api/v1/mvp/seller/campaigns/{x.Campaign}/finalize",null));
        await using(var scope=f.Services.CreateAsyncScope()) await scope.ServiceProvider.GetRequiredService<IPhaseOne>().Process(default);
        var status=await Read<CheckoutStatusResponse>(await customer.GetAsync($"/api/v1/me/checkouts/{order.Id}")); Assert.Equal(16000,status.Order.MaximumAmountMinor); Assert.Equal(12000,status.Order.FinalAmountMinor); Assert.Equal(4000,status.Order.RefundedMinor); Assert.Equal("refund_completed",status.Status); Assert.Single(status.Refunds); Assert.NotNull(status.Refunds[0].ProviderReference);
    }

    [Fact]
    public async Task Module5_failed_pending_and_cancelled_payments_remain_explicit_and_seller_only_tokens_cannot_quote()
    {
        var x=await Setup(4,true,true,ProductType.Physical,true); using var f=x.Factory; using var customer=x.Customer; using var seller=x.Seller; using var admin=x.Admin;
        var route=$"/api/v1/group-buys/{x.Campaign}/checkout";
        var quote=await Read<CheckoutQuoteResponse>(await customer.PostAsJsonAsync(route+"/quote",PickupQuote())); var request=new CheckoutConfirmRequest(quote.QuoteId);
        var pending=await Read<CheckoutStatusResponse>(await customer.PostAsJsonAsync(route+"/confirm",request)); Assert.Equal("processing",pending.Status);
        await Read<PaymentOrderResponse>(await customer.PostAsJsonAsync($"/api/v1/mvp/customer/orders/{pending.Order.Id}/sandbox?success=false",new{}));
        var failed=await Read<CheckoutStatusResponse>(await customer.PostAsJsonAsync(route+"/recover",request)); Assert.Equal("payment_failed",failed.Code); Assert.False(failed.ParticipationConfirmed); Assert.Equal(pending.Order.Id,failed.Order.Id);
        var fresh=await Read<CheckoutQuoteResponse>(await customer.PostAsJsonAsync(route+"/quote",PickupQuote()));
        var cancelOrder=await Read<CheckoutStatusResponse>(await customer.PostAsJsonAsync(route+"/confirm",new CheckoutConfirmRequest(fresh.QuoteId)));
        Assert.Equal(HttpStatusCode.NoContent,(await customer.PostAsync($"/api/v1/mvp/customer/orders/{cancelOrder.Order.Id}/cancel",null)).StatusCode);
        Assert.Equal("reservation_expired",(await Read<CheckoutStatusResponse>(await customer.GetAsync($"/api/v1/me/checkouts/{cancelOrder.Order.Id}"))).Code);
        using var sellerOnly=await CheckoutCustomer(f); var me=await Read<UserResponse>(await sellerOnly.GetAsync("/api/v1/auth/me"));
        await using(var scope=f.Services.CreateAsyncScope()) {var users=scope.ServiceProvider.GetRequiredService<UserManager<IdentityAccount>>();var user=await users.FindByIdAsync(me.Id.ToString());await users.RemoveFromRoleAsync(user!,"Customer");await users.AddToRoleAsync(user!,"Seller");}
        Assert.Equal(HttpStatusCode.Unauthorized,(await sellerOnly.PostAsJsonAsync(route+"/quote",PickupQuote())).StatusCode);
        await using(var scope=f.Services.CreateAsyncScope()) { var token=await scope.ServiceProvider.GetRequiredService<IIdentityService>().Login(new(me.Email!,"Synthetic!Password123"),default); sellerOnly.DefaultRequestHeaders.Authorization=new("Bearer",token.AccessToken); }
        Assert.Equal(HttpStatusCode.Forbidden,(await sellerOnly.PostAsJsonAsync(route+"/quote",PickupQuote())).StatusCode);
    }
    private sealed class LostCheckoutResponseGateway(IPaymentGateway inner) : IPaymentGateway
    {
        public string? CreatedOrderId { get; private set; }
        public string Name=>inner.Name; public bool Sandbox=>inner.Sandbox; public bool Available=>inner.Available;
        public string? CheckoutKeyId=>inner.CheckoutKeyId; public long MinimumRefundMinor=>inner.MinimumRefundMinor;
        public async Task<ProviderOrder> CreateOrder(Guid id,long amount,CancellationToken ct) { var created=await inner.CreateOrder(id,amount,ct); CreatedOrderId=created.Id; throw new HttpRequestException("Synthetic response loss"); }
        public Task<ProviderPayment?> Reconcile(string id,CancellationToken ct,bool captureAuthorized=false)=>inner.Reconcile(id,ct,captureAuthorized);
        public Task<ProviderPayment> Confirm(string id,string payment,string signature,CancellationToken ct)=>inner.Confirm(id,payment,signature,ct);
        public VerifiedPaymentEvent VerifyWebhook(byte[] raw,string signature,string id)=>inner.VerifyWebhook(raw,signature,id);
        public Task<ProviderRefund> Refund(string payment,Guid id,long amount,CancellationToken ct)=>inner.Refund(payment,id,amount,ct);
    }
    [Fact]
    public async Task Module5_provider_response_loss_recovers_the_same_receipt_without_a_second_payment_order()
    {
        var x=await Setup(4,true,true,ProductType.Physical,true); using var f=x.Factory; using var customer=x.Customer; using var seller=x.Seller; using var admin=x.Admin;
        var quote=await Read<CheckoutQuoteResponse>(await customer.PostAsJsonAsync($"/api/v1/group-buys/{x.Campaign}/checkout/quote",PickupQuote()));
        Guid orderId; string remoteId;
        await using(var scope=f.Services.CreateAsyncScope())
        {
            var fault=new LostCheckoutResponseGateway(scope.ServiceProvider.GetRequiredService<IPaymentGateway>());
            var checkout=ActivatorUtilities.CreateInstance<Marketplace>(scope.ServiceProvider,fault);
            var result=await checkout.ConfirmCheckout(new(x.CustomerId,false,"response-loss"),x.Campaign,new(quote.QuoteId),default);
            Assert.Equal("processing",result.Status); Assert.Null(result.Order.ProviderOrderId); Assert.False(result.RecoveryAvailable); Assert.NotNull(fault.CreatedOrderId); orderId=result.Order.Id; remoteId=fault.CreatedOrderId!;
            var db=scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
            await db.PaymentOrders.Where(o=>o.Id==orderId).ExecuteUpdateAsync(s=>s.SetProperty(o=>o.RetryAtUtc,(DateTimeOffset?)null));
            await db.PilotJobs.Where(j=>j.ItemId==orderId).ExecuteUpdateAsync(s=>s.SetProperty(j=>j.NextAttemptAtUtc,(DateTimeOffset?)null));
        }
        var recovered=await Read<CheckoutStatusResponse>(await customer.PostAsJsonAsync($"/api/v1/me/checkouts/{orderId}/recover",new{})); Assert.Equal(remoteId,recovered.Order.ProviderOrderId); Assert.Equal(orderId,recovered.Order.Id);
        await using var check=f.Services.CreateAsyncScope(); var ledger=check.ServiceProvider.GetRequiredService<MarketplaceDb>(); Assert.Equal(1,await ledger.PaymentOrders.CountAsync(o=>o.CampaignId==x.Campaign)); Assert.Equal(1,await ledger.SandboxReceipts.CountAsync(r=>r.LocalOrderId==orderId));
    }

    [Theory] [InlineData("cancelled")] [InlineData("variant")] [InlineData("seller")]
    public async Task Module5_unavailable_deals_catalog_and_sellers_cannot_be_quoted(string state)
    {
        var x=await Setup(4,true,true,ProductType.Physical,true); using var f=x.Factory; using var customer=x.Customer; using var seller=x.Seller; using var admin=x.Admin;
        if(state=="cancelled") await Read<CampaignResponse>(await admin.PostAsJsonAsync($"/api/v1/admin/group-buys/{x.Campaign}/cancel",new ReviewRequest("Synthetic cancellation")));
        else
        {
            await using var scope=f.Services.CreateAsyncScope(); var db=scope.ServiceProvider.GetRequiredService<MarketplaceDb>(); var campaign=await db.Campaigns.SingleAsync(c=>c.Id==x.Campaign);
            if(state=="variant") await db.Variants.Where(v=>v.Id==campaign.ProductVariantId).ExecuteUpdateAsync(s=>s.SetProperty(v=>v.IsActive,false));
            else await db.Sellers.Where(s=>s.Id==campaign.SellerId).ExecuteUpdateAsync(s=>s.SetProperty(s=>s.Status,ResourceStatus.Inactive));
        }
        var response=await customer.PostAsJsonAsync($"/api/v1/group-buys/{x.Campaign}/checkout/quote",PickupQuote()); Assert.Equal(HttpStatusCode.Conflict,response.StatusCode); Assert.Contains("deal_unavailable",await response.Content.ReadAsStringAsync());
    }

}

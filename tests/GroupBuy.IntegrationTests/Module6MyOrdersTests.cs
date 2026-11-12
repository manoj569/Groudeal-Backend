using System.Net;
using System.Net.Http.Json;
using GroupBuy.Application;
using GroupBuy.Domain;
using GroupBuy.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
namespace GroupBuy.IntegrationTests;
public partial class BackendTests
{
    [Theory]
    [InlineData(ProductType.Physical)] [InlineData(ProductType.Service)] [InlineData(ProductType.Membership)]
    public async Task Module6_owned_checkout_history_progress_privacy_and_payment_recovery(ProductType type)
    {
        var x=await Setup(30,true,true,type,true,[new(10,52),new(20,48),new(30,44)]);
        using var f=x.Factory; using var customer=x.Customer; using var seller=x.Seller; using var admin=x.Admin;
        using var other=await CheckoutCustomer(f); using var anonymous=f.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,(await anonymous.GetAsync("/api/v1/me/orders")).StatusCode);
        Assert.Empty((await Read<MyOrdersPage>(await customer.GetAsync("/api/v1/me/orders"))).Items);
        var order=await Read<PaymentOrderResponse>(await customer.PostAsJsonAsync("/api/v1/mvp/customer/orders",Checkout(x.Campaign,1)));
        var pending=await Read<MyOrderDetail>(await customer.GetAsync($"/api/v1/me/orders/{order.Id}"));
        Assert.Equal(ParticipationStatus.PaymentPending,pending.Order.Status); Assert.Equal("check_payment",pending.Order.NextAction);
        Assert.Null(pending.Order.Group.Progress.CurrentTier); Assert.Equal(10,pending.Order.Group.Progress.NextTier!.MinimumQuantity);
        Assert.Null(pending.Order.JoinedAtUtc); Assert.False(pending.Order.ParticipationConfirmed);
        Assert.Equal(HttpStatusCode.NotFound,(await other.GetAsync($"/api/v1/me/orders/{order.Id}")).StatusCode);
        Assert.Empty((await Read<MyOrdersPage>(await other.GetAsync("/api/v1/me/orders"))).Items);
        order=await Read<PaymentOrderResponse>(await customer.PostAsJsonAsync($"/api/v1/mvp/customer/orders/{order.Id}/sandbox?success=true",new{}));
        await using(var seed=f.Services.CreateAsyncScope()) await seed.ServiceProvider.GetRequiredService<MarketplaceDb>().Campaigns.Where(c=>c.Id==x.Campaign).ExecuteUpdateAsync(u=>u.SetProperty(c=>c.ReservedQuantity,18));
        var history=await Read<MyOrdersPage>(await customer.GetAsync("/api/v1/me/orders?status=active"));
        var card=Assert.Single(history.Items); Assert.Equal(order.Id,card.Id); Assert.True(card.ParticipationConfirmed);
        Assert.Equal(18,card.Group.Progress.CommittedQuantity); Assert.Equal(10,card.Group.Progress.CurrentTier!.MinimumQuantity);
        Assert.Equal(20,card.Group.Progress.NextTier!.MinimumQuantity); Assert.Equal(2,card.Group.Progress.QuantityNeededForNextTier);
        Assert.Equal((GroupDealType)type,card.Package.Type); Assert.Equal(order.MaximumAmountMinor,card.Commercial.PaidAmountMinor);
        Assert.Equal(1,history.Summary.Statuses[ParticipationStatus.Active]); Assert.Equal(1,history.Total);
        foreach(var quantity in new decimal[]{10,18,30}) {
            await using(var seed=f.Services.CreateAsyncScope()) await seed.ServiceProvider.GetRequiredService<MarketplaceDb>().Campaigns.Where(c=>c.Id==x.Campaign).ExecuteUpdateAsync(u=>u.SetProperty(c=>c.ReservedQuantity,quantity));
            var progress=(await Read<MyOrderDetail>(await customer.GetAsync($"/api/v1/me/orders/{order.Id}"))).Order.Group.Progress;
            Assert.Equal(quantity==30 ? 30 : 10,progress.CurrentTier!.MinimumQuantity);
            Assert.Equal(quantity==30 ? null : 20m,progress.NextTier?.MinimumQuantity);
            Assert.Equal(quantity==30 ? null : 20m-quantity,progress.QuantityNeededForNextTier);
        }
        var raw=await customer.GetStringAsync($"/api/v1/me/orders/{order.Id}");
        foreach(var forbidden in new[]{"ciphertext","addressDigest","tokenHash","backupCode","providerOrderId","providerPaymentId","idempotencyKey","contactEmail","contactPhone","decisionReason"})
            Assert.DoesNotContain(forbidden,raw,StringComparison.OrdinalIgnoreCase);
        f.Services.GetRequiredService<IConfiguration>()["Pilot:Enabled"]="false";
        f.Services.GetRequiredService<IConfiguration>()["Pilot:ServiceAreas:0:Id"]=null;
        f.Services.GetRequiredService<IConfiguration>()["Payments:Enabled"]="false";
        Assert.Equal(HttpStatusCode.OK,(await customer.GetAsync($"/api/v1/me/orders/{order.Id}")).StatusCode);
        Assert.Single((await Read<MyOrdersPage>(await customer.GetAsync($"/api/v1/me/orders?search={order.Id}"))).Items);
        Assert.Empty((await Read<MyOrdersPage>(await customer.GetAsync("/api/v1/me/orders?search=%25_unmatched"))).Items);
        Assert.Empty((await Read<MyOrdersPage>(await customer.GetAsync("/api/v1/me/orders?status=refunded"))).Items);
    }

    [Fact]
    public async Task Module6_real_paid_transitions_partial_refund_and_secure_pass()
    {
        var x=await Setup(2,true,true); using var f=x.Factory; using var customer=x.Customer; using var seller=x.Seller; using var admin=x.Admin;
        var order=await Read<PaymentOrderResponse>(await customer.PostAsJsonAsync("/api/v1/mvp/customer/orders",Checkout(x.Campaign,2)));
        await Read<PaymentOrderResponse>(await customer.PostAsJsonAsync($"/api/v1/mvp/customer/orders/{order.Id}/sandbox?success=true",new{}));
        async Task<MyOrderCard> Card()=> (await Read<MyOrderDetail>(await customer.GetAsync($"/api/v1/me/orders/{order.Id}"))).Order;
        Assert.Equal(ParticipationStatus.Active,(await Card()).Status);
        await Read<TierCampaignResponse>(await seller.PostAsync($"/api/v1/mvp/seller/campaigns/{x.Campaign}/finalize",null));
        var confirmed=await Card(); Assert.Equal(ParticipationStatus.Confirmed,confirmed.Status); Assert.Equal("pending",confirmed.Commercial.RefundStatus);
        await using(var scope=f.Services.CreateAsyncScope()) await scope.ServiceProvider.GetRequiredService<IPhaseOne>().Process(default);
        confirmed=await Card(); Assert.Equal("partially_refunded",confirmed.Commercial.RefundStatus); Assert.Equal(4000,confirmed.Commercial.RefundedMinor);
        Assert.Equal(60,confirmed.Group.FinalUnitPrice); Assert.Equal(2,confirmed.Group.Progress.CommittedQuantity);
        var id=confirmed.Fulfillment.Id!.Value;
        await Read<FulfillmentResponse>(await customer.PostAsync($"/api/v1/mvp/customer/fulfillments/{id}/confirm-choice",null));
        foreach(var state in new[]{FulfillmentState.Preparing,FulfillmentState.ReadyForCollection})
            await Read<FulfillmentResponse>(await seller.PostAsJsonAsync($"/api/v1/mvp/seller/fulfillments/{id}/status",new FulfillmentAction(state,null)));
        Assert.Equal(ParticipationStatus.Ready,(await Card()).Status); Assert.True((await Card()).Fulfillment.CanRequestPass);
        var pass=await Read<FulfillmentPass>(await customer.PostAsync($"/api/v1/mvp/customer/fulfillments/{id}/pass",null));
        Assert.False((await Card()).Fulfillment.CanRequestPass); Assert.Equal(ParticipationStatus.Ready,(await Card()).Status);
        await Read<FulfillmentResponse>(await seller.PostAsJsonAsync("/api/v1/mvp/seller/fulfillments/verify",new SecretRequest(pass.OpaqueToken)));
        await Read<FulfillmentResponse>(await seller.PostAsync($"/api/v1/mvp/seller/fulfillments/{id}/redeem",null));
        var detail=await Read<MyOrderDetail>(await customer.GetAsync($"/api/v1/me/orders/{order.Id}"));
        Assert.Equal(ParticipationStatus.Completed,detail.Order.Status); Assert.Contains(detail.Timeline,t=>t.Code=="fulfillment_completed");
        Assert.Single((await Read<MyOrdersPage>(await customer.GetAsync("/api/v1/me/orders?status=completed"))).Items);
        Assert.Empty((await Read<MyOrdersPage>(await customer.GetAsync("/api/v1/me/orders?status=refunded"))).Items);
    }

    [Theory]
    [InlineData("page=0")] [InlineData("pageSize=101")] [InlineData("status=invalid")] [InlineData("status=2")]
    public async Task Module6_invalid_filters_reject(string query)
    {
        using var f=new ApiFactory(fixture.Connection,disableWorker:true); using var customer=await CheckoutCustomer(f);
        Assert.Equal(HttpStatusCode.BadRequest,(await customer.GetAsync("/api/v1/me/orders?"+query)).StatusCode);
    }
}







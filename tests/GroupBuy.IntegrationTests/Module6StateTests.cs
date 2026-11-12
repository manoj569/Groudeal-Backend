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
    [Fact]
    public async Task Module6_paid_cancellation_waits_for_actual_full_refund()
    {
        var x=await Setup(4,true,true); using var f=x.Factory; using var customer=x.Customer; using var seller=x.Seller; using var admin=x.Admin;
        var order=await Read<PaymentOrderResponse>(await customer.PostAsJsonAsync("/api/v1/mvp/customer/orders",Checkout(x.Campaign,1)));
        await Read<PaymentOrderResponse>(await customer.PostAsJsonAsync($"/api/v1/mvp/customer/orders/{order.Id}/sandbox?success=true",new{}));
        Assert.Equal(HttpStatusCode.NoContent,(await customer.PostAsync($"/api/v1/mvp/customer/orders/{order.Id}/cancel",null)).StatusCode);
        var pending=await Read<MyOrderDetail>(await customer.GetAsync($"/api/v1/me/orders/{order.Id}"));
        Assert.Equal(ParticipationStatus.Cancelled,pending.Order.Status); Assert.Equal("pending",pending.Order.Commercial.RefundStatus); Assert.False(pending.Cancellation.CanCancel);
        await using(var scope=f.Services.CreateAsyncScope()) await scope.ServiceProvider.GetRequiredService<IPhaseOne>().Process(default);
        var refunded=await Read<MyOrderDetail>(await customer.GetAsync($"/api/v1/me/orders/{order.Id}"));
        Assert.Equal(ParticipationStatus.Refunded,refunded.Order.Status); Assert.Equal("refunded",refunded.Order.Commercial.RefundStatus);
        Assert.Equal(order.MaximumAmountMinor,refunded.Order.Commercial.RefundedMinor);
        Assert.Contains(refunded.Timeline,t=>t.Code=="refund_initiated");
        Assert.DoesNotContain(refunded.Timeline,t=>t.Code=="refund_completed");
    }
    [Theory]
    [InlineData(ProductType.Physical)] [InlineData(ProductType.Service)] [InlineData(ProductType.Membership)]
    public async Task Module6_fulfillment_kind_deadlines_refund_blocks_and_delivery_readiness(ProductType type)
    {
        var x=await Setup(2,true,true,type,true); using var f=x.Factory; using var customer=x.Customer; using var seller=x.Seller; using var admin=x.Admin;
        var order=await Read<PaymentOrderResponse>(await customer.PostAsJsonAsync("/api/v1/mvp/customer/orders",Checkout(x.Campaign,1)));
        await Read<PaymentOrderResponse>(await customer.PostAsJsonAsync($"/api/v1/mvp/customer/orders/{order.Id}/sandbox?success=true",new{}));
        using var other=await CheckoutCustomer(f);
        var second=await Read<PaymentOrderResponse>(await other.PostAsJsonAsync("/api/v1/mvp/customer/orders",Checkout(x.Campaign,1)));
        await Read<PaymentOrderResponse>(await other.PostAsJsonAsync($"/api/v1/mvp/customer/orders/{second.Id}/sandbox?success=true",new{}));
        await Read<TierCampaignResponse>(await seller.PostAsync($"/api/v1/mvp/seller/campaigns/{x.Campaign}/finalize",null));
        await using(var scope=f.Services.CreateAsyncScope()) await scope.ServiceProvider.GetRequiredService<IPhaseOne>().Process(default);
        await using var seed=f.Services.CreateAsyncScope(); var db=seed.ServiceProvider.GetRequiredService<MarketplaceDb>();
        var fulfillment=await db.OrderFulfillments.SingleAsync(o=>o.PaymentOrderId==order.Id);
        var pickup=await db.Pickups.SingleAsync(p=>p.Id==fulfillment.PickupId);
        fulfillment.ChoiceConfirmed=true;fulfillment.State=FulfillmentState.ReadyForCollection; await db.SaveChangesAsync();
        async Task<MyOrderCard> Card()=> (await Read<MyOrderDetail>(await customer.GetAsync($"/api/v1/me/orders/{order.Id}"))).Order;
        var card=await Card(); Assert.Equal(ParticipationStatus.Ready,card.Status);
        Assert.Equal(type==ProductType.Physical ? "seller_pickup" : "seller_redemption",card.Fulfillment.Kind);
        if(type!=ProductType.Physical) { Assert.Null(card.Fulfillment.PickupStatus); Assert.Equal("view_redemption_pass",card.NextAction); }
        pickup.DeadlineUtc=DateTimeOffset.UtcNow.AddMinutes(-1); await db.SaveChangesAsync(); Assert.NotEqual(ParticipationStatus.Ready,(await Card()).Status);
        pickup.DeadlineUtc=DateTimeOffset.UtcNow.AddHours(1); await db.SaveChangesAsync();
        var refund=new PaymentRefund { PaymentOrderId=order.Id,AmountMinor=100,State=RefundState.Pending,Reason="synthetic-block" };
        db.PaymentRefunds.Add(refund); await db.SaveChangesAsync(); Assert.NotEqual(ParticipationStatus.Ready,(await Card()).Status);
        refund.State=RefundState.Completed; await db.SaveChangesAsync(); Assert.Equal(ParticipationStatus.Ready,(await Card()).Status);
        if(type==ProductType.Physical) {
            await db.PaymentOrders.Where(o=>o.Id==order.Id).ExecuteUpdateAsync(u=>u.SetProperty(o=>o.FulfillmentMode,OrderFulfillmentMode.FounderDelivery));
            Assert.NotEqual(ParticipationStatus.Ready,(await Card()).Status);
            fulfillment.State=FulfillmentState.OutForDelivery; await db.SaveChangesAsync();
            card=await Card(); Assert.Equal(ParticipationStatus.Ready,card.Status); Assert.Equal("founder_delivery",card.Fulfillment.Kind);
        }
    }
}


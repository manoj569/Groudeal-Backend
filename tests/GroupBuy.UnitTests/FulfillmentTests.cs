using GroupBuy.Domain;
namespace GroupBuy.UnitTests;

public class FulfillmentTests
{
    [Theory]
    [InlineData(CampaignStatus.Open,false)] [InlineData(CampaignStatus.MinimumReached,false)]
    [InlineData(CampaignStatus.Finalizing,false)] [InlineData(CampaignStatus.Failed,false)]
    [InlineData(CampaignStatus.Cancelled,false)] [InlineData(CampaignStatus.Completed,false)]
    [InlineData(CampaignStatus.Successful,true)] [InlineData(CampaignStatus.Preparing,true)]
    [InlineData(CampaignStatus.ReadyForFulfillment,true)]
    public void Module7_campaign_gate(CampaignStatus state,bool expected) => Assert.Equal(expected,FulfillmentRules.CampaignAllows(state));

    [Theory]
    [InlineData("valid",true)] [InlineData("unfinalized",false)] [InlineData("uncaptured",false)]
    [InlineData("failed",false)] [InlineData("pending-refund",false)] [InlineData("cancelled",false)]
    [InlineData("zero-final",false)] [InlineData("fully-refunded",false)] [InlineData("tier-refund",true)]
    public void Module7_commercial_gate(string condition,bool expected) => Assert.Equal(expected,
        FulfillmentRules.CommercialAllows(CampaignStatus.Successful,condition!="unfinalized",
            condition=="failed" ? PaymentState.Failed : PaymentState.Captured,condition!="uncaptured",
            condition=="zero-final" ? 0 : 6000,8000,condition=="fully-refunded" ? 8000 : condition=="tier-refund" ? 2000 : 0,
            condition=="cancelled" ? ReservationStatus.Cancelled : ReservationStatus.Active,condition=="pending-refund"));

    [Theory]
    [InlineData(ProductType.Physical,OrderFulfillmentMode.SellerPickup,FulfillmentState.Preparing,"preparing")]
    [InlineData(ProductType.Physical,OrderFulfillmentMode.SellerPickup,FulfillmentState.ReadyForCollection,"ready_for_pickup")]
    [InlineData(ProductType.Physical,OrderFulfillmentMode.FounderDelivery,FulfillmentState.ReadyForCollection,"ready_for_collection")]
    [InlineData(ProductType.Service,OrderFulfillmentMode.SellerPickup,FulfillmentState.Preparing,"service_pending")]
    [InlineData(ProductType.Service,OrderFulfillmentMode.SellerPickup,FulfillmentState.ReadyForCollection,"service_ready")]
    [InlineData(ProductType.Membership,OrderFulfillmentMode.SellerPickup,FulfillmentState.ReadyForCollection,"service_ready")]
    [InlineData(ProductType.Service,OrderFulfillmentMode.SellerPickup,FulfillmentState.Delivered,"completed")]
    public void Module7_distinct_type_status(ProductType type,OrderFulfillmentMode mode,FulfillmentState state,string expected) =>
        Assert.Equal(expected,FulfillmentRules.Status(type,mode,state));

    [Theory]
    [InlineData(FulfillmentState.Pending,FulfillmentState.Delivered,OrderFulfillmentMode.SellerPickup)]
    [InlineData(FulfillmentState.CampaignConfirmed,FulfillmentState.ReadyForCollection,OrderFulfillmentMode.SellerPickup)]
    [InlineData(FulfillmentState.Preparing,FulfillmentState.Delivered,OrderFulfillmentMode.FounderDelivery)]
    [InlineData(FulfillmentState.ReadyForCollection,FulfillmentState.OutForDelivery,OrderFulfillmentMode.SellerPickup)]
    [InlineData(FulfillmentState.Delivered,FulfillmentState.Preparing,OrderFulfillmentMode.SellerPickup)]
    [InlineData(FulfillmentState.Cancelled,FulfillmentState.ReadyForCollection,OrderFulfillmentMode.SellerPickup)]
    public void Module7_cannot_skip_secure_completion(FulfillmentState from,FulfillmentState to,OrderFulfillmentMode mode) => Assert.False(TierPricing.CanFulfill(from,to,mode));

    [Theory]
    [InlineData("customer_unavailable",true)] [InlineData("address_inaccessible",true)]
    [InlineData("delivery_attempt_failed",true)] [InlineData("other",true)]
    [InlineData(null,false)] [InlineData("private internal note",false)]
    public void Module7_only_safe_failure_codes(string? reason,bool safe) => Assert.Equal(safe,FulfillmentRules.FailureReason(reason));

    [Fact]
    public void Module7_all_non_delivery_states_reject_delivery_pass()
    {
        foreach(var state in Enum.GetValues<FulfillmentState>())
        {
            Assert.Equal(state==FulfillmentState.OutForDelivery,FulfillmentRules.PassReady(state,OrderFulfillmentMode.FounderDelivery));
            Assert.Equal(state==FulfillmentState.ReadyForCollection,FulfillmentRules.PassReady(state,OrderFulfillmentMode.SellerPickup));
        }
    }
}

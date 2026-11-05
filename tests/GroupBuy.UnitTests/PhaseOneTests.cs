using GroupBuy.Domain;
namespace GroupBuy.UnitTests;
public class PhaseOneTests
{
    [Theory][InlineData(10000,9950,100,false)][InlineData(10000,50,100,false)][InlineData(50,50,100,false)][InlineData(10000,9900,100,true)][InlineData(10000,10000,100,true)][InlineData(1,1,1,true)]
    public void Every_possible_final_price_supports_both_difference_and_failure_refunds(long maximum,long final,long minimum,bool valid)
    {
        if(valid)TierPricing.ValidateRefundability(maximum,[final],minimum);else Assert.Throws<BusinessException>(()=>TierPricing.ValidateRefundability(maximum,[final],minimum));
    }
    [Fact] public void Paid_campaign_state_machine_does_not_allow_skipping_finalization(){Assert.True(CampaignRules.CanTransition(CampaignStatus.MinimumReached,CampaignStatus.Finalizing));Assert.True(CampaignRules.CanTransition(CampaignStatus.Finalizing,CampaignStatus.Successful));Assert.False(CampaignRules.CanTransition(CampaignStatus.Open,CampaignStatus.Completed));Assert.False(CampaignRules.CanTransition(CampaignStatus.Successful,CampaignStatus.Completed));}
    [Fact] public void Tiers_choose_highest_achieved_threshold(){TierPricing.Validate([(2m,80m),(5m,70m),(10m,60m)],10,100);Assert.Null(TierPricing.Achieved([(2m,80m)],1));Assert.Equal(70,TierPricing.Achieved([(2m,80m),(5m,70m),(10m,60m)],8));}
    [Theory][InlineData(2,80,1,70)][InlineData(2,80,3,90)][InlineData(2,80,11,70)][InlineData(2,80,3,0)] public void Invalid_tiers_are_rejected(decimal q1,decimal p1,decimal q2,decimal p2)=>Assert.Throws<BusinessException>(()=>TierPricing.Validate([(q1,p1),(q2,p2)],10,100));
    [Theory][InlineData("1.005",101)][InlineData("9.994",999)]public void Amounts_round_to_paise(string value,long expected)=>Assert.Equal(expected,TierPricing.Minor(decimal.Parse(value,System.Globalization.CultureInfo.InvariantCulture)));
    [Fact]public void Delivery_and_pickup_cannot_skip_verification(){Assert.False(TierPricing.CanFulfill(FulfillmentState.ReadyForCollection,FulfillmentState.Delivered,OrderFulfillmentMode.SellerPickup));Assert.False(TierPricing.CanFulfill(FulfillmentState.ReadyForCollection,FulfillmentState.CollectedFromSeller,OrderFulfillmentMode.SellerPickup));Assert.True(TierPricing.CanFulfill(FulfillmentState.ReadyForCollection,FulfillmentState.CollectedFromSeller,OrderFulfillmentMode.FounderDelivery));}
}

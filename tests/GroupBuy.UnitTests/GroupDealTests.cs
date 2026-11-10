using GroupBuy.Domain;
namespace GroupBuy.UnitTests;
public sealed class GroupDealTests
{
    private static readonly GroupDealTier[] Tiers = [new(10, 52000), new(20, 48000), new(30, 44000)];
    [Theory]
    [InlineData(0, null, 10, 10)] [InlineData(9, null, 10, 1)]
    [InlineData(10, 52000, 20, 10)] [InlineData(18, 52000, 20, 2)]
    [InlineData(19, 52000, 20, 1)] [InlineData(20, 48000, 30, 10)]
    [InlineData(29, 48000, 30, 1)] [InlineData(30, 44000, null, null)]
    public void Progress_uses_actual_thresholds(int quantity, int? price, int? next, int? needed)
    {
        var p = GroupDealRules.Progress(Tiers, quantity);
        Assert.Equal(price.HasValue ? (decimal?)price.Value : null, p.CurrentTier?.UnitPrice);
        Assert.Equal(next.HasValue ? (decimal?)next.Value : null, p.NextTier?.MinimumQuantity);
        Assert.Equal(needed.HasValue ? (decimal?)needed.Value : null, p.QuantityNeededForNextTier);
        Assert.Equal(quantity == 30, p.HighestTierReached);
    }
    [Fact] public void Unordered_read_input_is_deterministic() => Assert.Equal(GroupDealRules.Progress(Tiers, 18), GroupDealRules.Progress(Tiers.Reverse(), 18));
    [Fact] public void No_tiers_does_not_claim_highest_tier() => Assert.False(GroupDealRules.Progress([], 0).HighestTierReached);
    [Theory]
    [InlineData(10, 100, 10, 90)] [InlineData(0, 100, 20, 90)] [InlineData(10, 0, 20, 90)] [InlineData(10, 100, 20, 101)]
    public void Invalid_tiers_rejected(int q1, int p1, int q2, int p2) => Assert.Throws<BusinessException>(() => TierPricing.Validate([(q1,p1),(q2,p2)],30,200));
    [Theory]
    [InlineData(false, false, DemandType.Product)] [InlineData(true, true, DemandType.Product)] [InlineData(true, false, DemandType.Service)]
    public void Incompatible_demand_rejected(bool open, bool cancelled, DemandType type)
    {
        var category = Guid.NewGuid(); var d = new DiscoveryDemand { IsOpen = open, IsCancelled = cancelled, DemandType = type, CategoryId = category, ServiceArea = "test" };
        Assert.Throws<BusinessException>(() => GroupDealRules.ValidateLink(d, category, "test"));
    }
    [Fact] public void Link_requires_matching_area_and_category()
    {
        var category = Guid.NewGuid(); var d = new DiscoveryDemand { CategoryId = category, ServiceArea = "test" };
        GroupDealRules.ValidateLink(d, category, "test");
        Assert.Throws<BusinessException>(() => GroupDealRules.ValidateLink(d, Guid.NewGuid(), "test"));
        Assert.Throws<BusinessException>(() => GroupDealRules.ValidateLink(d, category, "other"));
    }
    [Fact] public void Interest_is_not_an_input_to_pricing() => Assert.Null(GroupDealRules.Progress(Tiers, 0).CurrentTier);
}

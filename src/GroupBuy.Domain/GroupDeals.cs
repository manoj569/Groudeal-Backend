namespace GroupBuy.Domain;

public enum GroupDealType { Product, Service, Membership }

public sealed partial class GroupBuyCampaign
{
    public Guid? OriginatingDemandId { get; set; }
    public DateTimeOffset? DemandOfferNotifiedAtUtc { get; set; }
    public string OfferTerms { get; set; } = "";
}

// Quantities are variant units, not the number of people. Never feed interest counts here.
public record GroupDealTier(decimal MinimumQuantity, decimal UnitPrice);
public record GroupDealProgress(decimal CommittedQuantity, GroupDealTier? CurrentTier,
    GroupDealTier? NextTier, decimal? QuantityNeededForNextTier, bool HighestTierReached);

public static class GroupDealRules
{
    public static GroupDealProgress Progress(IEnumerable<GroupDealTier> tiers, decimal committedQuantity)
    {
        var ordered = tiers.OrderBy(t => t.MinimumQuantity).ToArray();
        var current = ordered.LastOrDefault(t => t.MinimumQuantity <= committedQuantity);
        var next = ordered.FirstOrDefault(t => t.MinimumQuantity > committedQuantity);
        return new(committedQuantity, current, next, next is null ? null : next.MinimumQuantity - committedQuantity,
            ordered.Length > 0 && next is null);
    }

    public static void ValidateLink(DiscoveryDemand demand, Guid categoryId, string serviceArea, ProductType type = ProductType.Physical)
    {
        if (!demand.IsOpen || demand.IsCancelled || demand.DemandType != (type == ProductType.Physical ? DemandType.Product : DemandType.Service)
            || demand.CategoryId != categoryId || demand.ServiceArea != serviceArea)
            throw new BusinessException(400, "incompatible_demand", "Choose an open compatible demand in the offer category and service area.");
    }
}

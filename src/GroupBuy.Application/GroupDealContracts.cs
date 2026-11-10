using GroupBuy.Domain;
namespace GroupBuy.Application;

public record LinkDemandOfferRequest(Guid DemandId, string Terms);
public record DemandOpportunity(Guid Id, string Title, DemandType Type, Guid? CategoryId, string ServiceArea, int InterestedCount);
public record GroupDealSeller(string DisplayName, bool Verified);
public record GroupDealParticipation(bool Interested, int ActiveOrderCount, int PaidOrderCount);
public record GroupDealCancellation(DateTimeOffset CancellationDeadlineUtc, bool CancellationBeforeFinalization,
    string FailureBehavior, string BetterTierBehavior);
public record GroupDealDetail(Guid Id, GroupDealType Type, string Title, string Description, Guid ProductId,
    Guid CategoryId, string ProductName, string VariantName, UnitOfMeasure Unit, decimal UnitQuantity, GroupDealSeller Seller,
    Guid? OriginatingDemandId, string? DemandTitle, int InterestedCount, int? ReconfirmedCount,
    int CommittedMemberCount, int PaidMemberCount, GroupDealProgress Progress, IReadOnlyList<GroupDealTier> Tiers,
    CampaignStatus Status, DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc, DateTimeOffset ServerTimeUtc,
    string ServiceArea, decimal Capacity, decimal AvailableQuantity, decimal? FinalUnitPrice, string Terms,
    IReadOnlyList<OrderFulfillmentMode> FulfillmentOptions, PickupResponse? PickupLocation, GroupDealCancellation Cancellation,
    bool CanJoin, string? JoinUnavailableReason, GroupDealParticipation? Participation);
public record GroupDealSummary(Guid Id, GroupDealType Type, string Title, Guid CategoryId, GroupDealSeller Seller,
    CampaignStatus Status, DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc, string ServiceArea,
    GroupDealProgress Progress, int CommittedMemberCount, int PaidMemberCount, Guid? OriginatingDemandId);
public record GroupDealOffer(CampaignResponse Campaign, GroupDealType Type, Guid? OriginatingDemandId,
    string Terms, IReadOnlyList<PriceTierRequest> Tiers);
public record GroupDealFilter(int Page = 1, int PageSize = 20, Guid? CategoryId = null,
    GroupDealType? Type = null, CampaignStatus? Status = null, string? ServiceArea = null,
    string? Search = null, string Sort = "newest", Guid? DemandId = null);
public interface IGroupDeals
{
    Task<GroupDealOffer> Offer(Actor actor, Guid id, bool adminView, CancellationToken ct);
    Task<CampaignResponse> CancelDeal(Actor actor, Guid id, ReviewRequest request, CancellationToken ct);
    Task<GroupDealDetail> Deal(Guid id, Actor? actor, CancellationToken ct);
    Task<PageResponse<GroupDealSummary>> Deals(GroupDealFilter filter, Actor? actor, CancellationToken ct);
    Task<PageResponse<DemandOpportunity>> Opportunities(Actor actor, PageRequest page, string serviceArea, CancellationToken ct);
    Task<CampaignResponse> LinkDemandOffer(Actor actor, Guid id, LinkDemandOfferRequest request, CancellationToken ct);
}

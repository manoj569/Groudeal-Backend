namespace GroupBuy.Domain;
public static class CampaignRules
{
    public static bool CanTransition(CampaignStatus from, CampaignStatus to) => (from, to) switch
    {
        (CampaignStatus.Draft, CampaignStatus.PendingApproval or CampaignStatus.Cancelled) => true,
        (CampaignStatus.PendingApproval, CampaignStatus.Draft or CampaignStatus.Scheduled or CampaignStatus.Open or CampaignStatus.Cancelled or CampaignStatus.Expired) => true,
        (CampaignStatus.Scheduled, CampaignStatus.Open or CampaignStatus.Expired or CampaignStatus.Cancelled) => true,
        (CampaignStatus.Open, CampaignStatus.TargetReached or CampaignStatus.Expired or CampaignStatus.Cancelled) => true,
        (CampaignStatus.TargetReached, CampaignStatus.AwaitingSellerConfirmation or CampaignStatus.Open or CampaignStatus.Cancelled) => true,
        (CampaignStatus.AwaitingSellerConfirmation, CampaignStatus.Confirmed or CampaignStatus.Failed) => true,
        (CampaignStatus.Confirmed, CampaignStatus.FulfillmentInProgress or CampaignStatus.Failed) => true,
        (CampaignStatus.FulfillmentInProgress, CampaignStatus.Completed or CampaignStatus.Failed) => true,
        _ => false
    };
    public static void ValidateReservation(GroupBuyCampaign campaign, decimal quantity, UnitOfMeasure unit, DateTimeOffset now)
    {
        if (campaign.Status != CampaignStatus.Open || now < campaign.StartsAtUtc || now >= campaign.ExpiresAtUtc)
            throw new BusinessException(409, "campaign_closed", "Campaign is not accepting reservations.");
        if (quantity <= 0 || quantity < campaign.MinimumReservationQuantity || quantity > campaign.MaximumReservationQuantity || decimal.Round(quantity, 3) != quantity)
            throw new BusinessException(400, "invalid_quantity", "Quantity is outside campaign limits or precision.");
        if (unit is UnitOfMeasure.Piece or UnitOfMeasure.Pack && quantity != decimal.Truncate(quantity))
            throw new BusinessException(400, "invalid_unit", "Piece and pack quantities must be whole numbers.");
        if (campaign.ReservedQuantity + quantity > campaign.TargetQuantity)
            throw new BusinessException(409, "capacity_exceeded", "Insufficient campaign capacity.");
    }
}

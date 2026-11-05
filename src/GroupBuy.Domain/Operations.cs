namespace GroupBuy.Domain;

public enum PickupStatus { Pending, ReadyForPickup, Collected, NotCollected, Cancelled }
public enum PickupPaymentPolicy { NotTrackedOfflineResponsibility, VerifiedCapturedPayment }
public enum NotificationType { ReservationCreated, CampaignTargetReached, SellerConfirmationRequired, CampaignConfirmed, CampaignRejected, CampaignExpired, PickupReady, PickupCompleted, CampaignRescueReminder, PilotPaymentCaptured, PilotPaymentFailed, PilotCampaignFailed, PilotCampaignFinalized, PilotFulfillmentReady, PilotOutForDelivery, PilotFulfillmentCompleted, PilotRefundCompleted, PilotFulfillmentExpired }

// Quantities everywhere count variant units, including fractional bulk units.
public sealed class Inventory : TrackedEntity
{
    public Guid SellerId { get; set; }
    public Guid ProductVariantId { get; set; }
    public decimal AvailableQuantity { get; set; }
    public long Version { get; set; }
}

public sealed class PickupRecord : TrackedEntity
{
    public Guid ReservationId { get; set; }
    public Guid CampaignId { get; set; }
    public Guid SellerId { get; set; }
    public Guid CustomerUserId { get; set; }
    public PickupStatus Status { get; set; }
    public PickupPaymentPolicy PaymentPolicy { get; set; }
    // Snapshot approved instructions; future location edits do not move an existing pickup.
    public string Instructions { get; set; } = "";
    public DateTimeOffset DeadlineUtc { get; set; }
    public string? CodeHash { get; set; }
    public DateTimeOffset? CodeExpiresAtUtc { get; set; }
    public DateTimeOffset? CodeIssuedAtUtc { get; set; }
    public int FailedAttempts { get; set; }
    public DateTimeOffset? LockedUntilUtc { get; set; }
    public DateTimeOffset? CollectedAtUtc { get; set; }
    public Guid? CollectedByUserId { get; set; }
}

public sealed class InternalNotification : Entity
{
    public Guid RecipientUserId { get; set; }
    public NotificationType Type { get; set; }
    public Guid EntityId { get; set; }
    public string DeduplicationKey { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? ReadAtUtc { get; set; }
}

public static class InventoryRules
{
    public static void Validate(decimal available, decimal committed, UnitOfMeasure unit)
    {
        if (available < 0 || available > 1000000 || decimal.Round(available, 3) != available || available < committed)
            throw new BusinessException(409, "invalid_inventory", "Inventory must cover all commitments and use at most three decimals.");
        if (unit is UnitOfMeasure.Piece or UnitOfMeasure.Pack && available != decimal.Truncate(available))
            throw new BusinessException(400, "invalid_unit", "Discrete variant inventory must use whole quantities.");
    }
}

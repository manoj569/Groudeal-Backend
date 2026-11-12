namespace GroupBuy.Domain;

public enum PilotJobState { Ready, Running, Failed, Exhausted, Succeeded }
public sealed class PilotJob : Entity
{
    public string Stage { get; set; } = "";
    public Guid ItemId { get; set; }
    public PilotJobState State { get; set; }
    public int Attempts { get; set; }
    public int CycleAttempts { get; set; }
    public int RetryCount { get; set; }
    public DateTimeOffset? LastAttemptAtUtc { get; set; }
    public DateTimeOffset? NextAttemptAtUtc { get; set; }
    public Guid? LeaseId { get; set; }
    public string? ErrorCode { get; set; }
}

public enum PaymentState { Creating, Pending, Authorized, Captured, Failed, Expired, RefundPending, Refunded }
public enum RefundState { Pending, Processing, Completed, Exhausted }
public enum OrderFulfillmentMode { SellerPickup, FounderDelivery }
public enum FulfillmentState { Pending, CampaignConfirmed, Preparing, ReadyForCollection, CollectedFromSeller, OutForDelivery, Delivered, DeliveryFailed, RescheduleRequired, Cancelled, RefundRequired }
public sealed partial class GroupBuyCampaign
{
    public bool UsesTierPricing { get; set; }
    public string ServiceArea { get; set; } = "";
    public decimal? FinalUnitPrice { get; set; }
    public decimal? FinalQuantity { get; set; }
    public DateTimeOffset? FinalizedAtUtc { get; set; }
    public int ExtensionCount { get; set; }
    public decimal? NearTargetQuantity { get; set; }
    public bool NearTargetAccepted { get; set; }
    public DateTimeOffset? RescueNotifiedAtUtc { get; set; }
}
public sealed class CustomerInterest : TrackedEntity
{
    public decimal? RequestedQuantity { get; set; }
    public string? RequestedUnit { get; set; }
    public string? RequirementText { get; set; }
    public string? RequirementDetails { get; set; }
    public Guid CustomerId { get; set; }
    public Guid? ProductId { get; set; }
    public Guid? DemandId { get; set; }
    public string ServiceArea { get; set; } = "";
    public decimal DesiredQuantity { get; set; }
    public bool Active { get; set; } = true;
}
public sealed class CampaignPriceTier : Entity
{
    public Guid CampaignId { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}
public sealed class PaymentOrder : TrackedEntity
{
    public Guid CampaignId { get; set; }
    public Guid CustomerId { get; set; }
    public Guid? ReservationId { get; set; }
    public decimal Quantity { get; set; }
    public decimal MaximumUnitPrice { get; set; }
    public long MaximumAmountMinor { get; set; }
    public long DeliveryFeeMinor { get; set; }
    public long? FinalAmountMinor { get; set; }
    public long RefundedMinor { get; set; }
    public PaymentState State { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public string Provider { get; set; } = "";
    public string? ProviderOrderId { get; set; }
    public string? ProviderPaymentId { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? CapturedAtUtc { get; set; }
    public DateTimeOffset? ReconciledAtUtc { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? RetryAtUtc { get; set; }
    public string? ErrorCode { get; set; }
    public OrderFulfillmentMode FulfillmentMode { get; set; }
    public string AddressCiphertext { get; set; } = "";
    public string AddressDigest { get; set; } = "";
}
public sealed class PaymentWebhook : Entity
{
    public string Provider { get; set; } = "";
    public string EventId { get; set; } = "";
    public string PayloadHash { get; set; } = "";
    public string ProviderOrderId { get; set; } = "";
    public DateTimeOffset ReceivedAtUtc { get; set; }
    public DateTimeOffset? ProcessedAtUtc { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? RetryAtUtc { get; set; }
    public string? ErrorCode { get; set; }
}
public sealed class PaymentRefund : TrackedEntity
{
    public Guid PaymentOrderId { get; set; }
    public string Reason { get; set; } = "";
    public long AmountMinor { get; set; }
    public RefundState State { get; set; }
    public string? ProviderRefundId { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? RetryAtUtc { get; set; }
    public string? ErrorCode { get; set; }
}
public sealed class OrderFulfillment : TrackedEntity
{
    public DateTimeOffset? SellerHandedOverAtUtc { get; set; }
    public Guid PaymentOrderId { get; set; }
    public Guid PickupId { get; set; }
    public FulfillmentState State { get; set; }
    public bool ChoiceConfirmed { get; set; }
    public string? TokenHash { get; set; }
    public string? BackupCodeHash { get; set; }
    public DateTimeOffset? SecretExpiresAtUtc { get; set; }
    public DateTimeOffset? SecretIssuedAtUtc { get; set; }
    public int FailedAttempts { get; set; }
    public DateTimeOffset? LockedUntilUtc { get; set; }
    public Guid? VerifiedBy { get; set; }
    public DateTimeOffset? VerifiedUntilUtc { get; set; }
    public DateTimeOffset? RedeemedAtUtc { get; set; }
    public int CodeVersion { get; set; }
    public string? ExceptionReason { get; set; }
}
// Durable simulated external ledger. Never used by the Razorpay adapter.
public sealed class SandboxReceipt : Entity
{
    public Guid LocalOrderId { get; set; }
    public string ProviderOrderId { get; set; } = "";
    public long AmountMinor { get; set; }
    public string State { get; set; } = "created";
    public long RefundedMinor { get; set; }
}
public sealed class SandboxRefundReceipt : Entity
{
    public Guid RefundId { get; set; }
    public Guid LocalOrderId { get; set; }
    public long AmountMinor { get; set; }
}
public static class TierPricing
{
    public static void ValidateRefundability(long maximum, IEnumerable<long> possibleFinalAmounts, long minimumRefund)
    {
        if (minimumRefund <= 0 || maximum < minimumRefund || possibleFinalAmounts.Any(final => final < minimumRefund || final > maximum || (maximum - final > 0 && maximum - final < minimumRefund)))
            throw new BusinessException(400, "unsupported_payment_amount", "Quantity or pricing cannot be safely refunded with this provider. Review the quantity and tiers before payment.");
    }
    public static void Validate(IReadOnlyList<(decimal Quantity, decimal Price)> tiers, decimal capacity, decimal referencePrice)
    {
        if (tiers.Count is < 1 or > 10 || capacity <= 0) throw new BusinessException(400, "invalid_tiers", "Use one to ten ordered tiers.");
        decimal previousQuantity = 0, previousPrice = referencePrice;
        foreach (var t in tiers)
        {
            if (t.Quantity <= previousQuantity || t.Quantity > capacity || decimal.Round(t.Quantity, 3) != t.Quantity || t.Price <= 0 || t.Price > previousPrice || decimal.Round(t.Price, 2) != t.Price)
                throw new BusinessException(400, "invalid_tiers", "Thresholds must increase and prices must not increase.");
            previousQuantity = t.Quantity; previousPrice = t.Price;
        }
    }
    public static decimal? Achieved(IEnumerable<(decimal Quantity, decimal Price)> tiers, decimal quantity) => tiers.Where(t => t.Quantity <= quantity).OrderByDescending(t => t.Quantity).Select(t => (decimal?)t.Price).FirstOrDefault();
    public static long Minor(decimal amount) => checked((long)decimal.Round(amount * 100, 0, MidpointRounding.AwayFromZero));
    public static bool CanFulfill(FulfillmentState from, FulfillmentState to, OrderFulfillmentMode mode) => (from, to) switch
    {
        (FulfillmentState.CampaignConfirmed, FulfillmentState.Preparing) => true,
        (FulfillmentState.Preparing, FulfillmentState.ReadyForCollection) => true,
        (FulfillmentState.ReadyForCollection, FulfillmentState.CollectedFromSeller) => mode == OrderFulfillmentMode.FounderDelivery,
        (FulfillmentState.CollectedFromSeller, FulfillmentState.OutForDelivery) => mode == OrderFulfillmentMode.FounderDelivery,
        (FulfillmentState.OutForDelivery, FulfillmentState.DeliveryFailed) => mode == OrderFulfillmentMode.FounderDelivery,
        (FulfillmentState.DeliveryFailed, FulfillmentState.RescheduleRequired) => mode == OrderFulfillmentMode.FounderDelivery,
        (FulfillmentState.RescheduleRequired, FulfillmentState.OutForDelivery) => mode == OrderFulfillmentMode.FounderDelivery,
        (FulfillmentState.DeliveryFailed or FulfillmentState.RescheduleRequired, FulfillmentState.RefundRequired) => mode == OrderFulfillmentMode.FounderDelivery,
        _ => false
    };
}

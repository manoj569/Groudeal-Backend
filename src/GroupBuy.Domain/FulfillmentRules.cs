namespace GroupBuy.Domain;

// The existing persistence states are shared. Presentation preserves the three
// distinct experiences without storing a second participation/state machine.
public static class FulfillmentRules
{
    public static bool CampaignAllows(CampaignStatus state) => state is CampaignStatus.Successful or CampaignStatus.Preparing or CampaignStatus.ReadyForFulfillment;
    public static bool CommercialAllows(CampaignStatus campaign, bool finalized, PaymentState payment,
        bool captured, long? finalAmount, long maximum, long refunded, ReservationStatus reservation, bool refundOutstanding) =>
        CampaignAllows(campaign) && finalized && captured && payment == PaymentState.Captured &&
        finalAmount > 0 && refunded < maximum && reservation == ReservationStatus.Active && !refundOutstanding;

    public static bool PassReady(FulfillmentState state, OrderFulfillmentMode mode) => mode switch {
        OrderFulfillmentMode.SellerPickup => state == FulfillmentState.ReadyForCollection,
        OrderFulfillmentMode.FounderDelivery => state == FulfillmentState.OutForDelivery,
        _ => false
    };
    public static bool DeliveryOperation(FulfillmentState state) => state is FulfillmentState.CollectedFromSeller or
        FulfillmentState.OutForDelivery or FulfillmentState.DeliveryFailed or FulfillmentState.RescheduleRequired or FulfillmentState.RefundRequired;
    public static string Kind(ProductType type, OrderFulfillmentMode mode) => type != ProductType.Physical ? "seller_redemption" :
        mode == OrderFulfillmentMode.FounderDelivery ? "founder_delivery" : "seller_pickup";
    public static string Status(ProductType type, OrderFulfillmentMode mode, FulfillmentState state) => state switch {
        FulfillmentState.Pending => "waiting_for_group",
        FulfillmentState.CampaignConfirmed => "confirmed",
        FulfillmentState.Preparing => type != ProductType.Physical ? "service_pending" : "preparing",
        FulfillmentState.ReadyForCollection => type != ProductType.Physical ? "service_ready" : mode == OrderFulfillmentMode.FounderDelivery ? "ready_for_collection" : "ready_for_pickup",
        FulfillmentState.Delivered => "completed",
        FulfillmentState.CollectedFromSeller => "collected_from_seller",
        FulfillmentState.OutForDelivery => "out_for_delivery",
        FulfillmentState.DeliveryFailed => "delivery_failed",
        FulfillmentState.RescheduleRequired => "reschedule_required",
        FulfillmentState.Cancelled => "cancelled",
        FulfillmentState.RefundRequired => "refund_required",
        _ => "unavailable"
    };
    public static bool FailureReason(string? code) => code is "customer_unavailable" or "address_inaccessible" or "delivery_attempt_failed" or "other";
    public static string? SafeReason(string? code) => FailureReason(code) ? code : null;
}

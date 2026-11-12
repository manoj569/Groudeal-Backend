using GroupBuy.Domain;
namespace GroupBuy.Application;

public record MyOrdersFilter(int Page=1,int PageSize=20,string Status="all",string? Search=null);
public record MyOrderGroup(CampaignStatus Status, decimal TargetQuantity, GroupDealProgress Progress,
    decimal CurrentUnitPrice, decimal? FinalUnitPrice, GroupDealTier? FinalTier, DateTimeOffset EndsAtUtc, DateTimeOffset? FinalizedAtUtc);
public record MyOrderMoney(string Currency, decimal UnitPriceSnapshot, long QuotedSubtotalMinor,
    long? MaximumInitialAmountMinor,long? PaidAmountMinor,long? AuthorizedAmountMinor,long? FinalAmountMinor,
    long DeliveryFeeMinor,long? RefundedMinor,string RefundStatus);
public record MyOrderFulfillment(Guid? Id, Guid? PickupId, string Kind, FulfillmentState? State, PickupStatus? PickupStatus,
    bool Ready, bool CanRequestPass, DateTimeOffset? PassRequestAvailableAtUtc,DateTimeOffset? DeadlineUtc,DateTimeOffset? CompletedAtUtc,string? Status = null);
public record MyOrderCard(Guid Id,string Reference,string Source,Guid DealId,string Title,CheckoutPackage Package,
    string SellerLabel,decimal Quantity,DateTimeOffset CreatedAtUtc,DateTimeOffset? JoinedAtUtc,ParticipationStatus Status,
    ReservationStatus? ReservationStatus,PaymentState? PaymentState,string PaymentStatus,bool ParticipationConfirmed,
    MyOrderGroup Group,MyOrderMoney Commercial,MyOrderFulfillment Fulfillment,bool CanCancel,string NextAction,DateTimeOffset ServerTimeUtc);
public record MyOrderCounts(int All,IReadOnlyDictionary<ParticipationStatus,int> Statuses);
public record MyOrdersPage(IReadOnlyList<MyOrderCard> Items,int Page,int PageSize,int Total,bool HasMore,
    MyOrderCounts Summary,DateTimeOffset ServerTimeUtc);
public record MyOrderRefund(Guid Id,long AmountMinor,RefundState State,string ReasonCode,DateTimeOffset CreatedAtUtc,DateTimeOffset RecordUpdatedAtUtc);
public record MyOrderActivity(string Code,DateTimeOffset AtUtc,long? AmountMinor=null);
public record MyOrderCancellation(bool CanCancel,DateTimeOffset? CancelledAtUtc,string? ReasonCode);
public record MyOrderDetail(MyOrderCard Order,MyOrderCancellation Cancellation,IReadOnlyList<MyOrderRefund> Refunds,
    IReadOnlyList<MyOrderActivity> Timeline);
public interface IMyOrders
{
    Task<MyOrdersPage> MyOrders(Actor actor,MyOrdersFilter filter,CancellationToken ct);
    Task<MyOrderDetail> MyOrder(Actor actor,Guid id,CancellationToken ct);
}


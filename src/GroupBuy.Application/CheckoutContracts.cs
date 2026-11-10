using GroupBuy.Domain;
namespace GroupBuy.Application;

// Selections only. The campaign fixes the single catalog variant/package.
public record CheckoutQuoteRequest(decimal Quantity, OrderFulfillmentMode FulfillmentMode, DeliveryAddress? Address = null, Guid? ProductVariantId = null);
public record CheckoutConfirmRequest(string QuoteId);
public record CheckoutPackage(Guid ProductId, Guid ProductVariantId, string ProductName, string VariantName,
    GroupDealType Type, UnitOfMeasure Unit, decimal UnitQuantity);
public record CheckoutQuoteResponse(string QuoteId, Guid DealId, CheckoutPackage Package, decimal Quantity,
    GroupDealProgress Progress, decimal CurrentUnitPrice, decimal MaximumUnitPrice, long ProjectedSubtotalMinor,
    long SubtotalMinor, OrderFulfillmentMode FulfillmentType, long DeliveryFeeMinor, long TotalMinor,
    long MaximumAuthorizedAmountMinor, string Currency, decimal AvailableQuantity, DateTimeOffset ExpiresAtUtc,
    DateTimeOffset ServerTimeUtc, string Provider, string? CheckoutKeyId, bool Sandbox);
public record CheckoutRefund(Guid Id, long AmountMinor, RefundState State, string Reason,
    string? ProviderReference, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public record CheckoutStatusResponse(PaymentOrderResponse Order, string Status, string Code, bool ParticipationConfirmed,
    bool RecoveryAvailable, bool RequiresOperator, DateTimeOffset? NextRecoveryAtUtc,
    IReadOnlyList<CheckoutRefund> Refunds, DateTimeOffset ServerTimeUtc, string Currency, string? CheckoutKeyId);
public interface ICheckout
{
    Task<CheckoutQuoteResponse> Quote(Actor actor, Guid dealId, CheckoutQuoteRequest request, CancellationToken ct);
    Task<CheckoutStatusResponse> ConfirmCheckout(Actor actor, Guid dealId, CheckoutConfirmRequest request, CancellationToken ct);
    Task<CheckoutStatusResponse> RecoverQuote(Actor actor, Guid dealId, CheckoutConfirmRequest request, CancellationToken ct);
    Task<CheckoutStatusResponse> CheckoutStatus(Actor actor, Guid orderId, CancellationToken ct);
    Task<CheckoutStatusResponse> RecoverCheckout(Actor actor, Guid orderId, CancellationToken ct);
}

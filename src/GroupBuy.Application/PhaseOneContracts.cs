using GroupBuy.Domain;
namespace GroupBuy.Application;

public record ServiceAreaOption(string Id,string Name,string[] PostalCodes);
public record PilotOptions(bool Enabled,bool PaymentsEnabled,bool Sandbox,string Provider,string? CheckoutKeyId,IReadOnlyList<ServiceAreaOption> ServiceAreas,long? DeliveryFeeMinor);
public record InterestRequest(Guid ProductId,string ServiceArea,decimal DesiredQuantity);
public record InterestResponse(Guid Id,Guid ProductId,string ProductName,string ServiceArea,decimal DesiredQuantity,bool Active);
public record DemandResponse(Guid ProductId,string ProductName,string ServiceArea,int Customers,decimal Quantity,int RecentCustomers);
public record PriceTierRequest(decimal Quantity,decimal UnitPrice);
public record TierConfigurationRequest(string ServiceArea,IReadOnlyList<PriceTierRequest> Tiers,decimal? NearTargetQuantity);
public record TierCampaignResponse(CampaignResponse Campaign,string ServiceArea,IReadOnlyList<PriceTierRequest> Tiers,decimal MinimumQuantity,decimal CurrentProjectedUnitPrice,decimal? NextQuantity,decimal? FinalUnitPrice,decimal? FinalQuantity,int ExtensionCount,bool NearTargetAccepted);
public record DeliveryAddress(string Recipient,string Phone,string Address,string? Landmark,string ServiceArea,string PostalCode);
public record PaymentOrderRequest(Guid CampaignId,decimal Quantity,string IdempotencyKey,OrderFulfillmentMode FulfillmentMode,DeliveryAddress? Address);
public record PaymentOrderResponse(Guid Id,Guid CampaignId,Guid? ReservationId,decimal Quantity,long MaximumAmountMinor,long DeliveryFeeMinor,long? FinalAmountMinor,long RefundedMinor,PaymentState State,string Provider,string? ProviderOrderId,DateTimeOffset ExpiresAtUtc,OrderFulfillmentMode FulfillmentMode,Guid? FulfillmentId,bool Sandbox,int Attempts=0,DateTimeOffset? RetryAtUtc=null,string? ErrorCode=null);
public record CheckoutConfirmation(string PaymentId,string Signature);
public record RescueRequest(string Action,DateTimeOffset? NewDeadlineUtc,string Reason);
public record FulfillmentAction(FulfillmentState State,string? Reason);
public record SecretRequest(string Secret);
public record FulfillmentPass(string OpaqueToken,string BackupCode,DateTimeOffset ExpiresAtUtc,int Version);
public record FulfillmentResponse(Guid Id,Guid PaymentOrderId,Guid CampaignId,string ProductName,decimal Quantity,string SellerName,OrderFulfillmentMode Mode,FulfillmentState State,bool ChoiceConfirmed,long DeliveryFeeMinor,long? FinalAmountMinor,DateTimeOffset DeadlineUtc,bool Redeemed,DeliveryAddress? DeliveryAddress,string? ExceptionReason);
public record PaymentRefundResponse(Guid Id,Guid PaymentOrderId,string Reason,long AmountMinor,RefundState State,int Attempts,string? ErrorCode);
public record PilotMetrics(int CampaignsCreated,int MinimumReached,int Successful,int Failed,int Completed,decimal CompletionRate,int InterestedCustomers,int PaidCustomers,decimal InterestConversion,IReadOnlyDictionary<string,int> AchievedTiers,int PendingRefunds,int ExhaustedRefunds);
public record ProviderOrder(string Id,string Status,long AmountMinor,string Currency);
public record ProviderPayment(string Id,string OrderId,string Status,long AmountMinor,string Currency);
public record ProviderRefund(string Id,string Status,long AmountMinor);
public record VerifiedPaymentEvent(string EventId,string OrderId,string PaymentId,string Status,long AmountMinor,string Currency);
public record PilotJobResponse(Guid Id,string Stage,Guid ItemId,PilotJobState State,int Attempts,int CycleAttempts,int RetryCount,DateTimeOffset? LastAttemptAtUtc,DateTimeOffset? NextAttemptAtUtc,string? ErrorCode);
public interface IPaymentGateway
{
    string Name { get; }
    bool Sandbox { get; }
    bool Available { get; }
    string? CheckoutKeyId { get; }
    long MinimumRefundMinor { get; }
    Task<ProviderOrder> CreateOrder(Guid localId,long amount,CancellationToken ct);
    Task<ProviderPayment?> Reconcile(string orderId,CancellationToken ct,bool captureAuthorized=false);
    Task<ProviderPayment> Confirm(string orderId,string paymentId,string signature,CancellationToken ct);
    VerifiedPaymentEvent VerifyWebhook(byte[] raw,string signature,string eventId);
    Task<ProviderRefund> Refund(string paymentId,Guid refundId,long amount,CancellationToken ct);
}
public interface IPhaseOne
{
    Task<PageResponse<PilotJobResponse>> Jobs(Actor actor,PageRequest page,PilotJobState? state,CancellationToken ct);
    Task RetryJob(Actor actor,Guid id,int expectedRetryCount,CancellationToken ct);
    PilotOptions Options();
    Task<InterestResponse> Interest(Actor actor,InterestRequest request,bool withdraw,CancellationToken ct);
    Task<PageResponse<InterestResponse>> Interests(Actor actor,PageRequest page,CancellationToken ct);
    Task<PageResponse<DemandResponse>> Demand(Actor actor,bool seller,PageRequest page,CancellationToken ct);
    Task<TierCampaignResponse> ConfigureTiers(Actor actor,Guid id,TierConfigurationRequest request,CancellationToken ct);
    Task<TierCampaignResponse> Campaign(Guid id,CancellationToken ct);
    Task<PageResponse<TierCampaignResponse>> Campaigns(PageRequest page,CancellationToken ct);
    Task<PaymentOrderResponse> Join(Actor actor,PaymentOrderRequest request,CancellationToken ct);
    Task<PageResponse<PaymentOrderResponse>> Orders(Actor actor,PageRequest page,bool admin,CancellationToken ct);
    Task<PaymentOrderResponse> Order(Actor actor,Guid id,CancellationToken ct);
    Task<PaymentOrderResponse> ConfirmPayment(Actor actor,Guid id,CheckoutConfirmation request,CancellationToken ct);
    Task Webhook(byte[] raw,string signature,string eventId,CancellationToken ct);
    Task<PaymentOrderResponse> SandboxPay(Actor actor,Guid id,bool success,CancellationToken ct);
    Task Cancel(Actor actor,Guid id,CancellationToken ct);
    Task<TierCampaignResponse> Finalize(Actor? actor,Guid id,CancellationToken ct);
    Task<TierCampaignResponse> Rescue(Actor actor,Guid id,RescueRequest request,CancellationToken ct);
    Task<PageResponse<FulfillmentResponse>> Fulfillments(Actor actor,string view,PageRequest page,string? area,Guid? seller,Guid? campaign,FulfillmentState? state,CancellationToken ct);
    Task<FulfillmentResponse> Fulfillment(Actor actor,Guid id,string view,CancellationToken ct);
    Task<FulfillmentResponse> ConfirmChoice(Actor actor,Guid id,CancellationToken ct);
    Task<FulfillmentResponse> MoveFulfillment(Actor actor,Guid id,FulfillmentAction request,CancellationToken ct);
    Task<FulfillmentPass> IssuePass(Actor actor,Guid id,CancellationToken ct);
    Task<FulfillmentResponse> VerifyPass(Actor actor,Guid id,SecretRequest request,CancellationToken ct);
    Task<FulfillmentResponse> VerifyOpaquePass(Actor actor,SecretRequest request,CancellationToken ct);
    Task<FulfillmentResponse> Redeem(Actor actor,Guid id,CancellationToken ct);
    Task FulfillmentRefund(Actor actor,Guid id,string reason,CancellationToken ct);
    Task<PageResponse<PaymentRefundResponse>> Refunds(Actor actor,PageRequest page,CancellationToken ct);
    Task RetryRefund(Actor actor,Guid id,int expectedRetryCount,CancellationToken ct);
    Task RetryPayment(Actor actor,Guid id,int expectedRetryCount,CancellationToken ct);
    Task<PilotMetrics> Metrics(Actor actor,CancellationToken ct);
    Task Process(CancellationToken ct);
}

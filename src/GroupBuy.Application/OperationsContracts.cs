using FluentValidation;
using GroupBuy.Domain;

namespace GroupBuy.Application;

public record InventoryRequest(decimal AvailableQuantity, long ExpectedVersion);
public record InventoryResponse(Guid VariantId, Guid ProductId, string Name, UnitOfMeasure Unit, decimal UnitQuantity, decimal AvailableQuantity, decimal CommittedQuantity, decimal ReservedQuantity, decimal FulfilledQuantity, long Version)
{
    public decimal UncommittedQuantity => AvailableQuantity - CommittedQuantity;
}
public record DecisionRequest(string IdempotencyKey, string? Reason);
public record PickupCodeRequest(string Code);
public record IssuedPickupCode(string Code, DateTimeOffset ExpiresAtUtc);
public record OperationCampaignResponse(CampaignResponse Campaign, decimal CommittedQuantity, decimal FulfilledQuantity, DateTimeOffset? ConfirmationDeadlineUtc, DateTimeOffset? PickupDeadlineUtc, DateTimeOffset? DecisionAtUtc, string? DecisionReason);
public record SellerSummaryResponse(int Products, int ActiveCampaigns, int PendingCampaigns, int CompletedCampaigns, int ActiveReservations, int PendingPickups, int CollectedPickups, decimal ReservationValueInr, decimal FulfilledValueInr);
public record SellerReservationResponse(Guid Id, Guid CampaignId, decimal Quantity, decimal UnitPriceSnapshot, decimal TotalPriceSnapshot, ReservationStatus Status, Guid? PickupId, PickupStatus? PickupStatus);
public record CampaignPerformanceResponse(OperationCampaignResponse Campaign, int TotalReservations, int ActiveReservations, int CancelledReservations, int CollectedPickups, decimal FulfilledValueInr);
public record ReservationDetailResponse(ReservationResponse Reservation, CampaignStatus CampaignStatus, PickupResponse Location, DateTimeOffset? ConfirmationDeadlineUtc, DateTimeOffset? PickupDeadlineUtc, Guid? PickupId, string NextAction, string PaymentStatus = "NotTracked");
public record PickupDetailResponse(Guid Id, Guid ReservationId, Guid CampaignId, PickupStatus Status, decimal Quantity, decimal TotalPriceSnapshot, string Instructions, DateTimeOffset DeadlineUtc, PickupPaymentPolicy PaymentPolicy, string NextAction);
public record NotificationResponse(Guid Id, NotificationType Type, Guid EntityId, DateTimeOffset CreatedAtUtc, DateTimeOffset? ReadAtUtc);
public record OperationsHealthResponse(DateTimeOffset CheckedAtUtc, int RetryingCampaigns, int OverdueCampaigns, int MaximumFailureCount, int LegacyCampaignsRequiringReview, WorkerSnapshot? Worker = null, int ExhaustedCampaigns = 0, int WorkerMaxAttempts = 10);

public interface IOperations
{
    Task<InventoryResponse> UpdateInventory(Actor actor, Guid variantId, InventoryRequest request, CancellationToken ct);
    Task<PageResponse<InventoryResponse>> SellerInventory(Actor actor, PageRequest page, CancellationToken ct);
    Task<PageResponse<OperationCampaignResponse>> SellerCampaigns(Actor actor, PageRequest page, CampaignStatus? status, CancellationToken ct);
    Task<SellerSummaryResponse> SellerSummary(Actor actor, CancellationToken ct);
    Task<PageResponse<SellerReservationResponse>> SellerReservations(Actor actor, Guid campaignId, PageRequest page, ReservationStatus? status, CancellationToken ct);
    Task<CampaignPerformanceResponse> CampaignPerformance(Actor actor, Guid id, CancellationToken ct);
    Task<OperationCampaignResponse> Decide(Actor actor, Guid campaignId, DecisionRequest request, bool approve, CancellationToken ct);
    Task<ReservationDetailResponse> ReservationDetail(Actor actor, Guid id, CancellationToken ct);
    Task<PickupDetailResponse> CustomerPickup(Actor actor, Guid id, CancellationToken ct);
    Task<PickupDetailResponse> SellerPickup(Actor actor, Guid id, CancellationToken ct);
    Task<PageResponse<PickupDetailResponse>> Pickups(Actor actor, bool sellerView, PageRequest page, PickupStatus? status, CancellationToken ct);
    Task<PickupDetailResponse> ReadyPickup(Actor actor, Guid id, CancellationToken ct);
    Task<IssuedPickupCode> IssuePickupCode(Actor actor, Guid id, CancellationToken ct);
    Task<PickupDetailResponse> CollectPickup(Actor actor, Guid id, PickupCodeRequest request, CancellationToken ct);
    Task<PageResponse<NotificationResponse>> Notifications(Actor actor, PageRequest page, bool unreadOnly, CancellationToken ct);
    Task ReadNotification(Actor actor, Guid id, CancellationToken ct);
    Task<OperationsHealthResponse> OperationsHealth(Actor actor, CancellationToken ct);
}

public sealed class InventoryValidator : AbstractValidator<InventoryRequest>
{
    public InventoryValidator() { RuleFor(x => x.AvailableQuantity).InclusiveBetween(0, 1000000).Must(x => decimal.Round(x, 3) == x); RuleFor(x => x.ExpectedVersion).GreaterThanOrEqualTo(0); }
}
public sealed class DecisionValidator : AbstractValidator<DecisionRequest>
{
    public DecisionValidator() { RuleFor(x => x.IdempotencyKey).NotEmpty().Matches("^[A-Za-z0-9_-]{8,100}$"); RuleFor(x => x.Reason).MaximumLength(500); }
}
public sealed class PickupCodeValidator : AbstractValidator<PickupCodeRequest>
{
    public PickupCodeValidator() { RuleFor(x => x.Code).NotEmpty().Matches("^[A-F0-9]{32}$"); }
}

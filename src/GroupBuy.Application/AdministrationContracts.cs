using GroupBuy.Domain;

namespace GroupBuy.Application;

public record AdminSummaryResponse(int TotalSellers, int PendingSellers, int ApprovedSellers, int PendingCampaigns, int ActiveCampaigns, int AwaitingConfirmationCampaigns, int ExpiredCampaigns, int TotalReservations, int AwaitingCollectionPickups);
public record AdminSellerResponse(Guid Id, Guid OwnerUserId, string BusinessName, SellerType SellerType, VerificationStatus VerificationStatus, ResourceStatus Status, string ContactEmail, string ContactPhone, string? FssaiRegistrationNumber, DateTimeOffset? ReviewedAt, IReadOnlyList<PrivateSellerLocationResponse> PickupLocations);
public record AdminCampaignResponse(OperationCampaignResponse Operation, string SellerBusinessName, VerificationStatus SellerVerificationStatus, Guid ProductId, string ProductName, string VariantName, string SKU, UnitOfMeasure UnitOfMeasure, decimal UnitQuantity, int WorkerFailureCount, DateTimeOffset? WorkerRetryAtUtc, string? WorkerErrorCode);
public record AdminCategoryResponse(Guid Id, Guid? ParentCategoryId, string Name, string Slug, bool IsActive, int SortOrder);
public record AdminReservationResponse(Guid Id, Guid CampaignId, Guid SellerId, decimal Quantity, decimal UnitPriceSnapshot, decimal TotalPriceSnapshot, ReservationStatus Status, DateTimeOffset CreatedAt);
public record AdminPickupResponse(Guid Id, Guid ReservationId, Guid CampaignId, Guid SellerId, PickupStatus Status, decimal Quantity, DateTimeOffset DeadlineUtc, DateTimeOffset? CollectedAtUtc);
public record AdminAuditResponse(Guid Id, DateTimeOffset TimestampUtc, Guid? ActorUserId, string? ActorDisplayName, string Action, string EntityType, Guid EntityId, string CorrelationId, IReadOnlyDictionary<string, string> SafeMetadata);

public interface IAdministration
{
    Task<AdminSummaryResponse> AdminSummary(Actor actor, CancellationToken ct);
    Task<PageResponse<AdminSellerResponse>> AdminSellers(Actor actor, PageRequest page, VerificationStatus? status, string? search, CancellationToken ct);
    Task<AdminSellerResponse> AdminSeller(Actor actor, Guid id, CancellationToken ct);
    Task<PageResponse<AdminCampaignResponse>> AdminCampaigns(Actor actor, PageRequest page, CampaignStatus? status, string? search, bool failedOnly, CancellationToken ct);
    Task<AdminCampaignResponse> AdminCampaign(Actor actor, Guid id, CancellationToken ct);
    Task<PageResponse<AdminCategoryResponse>> AdminCategories(Actor actor, PageRequest page, bool? active, string? search, CancellationToken ct);
    Task<PageResponse<SellerProductResponse>> AdminProducts(Actor actor, PageRequest page, ResourceStatus? status, string? search, CancellationToken ct);
    Task<PageResponse<AdminReservationResponse>> AdminReservations(Actor actor, PageRequest page, ReservationStatus? status, Guid? campaignId, CancellationToken ct);
    Task<PageResponse<AdminPickupResponse>> AdminPickups(Actor actor, PageRequest page, PickupStatus? status, Guid? campaignId, CancellationToken ct);
    Task<PageResponse<AdminAuditResponse>> AdminAudit(Actor actor, PageRequest page, string? action, string? entityType, Guid? actorId, Guid? entityId, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, CancellationToken ct);
}

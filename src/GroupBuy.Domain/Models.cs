namespace GroupBuy.Domain;

public enum AccountStatus { Active, Suspended }
public enum SellerType { Grocery, Fashion, Electronics, VehicleDealer, PropertyDeveloper, Other }
public enum VerificationStatus { Pending, Approved, Rejected }
public enum ResourceStatus { Active, Inactive }
public enum ProductType { Physical }
public enum UnitOfMeasure { Kilogram, Litre, Piece, Pack }
public enum FulfillmentMode { Pickup }
public enum CampaignStatus { Draft, PendingApproval, Scheduled, Open, TargetReached, AwaitingSellerConfirmation, Confirmed, FulfillmentInProgress, Completed, Expired, Cancelled, Failed, MinimumReached, Finalizing, Successful, Preparing, ReadyForFulfillment }
public enum ReservationStatus { Active, Cancelled, Expired, Completed }
public abstract class Entity { public Guid Id { get; set; } = Guid.NewGuid(); }
public abstract class TrackedEntity : Entity
{
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class User : TrackedEntity
{
    public string? Email { get; set; }
    public string? PhoneNumber { get; set; }
    public bool PhoneNumberVerified { get; set; }
    public bool EmailVerified { get; set; }
    public string DisplayName { get; set; } = "";
    public AccountStatus Status { get; set; }
}
public sealed class Seller : TrackedEntity
{
    public Guid OwnerUserId { get; set; }
    public string BusinessName { get; set; } = "";
    public SellerType SellerType { get; set; }
    public string Description { get; set; } = "";
    public string ContactEmail { get; set; } = "";
    public string ContactPhone { get; set; } = "";
    public VerificationStatus VerificationStatus { get; set; }
    public ResourceStatus Status { get; set; }
    public string? FssaiRegistrationNumber { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public Guid? ReviewerUserId { get; set; }
}
public sealed class SellerLocation : Entity
{
    public Guid SellerId { get; set; }
    public string AddressLine1 { get; set; } = "";
    public string? AddressLine2 { get; set; }
    public string Area { get; set; } = "";
    public string City { get; set; } = "";
    public string State { get; set; } = "";
    public string PostalCode { get; set; } = "";
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public bool IsPickupEnabled { get; set; } = true;
    public bool IsActive { get; set; } = true;
}
public sealed class Category : Entity
{
    public Guid? ParentCategoryId { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsActive { get; set; }
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class Product : TrackedEntity
{
    public Guid SellerId { get; set; }
    public Guid CategoryId { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Description { get; set; } = "";
    public ProductType ProductType { get; set; }
    public ResourceStatus Status { get; set; }
}
public sealed class ProductVariant : Entity
{
    public Guid SellerId { get; set; }
    public Guid ProductId { get; set; }
    public string SKU { get; set; } = "";
    public string VariantName { get; set; } = "";
    public UnitOfMeasure UnitOfMeasure { get; set; }
    public decimal UnitQuantity { get; set; }
    public string AttributesJson { get; set; } = "{}";
    public bool IsActive { get; set; } = true;
}
public sealed partial class GroupBuyCampaign : TrackedEntity
{
    public Guid SellerId { get; set; }
    public Guid ProductVariantId { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal ReferenceUnitPrice { get; set; }
    public decimal GroupUnitPrice { get; set; }
    public decimal TargetQuantity { get; set; }
    public decimal ReservedQuantity { get; set; }
    public decimal MinimumReservationQuantity { get; set; }
    public decimal MaximumReservationQuantity { get; set; }
    public DateTimeOffset StartsAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public CampaignStatus Status { get; set; }
    public FulfillmentMode FulfillmentMode { get; set; }
    public Guid SellerLocationId { get; set; }
    public uint RowVersion { get; set; }
    public decimal CommittedQuantity { get; set; }
    public decimal FulfilledQuantity { get; set; }
    public DateTimeOffset? ConfirmationDeadlineUtc { get; set; }
    public DateTimeOffset? DecisionAtUtc { get; set; }
    public Guid? DecisionActorId { get; set; }
    public string? DecisionReason { get; set; }
    public string? DecisionKey { get; set; }
    public bool? DecisionApproved { get; set; }
    public DateTimeOffset? PickupDeadlineUtc { get; set; }
    public int WorkerFailureCount { get; set; }
    public DateTimeOffset? WorkerRetryAtUtc { get; set; }
    public string? WorkerErrorCode { get; set; }
    public decimal Progress => TargetQuantity == 0 ? 0 : decimal.Round(ReservedQuantity / TargetQuantity * 100, 2);
    public void TransitionTo(CampaignStatus next)
    {
        if (!CampaignRules.CanTransition(Status, next)) throw new BusinessException(409, "invalid_transition", $"Cannot move {Status} to {next}.");
        Status = next;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
public sealed class GroupBuyReservation : TrackedEntity
{
    public Guid CampaignId { get; set; }
    public Guid CustomerUserId { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPriceSnapshot { get; set; }
    public decimal TotalPriceSnapshot { get; set; }
    public ReservationStatus Status { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public DateTimeOffset? CancelledAt { get; set; }
}
public sealed class AuditLog : Entity
{
    public Guid? ActorUserId { get; set; }
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public Guid EntityId { get; set; }
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;
    public string CorrelationId { get; set; } = "";
    public string SafeMetadataJson { get; set; } = "{}";
}
public sealed class BusinessException(int statusCode, string code, string message) : Exception(message)
{
    public string? ErrorCode { get; init; }
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

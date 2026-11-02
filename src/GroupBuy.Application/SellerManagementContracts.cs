using FluentValidation;
using GroupBuy.Domain;

namespace GroupBuy.Application;

public record PrivateSellerLocationResponse(Guid Id, string AddressLine1, string? AddressLine2, string Area, string City, string State, string PostalCode, decimal? Latitude, decimal? Longitude, bool IsActive, bool IsPickupEnabled);
public record SellerPrivateProfileResponse(Guid Id, string BusinessName, SellerType SellerType, string Description, VerificationStatus VerificationStatus, ResourceStatus Status, string ContactEmail, string ContactPhone, string? FssaiRegistrationNumber, DateTimeOffset? ReviewedAt, IReadOnlyList<PrivateSellerLocationResponse> PickupLocations);
public record SellerProductResponse(Guid Id, Guid SellerId, Guid CategoryId, string Name, string Slug, string Description, ProductType ProductType, IReadOnlyList<VariantResponse> Variants, ResourceStatus Status);
public record ProductStatusRequest(ResourceStatus? Status);

public sealed class ProductStatusValidator : AbstractValidator<ProductStatusRequest>
{
    public ProductStatusValidator() => RuleFor(x => x.Status).Must(x => x.HasValue && Enum.IsDefined(x.Value)).WithMessage("Status must be Active or Inactive.");
}

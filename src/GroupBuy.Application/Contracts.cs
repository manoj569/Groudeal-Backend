using FluentValidation;
using GroupBuy.Domain;
using System.Text.Json;

namespace GroupBuy.Application;
public record RegisterRequest(string Email, string Password, string DisplayName);
public record LoginRequest(string Email, string Password);
public record TokenResponse(string? AccessToken, DateTimeOffset ExpiresAtUtc, string? MfaChallenge = null, bool EnrollmentRequired = false);
public record UserResponse(Guid Id, string? Email, string DisplayName, IReadOnlyList<string> Roles);
public record LocationRequest(string AddressLine1, string? AddressLine2, string Area, string City, string State, string PostalCode, decimal? Latitude, decimal? Longitude);
public record SellerRequest(string BusinessName, SellerType SellerType, string Description, string ContactEmail, string ContactPhone, string? FssaiRegistrationNumber, LocationRequest Location);
public record ProductRequest(Guid CategoryId, string Name, string Slug, string Description, ProductType ProductType, string SKU, string VariantName, UnitOfMeasure UnitOfMeasure, decimal UnitQuantity, string AttributesJson);
public record CampaignRequest(Guid ProductVariantId, Guid SellerLocationId, string Title, string Description, decimal ReferenceUnitPrice, decimal GroupUnitPrice, decimal TargetQuantity, decimal MinimumReservationQuantity, decimal MaximumReservationQuantity, DateTimeOffset StartsAtUtc, DateTimeOffset ExpiresAtUtc);
public record ReservationRequest(decimal Quantity, string IdempotencyKey);
public record TransitionRequest(CampaignStatus Status);
public record ReviewRequest(string? Reason);
public record PageRequest(int Page = 1, int PageSize = 20)
{
    public int Offset => (Page - 1) * PageSize;
}
public record PageResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);
public record CategoryResponse(Guid Id, Guid? ParentCategoryId, string Name, string Slug, string Description);
public record PickupResponse(Guid Id, string AddressLine1, string? AddressLine2, string Area, string City, string State, string PostalCode);
public record SellerResponse(Guid Id, string BusinessName, SellerType SellerType, string Description, VerificationStatus VerificationStatus, IReadOnlyList<PickupResponse> PickupLocations);
public record SellerReviewResponse(Guid Id, Guid OwnerUserId, string BusinessName, string ContactEmail, string ContactPhone, string? FssaiRegistrationNumber, VerificationStatus VerificationStatus, IReadOnlyList<PickupResponse> Locations);
public record VariantResponse(Guid Id, string SKU, string VariantName, UnitOfMeasure UnitOfMeasure, decimal UnitQuantity, string AttributesJson);
public record ProductResponse(Guid Id, Guid SellerId, Guid CategoryId, string Name, string Slug, string Description, ProductType ProductType, IReadOnlyList<VariantResponse> Variants);
public record CampaignResponse(Guid Id, Guid SellerId, Guid ProductVariantId, Guid SellerLocationId, string Title, string Description, decimal ReferenceUnitPrice, decimal GroupUnitPrice, decimal TargetQuantity, decimal ReservedQuantity, decimal Progress, decimal MinimumReservationQuantity, decimal MaximumReservationQuantity, DateTimeOffset StartsAtUtc, DateTimeOffset ExpiresAtUtc, CampaignStatus Status, bool UsesTierPricing = false);
public record ReservationResponse(Guid Id, Guid CampaignId, decimal Quantity, decimal UnitPriceSnapshot, decimal TotalPriceSnapshot, ReservationStatus Status, DateTimeOffset CreatedAt, decimal CampaignProgress, bool UsesTierPricing = false);
public record DashboardResponse(Guid SellerId, VerificationStatus VerificationStatus, int Products, int Campaigns, int ActiveReservations);

public sealed class RegisterValidator : AbstractValidator<RegisterRequest>
{
    public RegisterValidator() { RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(254); RuleFor(x => x.Password).NotEmpty().MinimumLength(12).MaximumLength(128); RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(100); }
}
public sealed class LoginValidator : AbstractValidator<LoginRequest>
{
    public LoginValidator() { RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(254); RuleFor(x => x.Password).NotEmpty().MaximumLength(128); }
}
public sealed class LocationValidator : AbstractValidator<LocationRequest>
{
    public LocationValidator()
    {
        RuleFor(x => x.AddressLine1).NotEmpty().MaximumLength(250); RuleFor(x => x.AddressLine2).MaximumLength(250);
        RuleFor(x => x.Area).NotEmpty().MaximumLength(100); RuleFor(x => x.City).NotEmpty().MaximumLength(100); RuleFor(x => x.State).NotEmpty().MaximumLength(100);
        RuleFor(x => x.PostalCode).NotEmpty().Matches("^[1-9][0-9]{5}$"); RuleFor(x => x.Latitude).InclusiveBetween(-90, 90); RuleFor(x => x.Longitude).InclusiveBetween(-180, 180);
    }
}
public sealed class SellerValidator : AbstractValidator<SellerRequest>
{
    public SellerValidator()
    {
        RuleFor(x => x.BusinessName).NotEmpty().MaximumLength(200); RuleFor(x => x.SellerType).IsInEnum(); RuleFor(x => x.Description).NotNull().MaximumLength(2000);
        RuleFor(x => x.ContactEmail).NotEmpty().EmailAddress().MaximumLength(254); RuleFor(x => x.ContactPhone).NotEmpty().Matches("^\\+?[0-9]{10,15}$");
        RuleFor(x => x.FssaiRegistrationNumber).Matches("^[0-9]{14}$").When(x => !string.IsNullOrEmpty(x.FssaiRegistrationNumber));
        RuleFor(x => x.Location).NotNull().SetValidator(new LocationValidator());
    }
}
public sealed class ProductValidator : AbstractValidator<ProductRequest>
{
    public ProductValidator()
    {
        RuleFor(x => x.CategoryId).NotEmpty(); RuleFor(x => x.Name).NotEmpty().MaximumLength(200); RuleFor(x => x.Slug).NotEmpty().Matches("^[a-z0-9]+(?:-[a-z0-9]+)*$").MaximumLength(200);
        RuleFor(x => x.Description).NotNull().MaximumLength(4000); RuleFor(x => x.ProductType).IsInEnum(); RuleFor(x => x.SKU).NotEmpty().MaximumLength(80);
        RuleFor(x => x.VariantName).NotEmpty().MaximumLength(100); RuleFor(x => x.UnitOfMeasure).IsInEnum(); RuleFor(x => x.UnitQuantity).GreaterThan(0).LessThanOrEqualTo(1000000).Must(x => decimal.Round(x, 3) == x);
        RuleFor(x => x.AttributesJson).Must(ValidAttributes).WithMessage("Attributes must be an object of at most 8 allowed string properties, up to 100 characters each.");
    }
    public static bool ValidAttributes(string? value)
    {
        if (value is null || value.Length > 2048) return false;
        try { using var json = JsonDocument.Parse(value); return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.EnumerateObject().Count() <= 8 && json.RootElement.EnumerateObject().All(p => new[] { "brand", "size", "color", "material", "model", "configuration", "grade", "origin" }.Contains(p.Name) && p.Value.ValueKind == JsonValueKind.String && p.Value.GetString()!.Length <= 100) && json.RootElement.EnumerateObject().Select(p => p.Name).Distinct().Count() == json.RootElement.EnumerateObject().Count(); }
        catch (JsonException) { return false; }
    }
}
public sealed class CampaignValidator : AbstractValidator<CampaignRequest>
{
    public CampaignValidator()
    {
        RuleFor(x => x.ProductVariantId).NotEmpty(); RuleFor(x => x.SellerLocationId).NotEmpty(); RuleFor(x => x.Title).NotEmpty().MaximumLength(200); RuleFor(x => x.Description).NotNull().MaximumLength(4000);
        RuleFor(x => x.ReferenceUnitPrice).GreaterThan(0).LessThanOrEqualTo(10000000).Must(x => decimal.Round(x, 2) == x);
        RuleFor(x => x.GroupUnitPrice).GreaterThan(0).LessThanOrEqualTo(x => x.ReferenceUnitPrice).Must(x => decimal.Round(x, 2) == x);
        RuleFor(x => x.TargetQuantity).GreaterThan(0).LessThanOrEqualTo(1000000).Must(x => decimal.Round(x, 3) == x);
        RuleFor(x => x.MinimumReservationQuantity).GreaterThan(0).LessThanOrEqualTo(x => x.MaximumReservationQuantity).Must(x => decimal.Round(x, 3) == x);
        RuleFor(x => x.MaximumReservationQuantity).LessThanOrEqualTo(x => x.TargetQuantity).Must(x => decimal.Round(x, 3) == x);
        RuleFor(x => x.ExpiresAtUtc).GreaterThan(x => x.StartsAtUtc);
        RuleFor(x => x.StartsAtUtc).Must(x => x.Offset == TimeSpan.Zero); RuleFor(x => x.ExpiresAtUtc).Must(x => x.Offset == TimeSpan.Zero);
    }
}
public sealed class ReservationValidator : AbstractValidator<ReservationRequest>
{
    public ReservationValidator() { RuleFor(x => x.Quantity).GreaterThan(0).LessThanOrEqualTo(1000000).Must(x => decimal.Round(x, 3) == x); RuleFor(x => x.IdempotencyKey).NotEmpty().Matches("^[A-Za-z0-9_-]{8,100}$"); }
}
public sealed class PageValidator : AbstractValidator<PageRequest>
{
    public PageValidator() { RuleFor(x => x.Page).InclusiveBetween(1, 100000); RuleFor(x => x.PageSize).InclusiveBetween(1, 100); }
}
public static class Validate
{
    public static void With<T>(T request, IValidator<T> validator) => validator.ValidateAndThrow(request);
}


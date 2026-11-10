using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GroupBuy.Domain;
namespace GroupBuy.Application;

public record DemandListFilter(DemandStatus? Status = null, DemandType? Type = null, Guid? CategoryId = null, string? ServiceArea = null);
public record DemandCategory(Guid Id, string Name);
public record DemandProduct(Guid Id, string Name);
public record DemandProgress(int CurrentInterest, int? TargetInterest = null, decimal? ProgressPercentage = null);
public record DemandParticipation(bool Joined, decimal? Quantity, string? Unit, string? Requirement, string? Details,
    DateTimeOffset? JoinedAtUtc, DateTimeOffset? UpdatedAtUtc, bool CanWithdraw);
public record DemandDetail(Guid DemandId, DemandType DemandType, string Title, DemandCategory? Category, DemandProduct? Product,
    string ServiceArea, string? Locality, DemandStatus Status, DemandProgress Progress, DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc, DemandParticipation? Membership, int ActiveOfferCount = 0)
{
    public bool IsFree => true;
    public bool GuaranteesFulfillment => false;
    public string SellerOpportunityState => ActiveOfferCount > 0 ? "OffersAvailable" : "NotStarted";
    public string? GroupDealsRoute => ActiveOfferCount > 0 ? $"/api/v1/group-buys/deals?demandId={DemandId:D}&serviceArea={ServiceArea}" : null;
    public string Message => Status == DemandStatus.BuildingInterest ? ActiveOfferCount > 0 ? "Seller offers are available. Review an offer before joining a paid group." : "Building local interest. Seller outreach has not started." : "This demand is no longer accepting interest.";
}
public record MyDemand(DemandDetail Demand, bool Created, DemandParticipation Participation);
public record DemandPreview(CreateDemandRequest Normalized, bool SimilarDemandFound, DemandDetail? Demand, bool CanSubmit = true, string? BlockingCode = null);
public record ModerateDemandRequest(DemandStatus Status, string Reason);
public record DemandAudit(string Action, DateTimeOffset TimestampUtc);
public record AdminDemandDetail(DemandDetail Demand, string? ClosureReason, DateTimeOffset? ClosedAtUtc, IReadOnlyList<DemandAudit> RecentAudit);

public static class DemandInput
{
    public static string CatalogTitle(string value)
    {
        string normalized;
        try { normalized = string.Join(' ', value.Normalize(NormalizationForm.FormKC).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)); }
        catch (ArgumentException) { throw new BusinessException(400, "invalid_product", "Choose a product with a valid catalog title."); }
        var title = new StringBuilder(100);
        foreach (var rune in normalized.EnumerateRunes())
        {
            if (title.Length + rune.Utf16SequenceLength > 100) break;
            title.Append(rune.ToString());
        }
        if (title.Length == 0) throw new BusinessException(400, "invalid_product", "Choose a product with a valid catalog title.");
        return title.ToString().TrimEnd();
    }
    public static bool Rich(CreateDemandRequest r) => r.DemandType.HasValue || r.ProductId.HasValue || r.Quantity.HasValue || r.Unit is not null || r.Requirement is not null || r.Details is not null;
    public static string? Text(string? value, int maximum)
    {
        if (value is null) return null;
        if (value.Length > maximum * 2 || value.Any(c => char.IsControl(c) && !char.IsWhiteSpace(c))) Invalid();
        string text;
        try { text = string.Join(' ', value.Normalize(NormalizationForm.FormKC).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)); }
        catch (ArgumentException) { throw new BusinessException(400, "validation_failed", "Use valid public requirement text."); }
        if (text.Length > maximum || text.IndexOfAny(['<', '>', '@']) >= 0 || text.Contains("://", StringComparison.Ordinal)
            || Regex.IsMatch(text, @"\d[\d\s+()-]{7,}\d", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) Invalid();
        return text.Length == 0 ? null : text;
    }
    public static CreateDemandRequest Normalize(CreateDemandRequest r)
    {
        var rich = Rich(r); var type = r.DemandType ?? GroupBuy.Domain.DemandType.Product;
        if (!Enum.IsDefined(type)) throw new BusinessException(400, "invalid_demand_type", "Choose Product or Service.");
        if (rich && (!r.DemandType.HasValue || !r.CategoryId.HasValue)) Invalid();
        var title = r.ProductId.HasValue ? "Catalog item" : DiscoveryInput.PublicTitle(r.Title);
        var unit = r.Unit?.Trim().ToLowerInvariant();
        if (r.Quantity.HasValue != (unit is not null) || r.Quantity is <= 0 or > 1000000 || (r.Quantity.HasValue && decimal.Round(r.Quantity.Value, 3) != r.Quantity.Value)) Invalid();
        if (unit is not null && !(type == GroupBuy.Domain.DemandType.Product ? new[] { "kg", "l", "piece", "pack" } : new[] { "household", "project", "hour", "visit", "square_metre" }).Contains(unit)) Invalid();
        if (rich && type == GroupBuy.Domain.DemandType.Product && !r.Quantity.HasValue) Invalid();
        if (type == GroupBuy.Domain.DemandType.Service && r.ProductId.HasValue) throw new BusinessException(400, "invalid_product", "The current catalog contains physical products; use a custom service requirement.");
        return r with { Title = title, DemandType = type, Unit = unit, Requirement = Text(r.Requirement, 500), Details = Text(r.Details, 1000) };
    }
    public static string Canonical(CreateDemandRequest r) => JsonSerializer.Serialize(new object?[] { r.DemandType ?? GroupBuy.Domain.DemandType.Product, r.CategoryId, r.ProductId, r.ProductId.HasValue ? "" : r.Title.ToLowerInvariant(), r.ServiceArea });
    public static void ValidateFilter(DemandListFilter filter)
    {
        if ((filter.Type.HasValue && !Enum.IsDefined(filter.Type.Value)) || (filter.Status.HasValue && !Enum.IsDefined(filter.Status.Value))) Invalid();
    }
    private static void Invalid() => throw new BusinessException(400, "validation_failed", "Check the demand type, category, quantity/unit and public requirement text.");
}

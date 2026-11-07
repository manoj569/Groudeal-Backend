using System.Text;
using System.Text.RegularExpressions;
using GroupBuy.Domain;

namespace GroupBuy.Application;

public record DiscoveryFilter(string? PostalCode = null, string? ServiceArea = null, Guid? CategoryId = null);
public record DiscoveryLocation(string State, bool Supported, string? PostalCode, string? ServiceArea, string? DisplayName);
public record DiscoveryCategory
{
    public Guid Id { get; init; }
    public Guid? ParentCategoryId { get; init; }
    public string Name { get; init; } = "";
    public string Slug { get; init; } = "";
    public string Description { get; init; } = "";
    public bool IsDiscoverable { get; init; }
    public string Type => "category";
}
public record DiscoveryProduct(Guid Id, Guid CategoryId, string Name, string Slug, bool HasEligibleDeal)
{ public string Type => "product"; public bool Purchasable => HasEligibleDeal; }
public record DealCard(Guid Id, Guid ProductId, Guid CategoryId, string Title, string ProductName, string SellerName,
    decimal EffectiveUnitPrice, decimal? ReferenceUnitPrice, decimal? DiscountPercent, decimal PaidQuantity,
    decimal MinimumQuantity, decimal TargetQuantity, decimal? NextTierQuantity, decimal? NextTierUnitPrice,
    decimal ProgressPercent, CampaignStatus Status, DateTimeOffset PublishedAtUtc, DateTimeOffset EndsAtUtc,
    string ServiceArea, string Locality, bool PickupAvailable, bool DeliveryAvailable)
{ public string Type => "deal"; }
public record DemandCard(Guid? DemandId, Guid? ProductId, Guid? CategoryId, string Title, string Source,
    string ServiceArea, string Locality, int InterestedCustomers, DateTimeOffset UpdatedAtUtc)
{ public string Type => "demand"; public bool IsFree => true; public bool GuaranteesFulfillment => false; }
public record DemandHandoff(string Query, string CreateMethod, string CreateRoute, bool RequiresAuthentication, bool CreatesAutomatically = false);
public record DiscoveryHome(DiscoveryLocation Location, IReadOnlyList<DiscoveryCategory> Categories, IReadOnlyList<DealCard> NearYou,
    IReadOnlyList<DealCard> TrendingDeals, IReadOnlyList<DemandCard> PopularDemands, IReadOnlyList<DealCard> NewDeals);
public record DiscoverySearch(DiscoveryLocation Location, string Query, PageResponse<DealCard> Deals,
    PageResponse<DemandCard> Demands, PageResponse<DiscoveryProduct> Products, PageResponse<DiscoveryCategory> Categories,
    bool NoResults, DemandHandoff? CreateDemand);
public record DiscoverySuggestion(string Type, Guid Id, string Text);
public record DiscoverySuggestions(DiscoveryLocation Location, string Query, IReadOnlyList<DiscoverySuggestion> Items);
public record CategoryDiscovery(DiscoveryLocation Location, DiscoveryCategory Category, IReadOnlyList<DiscoveryCategory> Subcategories,
    PageResponse<DealCard> Deals, PageResponse<DemandCard> Demands);
public record CreateDemandRequest(string Title, string ServiceArea, Guid? CategoryId = null);
public record DemandMembership(Guid DemandId, bool Joined);

public interface IDiscovery
{
    DiscoveryLocation ServiceArea(DiscoveryFilter filter);
    Task<DiscoveryHome> Home(DiscoveryFilter filter, CancellationToken ct);
    Task<DiscoverySearch> Search(string query, DiscoveryFilter filter, PageRequest page, CancellationToken ct);
    Task<DiscoverySuggestions> Suggestions(string query, DiscoveryFilter filter, int limit, CancellationToken ct);
    Task<CategoryDiscovery> Category(string slug, DiscoveryFilter filter, PageRequest page, CancellationToken ct);
    Task<PageResponse<DealCard>> Deals(string sort, DiscoveryFilter filter, PageRequest page, CancellationToken ct);
    Task<PageResponse<DemandCard>> Demands(DiscoveryFilter filter, PageRequest page, CancellationToken ct);
    Task<DemandMembership> CreateDemand(Actor actor, CreateDemandRequest request, CancellationToken ct);
    Task<DemandMembership> JoinDemand(Actor actor, Guid id, bool withdraw, CancellationToken ct);
    Task<DemandMembership> Membership(Actor actor, Guid id, CancellationToken ct);
    Task CloseDemand(Actor actor, Guid id, CancellationToken ct);
}

public static class DiscoveryInput
{
    public static string Query(string? value)
    {
        if (value is null || value.Length > 256 || value.Any(c => char.IsControl(c) && !char.IsWhiteSpace(c)))
            throw new BusinessException(400, "invalid_query", "Use a search query between 2 and 100 characters.");
        string normalized;
        try { normalized = string.Join(' ', value.Normalize(NormalizationForm.FormKC).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)); }
        catch (ArgumentException) { throw new BusinessException(400, "invalid_query", "Use valid text."); }
        if (normalized.Length is < 2 or > 100) throw new BusinessException(400, "invalid_query", "Use a search query between 2 and 100 characters.");
        return normalized;
    }

    public static string PublicTitle(string? value)
    {
        var title = Query(value);
        if (title.IndexOfAny(['<', '>', '@']) >= 0 || title.Contains("://", StringComparison.Ordinal)
            || Regex.IsMatch(title, @"\d[\d\s+()-]{7,}\d", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            throw new BusinessException(400, "invalid_demand_title", "Use a public product or service title without contact details, links or markup.");
        return title;
    }
}

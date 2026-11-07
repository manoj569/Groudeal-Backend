using GroupBuy.Application;
using GroupBuy.Domain;
using Microsoft.EntityFrameworkCore;

namespace GroupBuy.Infrastructure;

public sealed partial class DiscoveryService(MarketplaceDb db, DiscoveryAreas areas, DiscoveryQueries queries, TimeProvider clock) : IDiscovery
{
    private const int Preview = 6;
    private static void Page(PageRequest page) => Validate.With(page, new PageValidator());
    public DiscoveryLocation ServiceArea(DiscoveryFilter filter) => areas.Resolve(filter);

    private async Task<PageResponse<DealCard>> Deals(string sort, DiscoveryLocation location, DiscoveryFilter filter, PageRequest page, string? query, CancellationToken ct)
    {
        Page(page);
        var rows = queries.DealRows(location, filter.CategoryId, query);
        var ordered = sort switch
        {
            "trending" => rows.OrderByDescending(r => r.PaidQuantity).ThenByDescending(r => r.Published).ThenBy(r => r.Id),
            "new" or "near-you" => rows.OrderByDescending(r => r.Published).ThenBy(r => r.Id),
            _ => throw new BusinessException(400, "invalid_sort", "Use trending, new or near-you.")
        };
        var total = await rows.CountAsync(ct);
        var selected = await ordered.Skip(page.Offset).Take(page.PageSize).ToListAsync(ct);
        return new(await queries.Cards(selected, location, ct), page.Page, page.PageSize, total);
    }

    public Task<PageResponse<DealCard>> Deals(string sort, DiscoveryFilter filter, PageRequest page, CancellationToken ct) => Deals(sort, areas.Resolve(filter), filter, page, null, ct);

    private async Task<PageResponse<DemandCard>> Demands(DiscoveryLocation location, DiscoveryFilter filter, PageRequest page, string? query, CancellationToken ct)
    {
        Page(page); var rows = queries.DemandRows(location, filter.CategoryId, query);
        var total = await rows.CountAsync(ct);
        var selected = await rows.OrderByDescending(r => r.Customers).ThenByDescending(r => r.Updated).ThenBy(r => r.Source).ThenBy(r => r.Id)
            .Skip(page.Offset).Take(page.PageSize).ToListAsync(ct);
        return new(selected.Select(r => new DemandCard(r.ProductId is null ? r.Id : null, r.ProductId, r.CategoryId,
            r.Title, r.Source, r.ServiceArea, location.DisplayName!, r.Customers, r.Updated)).ToArray(), page.Page, page.PageSize, total);
    }

    public Task<PageResponse<DemandCard>> Demands(DiscoveryFilter filter, PageRequest page, CancellationToken ct) => Demands(areas.Resolve(filter), filter, page, null, ct);

    public async Task<DiscoveryHome> Home(DiscoveryFilter filter, CancellationToken ct)
    {
        var location = areas.Resolve(filter);
        var categories = await queries.Categories(location).Take(20).ToListAsync(ct);
        var newest = await Deals("new", location, filter, new(1, Preview), null, ct);
        var trending = await Deals("trending", location, filter, new(1, Preview), null, ct);
        var demands = await Demands(location, filter, new(1, Preview), null, ct);
        return new(location, categories, newest.Items, trending.Items, demands.Items, newest.Items);
    }

    public async Task<DiscoverySearch> Search(string query, DiscoveryFilter filter, PageRequest page, CancellationToken ct)
    {
        Page(page); var normalized = DiscoveryInput.Query(query); var match = normalized.ToLowerInvariant(); var location = areas.Resolve(filter);
        var deals = await Deals("trending", location, filter, page, match, ct);
        var demands = await Demands(location, filter, page, match, ct);
        var products = queries.Products(location, filter.CategoryId).Where(p => p.Name.ToLower().Contains(match));
        var eligible = queries.DealRows(location, filter.CategoryId);
        var productRows = await products.OrderBy(p => p.Name).ThenBy(p => p.Id).Skip(page.Offset).Take(page.PageSize)
            .Select(p => new DiscoveryProduct(p.Id, p.CategoryId, p.Name, p.Slug, eligible.Any(d => d.ProductId == p.Id))).ToListAsync(ct);
        var cats = queries.Categories(location, query: match);
        var categoryRows = await cats.Skip(page.Offset).Take(page.PageSize).ToListAsync(ct);
        var productTotal = await products.CountAsync(ct); var categoryTotal = await cats.CountAsync(ct);
        var empty = deals.Total + demands.Total + productTotal + categoryTotal == 0;
        return new(location, normalized, deals, demands, new(productRows, page.Page, page.PageSize, productTotal),
            new(categoryRows, page.Page, page.PageSize, categoryTotal), empty,
            deals.Total == 0 && demands.Total == 0 && location.Supported ? new(normalized, "POST", "/api/v1/discovery/demands", true) : null);
    }

    public async Task<DiscoverySuggestions> Suggestions(string query, DiscoveryFilter filter, int limit, CancellationToken ct)
    {
        if (limit is < 1 or > 10) throw new BusinessException(400, "invalid_limit", "Use a suggestion limit between 1 and 10.");
        var normalized = DiscoveryInput.Query(query); var match = normalized.ToLowerInvariant(); var location = areas.Resolve(filter);
        // Each source is bounded in SQL. No progress aggregates or private entities are loaded for suggestions.
        var cats = await queries.Categories(location, query: match).Take(limit).Select(c => new DiscoverySuggestion("category", c.Id, c.Name)).ToListAsync(ct);
        var products = await queries.Products(location, filter.CategoryId).Where(p => p.Name.ToLower().StartsWith(match))
            .OrderBy(p => p.Name).ThenBy(p => p.Id).Take(limit).Select(p => new DiscoverySuggestion("product", p.Id, p.Name)).ToListAsync(ct);
        var deals = await queries.DealRows(location, filter.CategoryId, match).OrderBy(r => r.Title).ThenBy(r => r.Id)
            .Take(limit).Select(r => new DiscoverySuggestion("deal", r.Id, r.Title)).ToListAsync(ct);
        var demands = await queries.DemandRows(location, filter.CategoryId, match).OrderBy(r => r.Title).ThenBy(r => r.Source).ThenBy(r => r.Id)
            .Take(limit).Select(r => new DiscoverySuggestion("demand", r.Id, r.Title)).ToListAsync(ct);
        var result = cats.Concat(products).Concat(deals).Concat(demands).OrderBy(s => s.Type, StringComparer.Ordinal)
            .ThenBy(s => s.Text, StringComparer.Ordinal).ThenBy(s => s.Id).Take(limit).ToArray();
        return new(location, normalized, result);
    }

    public async Task<CategoryDiscovery> Category(string slug, DiscoveryFilter filter, PageRequest page, CancellationToken ct)
    {
        Page(page); var location = areas.Resolve(filter);
        var category = await db.Categories.AsNoTracking().Where(c => c.IsActive && (c.Slug == slug || c.Id.ToString() == slug)
            && (c.ParentCategoryId == null || db.Categories.Any(p => p.Id == c.ParentCategoryId && p.IsActive)))
            .Select(c => new DiscoveryCategory { Id = c.Id, ParentCategoryId = c.ParentCategoryId, Name = c.Name, Slug = c.Slug, Description = c.Description, IsDiscoverable = false }).SingleOrDefaultAsync(ct)
            ?? throw new BusinessException(404, "not_found", "Category not found.");
        var discoverable = await queries.Categories(location).AnyAsync(c => c.Id == category.Id, ct);
        var children = await queries.Categories(location, category.Id).Take(100).ToListAsync(ct);
        var selected = filter with { CategoryId = category.Id };
        return new(location, category with { IsDiscoverable = discoverable }, children,
            await Deals("trending", location, selected, page, null, ct), await Demands(location, selected, page, null, ct));
    }
}

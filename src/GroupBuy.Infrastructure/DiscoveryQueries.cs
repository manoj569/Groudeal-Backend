using GroupBuy.Application;
using GroupBuy.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace GroupBuy.Infrastructure;

internal sealed class DealRow
{
    public Guid Id { get; init; }
    public Guid ProductId { get; init; }
    public Guid CategoryId { get; init; }
    public string Title { get; init; } = "";
    public string ProductName { get; init; } = "";
    public string SellerName { get; init; } = "";
    public decimal ReferencePrice { get; init; }
    public decimal PaidQuantity { get; init; }
    public decimal Target { get; init; }
    public CampaignStatus Status { get; init; }
    public DateTimeOffset Published { get; init; }
    public DateTimeOffset Ends { get; init; }
    public string ServiceArea { get; init; } = "";
}
internal sealed class DemandRow
{
    public Guid Id { get; init; }
    public Guid? ProductId { get; init; }
    public Guid? CategoryId { get; init; }
    public string Title { get; init; } = "";
    public string Source { get; init; } = "";
    public string ServiceArea { get; init; } = "";
    public int Customers { get; init; }
    public DateTimeOffset Updated { get; init; }
}

public sealed class DiscoveryQueries(MarketplaceDb db, IConfiguration config, TimeProvider clock, IPaymentGateway gateway, DiscoveryAreas areas)
{
    internal IQueryable<Product> Products(DiscoveryLocation location, Guid? category)
    {
        var codes = areas.PostalCodes(location);
        return PublicCatalog.Products(db).AsNoTracking().Where(p => location.Supported
            && db.Sellers.Any(s => s.Id == p.SellerId && db.UsersProfile.Any(u => u.Id == s.OwnerUserId && u.Status == AccountStatus.Active))
            && db.Categories.Any(c => c.Id == p.CategoryId && (c.ParentCategoryId == null || db.Categories.Any(parent => parent.Id == c.ParentCategoryId && parent.IsActive)))
            && db.Variants.Any(v => v.ProductId == p.Id && v.IsActive)
            && db.Locations.Any(l => l.SellerId == p.SellerId && l.IsActive && l.IsPickupEnabled && codes.Contains(l.PostalCode))
            && (category == null || p.CategoryId == category || db.Categories.Any(c => c.Id == p.CategoryId && c.ParentCategoryId == category)));
    }

    internal IQueryable<DealRow> DealRows(DiscoveryLocation location, Guid? category, string? query = null)
    {
        var now = clock.GetUtcNow(); var products = Products(location, category);
        var codes = areas.PostalCodes(location);
        var enabled = config.GetValue("Pilot:Enabled", false) && gateway.Available;
        var orders = db.PaymentOrders.AsNoTracking();
        return from c in db.Campaigns.AsNoTracking()
               join v in db.Variants.AsNoTracking() on c.ProductVariantId equals v.Id
               join p in products on v.ProductId equals p.Id
               join s in db.Sellers.AsNoTracking() on c.SellerId equals s.Id
               where enabled && c.UsesTierPricing && v.IsActive && v.SellerId == s.Id && p.SellerId == s.Id
                   && (c.Status == CampaignStatus.Open || c.Status == CampaignStatus.MinimumReached)
                   && c.StartsAtUtc <= now && c.ExpiresAtUtc > now && c.ServiceArea == location.ServiceArea
                   && db.Locations.Any(l => l.Id == c.SellerLocationId && l.SellerId == s.Id && l.IsActive && l.IsPickupEnabled && codes.Contains(l.PostalCode))
                   && db.PriceTiers.Any(t => t.CampaignId == c.Id)
                   && db.Inventories.Any(i => i.ProductVariantId == v.Id && i.SellerId == s.Id && i.AvailableQuantity >= c.CommittedQuantity)
                   && c.ReservedQuantity + (orders.Where(o => o.CampaignId == c.Id && o.ExpiresAtUtc > now
                       && (o.State == PaymentState.Creating || o.State == PaymentState.Pending || o.State == PaymentState.Authorized)).Sum(o => (decimal?)o.Quantity) ?? 0)
                       + c.MinimumReservationQuantity <= c.TargetQuantity
                   && c.CommittedQuantity >= c.TargetQuantity
                   && (query == null || c.Title.ToLower().Contains(query) || p.Name.ToLower().Contains(query))
               select new DealRow { Id = c.Id, ProductId = p.Id, CategoryId = p.CategoryId, Title = c.Title,
                   ProductName = p.Name, SellerName = s.BusinessName, ReferencePrice = c.ReferenceUnitPrice,
                   PaidQuantity = orders.Where(o => o.CampaignId == c.Id && o.State == PaymentState.Captured && o.CapturedAtUtc != null
                       && o.RefundedMinor == 0 && o.FinalAmountMinor == null
                       && !db.PaymentRefunds.Any(r => r.PaymentOrderId == o.Id)
                       && db.UsersProfile.Any(u => u.Id == o.CustomerId && u.Status == AccountStatus.Active)
                       && db.Reservations.Any(r => r.Id == o.ReservationId && r.CampaignId == c.Id && r.CustomerUserId == o.CustomerId
                           && r.Status == ReservationStatus.Active && r.Quantity == o.Quantity)).Sum(o => (decimal?)o.Quantity) ?? 0,
                   Target = c.TargetQuantity, Status = c.Status,
                   Published = db.AuditLogs.Where(a => a.EntityId == c.Id && a.EntityType == "GroupBuyCampaign" && a.Action == "CampaignApproved")
                       .Max(a => (DateTimeOffset?)a.TimestampUtc) ?? c.StartsAtUtc,
                   Ends = c.ExpiresAtUtc, ServiceArea = c.ServiceArea };
    }

    internal IQueryable<DemandRow> DemandRows(DiscoveryLocation location, Guid? category, string? query = null)
    {
        var interests = db.Interests.AsNoTracking().Where(i => i.Active && i.ServiceArea == location.ServiceArea
            && db.UsersProfile.Any(u => u.Id == i.CustomerId && u.Status == AccountStatus.Active));
        var productDemand = from i in interests
                            join p in Products(location, category) on i.ProductId equals p.Id
                            where query == null || p.Name.ToLower().Contains(query)
                            group i by new { p.Id, p.CategoryId, p.Name, i.ServiceArea } into g
                            select new DemandRow { Id = g.Key.Id, ProductId = g.Key.Id, CategoryId = g.Key.CategoryId, Title = g.Key.Name,
                                Source = "product_interest", ServiceArea = g.Key.ServiceArea, Customers = g.Select(i => i.CustomerId).Distinct().Count(), Updated = g.Max(i => i.UpdatedAt) };
        var freeDemand = from d in db.DiscoveryDemands.AsNoTracking()
                         where location.Supported && d.IsOpen && d.ServiceArea == location.ServiceArea
                             && (d.CategoryId == null || db.Categories.Any(c => c.Id == d.CategoryId && c.IsActive
                                 && (c.ParentCategoryId == null || db.Categories.Any(p => p.Id == c.ParentCategoryId && p.IsActive))))
                             && (category == null || d.CategoryId == category || db.Categories.Any(c => c.Id == d.CategoryId && c.ParentCategoryId == category))
                             && (query == null || d.Title.ToLower().Contains(query))
                             && interests.Any(i => i.DemandId == d.Id)
                         select new DemandRow { Id = d.Id, ProductId = null, CategoryId = d.CategoryId, Title = d.Title, Source = "free_text", ServiceArea = d.ServiceArea,
                             Customers = interests.Where(i => i.DemandId == d.Id).Select(i => i.CustomerId).Distinct().Count(),
                             Updated = interests.Where(i => i.DemandId == d.Id).Max(i => i.UpdatedAt) };
        return productDemand.Concat(freeDemand);
    }

    internal IQueryable<DiscoveryCategory> Categories(DiscoveryLocation location, Guid? parent = null, string? query = null)
    {
        var products = Products(location, null); var demands = db.DiscoveryDemands;
        return db.Categories.AsNoTracking().Where(c => c.IsActive && (c.ParentCategoryId == null || db.Categories.Any(p => p.Id == c.ParentCategoryId && p.IsActive))
            && (parent == null || c.ParentCategoryId == parent)
            && (query == null || c.Name.ToLower().Contains(query))
            && (products.Any(p => p.CategoryId == c.Id || db.Categories.Any(child => child.Id == p.CategoryId && child.ParentCategoryId == c.Id))
                || demands.Any(d => location.Supported && d.IsOpen && d.ServiceArea == location.ServiceArea
                    && (d.CategoryId == c.Id || db.Categories.Any(child => child.Id == d.CategoryId && child.ParentCategoryId == c.Id && child.IsActive))
                    && db.Interests.Any(i => i.DemandId == d.Id && i.Active && db.UsersProfile.Any(u => u.Id == i.CustomerId && u.Status == AccountStatus.Active)))))
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Id)
            .Select(c => new DiscoveryCategory { Id = c.Id, ParentCategoryId = c.ParentCategoryId, Name = c.Name, Slug = c.Slug, Description = c.Description, IsDiscoverable = true });
    }

    internal async Task<IReadOnlyList<DealCard>> Cards(IReadOnlyList<DealRow> rows, DiscoveryLocation location, CancellationToken ct)
    {
        var ids = rows.Select(r => r.Id).ToArray();
        var tiers = await db.PriceTiers.AsNoTracking().Where(t => ids.Contains(t.CampaignId)).OrderBy(t => t.Quantity).ToListAsync(ct);
        var delivery = config.GetValue<long?>("Pilot:DeliveryFeeMinor") is >= 0 && config.GetValue("Pilot:DeliveryFeeApproved", false);
        return rows.Select(r =>
        {
            var prices = tiers.Where(t => t.CampaignId == r.Id).ToArray();
            var price = TierPricing.Achieved(prices.Select(t => (t.Quantity, t.UnitPrice)), r.PaidQuantity) ?? prices[0].UnitPrice;
            var next = prices.FirstOrDefault(t => t.Quantity > r.PaidQuantity);
            decimal? reference = r.ReferencePrice > 0 && price <= r.ReferencePrice ? r.ReferencePrice : null;
            return new DealCard(r.Id, r.ProductId, r.CategoryId, r.Title, r.ProductName, r.SellerName, price, reference,
                reference.HasValue ? decimal.Round((reference.Value - price) / reference.Value * 100, 2) : null,
                r.PaidQuantity, prices[0].Quantity, r.Target, next?.Quantity, next?.UnitPrice,
                Math.Min(100, decimal.Round(r.PaidQuantity / prices[0].Quantity * 100, 2)), r.Status, r.Published,
                r.Ends, r.ServiceArea, location.DisplayName!, true, delivery);
        }).ToArray();
    }
}

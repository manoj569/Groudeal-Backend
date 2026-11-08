using GroupBuy.Application;
using GroupBuy.Domain;
using Microsoft.EntityFrameworkCore;
namespace GroupBuy.Infrastructure;

// Shared by discovery and the legacy product-interest action; caller owns the transaction.
internal static class DemandPersistence
{
    internal static Task<int> Advisory(MarketplaceDb db, string key, CancellationToken ct) => db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key},0))", ct);
    internal static async Task<DiscoveryDemand> Lock(MarketplaceDb db, Guid id, CancellationToken ct)
    {
        var demand = await db.DiscoveryDemands.FromSqlInterpolated($"SELECT * FROM discovery_demands WHERE \"Id\"={id} FOR UPDATE").SingleOrDefaultAsync(ct)
            ?? throw new BusinessException(404, "demand_not_found", "Demand not found.");
        await db.Entry(demand).ReloadAsync(ct); return demand;
    }
    internal static async Task<DiscoveryDemand?> Product(MarketplaceDb db, TimeProvider clock, Product product, string area, Guid actor, string correlation, bool create, CancellationToken ct)
    {
        await Advisory(db, $"demand:product:{product.Id}:{area}", ct);
        var demand = await db.DiscoveryDemands.SingleOrDefaultAsync(d => d.ProductId == product.Id && d.ServiceArea == area, ct);
        if (demand is null && create)
        {
            demand = new()
            {
                ProductId = product.Id,
                CategoryId = product.CategoryId,
                Title = DemandInput.CatalogTitle(product.Name),
                NormalizedTitle = "",
                ServiceArea = area,
                DemandType = DemandType.Product,
                CreatedBy = actor,
                LegacyIdentity = false,
                CreatedAt = clock.GetUtcNow(),
                UpdatedAt = clock.GetUtcNow()
            };
            db.Add(demand); Audit(db, actor, correlation, demand.Id, "DemandCreated"); await db.SaveChangesAsync(ct);
        }
        return demand is null ? null : await Lock(db, demand.Id, ct);
    }
    internal static void Audit(MarketplaceDb db, Guid actor, string correlation, Guid id, string action) => db.AuditLogs.Add(new()
    {
        ActorUserId = actor,
        CorrelationId = correlation,
        EntityType = nameof(DiscoveryDemand),
        EntityId = id,
        Action = action
    });
}

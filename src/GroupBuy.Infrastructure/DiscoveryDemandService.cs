using GroupBuy.Application;
using GroupBuy.Domain;
using Microsoft.EntityFrameworkCore;

namespace GroupBuy.Infrastructure;

public sealed partial class DiscoveryService
{
    private async Task<CreateDemandRequest> ValidateDemand(CreateDemandRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ServiceArea)) throw new BusinessException(400, "invalid_service_area", "Choose a supported service area.");
        var result = DemandInput.Normalize(request); var location = areas.Resolve(new(ServiceArea: request.ServiceArea));
        if (!location.Supported) throw new BusinessException(400, "service_area_unsupported", "Choose a supported service area.");
        if (result.CategoryId is Guid category && !await ActiveCategory(category, ct)) throw new BusinessException(400, "invalid_category", "Choose an active category.");
        if (result.ProductId is Guid productId)
        {
            var product = await PublicCatalog.Products(db).AsNoTracking().Where(p => db.Sellers.Any(s => s.Id == p.SellerId && db.UsersProfile.Any(u => u.Id == s.OwnerUserId && u.Status == AccountStatus.Active))).SingleOrDefaultAsync(p => p.Id == productId, ct)
                ?? throw new BusinessException(400, "invalid_product", "Choose an active catalog product.");
            if (product.CategoryId != result.CategoryId) throw new BusinessException(400, "product_category_mismatch", "The selected product belongs to a different category.");
            var allowed = result.Unit switch { "kg" => UnitOfMeasure.Kilogram, "l" => UnitOfMeasure.Litre, "piece" => UnitOfMeasure.Piece, "pack" => UnitOfMeasure.Pack, _ => (UnitOfMeasure?)null };
            if (!await db.Variants.AnyAsync(v => v.ProductId == productId && v.IsActive && v.UnitOfMeasure == allowed, ct)) throw new BusinessException(400, "validation_failed", "Choose a unit supported by the catalog product.");
            result = result with { Title = DemandInput.CatalogTitle(product.Name) };
        }
        return result with { ServiceArea = location.ServiceArea! };
    }
    private Task<bool> ActiveCategory(Guid category, CancellationToken ct) => db.Categories.AnyAsync(c => c.Id == category && c.IsActive && (c.ParentCategoryId == null || db.Categories.Any(p => p.Id == c.ParentCategoryId && p.IsActive)), ct);
    private async Task Eligible(DiscoveryDemand demand, CancellationToken ct)
    {
        if (!demand.IsOpen) throw new BusinessException(409, "demand_closed", "Demand is closed.");
        if (!areas.Resolve(new(ServiceArea: demand.ServiceArea)).Supported) throw new BusinessException(400, "service_area_unsupported", "Demand is outside supported service areas.");
        if (demand.CategoryId is Guid category && !await ActiveCategory(category, ct)) throw new BusinessException(409, "demand_unavailable", "Demand category is unavailable.");
        if (demand.ProductId is Guid product && !await PublicCatalog.Products(db).AnyAsync(p => p.Id == product && p.CategoryId == demand.CategoryId && db.Sellers.Any(s => s.Id == p.SellerId && db.UsersProfile.Any(u => u.Id == s.OwnerUserId && u.Status == AccountStatus.Active)), ct)) throw new BusinessException(409, "demand_unavailable", "Demand product is unavailable.");
    }
    public async Task<DemandMembership> CreateDemand(Actor actor, CreateDemandRequest request, CancellationToken ct)
    {
        var legacy = !DemandInput.Rich(request); var r = await ValidateDemand(request, ct); var normalized = r.ProductId.HasValue ? "" : r.Title.ToLowerInvariant();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        DiscoveryDemand? demand;
        if (r.ProductId is Guid productId)
        {
            var product = await db.Products.SingleAsync(p => p.Id == productId, ct);
            demand = await DemandPersistence.Product(db, clock, product, r.ServiceArea, actor.UserId, actor.CorrelationId, true, ct);
        }
        else
        {
            if (legacy)
            {
                await DemandPersistence.Advisory(db, "demand:legacy:" + r.ServiceArea + ":" + normalized, ct);
                var old = await db.DiscoveryDemands.AsNoTracking().SingleOrDefaultAsync(d => d.LegacyIdentity && d.NormalizedTitle == normalized && d.ServiceArea == r.ServiceArea, ct);
                if (old is not null && old.CategoryId != r.CategoryId) throw new BusinessException(409, "demand_conflict", "An existing demand uses a different category; join it explicitly.");
            }
            await DemandPersistence.Advisory(db, "demand:" + DemandInput.Canonical(r), ct);
            demand = await Match(r).SingleOrDefaultAsync(ct);
            if (demand is null)
            {
                demand = new()
                {
                    Title = r.Title,
                    NormalizedTitle = normalized,
                    ServiceArea = r.ServiceArea,
                    CategoryId = r.CategoryId,
                    DemandType = r.DemandType!.Value,
                    CreatedBy = actor.UserId,
                    LegacyIdentity = legacy,
                    CreatedAt = clock.GetUtcNow(),
                    UpdatedAt = clock.GetUtcNow()
                };
                db.DiscoveryDemands.Add(demand);
                DemandPersistence.Audit(db, actor.UserId, actor.CorrelationId, demand.Id, legacy ? "DiscoveryDemandCreated" : "DemandCreated");
                await db.SaveChangesAsync(ct);
            }
            else DemandPersistence.Audit(db, actor.UserId, actor.CorrelationId, demand.Id, "SimilarDemandMatched");
            demand = await DemandPersistence.Lock(db, demand.Id, ct);
        }
        await Eligible(demand!, ct);
        await SetMembership(actor, demand!, false, legacy ? null : r, ct);
        await db.SaveChangesAsync(ct); var reply = await Reply(actor, demand!, true, ct); await tx.CommitAsync(ct); return reply;
    }
    private IQueryable<DiscoveryDemand> Match(CreateDemandRequest r)
        => db.DiscoveryDemands.Where(d => d.ServiceArea == r.ServiceArea && d.DemandType == r.DemandType && d.CategoryId == r.CategoryId && d.ProductId == r.ProductId && d.NormalizedTitle == (r.ProductId.HasValue ? "" : r.Title.ToLowerInvariant()));
    private async Task SetMembership(Actor actor, DiscoveryDemand demand, bool withdraw, CreateDemandRequest? request, CancellationToken ct)
    {
        if (!withdraw && !demand.IsOpen) throw new BusinessException(409, "demand_closed", "Demand is closed.");
        if (demand.ProductId is Guid product) await DemandPersistence.Advisory(db, $"interest:{actor.UserId}:{product}:{demand.ServiceArea}", ct);
        var member = await db.Interests.SingleOrDefaultAsync(i => i.CustomerId == actor.UserId && (i.DemandId == demand.Id || (demand.ProductId != null && i.ProductId == demand.ProductId && i.ServiceArea == demand.ServiceArea)), ct);
        if (member is null)
        {
            if (withdraw) return;
            member = new() { CustomerId = actor.UserId, ProductId = demand.ProductId, DemandId = demand.Id, ServiceArea = demand.ServiceArea, DesiredQuantity = request?.Quantity ?? 1, CreatedAt = clock.GetUtcNow() }; db.Interests.Add(member);
        }
        var changed = member.Active != !withdraw || db.Entry(member).State == EntityState.Added || member.DemandId != demand.Id;
        if (request is not null)
        {
            changed |= member.RequestedQuantity != request.Quantity || member.RequestedUnit != request.Unit || member.RequirementText != request.Requirement || member.RequirementDetails != request.Details;
            member.RequestedQuantity = request.Quantity; member.RequestedUnit = request.Unit; member.RequirementText = request.Requirement; member.RequirementDetails = request.Details;
            if (request.Quantity.HasValue) member.DesiredQuantity = request.Quantity.Value;
        }
        if (!changed) return;
        member.DemandId = demand.Id; member.Active = !withdraw; member.UpdatedAt = clock.GetUtcNow(); demand.UpdatedAt = clock.GetUtcNow();
        DemandPersistence.Audit(db, actor.UserId, actor.CorrelationId, demand.Id, withdraw ? "DiscoveryDemandWithdrawn" : "DiscoveryDemandJoined"); await db.SaveChangesAsync(ct);
    }
    public async Task<DemandMembership> JoinDemand(Actor actor, Guid id, bool withdraw, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct); var demand = await DemandPersistence.Lock(db, id, ct);
        if (!withdraw) await Eligible(demand, ct);
        await SetMembership(actor, demand, withdraw, null, ct); var reply = await Reply(actor, demand, !withdraw, ct); await tx.CommitAsync(ct); return reply;
    }
    public async Task CloseDemand(Actor actor, Guid id, CancellationToken ct) => await ModerateDemand(actor, id, new(DemandStatus.Closed, "Closed by administrator."), ct);
    public async Task<DemandMembership> Membership(Actor actor, Guid id, CancellationToken ct)
    {
        var demand = await db.DiscoveryDemands.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct) ?? throw new BusinessException(404, "demand_not_found", "Demand not found.");
        return new(id, await Members(demand).AnyAsync(i => i.CustomerId == actor.UserId && i.Active, ct));
    }
    private async Task<DemandMembership> Reply(Actor actor, DiscoveryDemand demand, bool joined, CancellationToken ct)
    {
        try { return new(demand.Id, joined, (await Details([demand], actor, ct)).Single()); }
        catch (BusinessException e) when (e.StatusCode == 503) { return new(demand.Id, joined); }
    }
    private IQueryable<CustomerInterest> Members(DiscoveryDemand demand) => db.Interests.AsNoTracking().Where(i => i.DemandId == demand.Id || (demand.ProductId != null && i.ProductId == demand.ProductId && i.ServiceArea == demand.ServiceArea));
}

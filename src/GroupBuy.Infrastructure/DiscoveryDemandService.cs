using GroupBuy.Application;
using GroupBuy.Domain;
using Microsoft.EntityFrameworkCore;

namespace GroupBuy.Infrastructure;

public sealed partial class DiscoveryService
{
    public async Task<DemandMembership> CreateDemand(Actor actor, CreateDemandRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ServiceArea)) throw new BusinessException(400, "invalid_service_area", "Choose a supported service area.");
        var title = DiscoveryInput.PublicTitle(request.Title); var normalized = title.ToLowerInvariant();
        var location = areas.Resolve(new(ServiceArea: request.ServiceArea));
        if (!location.Supported) throw new BusinessException(400, "unsupported_area", "Choose a supported service area.");
        if (request.CategoryId is Guid category && !await db.Categories.AnyAsync(c => c.Id == category && c.IsActive
            && (c.ParentCategoryId == null || db.Categories.Any(p => p.Id == c.ParentCategoryId && p.IsActive)), ct))
            throw new BusinessException(400, "invalid_category", "Choose an active category.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Serialize canonical creation; the unique index remains the database authority.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"demand:" + location.ServiceArea + ":" + normalized},0))", ct);
        var demand = await db.DiscoveryDemands.SingleOrDefaultAsync(d => d.NormalizedTitle == normalized && d.ServiceArea == location.ServiceArea, ct);
        if (demand is null)
        {
            demand = new() { Title = title, NormalizedTitle = normalized, ServiceArea = location.ServiceArea!, CategoryId = request.CategoryId,
                CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow() };
            db.DiscoveryDemands.Add(demand); await db.SaveChangesAsync(ct);
            db.AuditLogs.Add(new() { ActorUserId = actor.UserId, Action = "DiscoveryDemandCreated", EntityType = nameof(DiscoveryDemand), EntityId = demand.Id, CorrelationId = actor.CorrelationId });
        }
        await LockDemand(demand.Id, ct);
        if (demand.CategoryId != request.CategoryId) throw new BusinessException(409, "demand_conflict", "An existing demand uses a different category; join it explicitly.");
        await SetMembership(actor, demand, false, ct); await tx.CommitAsync(ct);
        return new(demand.Id, true);
    }

    private async Task<DiscoveryDemand> LockDemand(Guid id, CancellationToken ct)
    {
        var demand = await db.DiscoveryDemands.FromSqlInterpolated($"SELECT * FROM discovery_demands WHERE \"Id\"={id} FOR UPDATE").SingleOrDefaultAsync(ct)
            ?? throw new BusinessException(404, "not_found", "Demand not found.");
        await db.Entry(demand).ReloadAsync(ct); return demand;
    }

    private async Task SetMembership(Actor actor, DiscoveryDemand demand, bool withdraw, CancellationToken ct)
    {
        if (!withdraw && !demand.IsOpen) throw new BusinessException(409, "demand_closed", "Demand is closed.");
        var member = await db.Interests.SingleOrDefaultAsync(i => i.CustomerId == actor.UserId && i.DemandId == demand.Id, ct);
        if (member is null)
        {
            if (withdraw) return;
            member = new() { CustomerId = actor.UserId, DemandId = demand.Id, ServiceArea = demand.ServiceArea, DesiredQuantity = 1, CreatedAt = clock.GetUtcNow() };
            db.Interests.Add(member);
        }
        if (member.Active == !withdraw && db.Entry(member).State != EntityState.Added) return;
        member.Active = !withdraw; member.UpdatedAt = clock.GetUtcNow();
        demand.UpdatedAt = clock.GetUtcNow();
        db.AuditLogs.Add(new() { ActorUserId = actor.UserId, Action = withdraw ? "DiscoveryDemandWithdrawn" : "DiscoveryDemandJoined",
            EntityType = nameof(DiscoveryDemand), EntityId = demand.Id, CorrelationId = actor.CorrelationId });
        await db.SaveChangesAsync(ct);
    }

    public async Task<DemandMembership> JoinDemand(Actor actor, Guid id, bool withdraw, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct); var demand = await LockDemand(id, ct);
        if (!withdraw && !areas.Resolve(new(ServiceArea: demand.ServiceArea)).Supported)
            throw new BusinessException(400, "unsupported_area", "Demand is outside supported service areas.");
        if (!withdraw && demand.CategoryId is Guid category && !await db.Categories.AnyAsync(c => c.Id == category && c.IsActive
            && (c.ParentCategoryId == null || db.Categories.Any(p => p.Id == c.ParentCategoryId && p.IsActive)), ct))
            throw new BusinessException(409, "demand_unavailable", "Demand category is unavailable.");
        await SetMembership(actor, demand, withdraw, ct); await tx.CommitAsync(ct); return new(id, !withdraw);
    }

    public async Task CloseDemand(Actor actor, Guid id, CancellationToken ct)
    {
        if (!actor.IsAdmin) throw new BusinessException(403, "forbidden", "Administrator access required.");
        await using var tx = await db.Database.BeginTransactionAsync(ct); var demand = await LockDemand(id, ct);
        demand.IsOpen = false; demand.UpdatedAt = clock.GetUtcNow();
        db.AuditLogs.Add(new() { ActorUserId = actor.UserId, Action = "DiscoveryDemandClosed", EntityType = nameof(DiscoveryDemand), EntityId = id, CorrelationId = actor.CorrelationId });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }

    public async Task<DemandMembership> Membership(Actor actor, Guid id, CancellationToken ct)
    {
        if (!await db.DiscoveryDemands.AsNoTracking().AnyAsync(d => d.Id == id, ct))
            throw new BusinessException(404, "not_found", "Demand not found.");
        return new(id, await db.Interests.AsNoTracking().AnyAsync(i => i.DemandId == id && i.CustomerId == actor.UserId && i.Active, ct));
    }
}

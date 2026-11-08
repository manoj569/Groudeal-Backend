using GroupBuy.Application;
using GroupBuy.Domain;
using Microsoft.EntityFrameworkCore;
namespace GroupBuy.Infrastructure;

public sealed partial class DiscoveryService
{
    public async Task<DemandPreview> PreviewDemand(Actor actor, CreateDemandRequest request, CancellationToken ct)
    {
        var normalized = await ValidateDemand(request, ct);
        var demand = await Match(normalized).AsNoTracking().SingleOrDefaultAsync(ct);
        var active = demand?.IsOpen == true;
        var detail = !active ? null : (await Details([demand!], actor, ct)).Single();
        return new(normalized, active, detail, demand?.IsOpen != false, demand?.IsOpen == false ? "demand_closed" : null);
    }
    public async Task<DemandDetail> DemandDetail(Guid id, Actor? actor, CancellationToken ct)
    {
        _ = areas.Resolve(new());
        var demand = await db.DiscoveryDemands.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct)
            ?? throw new BusinessException(404, "demand_not_found", "Demand not found.");
        return (await Details([demand], actor, ct)).Single();
    }
    private IQueryable<DiscoveryDemand> FilterDemands(DemandListFilter filter)
    {
        DemandInput.ValidateFilter(filter); _ = areas.Resolve(new());
        var q = db.DiscoveryDemands.AsNoTracking();
        if (filter.Type.HasValue) q = q.Where(d => d.DemandType == filter.Type);
        if (filter.CategoryId.HasValue) q = q.Where(d => d.CategoryId == filter.CategoryId);
        if (filter.ServiceArea is not null)
        {
            if (!areas.Resolve(new(ServiceArea: filter.ServiceArea)).Supported) throw new BusinessException(400, "service_area_unsupported", "Choose a supported service area.");
            q = q.Where(d => d.ServiceArea == filter.ServiceArea);
        }
        return filter.Status switch
        {
            DemandStatus.BuildingInterest => q.Where(d => d.IsOpen),
            DemandStatus.Closed => q.Where(d => !d.IsOpen && !d.IsCancelled),
            DemandStatus.Cancelled => q.Where(d => d.IsCancelled),
            _ => q
        };
    }
    public async Task<PageResponse<MyDemand>> MyDemands(Actor actor, DemandListFilter filter, PageRequest page, CancellationToken ct)
    {
        Page(page);
        var q = FilterDemands(filter).Where(d => d.CreatedBy == actor.UserId || db.Interests.Any(i => i.CustomerId == actor.UserId && (i.DemandId == d.Id || (d.ProductId != null && i.ProductId == d.ProductId && i.ServiceArea == d.ServiceArea))));
        var total = await q.CountAsync(ct); var rows = await q.OrderByDescending(d => d.CreatedAt).ThenBy(d => d.Id).Skip(page.Offset).Take(page.PageSize).ToListAsync(ct);
        var details = await Details(rows, actor, ct);
        return new(details.Select((d, i) => new MyDemand(d, rows[i].CreatedBy == actor.UserId, d.Membership ?? new(false, null, null, null, null, null, null, false))).ToArray(), page.Page, page.PageSize, total);
    }
    public async Task<PageResponse<DemandDetail>> AdminDemands(Actor actor, DemandListFilter filter, PageRequest page, CancellationToken ct)
    {
        Admin(actor); Page(page); var q = FilterDemands(filter); var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(d => d.CreatedAt).ThenBy(d => d.Id).Skip(page.Offset).Take(page.PageSize).ToListAsync(ct);
        return new(await Details(rows, null, ct), page.Page, page.PageSize, total);
    }
    private async Task<IReadOnlyList<DemandDetail>> Details(IReadOnlyList<DiscoveryDemand> rows, Actor? actor, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        var ids = rows.Select(d => d.Id).ToArray(); var categoryIds = rows.Where(d => d.CategoryId.HasValue).Select(d => d.CategoryId!.Value).Distinct().ToArray();
        var productIds = rows.Where(d => d.ProductId.HasValue).Select(d => d.ProductId!.Value).Distinct().ToArray();
        var categoryNames = await db.Categories.AsNoTracking().Where(c => categoryIds.Contains(c.Id)).Select(c => new { c.Id, c.Name }).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var productNames = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id)).Select(p => new { p.Id, p.Name }).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var counts = await db.DiscoveryDemands.AsNoTracking().Where(d => ids.Contains(d.Id)).Select(d => new { d.Id, Count = db.Interests.Where(i => i.Active && ((d.ProductId == null && i.DemandId == d.Id) || (d.ProductId != null && i.ProductId == d.ProductId && i.ServiceArea == d.ServiceArea)) && db.UsersProfile.Any(u => u.Id == i.CustomerId && u.Status == AccountStatus.Active)).Select(i => i.CustomerId).Distinct().Count() }).ToDictionaryAsync(d => d.Id, d => d.Count, ct);
        var members = actor is null ? [] : await db.Interests.AsNoTracking().Where(i => i.CustomerId == actor.UserId && db.DiscoveryDemands.Any(d => ids.Contains(d.Id) && (i.DemandId == d.Id || (d.ProductId != null && i.ProductId == d.ProductId && i.ServiceArea == d.ServiceArea)))).ToListAsync(ct);
        return rows.Select(d =>
        {
            var mine = members.SingleOrDefault(i => i.DemandId == d.Id || (d.ProductId != null && i.ProductId == d.ProductId && i.ServiceArea == d.ServiceArea));
            var membership = actor is null ? null : new DemandParticipation(mine?.Active ?? false, mine?.RequestedQuantity, mine?.RequestedUnit, mine?.RequirementText, mine?.RequirementDetails, mine?.CreatedAt, mine?.UpdatedAt, mine?.Active ?? false);
            var location = areas.Resolve(new(ServiceArea: d.ServiceArea));
            return new DemandDetail(d.Id, d.DemandType, d.Title, d.CategoryId.HasValue ? new(d.CategoryId.Value, categoryNames.GetValueOrDefault(d.CategoryId.Value, "")) : null,
                d.ProductId.HasValue ? new(d.ProductId.Value, productNames.GetValueOrDefault(d.ProductId.Value, "")) : null,
                d.ServiceArea, location.DisplayName, d.Status, new(counts[d.Id]), d.CreatedAt, d.UpdatedAt, membership);
        }).ToArray();
    }
    private static void Admin(Actor actor) { if (!actor.IsAdmin) throw new BusinessException(403, "forbidden", "Administrator access required."); }
    public async Task<AdminDemandDetail> AdminDemand(Actor actor, Guid id, CancellationToken ct)
    {
        Admin(actor); var detail = await DemandDetail(id, null, ct);
        var row = await db.DiscoveryDemands.AsNoTracking().SingleAsync(d => d.Id == id, ct);
        var audit = await db.AuditLogs.AsNoTracking().Where(a => a.EntityId == id && a.EntityType == nameof(DiscoveryDemand)).OrderByDescending(a => a.TimestampUtc).ThenBy(a => a.Id).Take(50).Select(a => new DemandAudit(a.Action, a.TimestampUtc)).ToArrayAsync(ct);
        return new(detail, row.ClosureReason, row.ClosedAtUtc, audit);
    }
    public async Task<PageResponse<DemandAudit>> DemandAuditHistory(Actor actor, Guid id, PageRequest page, CancellationToken ct)
    {
        Admin(actor); Page(page);
        if (!await db.DiscoveryDemands.AsNoTracking().AnyAsync(d => d.Id == id, ct)) throw new BusinessException(404, "demand_not_found", "Demand not found.");
        var q = db.AuditLogs.AsNoTracking().Where(a => a.EntityId == id && a.EntityType == nameof(DiscoveryDemand));
        var total = await q.CountAsync(ct); var items = await q.OrderByDescending(a => a.TimestampUtc).ThenBy(a => a.Id).Skip(page.Offset).Take(page.PageSize).Select(a => new DemandAudit(a.Action, a.TimestampUtc)).ToArrayAsync(ct);
        return new(items, page.Page, page.PageSize, total);
    }
    public async Task ModerateDemand(Actor actor, Guid id, ModerateDemandRequest request, CancellationToken ct)
    {
        Admin(actor);
        if (request.Status is not (DemandStatus.Closed or DemandStatus.Cancelled)) throw new BusinessException(409, "invalid_transition", "Demand may only be closed or cancelled.");
        var reason = DemandInput.Text(request.Reason, 500);
        if (reason is null || reason.Length < 10) throw new BusinessException(400, "validation_failed", "Provide a moderation reason between 10 and 500 characters.");
        await using var tx = await db.Database.BeginTransactionAsync(ct); var demand = await DemandPersistence.Lock(db, id, ct);
        if (demand.Status == request.Status) { await tx.CommitAsync(ct); return; }
        if (!demand.IsOpen) throw new BusinessException(409, "invalid_transition", "A terminal demand cannot change state.");
        demand.IsOpen = false; demand.IsCancelled = request.Status == DemandStatus.Cancelled; demand.ClosureReason = reason; demand.ClosedAtUtc = clock.GetUtcNow(); demand.UpdatedAt = clock.GetUtcNow();
        DemandPersistence.Audit(db, actor.UserId, actor.CorrelationId, id, demand.IsCancelled ? "DemandCancelled" : "DiscoveryDemandClosed"); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
}

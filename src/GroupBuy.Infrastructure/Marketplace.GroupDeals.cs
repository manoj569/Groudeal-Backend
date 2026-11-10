using GroupBuy.Application;
using GroupBuy.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
namespace GroupBuy.Infrastructure;

public sealed partial class Marketplace : IGroupDeals
{
    // Match Module 3's active-account and legacy product-interest semantics.
    private IQueryable<CustomerInterest> OriginInterests(Guid demandId) => db.Interests.Where(i => i.Active
        && db.UsersProfile.Any(u => u.Id == i.CustomerId && u.Status == AccountStatus.Active)
        && db.DiscoveryDemands.Any(d => d.Id == demandId &&
            ((d.ProductId == null && i.DemandId == d.Id) || (d.ProductId != null && i.ProductId == d.ProductId && i.ServiceArea == d.ServiceArea))));

    public async Task<GroupDealOffer> Offer(Actor actor, Guid id, bool adminView, CancellationToken ct)
    {
        if (adminView) Admin(actor);
        var c = await db.Campaigns.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id &&
            (adminView || db.Sellers.Any(s => s.Id == c.SellerId && s.OwnerUserId == actor.UserId)), ct) ?? throw Missing();
        var type = await (from v in db.Variants join p in db.Products on v.ProductId equals p.Id where v.Id == c.ProductVariantId select p.ProductType).SingleAsync(ct);
        var tiers = await db.PriceTiers.AsNoTracking().Where(t => t.CampaignId == id).OrderBy(t => t.Quantity).Select(t => new PriceTierRequest(t.Quantity, t.UnitPrice)).ToListAsync(ct);
        return new(CampaignDto(c), (GroupDealType)type, c.OriginatingDemandId, c.OfferTerms, tiers);
    }

    public async Task<CampaignResponse> CancelDeal(Actor actor, Guid id, ReviewRequest request, CancellationToken ct)
    {
        Admin(actor); Require(request.Reason is { Length: >= 5 and <= 500 } && !string.IsNullOrWhiteSpace(request.Reason), "A cancellation reason is required.", 400);
        await using var tx = await db.Database.BeginTransactionAsync(ct); var (_, c) = await LockFinancialCampaign(id, ct);
        if (c.Status == CampaignStatus.Cancelled) { await tx.CommitAsync(ct); return CampaignDto(c); }
        Require(c.FinalizedAtUtc is null && c.Status is CampaignStatus.Draft or CampaignStatus.PendingApproval or CampaignStatus.Scheduled or CampaignStatus.Open or CampaignStatus.MinimumReached, "Only pre-finalization deals can be cancelled.");
        var orders = await db.PaymentOrders.Where(o => o.CampaignId == id && o.FinalAmountMinor == null).ToListAsync(ct);
        foreach (var order in orders)
        {
            order.FinalAmountMinor = 0; order.ExpiresAtUtc = clock.GetUtcNow();
            if (order.CapturedAtUtc is not null) QueueRefund(order, "admin_cancellation", order.MaximumAmountMinor - order.RefundedMinor);
            else order.State = PaymentState.Expired;
        }
        foreach (var reservation in await db.Reservations.Where(r => r.CampaignId == id && r.Status == ReservationStatus.Active).ToListAsync(ct))
        { reservation.Status = ReservationStatus.Cancelled; reservation.CancelledAt = clock.GetUtcNow(); }
        c.ReservedQuantity = 0; c.CommittedQuantity = 0; c.TransitionTo(CampaignStatus.Cancelled);
        Audit(actor, "GroupDealCancelled", c, request.Reason); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return CampaignDto(c);
    }

    private async Task NotifyDemandOffer(GroupBuyCampaign c, CancellationToken ct)
    {
        if (c.OriginatingDemandId is null || c.DemandOfferNotifiedAtUtc is not null || c.Status != CampaignStatus.Open) return;
        var recipients = await OriginInterests(c.OriginatingDemandId.Value).Select(i => i.CustomerId).Distinct().ToListAsync(ct);
        foreach (var recipient in recipients) Notify(recipient, NotificationType.DemandOfferAvailable, c.Id);
        c.DemandOfferNotifiedAtUtc = clock.GetUtcNow();
        Audit(null, "DemandOfferPublished", c);
    }

    private string SupportedDealArea(string? serviceArea)
    {
        var location = scopes.CreateScope();
        using (location)
        {
            var result = location.ServiceProvider.GetRequiredService<DiscoveryAreas>().Resolve(new(ServiceArea: serviceArea));
            if (!result.Supported) throw new BusinessException(400, "service_area_unsupported", "Choose a supported service area.");
            return result.ServiceArea!;
        }
    }

    public async Task<CampaignResponse> LinkDemandOffer(Actor actor, Guid id, LinkDemandOfferRequest request, CancellationToken ct)
    {
        Require(request.Terms is { Length: >= 10 and <= 4000 } && !string.IsNullOrWhiteSpace(request.Terms), "Provide customer-readable offer terms.", 400);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var own = await OwnedSeller(actor, ct); var seller = await LockSeller(own.Id, ct);
        Require(Eligible(seller) && await db.UsersProfile.AnyAsync(u => u.Id == seller.OwnerUserId && u.Status == AccountStatus.Active, ct), "Approved active seller required.", 403);
        var c = await LockCampaign(id, ct); Require(c.SellerId == seller.Id, "Resource not found.", 404);
        Require(c.Status == CampaignStatus.Draft && c.UsesTierPricing && c.NearTargetQuantity is null, "Only a tier-priced draft can become a demand offer.");
        _ = SupportedDealArea(c.ServiceArea);
        var demand = await DemandPersistence.Lock(db, request.DemandId, ct);
        var category = await (from v in db.Variants join p in db.Products on v.ProductId equals p.Id where v.Id == c.ProductVariantId select new { p.CategoryId, p.ProductType }).SingleAsync(ct);
        GroupDealRules.ValidateLink(demand, category.CategoryId, c.ServiceArea, category.ProductType);
        Require(c.OriginatingDemandId is null || c.OriginatingDemandId == request.DemandId, "The originating demand cannot be replaced.");
        if (c.OriginatingDemandId != request.DemandId || c.OfferTerms != request.Terms)
        {
            c.OriginatingDemandId = request.DemandId; c.OfferTerms = request.Terms; c.UpdatedAt = clock.GetUtcNow();
            Audit(actor, "DemandOfferLinked", c); await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct); return CampaignDto(c);
    }

    public async Task<PageResponse<DemandOpportunity>> Opportunities(Actor actor, PageRequest page, string serviceArea, CancellationToken ct)
    {
        Page(page); var seller = await OwnedSeller(actor, ct);
        Require(Eligible(seller) && await db.UsersProfile.AnyAsync(u => u.Id == actor.UserId && u.Status == AccountStatus.Active, ct), "Approved active seller required.", 403);
        var area = SupportedDealArea(serviceArea);
        var q = db.DiscoveryDemands.AsNoTracking().Where(d => d.IsOpen && !d.IsCancelled && d.ServiceArea == area);
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(d => d.CreatedAt).ThenBy(d => d.Id).Skip(page.Offset).Take(page.PageSize)
            .Select(d => new DemandOpportunity(d.Id, d.Title, d.DemandType, d.CategoryId, d.ServiceArea,
                db.Interests.Where(i => i.Active && db.UsersProfile.Any(u => u.Id == i.CustomerId && u.Status == AccountStatus.Active) && ((d.ProductId == null && i.DemandId == d.Id) || (d.ProductId != null && i.ProductId == d.ProductId && i.ServiceArea == d.ServiceArea))).Select(i => i.CustomerId).Distinct().Count())).ToListAsync(ct);
        return new(items, page.Page, page.PageSize, total);
    }

    public async Task<PageResponse<GroupDealSummary>> Deals(GroupDealFilter filter, Actor? actor, CancellationToken ct)
    {
        var page = new PageRequest(filter.Page, filter.PageSize); Page(page);
        await using var snapshot = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        Require(filter.Type is null || Enum.IsDefined(filter.Type.Value), "Unknown deal type.", 400);
        Require(filter.Status is null || Enum.IsDefined(filter.Status.Value), "Unknown status.", 400);
        Require(filter.Search is null || filter.Search.Length <= 100, "Search is too long.", 400);
        Require(filter.Sort is "newest" or "ending-soon", "Use newest or ending-soon sorting.", 400);
        var area = SupportedDealArea(filter.ServiceArea);
        var q = PublicCampaigns.AsNoTracking().Where(c => c.UsesTierPricing && c.ServiceArea == area);
        if (filter.Type is not null) q = q.Where(c => db.Variants.Any(v => v.Id == c.ProductVariantId && db.Products.Any(p => p.Id == v.ProductId && (int)p.ProductType == (int)filter.Type.Value)));
        if (filter.DemandId is not null) q = q.Where(c => c.OriginatingDemandId == filter.DemandId);
        if (filter.Status is not null) q = q.Where(c => c.Status == filter.Status);
        if (filter.CategoryId is not null) q = q.Where(c => db.Variants.Any(v => v.Id == c.ProductVariantId && db.Products.Any(p => p.Id == v.ProductId && p.CategoryId == filter.CategoryId)));
        if (!string.IsNullOrWhiteSpace(filter.Search)) q = q.Where(c => c.Title.Contains(filter.Search));
        var total = await q.CountAsync(ct);
        q = filter.Sort == "ending-soon" ? q.OrderBy(c => c.ExpiresAtUtc).ThenBy(c => c.Id) : q.OrderByDescending(c => c.CreatedAt).ThenBy(c => c.Id);
        var rows = await (from c in q.Skip(page.Offset).Take(page.PageSize)
                          join v in db.Variants on c.ProductVariantId equals v.Id
                          join p in db.Products on v.ProductId equals p.Id
                          join seller in db.Sellers on c.SellerId equals seller.Id
                          select new { Campaign = c, p.ProductType, p.CategoryId, seller.BusinessName }).ToListAsync(ct);
        var ids = rows.Select(r => r.Campaign.Id).ToArray();
        var tiers = await db.PriceTiers.AsNoTracking().Where(t => ids.Contains(t.CampaignId)).ToListAsync(ct);
        var members = await db.Reservations.Where(r => ids.Contains(r.CampaignId) && (r.Status == ReservationStatus.Active || r.Status == ReservationStatus.Completed))
            .GroupBy(r => r.CampaignId).Select(g => new { Id = g.Key, Count = g.Select(r => r.CustomerUserId).Distinct().Count() }).ToDictionaryAsync(g => g.Id, g => g.Count, ct);
        var paid = await db.PaymentOrders.Where(o => ids.Contains(o.CampaignId) && o.CapturedAtUtc != null && o.ReservationId != null && db.Reservations.Any(r => r.Id == o.ReservationId && (r.Status == ReservationStatus.Active || r.Status == ReservationStatus.Completed)))
            .GroupBy(o => o.CampaignId).Select(g => new { Id = g.Key, Count = g.Select(o => o.CustomerId).Distinct().Count() }).ToDictionaryAsync(g => g.Id, g => g.Count, ct);
        rows = filter.Sort == "ending-soon" ? rows.OrderBy(r => r.Campaign.ExpiresAtUtc).ThenBy(r => r.Campaign.Id).ToList()
            : rows.OrderByDescending(r => r.Campaign.CreatedAt).ThenBy(r => r.Campaign.Id).ToList();
        var result = rows.Select(r => new GroupDealSummary(r.Campaign.Id, (GroupDealType)r.ProductType, r.Campaign.Title,
            r.CategoryId, new(r.BusinessName, true), r.Campaign.Status, r.Campaign.StartsAtUtc, r.Campaign.ExpiresAtUtc,
            r.Campaign.ServiceArea, GroupDealRules.Progress(tiers.Where(t => t.CampaignId == r.Campaign.Id).Select(t => new GroupDealTier(t.Quantity, t.UnitPrice)), r.Campaign.FinalQuantity ?? r.Campaign.ReservedQuantity),
            members.GetValueOrDefault(r.Campaign.Id), paid.GetValueOrDefault(r.Campaign.Id), r.Campaign.OriginatingDemandId)).ToArray();
        await snapshot.CommitAsync(ct);
        return new(result, page.Page, page.PageSize, total);
    }

    public async Task<GroupDealDetail> Deal(Guid id, Actor? actor, CancellationToken ct)
    {
        // One MVCC snapshot keeps counts, holds, and tier progress consistent during checkout races.
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var c = await PublicCampaigns.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id && c.UsesTierPricing, ct) ?? throw Missing();
        _ = SupportedDealArea(c.ServiceArea);
        var catalog = await (from v in db.Variants join p in db.Products on v.ProductId equals p.Id where v.Id == c.ProductVariantId select new { Product = p, Variant = v }).SingleAsync(ct);
        var seller = await db.Sellers.AsNoTracking().SingleAsync(s => s.Id == c.SellerId, ct);
        var tiers = await db.PriceTiers.AsNoTracking().Where(t => t.CampaignId == id).OrderBy(t => t.Quantity).Select(t => new GroupDealTier(t.Quantity, t.UnitPrice)).ToListAsync(ct);
        var reservations = db.Reservations.Where(r => r.CampaignId == id && (r.Status == ReservationStatus.Active || r.Status == ReservationStatus.Completed));
        var paid = db.PaymentOrders.Where(o => o.CampaignId == id && o.CapturedAtUtc != null && o.ReservationId != null && reservations.Any(r => r.Id == o.ReservationId));
        var now = clock.GetUtcNow();
        var holds = await db.PaymentOrders.Where(o => o.CampaignId == id && (o.State == PaymentState.Creating || o.State == PaymentState.Pending || o.State == PaymentState.Authorized) && o.ExpiresAtUtc > now).SumAsync(o => (decimal?)o.Quantity, ct) ?? 0;
        var available = Math.Max(0, c.TargetQuantity - c.ReservedQuantity - holds);
        var interested = c.OriginatingDemandId is null ? 0 : await OriginInterests(c.OriginatingDemandId.Value).Select(i => i.CustomerId).Distinct().CountAsync(ct);
        var fulfillment = new List<OrderFulfillmentMode>();
        var pickupLocation = await db.Locations.AsNoTracking().Where(l => l.Id == c.SellerLocationId && l.IsActive && l.IsPickupEnabled)
            .Select(l => new PickupResponse(l.Id, l.AddressLine1, l.AddressLine2, l.Area, l.City, l.State, l.PostalCode)).SingleOrDefaultAsync(ct);
        if (pickupLocation is not null) fulfillment.Add(OrderFulfillmentMode.SellerPickup);
        if (fulfillment.Count > 0 && catalog.Product.ProductType == ProductType.Physical && config.GetValue<long?>("Pilot:DeliveryFeeMinor") is >= 0) fulfillment.Add(OrderFulfillmentMode.FounderDelivery);
        var callerOrders = actor is null ? null : db.PaymentOrders.Where(o => o.CampaignId == id && o.CustomerId == actor.UserId);
        var activeOrders = callerOrders is null ? 0 : await callerOrders.CountAsync(o => (o.ReservationId != null && reservations.Any(r => r.Id == o.ReservationId)) || (o.ExpiresAtUtc > now && (o.State == PaymentState.Creating || o.State == PaymentState.Pending || o.State == PaymentState.Authorized)), ct);
        GroupDealParticipation? participation = actor is null ? null : new(
            c.OriginatingDemandId is not null && await OriginInterests(c.OriginatingDemandId.Value).AnyAsync(i => i.CustomerId == actor.UserId, ct),
            activeOrders, await paid.CountAsync(o => o.CustomerId == actor.UserId, ct));
        var ownerActive = await db.UsersProfile.AnyAsync(u => u.Id == seller.OwnerUserId && u.Status == AccountStatus.Active, ct);
        string? reason = !config.GetValue<bool>("Pilot:Enabled") || !gateway.Available ? "payments_unavailable"
            : !ownerActive ? "seller_unavailable" : !FinancialOpen(c) || now < c.StartsAtUtc || now >= c.ExpiresAtUtc ? "deal_closed"
            : available < c.MinimumReservationQuantity ? "capacity_unavailable" : activeOrders > 0 ? "already_joined"
            : fulfillment.Count == 0 ? "fulfillment_unavailable" : actor is null ? "authentication_required" : null;
        var result = new GroupDealDetail(c.Id, (GroupDealType)catalog.Product.ProductType, c.Title, c.Description, catalog.Product.Id, catalog.Product.CategoryId,
            catalog.Product.Name, catalog.Variant.VariantName, catalog.Variant.UnitOfMeasure, catalog.Variant.UnitQuantity, new(seller.BusinessName, Eligible(seller)),
            c.OriginatingDemandId, c.OriginatingDemandId is null ? null : await db.DiscoveryDemands.Where(d => d.Id == c.OriginatingDemandId).Select(d => d.Title).SingleAsync(ct),
            interested, null, await reservations.Select(r => r.CustomerUserId).Distinct().CountAsync(ct), await paid.Select(o => o.CustomerId).Distinct().CountAsync(ct),
            GroupDealRules.Progress(tiers, c.FinalQuantity ?? c.ReservedQuantity), tiers, c.Status, c.StartsAtUtc, c.ExpiresAtUtc, now,
            c.ServiceArea, c.TargetQuantity, available, c.FinalUnitPrice, c.OfferTerms, fulfillment, pickupLocation,
            new(c.ExpiresAtUtc, c.FinalizedAtUtc is null && FinancialOpen(c) && now < c.ExpiresAtUtc, "Captured payments are queued for refund if the group fails.", "A lower final tier queues a refund of the price difference."),
            reason is null, reason, participation);
        await tx.CommitAsync(ct); return result;
    }
}

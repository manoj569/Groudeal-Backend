using System.Security.Cryptography;
using System.Text;
using GroupBuy.Application;
using GroupBuy.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace GroupBuy.Infrastructure;

public sealed partial class Marketplace
{
    private void Notify(Guid user, NotificationType type, Guid entity)
    {
        // This is the durable delivery destination itself: no external dispatcher/outbox gap.
        db.Notifications.Add(new InternalNotification { RecipientUserId = user, Type = type, EntityId = entity,
            DeduplicationKey = $"{type}:{entity:N}:{user:N}", CreatedAtUtc = clock.GetUtcNow() });
    }

    private Task<bool> PhysicalVariant(Guid variant, CancellationToken ct) => db.Variants.AnyAsync(v => v.Id == variant && db.Products.Any(p => p.Id == v.ProductId && p.ProductType == ProductType.Physical), ct);

    private async Task<decimal> FreeInventory(Guid variant, CancellationToken ct)
    {
        var available = await db.Inventories.Where(i => i.ProductVariantId == variant).Select(i => (decimal?)i.AvailableQuantity).SingleOrDefaultAsync(ct) ?? 0;
        return available - await db.Campaigns.Where(c => c.ProductVariantId == variant).SumAsync(c => c.CommittedQuantity, ct);
    }

    private async Task CheckOperationalResources(Seller seller, GroupBuyCampaign c, CancellationToken ct)
    {
        Require(Eligible(seller) && await db.UsersProfile.AnyAsync(u => u.Id == seller.OwnerUserId && u.Status == AccountStatus.Active, ct), "Seller is unavailable.");
        Require(await db.Variants.AnyAsync(v => v.Id == c.ProductVariantId && v.IsActive && PublicProducts.Any(p => p.Id == v.ProductId && p.SellerId == seller.Id), ct), "Product is unavailable.");
        Require(await db.Locations.AnyAsync(l => l.Id == c.SellerLocationId && l.SellerId == seller.Id && l.IsActive && l.IsPickupEnabled, ct), "Pickup location is unavailable.");
    }

    private static OperationCampaignResponse OperationDto(GroupBuyCampaign c) => new(CampaignDto(c), c.CommittedQuantity, c.FulfilledQuantity, c.ConfirmationDeadlineUtc, c.PickupDeadlineUtc, c.DecisionAtUtc, c.DecisionReason);

    public async Task<InventoryResponse> UpdateInventory(Actor actor, Guid variantId, InventoryRequest request, CancellationToken ct)
    {
        Validate.With(request, new InventoryValidator());
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var seller = await OwnedSeller(actor, ct); seller = await LockSeller(seller.Id, ct); Require(Eligible(seller), "Approved seller required.", 403);
        var variant = await db.Variants.AsNoTracking().SingleOrDefaultAsync(v => v.Id == variantId && db.Products.Any(p => p.Id == v.ProductId && p.SellerId == seller.Id), ct) ?? throw Missing();
        Require(await PhysicalVariant(variantId, ct), "Nonphysical offers use campaign capacity, not stock inventory.", 400);
        var committed = await db.Campaigns.Where(c => c.ProductVariantId == variantId).SumAsync(c => c.CommittedQuantity, ct);
        InventoryRules.Validate(request.AvailableQuantity, committed, variant.UnitOfMeasure);
        var inventory = await db.Inventories.SingleOrDefaultAsync(i => i.ProductVariantId == variantId, ct);
        if (inventory is null) { Require(request.ExpectedVersion == 0, "Inventory version changed."); inventory = new Inventory { SellerId = seller.Id, ProductVariantId = variantId }; db.Inventories.Add(inventory); }
        else { await db.Entry(inventory).ReloadAsync(ct); Require(inventory.Version == request.ExpectedVersion, "Inventory version changed."); }
        inventory.AvailableQuantity = request.AvailableQuantity; inventory.Version++; inventory.UpdatedAt = clock.GetUtcNow();
        Audit(actor, "InventoryDeclared", inventory); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return await InventoryDto(variant, inventory, ct);
    }

    private async Task<InventoryResponse> InventoryDto(ProductVariant variant, Inventory? inventory, CancellationToken ct)
    {
        var product = await db.Products.AsNoTracking().SingleAsync(p => p.Id == variant.ProductId, ct);
        var campaigns = db.Campaigns.Where(c => c.ProductVariantId == variant.Id);
        return new(variant.Id, product.Id, product.Name, variant.UnitOfMeasure, variant.UnitQuantity, inventory?.AvailableQuantity ?? 0,
            await campaigns.SumAsync(c => c.CommittedQuantity, ct),
            await db.Reservations.Where(r => r.Status == ReservationStatus.Active && campaigns.Any(c => c.Id == r.CampaignId)).SumAsync(r => r.Quantity, ct),
            await campaigns.SumAsync(c => c.FulfilledQuantity, ct), inventory?.Version ?? 0);
    }

    public async Task<PageResponse<InventoryResponse>> SellerInventory(Actor actor, PageRequest page, CancellationToken ct)
    {
        Page(page); var seller = await OwnedSeller(actor, ct);
        var query = db.Variants.AsNoTracking().Where(v => db.Products.Any(p => p.Id == v.ProductId && p.SellerId == seller.Id));
        var items = new List<InventoryResponse>();
        foreach (var v in await query.OrderBy(v => v.Id).Skip(page.Offset).Take(page.PageSize).ToListAsync(ct))
            items.Add(await InventoryDto(v, await db.Inventories.AsNoTracking().SingleOrDefaultAsync(i => i.ProductVariantId == v.Id, ct), ct));
        return new(items, page.Page, page.PageSize, await query.CountAsync(ct));
    }

    public async Task<PageResponse<OperationCampaignResponse>> SellerCampaigns(Actor actor, PageRequest page, CampaignStatus? status, CancellationToken ct)
    {
        Page(page); Require(status is null || Enum.IsDefined(status.Value), "Unknown campaign status.", 400);
        var seller = await OwnedSeller(actor, ct); var q = db.Campaigns.AsNoTracking().Where(c => c.SellerId == seller.Id && (status == null || c.Status == status));
        return new((await q.OrderByDescending(c => c.CreatedAt).ThenBy(c => c.Id).Skip(page.Offset).Take(page.PageSize).ToListAsync(ct)).Select(OperationDto).ToArray(), page.Page, page.PageSize, await q.CountAsync(ct));
    }

    public async Task<SellerSummaryResponse> SellerSummary(Actor actor, CancellationToken ct)
    {
        var seller = await OwnedSeller(actor, ct); var campaigns = db.Campaigns.Where(c => c.SellerId == seller.Id);
        var reservations = db.Reservations.Where(r => campaigns.Any(c => c.Id == r.CampaignId)); var pickups = PhysicalPickupRows.Where(p => p.SellerId == seller.Id);
        return new(await db.Products.CountAsync(p => p.SellerId == seller.Id, ct),
            await campaigns.CountAsync(c => c.Status == CampaignStatus.Open || c.Status == CampaignStatus.Scheduled || c.Status == CampaignStatus.TargetReached || c.Status == CampaignStatus.AwaitingSellerConfirmation || c.Status == CampaignStatus.Confirmed || c.Status == CampaignStatus.FulfillmentInProgress, ct),
            await campaigns.CountAsync(c => c.Status == CampaignStatus.Draft || c.Status == CampaignStatus.PendingApproval, ct),
            await campaigns.CountAsync(c => c.Status == CampaignStatus.Completed, ct), await reservations.CountAsync(r => r.Status == ReservationStatus.Active, ct),
            await pickups.CountAsync(p => p.Status == PickupStatus.Pending || p.Status == PickupStatus.ReadyForPickup, ct), await pickups.CountAsync(p => p.Status == PickupStatus.Collected, ct),
            await reservations.Where(r => r.Status == ReservationStatus.Active || r.Status == ReservationStatus.Completed).SumAsync(r => r.TotalPriceSnapshot, ct),
            await reservations.Where(r => r.Status == ReservationStatus.Completed).SumAsync(r => r.TotalPriceSnapshot, ct));
    }

    public async Task<PageResponse<SellerReservationResponse>> SellerReservations(Actor actor, Guid campaignId, PageRequest page, ReservationStatus? status, CancellationToken ct)
    {
        Page(page); Require(status is null || Enum.IsDefined(status.Value), "Unknown reservation status.", 400);
        var seller = await OwnedSeller(actor, ct);
        Require(await db.Campaigns.AnyAsync(c => c.Id == campaignId && c.SellerId == seller.Id, ct), "Resource not found.", 404);
        var q = db.Reservations.AsNoTracking().Where(r => r.CampaignId == campaignId && (status == null || r.Status == status));
        var items = await (from r in q
                           join pickup in PhysicalPickupRows on r.Id equals pickup.ReservationId into pickups
                           from pickup in pickups.DefaultIfEmpty()
                           orderby r.CreatedAt, r.Id
                           select new SellerReservationResponse(r.Id, r.CampaignId, r.Quantity, r.UnitPriceSnapshot, r.TotalPriceSnapshot, r.Status,
                               pickup == null ? null : pickup.Id, pickup == null ? null : pickup.Status)).Skip(page.Offset).Take(page.PageSize).ToListAsync(ct);
        return new(items, page.Page, page.PageSize, await q.CountAsync(ct));
    }

    public async Task<CampaignPerformanceResponse> CampaignPerformance(Actor actor, Guid id, CancellationToken ct)
    {
        var seller = await OwnedSeller(actor, ct);
        var campaign = await db.Campaigns.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id && c.SellerId == seller.Id, ct) ?? throw Missing();
        var q = db.Reservations.Where(r => r.CampaignId == id);
        return new(OperationDto(campaign), await q.CountAsync(ct), await q.CountAsync(r => r.Status == ReservationStatus.Active, ct),
            await q.CountAsync(r => r.Status == ReservationStatus.Cancelled, ct), await PhysicalPickupRows.CountAsync(p => p.CampaignId == id && p.Status == PickupStatus.Collected, ct),
            await q.Where(r => r.Status == ReservationStatus.Completed).SumAsync(r => r.TotalPriceSnapshot, ct));
    }

    public async Task<OperationCampaignResponse> Decide(Actor actor, Guid campaignId, DecisionRequest request, bool approve, CancellationToken ct)
    {
        Validate.With(request, new DecisionValidator()); Require(approve || !string.IsNullOrWhiteSpace(request.Reason), "Provide a rejection reason.", 400);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var seller = await OwnedSeller(actor, ct); seller = await LockSeller(seller.Id, ct);
        var c = await LockCampaign(campaignId, ct); Require(c.SellerId == seller.Id, "Resource not found.", 404); Require(Eligible(seller), "Approved seller required.", 403);
        Require(!c.UsesTierPricing, "Use tier campaign finalization and fulfillment.");
        if (c.DecisionKey == request.IdempotencyKey)
        {
            Require(c.DecisionApproved == approve && c.DecisionReason == request.Reason, "Idempotency key already used with a different decision.");
            await tx.CommitAsync(ct); return OperationDto(c);
        }
        Require(c.DecisionKey is null, "Campaign already has a seller decision.");
        Require(c.Status == CampaignStatus.AwaitingSellerConfirmation && c.ConfirmationDeadlineUtc > clock.GetUtcNow(), "Confirmation window is closed.");
        c.DecisionKey = request.IdempotencyKey; c.DecisionApproved = approve; c.DecisionReason = request.Reason;
        c.DecisionAtUtc = clock.GetUtcNow(); c.DecisionActorId = actor.UserId;
        if (approve)
        {
            await CheckOperationalResources(seller, c, ct);
            Require(c.CommittedQuantity == c.TargetQuantity && c.ReservedQuantity == c.TargetQuantity, "Inventory and target must be fully committed.");
            Require(await FreeInventory(c.ProductVariantId, ct) >= 0 && await db.Inventories.AnyAsync(i => i.ProductVariantId == c.ProductVariantId, ct), "Declare inventory before confirmation.");
            c.TransitionTo(CampaignStatus.Confirmed); c.PickupDeadlineUtc = clock.GetUtcNow().AddHours(config.GetValue("Operations:PickupHours", 48));
            var location = await db.Locations.AsNoTracking().SingleAsync(l => l.Id == c.SellerLocationId, ct);
            foreach (var r in await db.Reservations.Where(r => r.CampaignId == c.Id && r.Status == ReservationStatus.Active).ToListAsync(ct))
            {
                db.Pickups.Add(new PickupRecord { ReservationId = r.Id, CampaignId = c.Id, SellerId = seller.Id, CustomerUserId = r.CustomerUserId,
                    DeadlineUtc = c.PickupDeadlineUtc.Value, Instructions = $"Collect from {seller.BusinessName}: {location.AddressLine1}, {location.AddressLine2}, {location.Area}, {location.City}, {location.State} {location.PostalCode}. Present your one-time code only at physical handover. Payment is not tracked by this service." });
            }
            foreach (var user in await db.Reservations.Where(r => r.CampaignId == c.Id && r.Status == ReservationStatus.Active).Select(r => r.CustomerUserId).Distinct().ToListAsync(ct)) Notify(user, NotificationType.CampaignConfirmed, c.Id);
            Audit(actor, "InventoryConfirmed", c);
        }
        else { await FailCampaign(c, NotificationType.CampaignRejected, ct); Audit(actor, "CampaignFailed", c); }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return OperationDto(c);
    }

    private async Task FailCampaign(GroupBuyCampaign c, NotificationType notification, CancellationToken ct)
    {
        c.TransitionTo(CampaignStatus.Failed); c.CommittedQuantity = 0;
        if (c.DecisionAtUtc is null) { c.DecisionAtUtc = clock.GetUtcNow(); c.DecisionApproved = false; }
        var reservations = await db.Reservations.Where(r => r.CampaignId == c.Id && r.Status == ReservationStatus.Active).ToListAsync(ct);
        foreach (var user in reservations.Select(r => r.CustomerUserId).Distinct()) Notify(user, notification, c.Id);
        foreach (var r in reservations) { r.Status = ReservationStatus.Expired; r.UpdatedAt = clock.GetUtcNow(); }
        foreach (var pickup in await db.Pickups.Where(p => p.CampaignId == c.Id && (p.Status == PickupStatus.Pending || p.Status == PickupStatus.ReadyForPickup)).ToListAsync(ct))
        { pickup.Status = PickupStatus.Cancelled; pickup.CodeHash = null; pickup.CodeExpiresAtUtc = null; pickup.UpdatedAt = clock.GetUtcNow(); }
        c.ReservedQuantity = c.FulfilledQuantity;
    }

    private async Task<CampaignResponse> ReportFulfillmentFailure(Actor actor, Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var seller = await OwnedSeller(actor, ct); seller = await LockSeller(seller.Id, ct); Require(Eligible(seller), "Approved seller required.", 403);
        var c = await LockCampaign(id, ct); Require(c.SellerId == seller.Id, "Resource not found.", 404);
        Require(c.Status is CampaignStatus.AwaitingSellerConfirmation or CampaignStatus.Confirmed or CampaignStatus.FulfillmentInProgress, "Campaign cannot fail from its current state.");
        const string reason = "Seller reported fulfillment failure via legacy transition.";
        if (c.Status == CampaignStatus.AwaitingSellerConfirmation) { c.DecisionReason = reason; c.DecisionActorId = actor.UserId; c.DecisionKey = $"legacy-failure-{id:N}"; }
        await FailCampaign(c, NotificationType.CampaignRejected, ct); Audit(actor, "CampaignFailed", c, reason);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return CampaignDto(c);
    }

    public async Task<ReservationDetailResponse> ReservationDetail(Actor actor, Guid id, CancellationToken ct)
    {
        var r = await db.Reservations.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id && r.CustomerUserId == actor.UserId, ct) ?? throw Missing();
        var c = await db.Campaigns.AsNoTracking().SingleAsync(c => c.Id == r.CampaignId, ct);
        var location = await db.Locations.AsNoTracking().SingleAsync(l => l.Id == c.SellerLocationId, ct);
        var pickupId = await PhysicalPickupRows.Where(p => p.ReservationId == r.Id).Select(p => (Guid?)p.Id).SingleOrDefaultAsync(ct);
        return new(ReservationDto(r, c), c.Status, Pickup(location), c.ConfirmationDeadlineUtc, c.PickupDeadlineUtc, pickupId,
            pickupId != null ? "ViewPickup" : r.Status != ReservationStatus.Active ? "None" : c.Status == CampaignStatus.Open && clock.GetUtcNow() < c.ExpiresAtUtc ? "WaitForTargetOrCancel" : "WaitForSellerConfirmation", c.UsesTierPricing ? "ProviderPaymentTracked" : "NotTracked");
    }

    private async Task<PickupDetailResponse> PickupDto(PickupRecord p, CancellationToken ct)
    {
        var r = await db.Reservations.AsNoTracking().SingleAsync(r => r.Id == p.ReservationId, ct);
        var paid = await db.Campaigns.AnyAsync(c => c.Id == p.CampaignId && c.UsesTierPricing, ct);
        var fulfillmentId = paid ? await db.OrderFulfillments.Where(f => f.PickupId == p.Id).Select(f => (Guid?)f.Id).SingleOrDefaultAsync(ct) : null;
        var action = p.Status == PickupStatus.Pending ? "WaitForReady" : p.Status == PickupStatus.ReadyForPickup && p.DeadlineUtc > clock.GetUtcNow() ? "RequestCodeAndCollect" : "None";
        return new(p.Id, p.ReservationId, p.CampaignId, p.Status, r.Quantity, r.TotalPriceSnapshot, p.Instructions, p.DeadlineUtc, p.PaymentPolicy, paid ? "ViewPilotFulfillment" : action, paid, fulfillmentId);
    }

    public async Task<PickupDetailResponse> CustomerPickup(Actor actor, Guid id, CancellationToken ct) => await PickupDto(await PhysicalPickupRows.SingleOrDefaultAsync(p => p.Id == id && p.CustomerUserId == actor.UserId, ct) ?? throw Missing(), ct);

    public async Task<PageResponse<PickupDetailResponse>> Pickups(Actor actor, bool sellerView, PageRequest page, PickupStatus? status, CancellationToken ct)
    {
        Page(page); Require(status is null || Enum.IsDefined(status.Value), "Unknown pickup status.", 400);
        var sellerId = sellerView ? (await OwnedSeller(actor, ct)).Id : Guid.Empty;
        var q = PhysicalPickupRows.Where(p => (sellerView ? p.SellerId == sellerId : p.CustomerUserId == actor.UserId) && (status == null || p.Status == status));
        var items = new List<PickupDetailResponse>(); foreach (var p in await q.OrderBy(p => p.DeadlineUtc).ThenBy(p => p.Id).Skip(page.Offset).Take(page.PageSize).ToListAsync(ct)) items.Add(await PickupDto(p, ct));
        return new(items, page.Page, page.PageSize, await q.CountAsync(ct));
    }

    // All pickup mutations lock seller -> campaign -> pickup, including customer code issuance.
    private async Task<(Seller Seller, GroupBuyCampaign Campaign, PickupRecord Pickup)> LockPickup(Actor actor, Guid id, bool customer, CancellationToken ct)
    {
        var lookup = await PhysicalPickupRows.Where(p => p.Id == id && (customer ? p.CustomerUserId == actor.UserId : db.Sellers.Any(s => s.Id == p.SellerId && s.OwnerUserId == actor.UserId))).Select(p => new { p.SellerId, p.CampaignId }).SingleOrDefaultAsync(ct) ?? throw Missing();
        var seller = await LockSeller(lookup.SellerId, ct); var campaign = await LockCampaign(lookup.CampaignId, ct);
        var pickup = await db.Pickups.FromSqlInterpolated($"SELECT * FROM pickup_records WHERE \"Id\" = {id} FOR UPDATE").SingleAsync(ct);
        await db.Entry(pickup).ReloadAsync(ct);
        Require(customer ? pickup.CustomerUserId == actor.UserId : seller.OwnerUserId == actor.UserId, "Resource not found.", 404);
        Require(Eligible(seller) && await db.UsersProfile.AnyAsync(u => u.Id == seller.OwnerUserId && u.Status == AccountStatus.Active, ct), "Seller is unavailable.", 403);
        Require(!campaign.UsesTierPricing, "Use the paid fulfillment pass workflow.");
        return (seller, campaign, pickup);
    }

    public async Task<PickupDetailResponse> ReadyPickup(Actor actor, Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct); var (_, c, p) = await LockPickup(actor, id, false, ct);
        Require(c.Status is CampaignStatus.Confirmed or CampaignStatus.FulfillmentInProgress && p.DeadlineUtc > clock.GetUtcNow(), "Pickup window is closed.");
        if (p.Status == PickupStatus.ReadyForPickup) { await tx.CommitAsync(ct); return await PickupDto(p, ct); }
        Require(p.Status == PickupStatus.Pending, "Pickup cannot be marked ready.");
        if (c.Status == CampaignStatus.Confirmed) c.TransitionTo(CampaignStatus.FulfillmentInProgress);
        p.Status = PickupStatus.ReadyForPickup; p.UpdatedAt = clock.GetUtcNow(); Notify(p.CustomerUserId, NotificationType.PickupReady, p.Id); Audit(actor, "PickupReady", p);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return await PickupDto(p, ct);
    }

    private static string HashCode(Guid id, string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{id:N}:{code}")));

    public async Task<IssuedPickupCode> IssuePickupCode(Actor actor, Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct); var (_, c, p) = await LockPickup(actor, id, true, ct); var now = clock.GetUtcNow();
        Require(c.Status == CampaignStatus.FulfillmentInProgress && p.Status == PickupStatus.ReadyForPickup && p.DeadlineUtc > now, "Pickup is not eligible for collection.");
        Require(p.CodeIssuedAtUtc is null || p.CodeIssuedAtUtc <= now.AddMinutes(-1), "Wait before requesting another code.", 429);
        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        p.CodeHash = HashCode(p.Id, code); p.CodeIssuedAtUtc = now;
        p.CodeExpiresAtUtc = now.AddMinutes(30) < p.DeadlineUtc ? now.AddMinutes(30) : p.DeadlineUtc;
        p.UpdatedAt = now; Audit(actor, "PickupCodeIssued", p); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new(code, p.CodeExpiresAtUtc.Value);
    }

    public async Task<PickupDetailResponse> CollectPickup(Actor actor, Guid id, PickupCodeRequest request, CancellationToken ct)
    {
        Validate.With(request, new PickupCodeValidator());
        await using var tx = await db.Database.BeginTransactionAsync(ct); var (_, c, p) = await LockPickup(actor, id, false, ct); var now = clock.GetUtcNow();
        Require(c.Status == CampaignStatus.FulfillmentInProgress && p.Status == PickupStatus.ReadyForPickup && p.DeadlineUtc > now, "Pickup is not eligible for collection.");
        Require(p.LockedUntilUtc is null || p.LockedUntilUtc <= now, "Pickup verification temporarily locked.", 429);
        var valid = p.CodeHash != null && p.CodeExpiresAtUtc > now && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(p.CodeHash), Convert.FromHexString(HashCode(id, request.Code)));
        if (!valid)
        {
            p.FailedAttempts++;
            if (p.FailedAttempts % 5 == 0) p.LockedUntilUtc = now.AddMinutes(15);
            p.UpdatedAt = now; Audit(actor, "PickupVerificationFailed", p);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            throw new BusinessException(400, "invalid_pickup_code", "Invalid or expired pickup code.");
        }
        var r = await db.Reservations.SingleAsync(r => r.Id == p.ReservationId, ct); Require(r.Status == ReservationStatus.Active, "Reservation is unavailable.");
        var inventory = await db.Inventories.SingleAsync(i => i.ProductVariantId == c.ProductVariantId, ct); await db.Entry(inventory).ReloadAsync(ct);
        Require(inventory.AvailableQuantity >= r.Quantity && c.CommittedQuantity >= r.Quantity, "Committed inventory is unavailable.");
        inventory.AvailableQuantity -= r.Quantity; inventory.Version++; inventory.UpdatedAt = now;
        c.CommittedQuantity -= r.Quantity; c.FulfilledQuantity += r.Quantity; c.UpdatedAt = now;
        r.Status = ReservationStatus.Completed; r.UpdatedAt = now;
        p.Status = PickupStatus.Collected; p.CollectedAtUtc = now; p.CollectedByUserId = actor.UserId; p.CodeHash = null; p.CodeExpiresAtUtc = null; p.UpdatedAt = now;
        Notify(p.CustomerUserId, NotificationType.PickupCompleted, p.Id); Audit(actor, "PickupCollected", p);
        // Save first so the terminal query observes this pickup inside the same transaction.
        await db.SaveChangesAsync(ct); await FinishFromPickups(c, ct); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return await PickupDto(p, ct);
    }

    private async Task FinishFromPickups(GroupBuyCampaign c, CancellationToken ct)
    {
        if (await db.Pickups.AnyAsync(p => p.CampaignId == c.Id && (p.Status == PickupStatus.Pending || p.Status == PickupStatus.ReadyForPickup), ct)) return;
        var allCollected = !await db.Pickups.AnyAsync(p => p.CampaignId == c.Id && p.Status != PickupStatus.Collected, ct);
        c.TransitionTo(allCollected ? CampaignStatus.Completed : CampaignStatus.Failed); c.CommittedQuantity = 0;
        Audit(null, allCollected ? "CampaignCompleted" : "CampaignFailed", c);
    }

    public async Task<PageResponse<NotificationResponse>> Notifications(Actor actor, PageRequest page, bool unreadOnly, CancellationToken ct)
    {
        Page(page); var q = db.Notifications.AsNoTracking().Where(n => n.RecipientUserId == actor.UserId && (!unreadOnly || n.ReadAtUtc == null));
        return new(await q.OrderByDescending(n => n.CreatedAtUtc).ThenBy(n => n.Id).Skip(page.Offset).Take(page.PageSize).Select(n => new NotificationResponse(n.Id, n.Type, n.EntityId, n.CreatedAtUtc, n.ReadAtUtc)).ToListAsync(ct), page.Page, page.PageSize, await q.CountAsync(ct));
    }

    public async Task ReadNotification(Actor actor, Guid id, CancellationToken ct)
    {
        Require(await db.Notifications.Where(n => n.Id == id && n.RecipientUserId == actor.UserId).ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAtUtc, n => n.ReadAtUtc ?? clock.GetUtcNow()), ct) == 1, "Resource not found.", 404);
    }

    private IQueryable<GroupBuyCampaign> DueCampaigns(DateTimeOffset now) => db.Campaigns.AsNoTracking().Where(c => !c.UsesTierPricing).Where(c =>
        ((c.Status == CampaignStatus.Open || c.Status == CampaignStatus.Scheduled || c.Status == CampaignStatus.PendingApproval) && c.ExpiresAtUtc <= now) ||
        (c.Status == CampaignStatus.Scheduled && c.StartsAtUtc <= now) ||
        (c.Status == CampaignStatus.TargetReached && c.CommittedQuantity > 0) ||
        (c.Status == CampaignStatus.AwaitingSellerConfirmation && c.ConfirmationDeadlineUtc <= now) ||
        ((c.Status == CampaignStatus.Confirmed || c.Status == CampaignStatus.FulfillmentInProgress) && c.PickupDeadlineUtc <= now));

    public async Task<OperationsHealthResponse> OperationsHealth(Actor actor, CancellationToken ct)
    {
        Admin(actor); var now = clock.GetUtcNow();
        return new(now, await db.Campaigns.CountAsync(c => c.WorkerFailureCount > 0 && c.WorkerRetryAtUtc != null, ct), await DueCampaigns(now).CountAsync(ct), await db.Campaigns.Select(c => (int?)c.WorkerFailureCount).MaxAsync(ct) ?? 0,
            await db.Campaigns.CountAsync(c => (c.Status == CampaignStatus.PendingApproval || c.Status == CampaignStatus.Scheduled || c.Status == CampaignStatus.Open || c.Status == CampaignStatus.TargetReached || c.Status == CampaignStatus.AwaitingSellerConfirmation || c.Status == CampaignStatus.Confirmed || c.Status == CampaignStatus.FulfillmentInProgress) && c.CommittedQuantity == 0, ct), workerHealth.Read(), await DueCampaigns(now).CountAsync(c => c.WorkerFailureCount >= config.GetValue("Operations:WorkerMaxAttempts", 10), ct), config.GetValue("Operations:WorkerMaxAttempts", 10));
    }

    internal async Task ProcessOne(Guid id, CancellationToken ct)
    {
        db.Database.SetCommandTimeout(10);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '3s'", ct);
        var sellerId = await db.Campaigns.Where(c => c.Id == id).Select(c => c.SellerId).SingleAsync(ct);
        var seller = await LockSeller(sellerId, ct); var c = await LockCampaign(id, ct); var now = clock.GetUtcNow();
        if (c.WorkerRetryAtUtc > now || c.WorkerFailureCount >= config.GetValue("Operations:WorkerMaxAttempts", 10)) { await tx.CommitAsync(ct); return; }
        if (c.Status is CampaignStatus.Open or CampaignStatus.Scheduled or CampaignStatus.PendingApproval && c.ExpiresAtUtc <= now)
        {
            c.TransitionTo(CampaignStatus.Expired); c.CommittedQuantity = 0;
            var reservations = await db.Reservations.Where(r => r.CampaignId == c.Id && r.Status == ReservationStatus.Active).ToListAsync(ct);
            foreach (var user in reservations.Select(r => r.CustomerUserId).Distinct()) Notify(user, NotificationType.CampaignExpired, c.Id);
            foreach (var r in reservations) { r.Status = ReservationStatus.Expired; r.UpdatedAt = now; }
            c.ReservedQuantity = 0; Audit(null, "CampaignExpired", c);
        }
        else if (c.Status == CampaignStatus.Scheduled && c.StartsAtUtc <= now) { c.TransitionTo(CampaignStatus.Open); await NotifyDemandOffer(c, ct); Audit(null, "CampaignOpened", c); }
        else if (c.Status == CampaignStatus.TargetReached && c.CommittedQuantity > 0)
        {
            c.TransitionTo(CampaignStatus.AwaitingSellerConfirmation); c.ConfirmationDeadlineUtc = now.AddMinutes(config.GetValue("Operations:ConfirmationMinutes", 60));
            Notify(seller.OwnerUserId, NotificationType.CampaignTargetReached, c.Id); Notify(seller.OwnerUserId, NotificationType.SellerConfirmationRequired, c.Id);
        }
        else if (c.Status == CampaignStatus.AwaitingSellerConfirmation && c.ConfirmationDeadlineUtc <= now)
        { c.DecisionReason = "Seller confirmation deadline expired."; await FailCampaign(c, NotificationType.CampaignExpired, ct); Audit(null, "ConfirmationExpired", c); }
        else if (c.Status is CampaignStatus.Confirmed or CampaignStatus.FulfillmentInProgress && c.PickupDeadlineUtc <= now)
        {
            // Legacy confirmed campaigns without pickup records are failed explicitly, never auto-collected.
            if (!await db.Pickups.AnyAsync(p => p.CampaignId == id, ct)) { await FailCampaign(c, NotificationType.CampaignExpired, ct); Audit(null, "LegacyFulfillmentExpired", c); }
            else
            {
                var pickups = await db.Pickups.Where(p => p.CampaignId == id && (p.Status == PickupStatus.Pending || p.Status == PickupStatus.ReadyForPickup)).ToListAsync(ct);
                foreach (var p in pickups)
                {
                    p.Status = PickupStatus.NotCollected; p.CodeHash = null; p.CodeExpiresAtUtc = null; p.UpdatedAt = now;
                    var r = await db.Reservations.SingleAsync(r => r.Id == p.ReservationId, ct); r.Status = ReservationStatus.Expired; r.UpdatedAt = now;
                    c.CommittedQuantity -= r.Quantity; c.ReservedQuantity -= r.Quantity; Notify(p.CustomerUserId, NotificationType.CampaignExpired, p.Id);
                }
                await db.SaveChangesAsync(ct); await FinishFromPickups(c, ct);
            }
        }
        c.WorkerFailureCount = 0; c.WorkerRetryAtUtc = null; c.WorkerErrorCode = null;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
}

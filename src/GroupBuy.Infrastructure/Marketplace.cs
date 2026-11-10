using System.Text.Json;
using GroupBuy.Application;
using GroupBuy.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GroupBuy.Infrastructure;
public sealed partial class Marketplace(MarketplaceDb db, UserManager<IdentityAccount> users, TimeProvider clock, IConfiguration config, IServiceScopeFactory scopes, ILogger<Marketplace> logger, WorkerHealthState workerHealth, OperationalMetrics metrics, IPaymentGateway gateway, Microsoft.AspNetCore.DataProtection.IDataProtectionProvider protection) : IMarketplace, ICampaignExpiryProcessor, IOperations, IAdministration
{
    private static BusinessException Missing() => new(404, "not_found", "Resource not found.");
    private static void Require(bool condition, string message, int status = 409) { if (!condition) throw new BusinessException(status, "rule_violation", message); }
    private static void Admin(Actor actor) => Require(actor.IsAdmin, "Administrator access required.", 403);
    private static void Page(PageRequest p) => Validate.With(p, new PageValidator());
    private void Audit(Actor? actor, string action, Entity entity, string? reason = null) => db.AuditLogs.Add(new AuditLog { ActorUserId = actor?.UserId, Action = action, EntityType = entity.GetType().Name, EntityId = entity.Id, CorrelationId = actor?.CorrelationId ?? "expiry-worker", SafeMetadataJson = reason is null ? "{}" : JsonSerializer.Serialize(new { reason }) });
    private async Task<Seller> OwnedSeller(Actor actor, CancellationToken ct) => await db.Sellers.SingleOrDefaultAsync(x => x.OwnerUserId == actor.UserId, ct) ?? throw Missing();
    private static bool Eligible(Seller s) => s.Status == ResourceStatus.Active && s.VerificationStatus == VerificationStatus.Approved;
    private async Task<GroupBuyCampaign> LockCampaign(Guid id, CancellationToken ct)
    {
        var campaign = await db.Campaigns.FromSqlInterpolated($"SELECT *, xmin FROM group_buy_campaigns WHERE \"Id\" = {id} FOR UPDATE").SingleOrDefaultAsync(ct) ?? throw Missing();
        await db.Entry(campaign).ReloadAsync(ct);
        return campaign;
    }
    // Seller-first exclusive locks serialize inventory accounting across that seller's campaigns.
    private async Task<Seller> LockSeller(Guid id, CancellationToken ct, bool write = false)
    {
        var seller = await db.Sellers.FromSqlInterpolated($"SELECT * FROM sellers WHERE \"Id\" = {id} FOR UPDATE").SingleAsync(ct);
        // A tracking query does not refresh an entity loaded before waiting for the lock.
        // Read current approval/profile values while the acquired lock is held.
        await db.Entry(seller).ReloadAsync(ct);
        return seller;
    }
    private static PickupResponse Pickup(SellerLocation l) => new(l.Id, l.AddressLine1, l.AddressLine2, l.Area, l.City, l.State, l.PostalCode);
    private async Task<SellerResponse> SellerDto(Seller s, CancellationToken ct) => new(s.Id, s.BusinessName, s.SellerType, s.Description, s.VerificationStatus, (await db.Locations.Where(x => x.SellerId == s.Id && x.IsActive && x.IsPickupEnabled).ToListAsync(ct)).Select(Pickup).ToArray());
    private async Task<SellerReviewResponse> ReviewDto(Seller s, CancellationToken ct) => new(s.Id, s.OwnerUserId, s.BusinessName, s.ContactEmail, s.ContactPhone, s.FssaiRegistrationNumber, s.VerificationStatus, (await db.Locations.Where(x => x.SellerId == s.Id).ToListAsync(ct)).Select(Pickup).ToArray());
    private static CampaignResponse CampaignDto(GroupBuyCampaign c) => new(c.Id, c.SellerId, c.ProductVariantId, c.SellerLocationId, c.Title, c.Description, c.ReferenceUnitPrice, c.GroupUnitPrice, c.TargetQuantity, c.ReservedQuantity, c.Progress, c.MinimumReservationQuantity, c.MaximumReservationQuantity, c.StartsAtUtc, c.ExpiresAtUtc, c.Status, c.UsesTierPricing);
    private static ReservationResponse ReservationDto(GroupBuyReservation r, GroupBuyCampaign c) => new(r.Id, r.CampaignId, r.Quantity, r.UnitPriceSnapshot, r.TotalPriceSnapshot, r.Status, r.CreatedAt, c.Progress, c.UsesTierPricing);
    private async Task<ProductResponse> ProductDto(Product p, CancellationToken ct) => new(p.Id, p.SellerId, p.CategoryId, p.Name, p.Slug, p.Description, p.ProductType, (await db.Variants.Where(x => x.ProductId == p.Id && x.IsActive).ToListAsync(ct)).Select(v => new VariantResponse(v.Id, v.SKU, v.VariantName, v.UnitOfMeasure, v.UnitQuantity, v.AttributesJson)).ToArray());
    private IQueryable<Product> PublicProducts => PublicCatalog.Products(db);
    private IQueryable<GroupBuyCampaign> PublicCampaigns => db.Campaigns.Where(c => c.Status != CampaignStatus.Draft && c.Status != CampaignStatus.PendingApproval && db.Sellers.Any(s => s.Id == c.SellerId && s.Status == ResourceStatus.Active && s.VerificationStatus == VerificationStatus.Approved) && db.Variants.Any(v => v.Id == c.ProductVariantId && v.IsActive && PublicProducts.Any(p => p.Id == v.ProductId)));
    public async Task<PageResponse<CategoryResponse>> Categories(PageRequest page, CancellationToken ct)
    {
        Page(page); var q = db.Categories.Where(x => x.IsActive); var total = await q.CountAsync(ct);
        return new(await q.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).Skip(page.Offset).Take(page.PageSize).Select(x => new CategoryResponse(x.Id, x.ParentCategoryId, x.Name, x.Slug, x.Description)).ToListAsync(ct), page.Page, page.PageSize, total);
    }
    public async Task<CategoryResponse> Category(string slug, CancellationToken ct) => await db.Categories.Where(x => x.Slug == slug && x.IsActive).Select(x => new CategoryResponse(x.Id, x.ParentCategoryId, x.Name, x.Slug, x.Description)).SingleOrDefaultAsync(ct) ?? throw Missing();
    public async Task<PageResponse<SellerResponse>> Sellers(PageRequest page, CancellationToken ct)
    {
        Page(page); var q = db.Sellers.Where(x => x.Status == ResourceStatus.Active && x.VerificationStatus == VerificationStatus.Approved); var total = await q.CountAsync(ct);
        var items = new List<SellerResponse>(); foreach (var s in await q.OrderBy(x => x.Id).Skip(page.Offset).Take(page.PageSize).ToListAsync(ct)) items.Add(await SellerDto(s, ct));
        return new(items, page.Page, page.PageSize, total);
    }
    public async Task<SellerResponse> Seller(Guid id, CancellationToken ct) => await SellerDto(await db.Sellers.SingleOrDefaultAsync(x => x.Id == id && x.Status == ResourceStatus.Active && x.VerificationStatus == VerificationStatus.Approved, ct) ?? throw Missing(), ct);
    public async Task<SellerResponse> SaveSeller(Actor actor, SellerRequest request, bool create, CancellationToken ct)
    {
        Validate.With(request, new SellerValidator());
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var seller = await db.Sellers.SingleOrDefaultAsync(x => x.OwnerUserId == actor.UserId, ct);
        Require(create ? seller is null : seller is not null, create ? "Seller profile already exists." : "Create a profile first.");
        if (seller is not null) seller = await LockSeller(seller.Id, ct, true);
        else { seller = new Seller { OwnerUserId = actor.UserId }; db.Sellers.Add(seller); }
        seller.BusinessName = request.BusinessName; seller.SellerType = request.SellerType; seller.Description = request.Description;
        seller.ContactEmail = request.ContactEmail; seller.ContactPhone = request.ContactPhone; seller.FssaiRegistrationNumber = request.FssaiRegistrationNumber;
        seller.VerificationStatus = VerificationStatus.Pending; seller.ReviewerUserId = null; seller.ReviewedAt = null; seller.UpdatedAt = clock.GetUtcNow();
        // Keep previous pickup locations for existing campaigns; a changed address requires a new approval.
        var location = request.Location;
        db.Locations.Add(new SellerLocation { SellerId = seller.Id, AddressLine1 = location.AddressLine1, AddressLine2 = location.AddressLine2, Area = location.Area, City = location.City, State = location.State, PostalCode = location.PostalCode, Latitude = location.Latitude, Longitude = location.Longitude });
        Audit(actor, create ? "SellerRequested" : "SellerProfileUpdated", seller); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return await SellerDto(seller, ct);
    }
    public async Task<DashboardResponse> Dashboard(Actor actor, CancellationToken ct)
    {
        var s = await OwnedSeller(actor, ct); return new(s.Id, s.VerificationStatus, await db.Products.CountAsync(x => x.SellerId == s.Id, ct), await db.Campaigns.CountAsync(x => x.SellerId == s.Id, ct), await db.Reservations.CountAsync(r => r.Status == ReservationStatus.Active && db.Campaigns.Any(c => c.Id == r.CampaignId && c.SellerId == s.Id), ct));
    }
    public async Task<PageResponse<ProductResponse>> Products(PageRequest page, CancellationToken ct)
    {
        Page(page); var q = PublicProducts; var total = await q.CountAsync(ct); var items = new List<ProductResponse>();
        foreach (var p in await q.OrderBy(x => x.Id).Skip(page.Offset).Take(page.PageSize).ToListAsync(ct)) items.Add(await ProductDto(p, ct));
        return new(items, page.Page, page.PageSize, total);
    }
    public async Task<ProductResponse> Product(Guid id, CancellationToken ct) => await ProductDto(await PublicProducts.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw Missing(), ct);
    public async Task<ProductResponse> SaveProduct(Actor actor, Guid? id, ProductRequest request, CancellationToken ct)
    {
        Validate.With(request, new ProductValidator());
        Require(request.ProductType == ProductType.Physical || (request.UnitOfMeasure == UnitOfMeasure.Piece && request.UnitQuantity == 1), "Service and membership catalog units represent one entitlement.", 400); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var seller = await OwnedSeller(actor, ct); seller = await LockSeller(seller.Id, ct, true); Require(Eligible(seller), "Approved seller required.", 403);
        Require(await db.Categories.AnyAsync(x => x.Id == request.CategoryId && x.IsActive, ct), "Active category required.", 400);
        Product p;
        if (id is null) { p = new Product { SellerId = seller.Id }; db.Products.Add(p); }
        else p = await db.Products.SingleOrDefaultAsync(x => x.Id == id && x.SellerId == seller.Id, ct) ?? throw Missing();
        // Existing variants are immutable once referenced: changing units would change campaign meaning.
        var variant = id is null ? null : await db.Variants.SingleOrDefaultAsync(x => x.ProductId == p.Id && x.IsActive, ct);
        if (variant is not null && p.ProductType != request.ProductType)
            Require(!await db.Inventories.AnyAsync(i => i.ProductVariantId == variant.Id && i.AvailableQuantity > 0, ct), "Clear physical stock before changing the catalog offer type.");
        if (variant is not null && (variant.UnitOfMeasure != request.UnitOfMeasure || variant.UnitQuantity != request.UnitQuantity))
            Require(!await db.Inventories.AnyAsync(x => x.ProductVariantId == variant.Id && x.AvailableQuantity > 0, ct), "Declare zero inventory before changing variant units.");
        if (variant is not null) Require(!await db.Campaigns.AnyAsync(x => x.ProductVariantId == variant.Id, ct), "Product variants referenced by campaigns cannot be edited.");
        p.CategoryId = request.CategoryId; p.Name = request.Name; p.Slug = request.Slug; p.Description = request.Description; p.ProductType = request.ProductType; p.UpdatedAt = clock.GetUtcNow();
        if (variant is null) { variant = new ProductVariant { SellerId = seller.Id, ProductId = p.Id }; db.Variants.Add(variant); }
        variant.SKU = request.SKU; variant.VariantName = request.VariantName; variant.UnitOfMeasure = request.UnitOfMeasure; variant.UnitQuantity = request.UnitQuantity; variant.AttributesJson = request.AttributesJson;
        Require(request.UnitOfMeasure is not (UnitOfMeasure.Piece or UnitOfMeasure.Pack) || request.UnitQuantity == decimal.Truncate(request.UnitQuantity), "Discrete unit quantities must be whole.", 400);
        Audit(actor, id is null ? "ProductCreated" : "ProductUpdated", p); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return await ProductDto(p, ct);
    }
    public async Task<PageResponse<CampaignResponse>> Campaigns(PageRequest page, CancellationToken ct)
    {
        Page(page); var q = PublicCampaigns; return new((await q.OrderBy(x => x.Id).Skip(page.Offset).Take(page.PageSize).ToListAsync(ct)).Select(CampaignDto).ToArray(), page.Page, page.PageSize, await q.CountAsync(ct));
    }
    public async Task<CampaignResponse> Campaign(Guid id, CancellationToken ct) => CampaignDto(await PublicCampaigns.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw Missing());
    private async Task ValidateCampaignResources(Seller seller, CampaignRequest request, CancellationToken ct)
    {
        Require(Eligible(seller), "Approved seller required.", 403);
        var variant = await db.Variants.SingleOrDefaultAsync(x => x.Id == request.ProductVariantId && x.IsActive, ct) ?? throw Missing();
        Require(await db.Products.AnyAsync(x => x.Id == variant.ProductId && x.SellerId == seller.Id && x.Status == ResourceStatus.Active && db.Categories.Any(c => c.Id == x.CategoryId && c.IsActive), ct), "Product must belong to seller and use an active category.", 403);
        Require(await db.Locations.AnyAsync(x => x.Id == request.SellerLocationId && x.SellerId == seller.Id && x.IsActive && x.IsPickupEnabled, ct), "Active seller pickup location required.", 400);
        Require(variant.UnitOfMeasure is not (UnitOfMeasure.Piece or UnitOfMeasure.Pack) || new[] { request.TargetQuantity, request.MinimumReservationQuantity, request.MaximumReservationQuantity }.All(x => x == decimal.Truncate(x)), "Discrete units require whole quantities.", 400);
        Require(request.ExpiresAtUtc > clock.GetUtcNow(), "Expiry must be in the future.", 400);
    }
    public async Task<CampaignResponse> SaveCampaign(Actor actor, Guid? id, CampaignRequest request, CancellationToken ct)
    {
        Validate.With(request, new CampaignValidator()); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var own = await OwnedSeller(actor, ct); var s = await LockSeller(own.Id, ct); await ValidateCampaignResources(s, request, ct);
        Require(!await PhysicalVariant(request.ProductVariantId, ct) || await FreeInventory(request.ProductVariantId, ct) >= request.TargetQuantity, "Declare sufficient uncommitted inventory before creating a campaign.");
        GroupBuyCampaign c;
        if (id is null) { c = new GroupBuyCampaign { SellerId = s.Id }; db.Campaigns.Add(c); }
        else { c = await LockCampaign(id.Value, ct); Require(c.SellerId == s.Id, "Resource not found.", 404); Require(c.Status == CampaignStatus.Draft, "Only drafts can be edited."); }
        Require(!c.UsesTierPricing, "Configure a new draft before editing tier campaign resources.");
        if (!await PhysicalVariant(request.ProductVariantId, ct))
            Require(request.MinimumReservationQuantity == 1 && request.MaximumReservationQuantity == 1, "Service and membership offers use one entitlement per customer.", 400);
        c.ProductVariantId = request.ProductVariantId; c.SellerLocationId = request.SellerLocationId; c.Title = request.Title; c.Description = request.Description;
        c.ReferenceUnitPrice = request.ReferenceUnitPrice; c.GroupUnitPrice = request.GroupUnitPrice; c.TargetQuantity = request.TargetQuantity; c.MinimumReservationQuantity = request.MinimumReservationQuantity; c.MaximumReservationQuantity = request.MaximumReservationQuantity; c.StartsAtUtc = request.StartsAtUtc; c.ExpiresAtUtc = request.ExpiresAtUtc; c.UpdatedAt = clock.GetUtcNow();
        Audit(actor, id is null ? "CampaignCreated" : "CampaignUpdated", c); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return CampaignDto(c);
    }
    public async Task<CampaignResponse> Submit(Actor actor, Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct); var own = await OwnedSeller(actor, ct); var seller = await LockSeller(own.Id, ct);
        var c = await LockCampaign(id, ct); Require(c.SellerId == seller.Id, "Resource not found.", 404); Require(Eligible(seller), "Approved seller required.", 403); Require(c.ExpiresAtUtc > clock.GetUtcNow(), "Campaign deadline passed.");
        if (c.Status == CampaignStatus.PendingApproval) { await tx.CommitAsync(ct); return CampaignDto(c); }
        Require(c.Status == CampaignStatus.Draft, "Only drafts can be submitted.");
        Require((!config.GetValue("Pilot:Enabled", false) && await PhysicalVariant(c.ProductVariantId, ct)) || c.UsesTierPricing, "Pilot campaigns require tiers and paid checkout.");
        await CheckOperationalResources(seller, c, ct);
        Require(!await PhysicalVariant(c.ProductVariantId, ct) || await FreeInventory(c.ProductVariantId, ct) >= c.TargetQuantity, "Insufficient uncommitted inventory.");
        c.CommittedQuantity = c.TargetQuantity;
        c.TransitionTo(CampaignStatus.PendingApproval); Audit(actor, "CampaignSubmitted", c); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return CampaignDto(c);
    }
    public async Task<CampaignResponse> Transition(Actor actor, Guid id, TransitionRequest request, CancellationToken ct)
    {
        // Legacy route remains, but cannot bypass confirmation, deadlines or pickup evidence.
        if (request.Status == CampaignStatus.Confirmed)
            return (await Decide(actor, id, new($"legacy-{id:N}-{request.Status}", null), true, ct)).Campaign;
        if (request.Status == CampaignStatus.Failed) return await ReportFulfillmentFailure(actor, id, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct); var seller = await OwnedSeller(actor, ct); seller = await LockSeller(seller.Id, ct); Require(Eligible(seller), "Approved seller required.", 403);
        var c = await LockCampaign(id, ct); Require(c.SellerId == seller.Id, "Resource not found.", 404);
        Require(request.Status is CampaignStatus.AwaitingSellerConfirmation or CampaignStatus.FulfillmentInProgress or CampaignStatus.Completed, "Unsupported seller transition.", 400);
        if (c.Status == CampaignStatus.AwaitingSellerConfirmation && request.Status == c.Status) { await tx.CommitAsync(ct); return CampaignDto(c); }
        if (request.Status == CampaignStatus.Completed)
            Require(await db.Pickups.AnyAsync(p => p.CampaignId == id, ct) && !await db.Pickups.AnyAsync(p => p.CampaignId == id && p.Status != PickupStatus.Collected, ct), "Every pickup must be verified before successful completion.");
        c.TransitionTo(request.Status);
        Audit(actor, $"Campaign{request.Status}", c); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return CampaignDto(c);
    }
    public async Task<ReservationResponse> Reserve(Actor actor, Guid id, ReservationRequest request, CancellationToken ct)
    {
        Validate.With(request, new ReservationValidator()); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var sellerId = await db.Campaigns.Where(x => x.Id == id).Select(x => (Guid?)x.SellerId).SingleOrDefaultAsync(ct) ?? throw Missing();
        var seller = await LockSeller(sellerId, ct); var c = await LockCampaign(id, ct);
        Require(!c.UsesTierPricing && await PhysicalVariant(c.ProductVariantId, ct), "Use paid checkout for tier and nonphysical campaigns.");
        var existing = await db.Reservations.SingleOrDefaultAsync(x => x.CampaignId == id && x.CustomerUserId == actor.UserId && x.IdempotencyKey == request.IdempotencyKey, ct);
        if (existing is not null) { Require(existing.Quantity == request.Quantity, "Idempotency key was used with another quantity."); await tx.CommitAsync(ct); return ReservationDto(existing, c); }
        Require(Eligible(seller), "Seller is unavailable.");
        await CheckOperationalResources(seller, c, ct);
        Require(c.CommittedQuantity >= c.ReservedQuantity + request.Quantity && await FreeInventory(c.ProductVariantId, ct) >= 0 && await db.Inventories.AnyAsync(i => i.ProductVariantId == c.ProductVariantId, ct), "Seller must declare inventory covering this campaign.");
        var variant = await db.Variants.SingleAsync(x => x.Id == c.ProductVariantId, ct);
        Require(variant.IsActive && await PublicProducts.AnyAsync(x => x.Id == variant.ProductId, ct), "Product is unavailable.");
        CampaignRules.ValidateReservation(c, request.Quantity, variant.UnitOfMeasure, clock.GetUtcNow());
        var r = new GroupBuyReservation { CampaignId = id, CustomerUserId = actor.UserId, Quantity = request.Quantity, UnitPriceSnapshot = c.GroupUnitPrice, TotalPriceSnapshot = decimal.Round(request.Quantity * c.GroupUnitPrice, 2, MidpointRounding.AwayFromZero), IdempotencyKey = request.IdempotencyKey };
        db.Reservations.Add(r); c.ReservedQuantity += r.Quantity; c.UpdatedAt = clock.GetUtcNow();
        Notify(actor.UserId, NotificationType.ReservationCreated, r.Id);
        if (c.ReservedQuantity == c.TargetQuantity)
        {
            c.TransitionTo(CampaignStatus.TargetReached); Audit(actor, "TargetReached", c);
            c.TransitionTo(CampaignStatus.AwaitingSellerConfirmation);
            c.ConfirmationDeadlineUtc = clock.GetUtcNow().AddMinutes(config.GetValue("Operations:ConfirmationMinutes", 60));
            Notify(seller.OwnerUserId, NotificationType.CampaignTargetReached, c.Id);
            Notify(seller.OwnerUserId, NotificationType.SellerConfirmationRequired, c.Id);
        }
        Audit(actor, "ReservationCreated", r); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return ReservationDto(r, c);
    }
    public async Task<PageResponse<ReservationResponse>> Reservations(Actor actor, PageRequest page, CancellationToken ct)
    {
        Page(page); var q = db.Reservations.Where(x => x.CustomerUserId == actor.UserId); var total = await q.CountAsync(ct);
        var items = new List<ReservationResponse>(); foreach (var r in await q.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Skip(page.Offset).Take(page.PageSize).ToListAsync(ct)) items.Add(ReservationDto(r, await db.Campaigns.SingleAsync(x => x.Id == r.CampaignId, ct)));
        return new(items, page.Page, page.PageSize, total);
    }
    public async Task<ReservationResponse> Cancel(Actor actor, Guid id, CancellationToken ct)
    {
        var campaignId = await db.Reservations.Where(x => x.Id == id && x.CustomerUserId == actor.UserId).Select(x => (Guid?)x.CampaignId).SingleOrDefaultAsync(ct) ?? throw Missing();
        await using var tx = await db.Database.BeginTransactionAsync(ct); var c = await LockCampaign(campaignId, ct);
        Require(!c.UsesTierPricing, "Use payment order cancellation for tier campaigns.");
        var r = await db.Reservations.SingleAsync(x => x.Id == id && x.CustomerUserId == actor.UserId, ct);
        if (r.Status == ReservationStatus.Cancelled) { await tx.CommitAsync(ct); return ReservationDto(r, c); }
        Require(r.Status == ReservationStatus.Active && c.Status is CampaignStatus.Open or CampaignStatus.TargetReached && clock.GetUtcNow() < c.ExpiresAtUtc, "Cancellation window is closed.");
        r.Status = ReservationStatus.Cancelled; r.CancelledAt = r.UpdatedAt = clock.GetUtcNow(); c.ReservedQuantity -= r.Quantity; c.UpdatedAt = clock.GetUtcNow();
        if (c.Status == CampaignStatus.TargetReached) c.TransitionTo(CampaignStatus.Open);
        Audit(actor, "ReservationCancelled", r); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return ReservationDto(r, c);
    }
    public async Task<PageResponse<SellerReviewResponse>> PendingSellers(Actor actor, PageRequest page, CancellationToken ct)
    {
        Admin(actor); Page(page); var q = db.Sellers.Where(x => x.VerificationStatus == VerificationStatus.Pending); var total = await q.CountAsync(ct); var items = new List<SellerReviewResponse>();
        foreach (var s in await q.OrderBy(x => x.Id).Skip(page.Offset).Take(page.PageSize).ToListAsync(ct)) items.Add(await ReviewDto(s, ct)); return new(items, page.Page, page.PageSize, total);
    }
    public async Task<SellerReviewResponse> ReviewSeller(Actor actor, Guid id, bool approve, ReviewRequest request, CancellationToken ct)
    {
        Require(approve || !string.IsNullOrWhiteSpace(request.Reason), "A rejection reason is required.", 400);
        Admin(actor); Require(request.Reason?.Length is null or <= 500, "Reason is too long.", 400); await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.Sellers.AnyAsync(x => x.Id == id, ct)) throw Missing(); var s = await LockSeller(id, ct, true);
        Require(s.VerificationStatus == VerificationStatus.Pending, "Seller is not awaiting review.");
        Require(!approve || s.SellerType != SellerType.Grocery || !string.IsNullOrWhiteSpace(s.FssaiRegistrationNumber), "Record applicable grocery registration details before approval.", 400);
        s.VerificationStatus = approve ? VerificationStatus.Approved : VerificationStatus.Rejected; s.ReviewedAt = s.UpdatedAt = clock.GetUtcNow(); s.ReviewerUserId = actor.UserId;
        var owner = await users.FindByIdAsync(s.OwnerUserId.ToString()) ?? throw Missing();
        var alreadySeller = await users.IsInRoleAsync(owner, "Seller");
        if (approve != alreadySeller)
        {
            var result = approve ? await users.AddToRoleAsync(owner, "Seller") : await users.RemoveFromRoleAsync(owner, "Seller");
            Require(result.Succeeded, "Could not update seller role.");
        }
        Audit(actor, approve ? "SellerApproved" : "SellerRejected", s, request.Reason); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return await ReviewDto(s, ct);
    }
    public async Task<PageResponse<CampaignResponse>> PendingCampaigns(Actor actor, PageRequest page, CancellationToken ct)
    {
        Admin(actor); Page(page); var q = db.Campaigns.Where(x => x.Status == CampaignStatus.PendingApproval); return new((await q.OrderBy(x => x.Id).Skip(page.Offset).Take(page.PageSize).ToListAsync(ct)).Select(CampaignDto).ToArray(), page.Page, page.PageSize, await q.CountAsync(ct));
    }
    public async Task<CampaignResponse> ReviewCampaign(Actor actor, Guid id, bool approve, ReviewRequest request, CancellationToken ct)
    {
        Require(approve || !string.IsNullOrWhiteSpace(request.Reason), "A rejection reason is required.", 400);
        Admin(actor); Require(request.Reason?.Length is null or <= 500, "Reason is too long.", 400); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var sellerId = await db.Campaigns.Where(x => x.Id == id).Select(x => (Guid?)x.SellerId).SingleOrDefaultAsync(ct) ?? throw Missing(); var s = await LockSeller(sellerId, ct); var c = await LockCampaign(id, ct);
        Require(c.Status == CampaignStatus.PendingApproval, "Campaign is not awaiting approval.");
        if (approve) { Require(Eligible(s), "Seller approval required."); Require(c.ExpiresAtUtc > clock.GetUtcNow(), "Campaign deadline passed."); await CheckOperationalResources(s, c, ct); Require(c.CommittedQuantity == c.TargetQuantity, "Submit an inventory-backed campaign first."); }
        else c.CommittedQuantity = 0;
        c.TransitionTo(!approve ? CampaignStatus.Draft : c.StartsAtUtc > clock.GetUtcNow() ? CampaignStatus.Scheduled : CampaignStatus.Open);
        await NotifyDemandOffer(c, ct);
        Audit(actor, approve ? "CampaignApproved" : "CampaignRejected", c, request.Reason); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return CampaignDto(c);
    }
    public async Task Process(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var ids = await DueCampaigns(now).Where(x => x.WorkerFailureCount < config.GetValue("Operations:WorkerMaxAttempts", 10) && (x.WorkerRetryAtUtc == null || x.WorkerRetryAtUtc <= now)).OrderBy(x => x.ExpiresAtUtc).ThenBy(x => x.Id).Take(100).Select(x => x.Id).ToListAsync(ct);
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<Marketplace>().ProcessOne(id, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogError("Campaign processing failed {CampaignId} {ErrorType}; scheduling bounded retry", id, ex.GetType().Name);
                try
                {
                    await using var recovery = scopes.CreateAsyncScope();
                    var recoveryDb = recovery.ServiceProvider.GetRequiredService<MarketplaceDb>(); recoveryDb.Database.SetCommandTimeout(5);
                    var failures = await recoveryDb.Campaigns.Where(c => c.Id == id).Select(c => c.WorkerFailureCount).SingleAsync(ct);
                    var retry = clock.GetUtcNow().AddSeconds(Math.Min(900, 15 * Math.Pow(2, Math.Min(failures, 6))));
                    var recoveryService = recovery.ServiceProvider.GetRequiredService<Marketplace>();
                    var scheduled = await recoveryService.DueCampaigns(clock.GetUtcNow()).Where(c => c.Id == id && c.WorkerFailureCount < config.GetValue("Operations:WorkerMaxAttempts", 10)).ExecuteUpdateAsync(s => s.SetProperty(c => c.WorkerFailureCount, c => c.WorkerFailureCount + 1).SetProperty(c => c.WorkerRetryAtUtc, retry).SetProperty(c => c.WorkerErrorCode, "processing_failed"), ct);
                    var maxAttempts = config.GetValue("Operations:WorkerMaxAttempts", 10);
                    if (scheduled > 0)
                    {
                        if (failures + 1 >= maxAttempts) metrics.RecordCampaignRetryExhaustion();
                        else metrics.RecordCampaignRetry(ex.GetType().Name);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception recoveryError) { logger.LogError("Campaign retry persistence failed {CampaignId} {ErrorType}", id, recoveryError.GetType().Name); }
            }
            finally { workerHealth.Progress(clock.GetUtcNow()); }
        }
    }
}

using GroupBuy.Application;
using GroupBuy.Domain;
using Microsoft.EntityFrameworkCore;

namespace GroupBuy.Infrastructure;

public sealed partial class Marketplace
{
    public async Task<SellerPrivateProfileResponse> SellerProfile(Actor actor, CancellationToken ct)
    {
        // No supplied seller ID and no Seller-role requirement: onboarding owners need this read too.
        var s = await db.Sellers.AsNoTracking().SingleOrDefaultAsync(s => s.OwnerUserId == actor.UserId, ct) ?? throw Missing();
        var locations = await db.Locations.AsNoTracking().Where(l => l.SellerId == s.Id).OrderBy(l => l.Id)
            .Select(l => new PrivateSellerLocationResponse(l.Id, l.AddressLine1, l.AddressLine2, l.Area, l.City, l.State, l.PostalCode, l.Latitude, l.Longitude, l.IsActive, l.IsPickupEnabled)).ToListAsync(ct);
        return new(s.Id, s.BusinessName, s.SellerType, s.Description, s.VerificationStatus, s.Status, s.ContactEmail, s.ContactPhone, s.FssaiRegistrationNumber, s.ReviewedAt, locations);
    }

    private async Task<SellerProductResponse> SellerProductDto(Product p, CancellationToken ct)
    {
        var dto = await ProductDto(p, ct);
        return new(dto.Id, dto.SellerId, dto.CategoryId, dto.Name, dto.Slug, dto.Description, dto.ProductType, dto.Variants, p.Status);
    }

    public async Task<PageResponse<SellerProductResponse>> SellerProducts(Actor actor, PageRequest page, ResourceStatus? status, CancellationToken ct)
    {
        Page(page); Require(status is null || Enum.IsDefined(status.Value), "Unknown product status.", 400);
        var seller = await OwnedSeller(actor, ct);
        var q = db.Products.AsNoTracking().Where(p => p.SellerId == seller.Id && (status == null || p.Status == status));
        var items = new List<SellerProductResponse>();
        foreach (var p in await q.OrderBy(p => p.Id).Skip(page.Offset).Take(page.PageSize).ToListAsync(ct)) items.Add(await SellerProductDto(p, ct));
        return new(items, page.Page, page.PageSize, await q.CountAsync(ct));
    }

    public async Task<SellerProductResponse> SetProductStatus(Actor actor, Guid id, ProductStatusRequest request, CancellationToken ct)
    {
        Validate.With(request, new ProductStatusValidator());
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var seller = await OwnedSeller(actor, ct); seller = await LockSeller(seller.Id, ct);
        Require(Eligible(seller), "Approved seller required.", 403);
        var p = await db.Products.SingleOrDefaultAsync(p => p.Id == id && p.SellerId == seller.Id, ct) ?? throw Missing();
        await db.Entry(p).ReloadAsync(ct);
        Require(Enum.IsDefined(p.Status), "Current product status is invalid.");
        var next = request.Status!.Value;
        if (p.Status != next)
        {
            var campaigns = db.Campaigns.Where(c => db.Variants.Any(v => v.Id == c.ProductVariantId && v.ProductId == p.Id));
            if (next == ResourceStatus.Inactive)
                Require(!await campaigns.AnyAsync(c => c.CommittedQuantity > 0 ||
                    c.Status == CampaignStatus.PendingApproval || c.Status == CampaignStatus.Scheduled || c.Status == CampaignStatus.Open ||
                    c.Status == CampaignStatus.TargetReached || c.Status == CampaignStatus.AwaitingSellerConfirmation || c.Status == CampaignStatus.Confirmed || c.Status == CampaignStatus.FulfillmentInProgress ||
                    db.Reservations.Any(r => r.CampaignId == c.Id && r.Status == ReservationStatus.Active) ||
                    db.Pickups.Any(pickup => pickup.CampaignId == c.Id && (pickup.Status == PickupStatus.Pending || pickup.Status == PickupStatus.ReadyForPickup)), ct),
                    "Product supports a live campaign or outstanding fulfillment. Resolve it before deactivation.");
            else
            {
                Require(await db.Categories.AnyAsync(c => c.Id == p.CategoryId && c.IsActive, ct), "Active category required for activation.");
                Require(await db.Variants.AnyAsync(v => v.ProductId == p.Id && v.IsActive, ct), "An active variant is required for activation.");
            }
            p.Status = next; p.UpdatedAt = clock.GetUtcNow(); Audit(actor, next == ResourceStatus.Active ? "ProductActivated" : "ProductDeactivated", p);
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct); return await SellerProductDto(p, ct);
    }

    public async Task<PickupDetailResponse> SellerPickup(Actor actor, Guid id, CancellationToken ct)
    {
        var pickup = await db.Pickups.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id && db.Sellers.Any(s => s.Id == p.SellerId && s.OwnerUserId == actor.UserId), ct) ?? throw Missing();
        // Reuse the allowlisted operational DTO; never serialize the record with code/attempt/customer fields.
        return await PickupDto(pickup, ct);
    }
}

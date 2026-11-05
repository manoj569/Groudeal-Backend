using System.Text.Json;
using GroupBuy.Application;
using GroupBuy.Domain;
using Microsoft.EntityFrameworkCore;

namespace GroupBuy.Infrastructure;

public sealed partial class Marketplace
{
    private static string Search(string? value) { Require(value is null || value.Length <= 100, "Search/filter is limited to 100 characters.", 400); return value?.Trim().ToLowerInvariant() ?? ""; }
    private static void Status<T>(T? value) where T : struct, Enum => Require(value is null || Enum.IsDefined(value.Value), "Unknown status.", 400);
    public async Task<AdminSummaryResponse> AdminSummary(Actor actor, CancellationToken ct)
    {
        Admin(actor);
        return new(await db.Sellers.CountAsync(ct), await db.Sellers.CountAsync(s => s.VerificationStatus == VerificationStatus.Pending, ct), await db.Sellers.CountAsync(s => s.VerificationStatus == VerificationStatus.Approved, ct),
            await db.Campaigns.CountAsync(c => c.Status == CampaignStatus.PendingApproval, ct),
            await db.Campaigns.CountAsync(c => c.Status == CampaignStatus.Scheduled || c.Status == CampaignStatus.Open || c.Status == CampaignStatus.TargetReached || c.Status == CampaignStatus.AwaitingSellerConfirmation || c.Status == CampaignStatus.Confirmed || c.Status == CampaignStatus.FulfillmentInProgress, ct),
            await db.Campaigns.CountAsync(c => c.Status == CampaignStatus.AwaitingSellerConfirmation, ct), await db.Campaigns.CountAsync(c => c.Status == CampaignStatus.Expired, ct),
            await db.Reservations.CountAsync(ct), await db.Pickups.CountAsync(p => p.Status == PickupStatus.Pending || p.Status == PickupStatus.ReadyForPickup, ct));
    }
    private async Task<AdminSellerResponse> AdminSellerDto(Seller s, CancellationToken ct) => new(s.Id, s.OwnerUserId, s.BusinessName, s.SellerType, s.VerificationStatus, s.Status, s.ContactEmail, s.ContactPhone, s.FssaiRegistrationNumber, s.ReviewedAt,
        await db.Locations.AsNoTracking().Where(l => l.SellerId == s.Id).OrderBy(l => l.Id).Select(l => new PrivateSellerLocationResponse(l.Id, l.AddressLine1, l.AddressLine2, l.Area, l.City, l.State, l.PostalCode, l.Latitude, l.Longitude, l.IsActive, l.IsPickupEnabled)).ToListAsync(ct));
    public async Task<PageResponse<AdminSellerResponse>> AdminSellers(Actor actor, PageRequest page, VerificationStatus? status, string? search, CancellationToken ct)
    {
        Admin(actor); Page(page); Status(status); var term = Search(search);
        var q = db.Sellers.AsNoTracking().Where(s => (status == null || s.VerificationStatus == status) && (term == "" || s.BusinessName.ToLower().Contains(term)));
        var items = new List<AdminSellerResponse>(); foreach (var s in await q.OrderBy(s => s.Id).Skip(page.Offset).Take(page.PageSize).ToListAsync(ct)) items.Add(await AdminSellerDto(s, ct));
        return new(items, page.Page, page.PageSize, await q.CountAsync(ct));
    }
    public async Task<AdminSellerResponse> AdminSeller(Actor actor, Guid id, CancellationToken ct) { Admin(actor); return await AdminSellerDto(await db.Sellers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, ct) ?? throw Missing(), ct); }

    private IQueryable<AdminCampaignResponse> AdminCampaignQuery(IQueryable<GroupBuyCampaign> campaigns) => from c in campaigns.AsNoTracking()
        join s in db.Sellers on c.SellerId equals s.Id
        join v in db.Variants on c.ProductVariantId equals v.Id
        join p in db.Products on v.ProductId equals p.Id
        orderby c.Id
        select new AdminCampaignResponse(new OperationCampaignResponse(new CampaignResponse(c.Id, c.SellerId, c.ProductVariantId, c.SellerLocationId, c.Title, c.Description, c.ReferenceUnitPrice, c.GroupUnitPrice, c.TargetQuantity, c.ReservedQuantity,
            c.TargetQuantity == 0 ? 0 : decimal.Round(c.ReservedQuantity / c.TargetQuantity * 100, 2), c.MinimumReservationQuantity, c.MaximumReservationQuantity, c.StartsAtUtc, c.ExpiresAtUtc, c.Status, c.UsesTierPricing), c.CommittedQuantity, c.FulfilledQuantity, c.ConfirmationDeadlineUtc, c.PickupDeadlineUtc, c.DecisionAtUtc, c.DecisionReason),
            s.BusinessName, s.VerificationStatus, p.Id, p.Name, v.VariantName, v.SKU, v.UnitOfMeasure, v.UnitQuantity, c.WorkerFailureCount, c.WorkerRetryAtUtc, c.WorkerErrorCode == null ? null : "processing_failed");
    public async Task<PageResponse<AdminCampaignResponse>> AdminCampaigns(Actor actor, PageRequest page, CampaignStatus? status, string? search, bool failedOnly, CancellationToken ct)
    {
        Admin(actor); Page(page); Status(status); var term = Search(search);
        var ids = db.Campaigns.Where(c => (status == null || c.Status == status) && (!failedOnly || c.WorkerFailureCount > 0) &&
            (term == "" || c.Title.ToLower().Contains(term) || db.Sellers.Any(s => s.Id == c.SellerId && s.BusinessName.ToLower().Contains(term))));
        return new(await AdminCampaignQuery(ids.OrderBy(c => c.Id).Skip(page.Offset).Take(page.PageSize)).ToListAsync(ct), page.Page, page.PageSize, await ids.CountAsync(ct));
    }
    public async Task<AdminCampaignResponse> AdminCampaign(Actor actor, Guid id, CancellationToken ct) { Admin(actor); return await AdminCampaignQuery(db.Campaigns.Where(c => c.Id == id)).SingleOrDefaultAsync(ct) ?? throw Missing(); }

    public async Task<PageResponse<AdminCategoryResponse>> AdminCategories(Actor actor, PageRequest page, bool? active, string? search, CancellationToken ct)
    {
        Admin(actor); Page(page); var term = Search(search); var q = db.Categories.AsNoTracking().Where(c => (active == null || c.IsActive == active) && (term == "" || c.Name.ToLower().Contains(term)));
        return new(await q.OrderBy(c => c.SortOrder).ThenBy(c => c.Id).Skip(page.Offset).Take(page.PageSize).Select(c => new AdminCategoryResponse(c.Id, c.ParentCategoryId, c.Name, c.Slug, c.IsActive, c.SortOrder)).ToListAsync(ct), page.Page, page.PageSize, await q.CountAsync(ct));
    }
    public async Task<PageResponse<SellerProductResponse>> AdminProducts(Actor actor, PageRequest page, ResourceStatus? status, string? search, CancellationToken ct)
    {
        Admin(actor); Page(page); Status(status); var term = Search(search); var q = db.Products.AsNoTracking().Where(p => (status == null || p.Status == status) && (term == "" || p.Name.ToLower().Contains(term)));
        var items = new List<SellerProductResponse>(); foreach (var p in await q.OrderBy(p => p.Id).Skip(page.Offset).Take(page.PageSize).ToListAsync(ct)) items.Add(await SellerProductDto(p, ct));
        return new(items, page.Page, page.PageSize, await q.CountAsync(ct));
    }
    public async Task<PageResponse<AdminReservationResponse>> AdminReservations(Actor actor, PageRequest page, ReservationStatus? status, Guid? campaignId, CancellationToken ct)
    {
        Admin(actor); Page(page); Status(status);
        var q = from r in db.Reservations.AsNoTracking() join c in db.Campaigns on r.CampaignId equals c.Id where (status == null || r.Status == status) && (campaignId == null || r.CampaignId == campaignId)
            select new { Reservation = r, c.SellerId };
        return new(await q.OrderByDescending(row => row.Reservation.CreatedAt).ThenBy(row => row.Reservation.Id).Skip(page.Offset).Take(page.PageSize)
            .Select(row => new AdminReservationResponse(row.Reservation.Id, row.Reservation.CampaignId, row.SellerId, row.Reservation.Quantity, row.Reservation.UnitPriceSnapshot, row.Reservation.TotalPriceSnapshot, row.Reservation.Status, row.Reservation.CreatedAt)).ToListAsync(ct), page.Page, page.PageSize, await q.CountAsync(ct));
    }
    public async Task<PageResponse<AdminPickupResponse>> AdminPickups(Actor actor, PageRequest page, PickupStatus? status, Guid? campaignId, CancellationToken ct)
    {
        Admin(actor); Page(page); Status(status);
        var q = from p in db.Pickups.AsNoTracking() join r in db.Reservations on p.ReservationId equals r.Id where (status == null || p.Status == status) && (campaignId == null || p.CampaignId == campaignId)
            select new { Pickup = p, r.Quantity };
        return new(await q.OrderBy(row => row.Pickup.DeadlineUtc).ThenBy(row => row.Pickup.Id).Skip(page.Offset).Take(page.PageSize)
            .Select(row => new AdminPickupResponse(row.Pickup.Id, row.Pickup.ReservationId, row.Pickup.CampaignId, row.Pickup.SellerId, row.Pickup.Status, row.Quantity, row.Pickup.DeadlineUtc, row.Pickup.CollectedAtUtc)).ToListAsync(ct), page.Page, page.PageSize, await q.CountAsync(ct));
    }

    // Never return arbitrary JSON from historical/operator-written audit rows.
    private static IReadOnlyDictionary<string, string> SafeAuditMetadata(AuditLog row)
    {
        if (row.Action is not ("SellerApproved" or "SellerRejected" or "CampaignApproved" or "CampaignRejected" or "CampaignFailed")) return new Dictionary<string, string>();
        try
        {
            using var json = JsonDocument.Parse(row.SafeMetadataJson);
            if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String && reason.GetString() is { Length: <= 500 } text)
                return new Dictionary<string, string> { ["reason"] = text };
        }
        catch (JsonException) { }
        return new Dictionary<string, string>();
    }
    public async Task<PageResponse<AdminAuditResponse>> AdminAudit(Actor actor, PageRequest page, string? action, string? entityType, Guid? actorId, Guid? entityId, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, CancellationToken ct)
    {
        Admin(actor); Page(page); _ = Search(action); _ = Search(entityType);
        Require(fromUtc is null || fromUtc.Value.Offset == TimeSpan.Zero, "fromUtc must use UTC.", 400); Require(toUtc is null || toUtc.Value.Offset == TimeSpan.Zero, "toUtc must use UTC.", 400);
        Require(fromUtc == null || toUtc == null || fromUtc <= toUtc, "Invalid audit time range.", 400);
        var q = db.AuditLogs.AsNoTracking().Where(a => (action == null || a.Action == action) && (entityType == null || a.EntityType == entityType) && (actorId == null || a.ActorUserId == actorId) && (entityId == null || a.EntityId == entityId) && (fromUtc == null || a.TimestampUtc >= fromUtc) && (toUtc == null || a.TimestampUtc <= toUtc));
        var rows = await (from a in q join u in db.UsersProfile on a.ActorUserId equals u.Id into users from u in users.DefaultIfEmpty() orderby a.TimestampUtc descending, a.Id
            select new { Row = a, ActorName = u == null ? null : u.DisplayName }).Skip(page.Offset).Take(page.PageSize).ToListAsync(ct);
        return new(rows.Select(r => new AdminAuditResponse(r.Row.Id, r.Row.TimestampUtc, r.Row.ActorUserId, r.ActorName, r.Row.Action, r.Row.EntityType, r.Row.EntityId, r.Row.CorrelationId, SafeAuditMetadata(r.Row))).ToArray(), page.Page, page.PageSize, await q.CountAsync(ct));
    }
}

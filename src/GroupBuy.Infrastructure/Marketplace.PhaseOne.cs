using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GroupBuy.Application;
using GroupBuy.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GroupBuy.Infrastructure;

public sealed partial class Marketplace : IPhaseOne
{
    private void Pilot() => Require(config.GetValue("Pilot:Enabled", false), "Pilot is not activated.", 503);
    private ServiceAreaOption Area(string id) => PilotConfiguration.Areas(config).SingleOrDefault(a => a.Id == id) ?? throw new BusinessException(400, "unsupported_area", "Choose a configured service area.");
    private async Task Advisory(string key, CancellationToken ct) => _ = await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key},0))", ct);
    private static string Digest(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public PilotOptions Options() => new(config.GetValue("Pilot:Enabled", false), gateway.Available, gateway.Sandbox, gateway.Name, gateway.CheckoutKeyId, PilotConfiguration.Areas(config), config.GetValue<long?>("Pilot:DeliveryFeeMinor"));

    public async Task<InterestResponse> Interest(Actor actor, InterestRequest request, bool withdraw, CancellationToken ct)
    {
        if (!withdraw) { Pilot(); _ = Area(request.ServiceArea); Require(request.DesiredQuantity is > 0 and <= 1000000 && decimal.Round(request.DesiredQuantity, 3) == request.DesiredQuantity, "Invalid interest quantity.", 400); Require(await PublicProducts.AnyAsync(p => p.Id == request.ProductId, ct), "Resource not found.", 404); }
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var product = await db.Products.SingleOrDefaultAsync(p => p.Id == request.ProductId, ct);
        var demand = product is null ? null : await DemandPersistence.Product(db, clock, product, request.ServiceArea, actor.UserId, actor.CorrelationId, !withdraw, ct);
        if (!withdraw && demand is { IsOpen: false }) throw new BusinessException(409, "demand_closed", "Demand is closed.");
        await Advisory($"interest:{actor.UserId}:{request.ProductId}:{request.ServiceArea}", ct);
        var interest = await db.Interests.SingleOrDefaultAsync(x => x.CustomerId == actor.UserId && x.ProductId == request.ProductId && x.ServiceArea == request.ServiceArea, ct);
        if (interest is null) { Require(!withdraw, "Resource not found.", 404); interest = new() { CustomerId = actor.UserId, ProductId = request.ProductId, ServiceArea = request.ServiceArea }; db.Interests.Add(interest); }
        var changed = db.Entry(interest).State == EntityState.Added || interest.Active == withdraw || (!withdraw && (interest.DesiredQuantity != request.DesiredQuantity || interest.RequestedUnit is not null));
        if (demand is not null) { interest.DemandId = demand.Id; if (changed) { demand.UpdatedAt = clock.GetUtcNow(); DemandPersistence.Audit(db, actor.UserId, actor.CorrelationId, demand.Id, withdraw ? "DiscoveryDemandWithdrawn" : "DiscoveryDemandJoined"); } }
        if (!withdraw) { interest.DesiredQuantity = request.DesiredQuantity; interest.RequestedQuantity = null; interest.RequestedUnit = null; }
        interest.Active = !withdraw; interest.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return await InterestDto(interest, ct);
    }
    private async Task<InterestResponse> InterestDto(CustomerInterest i, CancellationToken ct) => new(i.Id, i.ProductId!.Value, await db.Products.Where(p => p.Id == i.ProductId).Select(p => p.Name).SingleAsync(ct), i.ServiceArea, i.DesiredQuantity, i.Active);
    public async Task<PageResponse<InterestResponse>> Interests(Actor actor, PageRequest page, CancellationToken ct)
    {
        Pilot(); Page(page); var q = db.Interests.AsNoTracking().Where(i => i.CustomerId == actor.UserId && i.ProductId != null); var rows = await q.OrderByDescending(i => i.UpdatedAt).ThenBy(i => i.Id).Skip(page.Offset).Take(page.PageSize).ToListAsync(ct);
        var result = new List<InterestResponse>(); foreach (var i in rows) result.Add(await InterestDto(i, ct)); return new(result, page.Page, page.PageSize, await q.CountAsync(ct));
    }
    public async Task<PageResponse<DemandResponse>> Demand(Actor actor, bool seller, PageRequest page, CancellationToken ct)
    {
        Pilot(); Page(page); if (seller) Require(Eligible(await OwnedSeller(actor, ct)), "Approved seller required.", 403); else Admin(actor);
        var recent = clock.GetUtcNow().AddDays(-7); var q = from i in db.Interests.AsNoTracking() join p in PublicProducts on i.ProductId equals p.Id where i.Active group i by new { i.ProductId, p.Name, i.ServiceArea } into g select new { g.Key.ProductId, g.Key.Name, g.Key.ServiceArea, Customers = g.Count(), Quantity = g.Sum(i => i.DesiredQuantity), RecentCustomers = g.Count(i => i.UpdatedAt >= recent) };
        var rows = await q.OrderByDescending(x => x.Quantity).ThenBy(x => x.ProductId).ThenBy(x => x.ServiceArea).Skip(page.Offset).Take(page.PageSize).ToListAsync(ct);
        return new(rows.Select(x => new DemandResponse(x.ProductId!.Value, x.Name, x.ServiceArea, x.Customers, x.Quantity, x.RecentCustomers)).ToList(), page.Page, page.PageSize, await q.CountAsync(ct));
    }
    private async Task<TierCampaignResponse> TierDto(GroupBuyCampaign c, CancellationToken ct)
    {
        var tiers = await db.PriceTiers.Where(t => t.CampaignId == c.Id).OrderBy(t => t.Quantity).Select(t => new PriceTierRequest(t.Quantity, t.UnitPrice)).ToListAsync(ct);
        return new(CampaignDto(c), c.ServiceArea, tiers, tiers.First().Quantity, TierPricing.Achieved(tiers.Select(t => (t.Quantity, t.UnitPrice)), c.ReservedQuantity) ?? tiers[0].UnitPrice, tiers.Where(t => t.Quantity > c.ReservedQuantity).Select(t => (decimal?)t.Quantity).FirstOrDefault(), c.FinalUnitPrice, c.FinalQuantity, c.ExtensionCount, c.NearTargetAccepted);
    }
    public async Task<TierCampaignResponse> ConfigureTiers(Actor actor, Guid id, TierConfigurationRequest request, CancellationToken ct)
    {
        if (request.Tiers is null) throw new BusinessException(400, "invalid_tiers", "Configure ordered price tiers.");
        Pilot(); _ = Area(request.ServiceArea); await using var tx = await db.Database.BeginTransactionAsync(ct); var s = await OwnedSeller(actor, ct); await LockSeller(s.Id, ct); var c = await LockCampaign(id, ct); Require(c.SellerId == s.Id, "Resource not found.", 404); Require(c.Status == CampaignStatus.Draft, "Tiers are immutable after submission.");
        if (!await PhysicalVariant(c.ProductVariantId, ct)) Require(request.Tiers.All(t => t.Quantity == decimal.Truncate(t.Quantity)), "Entitlement tier thresholds must be whole members.", 400);
        Require(c.OriginatingDemandId is null || request.NearTargetQuantity is null, "Demand offers require exact tier thresholds.", 400);
        TierPricing.Validate(request.Tiers.Select(t => (t.Quantity, t.UnitPrice)).ToList(), c.TargetQuantity, c.ReferenceUnitPrice);
        Require(request.NearTargetQuantity is null || (request.NearTargetQuantity >= request.Tiers[0].Quantity * .9m && request.NearTargetQuantity < request.Tiers[0].Quantity && decimal.Round(request.NearTargetQuantity.Value, 3) == request.NearTargetQuantity), "Near-target threshold must be at least 90% of minimum.", 400);
        db.PriceTiers.RemoveRange(await db.PriceTiers.Where(t => t.CampaignId == id).ToListAsync(ct)); foreach (var t in request.Tiers) db.PriceTiers.Add(new() { CampaignId = id, Quantity = t.Quantity, UnitPrice = t.UnitPrice });
        c.UsesTierPricing = true; c.ServiceArea = request.ServiceArea; c.GroupUnitPrice = request.Tiers[0].UnitPrice; c.NearTargetQuantity = request.NearTargetQuantity; c.UpdatedAt = clock.GetUtcNow(); Audit(actor, "CampaignTiersConfigured", c); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return await TierDto(c, ct);
    }
    async Task<TierCampaignResponse> IPhaseOne.Campaign(Guid id, CancellationToken ct) => await TierDto(await PublicCampaigns.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id && c.UsesTierPricing, ct) ?? throw Missing(), ct);
    async Task<PageResponse<TierCampaignResponse>> IPhaseOne.Campaigns(PageRequest page, CancellationToken ct)
    {
        Page(page); var q = PublicCampaigns.AsNoTracking().Where(c => c.UsesTierPricing); var result = new List<TierCampaignResponse>(); foreach (var c in await q.OrderByDescending(c => c.CreatedAt).ThenBy(c => c.Id).Skip(page.Offset).Take(page.PageSize).ToListAsync(ct)) result.Add(await TierDto(c, ct)); return new(result, page.Page, page.PageSize, await q.CountAsync(ct));
    }
    private async Task<(Seller Seller, GroupBuyCampaign Campaign)> LockFinancialCampaign(Guid id, CancellationToken ct)
    {
        var sid = await db.Campaigns.Where(c => c.Id == id).Select(c => (Guid?)c.SellerId).SingleOrDefaultAsync(ct) ?? throw Missing(); var s = await LockSeller(sid, ct); var c = await LockCampaign(id, ct); Require(c.UsesTierPricing, "Tier campaign required.", 400); return (s, c);
    }
    private static bool FinancialOpen(GroupBuyCampaign c) => c.Status is CampaignStatus.Open or CampaignStatus.MinimumReached;
    private async Task<PaymentOrderResponse> OrderDto(PaymentOrder o, CancellationToken ct) => new(o.Id, o.CampaignId, o.ReservationId, o.Quantity, o.MaximumAmountMinor, o.DeliveryFeeMinor, o.FinalAmountMinor, o.RefundedMinor, o.State, o.Provider, o.ProviderOrderId, o.ExpiresAtUtc, o.FulfillmentMode, await db.OrderFulfillments.Where(f => f.PaymentOrderId == o.Id).Select(f => (Guid?)f.Id).SingleOrDefaultAsync(ct), o.Provider == "Sandbox", o.Attempts, o.RetryAtUtc, o.ErrorCode);
    public async Task RetryPayment(Actor actor, Guid id, int expectedRetryCount, CancellationToken ct)
    {
        Admin(actor); await using var tx = await db.Database.BeginTransactionAsync(ct); var o = await db.PaymentOrders.SingleOrDefaultAsync(o => o.Id == id, ct) ?? throw Missing(); await LockFinancialCampaign(o.CampaignId, ct); await db.Entry(o).ReloadAsync(ct);
        var job = await db.PilotJobs.AsNoTracking().SingleOrDefaultAsync(j => j.Stage == "payment_reconciliation" && j.ItemId == id, ct);
        Require(expectedRetryCount >= 0, "Expected retry count must be non-negative.", 400);
        if ((job?.RetryCount ?? 0) > expectedRetryCount) { await tx.CommitAsync(ct); return; }
        Require((job?.RetryCount ?? 0) == expectedRetryCount, "Retry approval version changed.");
        Require(gateway.Available && o.CapturedAtUtc is null && (job is null ? o.Attempts >= 10 : job.State == PilotJobState.Exhausted) && o.Provider == gateway.Name, "Only exhausted matching-provider reconciliation requires operator retry.");
        o.Attempts = 0; o.RetryAtUtc = null; o.ErrorCode = null; await ResetPilotJob(actor, "payment_reconciliation", id, ct); Audit(actor, "PaymentReconciliationRetryApproved", o); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    private async Task<PaymentOrder> OwnOrder(Actor a, Guid id, CancellationToken ct) => await db.PaymentOrders.SingleOrDefaultAsync(o => o.Id == id && o.CustomerId == a.UserId, ct) ?? throw Missing();
    public Task<PaymentOrderResponse> Join(Actor actor, PaymentOrderRequest request, CancellationToken ct) => JoinCore(actor,request,null,ct);
    private async Task<PaymentOrderResponse> JoinCore(Actor actor, PaymentOrderRequest request, QuotePayload? quote, CancellationToken ct)
    {
        if (quote is null) { Pilot(); Require(gateway.Available, "Payments are not activated.", 503); }
        Require(request.IdempotencyKey is { Length: >= 8 and <= 100 } && Enum.IsDefined(request.FulfillmentMode), "Invalid checkout request.", 400);
        var address = request.Address is null ? "" : JsonSerializer.Serialize(request.Address); var digest = Digest(address); Guid orderId;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var (s, c) = await LockFinancialCampaign(request.CampaignId, ct); var previous = await db.PaymentOrders.SingleOrDefaultAsync(o => o.CustomerId == actor.UserId && o.CampaignId == c.Id && o.IdempotencyKey == request.IdempotencyKey, ct);
            if (previous is not null) { Require(previous.Quantity == request.Quantity && previous.FulfillmentMode == request.FulfillmentMode && previous.AddressDigest == digest, "Idempotency key payload changed."); orderId = previous.Id; }
            else
            {
                var now = clock.GetUtcNow();
                if (quote is not null) CheckoutRequire(quote.ExpiresAtUtc > now,"checkout_quote_expired","Checkout quote expired. Request a new quote.");
                var selection = await CheckoutSelectionFor(actor,s,c,new(request.Quantity,request.FulfillmentMode,request.Address,quote?.Selections.ProductVariantId),quote is not null,ct);
                if (quote is not null) CheckoutRequire(selection.Fingerprint == quote.Fingerprint,"checkout_quote_expired","Commercial conditions changed. Request a new quote.");
                var price = selection.MaximumPrice; var fee = selection.Fee;
                var o = new PaymentOrder { CampaignId = c.Id, CustomerId = actor.UserId, Quantity = request.Quantity, MaximumUnitPrice = price, MaximumAmountMinor = checked(TierPricing.Minor(price * request.Quantity) + fee), DeliveryFeeMinor = fee, State = PaymentState.Creating, IdempotencyKey = request.IdempotencyKey, Provider = gateway.Name, ExpiresAtUtc = now.AddMinutes(10) < c.ExpiresAtUtc ? now.AddMinutes(10) : c.ExpiresAtUtc, FulfillmentMode = request.FulfillmentMode, AddressDigest = digest, AddressCiphertext = address.Length == 0 ? "" : protection.CreateProtector("Groudeal.PhaseOne.DeliveryAddress.v1").Protect(address) };
                db.PaymentOrders.Add(o);
                if (quote is not null) db.AuditLogs.Add(new AuditLog { ActorUserId = actor.UserId, Action = "CheckoutConfirmed", EntityType = nameof(PaymentOrder), EntityId = o.Id, CorrelationId = actor.CorrelationId, SafeMetadataJson = JsonSerializer.Serialize(new { quoteId = quote.Id }) });
                Audit(actor, "PaymentOrderCreated", o); await db.SaveChangesAsync(ct); orderId = o.Id;
            }
            await tx.CommitAsync(ct);
        }
        await EnsureProviderOrder(orderId, ct); return await OrderDto(await OwnOrder(actor, orderId, ct), ct);
    }
    private async Task EnsureProviderOrder(Guid id, CancellationToken ct)
    {
        // A short persisted lease prevents simultaneous remote creation; retries keep the same receipt.
        PaymentOrder snapshot;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            snapshot = await db.PaymentOrders.SingleAsync(o => o.Id == id, ct); await LockFinancialCampaign(snapshot.CampaignId, ct); await db.Entry(snapshot).ReloadAsync(ct);
            if (snapshot.ProviderOrderId is not null || snapshot.RetryAtUtc > clock.GetUtcNow() || snapshot.State != PaymentState.Creating) { await tx.CommitAsync(ct); return; }
            if (snapshot.Attempts >= 10 || await db.PilotJobs.AnyAsync(j => j.Stage == "payment_reconciliation" && j.ItemId == id && j.State == PilotJobState.Exhausted, ct))
            {
                await Advisory(JobKey("payment_reconciliation", id), ct);
                if (!await db.PilotJobs.AnyAsync(j => j.Stage == "payment_reconciliation" && j.ItemId == id, ct))
                {
                    var exhausted = new PilotJob { Stage = "payment_reconciliation", ItemId = id, State = PilotJobState.Exhausted, Attempts = snapshot.Attempts, CycleAttempts = 10, ErrorCode = "provider_order_creation_exhausted" };
                    db.PilotJobs.Add(exhausted); Audit(null, "PilotJobExhausted", exhausted); await db.SaveChangesAsync(ct);
                }
                await tx.CommitAsync(ct); throw new BusinessException(503, "provider_order_exhausted", "Provider order creation requires an audited operator retry.");
            }
            snapshot.RetryAtUtc = clock.GetUtcNow().AddSeconds(45); snapshot.Attempts++; await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        }
        try
        {
            var remote = await gateway.CreateOrder(snapshot.Id, snapshot.MaximumAmountMinor, ct);
            await using var finish = await db.Database.BeginTransactionAsync(ct); await LockFinancialCampaign(snapshot.CampaignId, ct); await db.Entry(snapshot).ReloadAsync(ct);
            Require(remote.AmountMinor == snapshot.MaximumAmountMinor && remote.Currency == "INR", "Provider amount mismatch."); snapshot.ProviderOrderId = remote.Id; snapshot.State = snapshot.ExpiresAtUtc <= clock.GetUtcNow() || snapshot.State == PaymentState.Expired ? PaymentState.Expired : PaymentState.Pending; snapshot.Attempts = 0; snapshot.RetryAtUtc = null; snapshot.ErrorCode = null; await db.SaveChangesAsync(ct); await finish.CommitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { await RecordProviderCreationFailure(snapshot.Id, snapshot.Attempts, ct); throw; }
    }
    public async Task<PaymentOrderResponse> Order(Actor actor, Guid id, CancellationToken ct) => await OrderDto(await OwnOrder(actor, id, ct), ct);
    public async Task<PageResponse<PaymentOrderResponse>> Orders(Actor actor, PageRequest page, bool admin, CancellationToken ct)
    {
        Page(page); if (admin) Admin(actor); var q = db.PaymentOrders.AsNoTracking().Where(o => admin || o.CustomerId == actor.UserId); var result = new List<PaymentOrderResponse>(); foreach (var o in await q.OrderByDescending(o => o.CreatedAt).ThenBy(o => o.Id).Skip(page.Offset).Take(page.PageSize).ToListAsync(ct)) result.Add(await OrderDto(o, ct)); return new(result, page.Page, page.PageSize, await q.CountAsync(ct));
    }
    public async Task<PaymentOrderResponse> ConfirmPayment(Actor actor, Guid id, CheckoutConfirmation request, CancellationToken ct)
    {
        Require(request.PaymentId is { Length: >= 1 and <= 100 } && request.Signature is { Length: 64 }, "Invalid payment confirmation.", 400);
        var o = await OwnOrder(actor, id, ct); Require(o.ProviderOrderId is not null, "Provider order is not ready."); if (o.CapturedAtUtc is not null) { Require(o.ProviderPaymentId == request.PaymentId, "Payment confirmation does not match this order."); return await OrderDto(o, ct); }
        Require(await CanCapture(o, ct), "Checkout hold expired; provider reconciliation will safely resolve any existing payment."); var p = await gateway.Confirm(o.ProviderOrderId!, request.PaymentId, request.Signature, ct); await ApplyPayment(o.Id, p, ct); return await OrderDto(o, ct);
    }
    public async Task<PaymentOrderResponse> SandboxPay(Actor actor, Guid id, bool success, CancellationToken ct)
    {
        Require(gateway is SandboxPaymentGateway, "Sandbox checkout unavailable.", 404); var o = await OwnOrder(actor, id, ct); Require(o.ProviderOrderId is not null, "Provider order not ready."); var e = await ((SandboxPaymentGateway)gateway).Simulate(o.ProviderOrderId!, success ? "captured" : "failed", ct); await Webhook(e.Raw, e.Signature, e.EventId, ct); await ReconcileOrder(o.Id, ct); return await OrderDto(o, ct);
    }
    public async Task Webhook(byte[] raw, string signature, string eventId, CancellationToken ct)
    {
        Require(raw.Length <= 65536, "Webhook too large.", 413); var e = gateway.VerifyWebhook(raw, signature, eventId); var hash = Convert.ToHexString(SHA256.HashData(raw)); await using var tx = await db.Database.BeginTransactionAsync(ct); await Advisory($"webhook:{gateway.Name}:{eventId}", ct);
        var old = await db.PaymentWebhooks.SingleOrDefaultAsync(w => w.Provider == gateway.Name && w.EventId == eventId, ct); if (old is not null) Require(old.PayloadHash == hash, "Webhook event identifier payload changed.");
        else db.PaymentWebhooks.Add(new() { Provider = gateway.Name, EventId = eventId, PayloadHash = hash, ProviderOrderId = e.OrderId, ReceivedAtUtc = clock.GetUtcNow(), ProcessedAtUtc = e.Status == "ignored" ? clock.GetUtcNow() : null });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    private async Task ReconcileOrder(Guid id, CancellationToken ct)
    {
        var o = await db.PaymentOrders.AsNoTracking().SingleAsync(o => o.Id == id, ct); if (o.ProviderOrderId is null) { await EnsureProviderOrder(id, ct); return; }
        Require(o.Provider == gateway.Name, "Original payment provider must be configured for reconciliation."); var p = await gateway.Reconcile(o.ProviderOrderId, ct, await CanCapture(o, ct)); if (p is not null) await ApplyPayment(id, p, ct);
        await db.PaymentOrders.Where(x => x.Id == id && x.CapturedAtUtc == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.ReconciledAtUtc, clock.GetUtcNow()).SetProperty(x => x.RetryAtUtc, clock.GetUtcNow().AddMinutes(1)), ct);
    }
    private async Task<bool> CanCapture(PaymentOrder o, CancellationToken ct)
    {
        var now = clock.GetUtcNow(); return o.FinalAmountMinor is null && o.ExpiresAtUtc > now && (o.State is PaymentState.Creating or PaymentState.Pending or PaymentState.Authorized) && await db.Campaigns.AnyAsync(c => c.Id == o.CampaignId && (c.Status == CampaignStatus.Open || c.Status == CampaignStatus.MinimumReached) && c.ExpiresAtUtc > now && db.Sellers.Any(s => s.Id == c.SellerId && s.Status == ResourceStatus.Active && s.VerificationStatus == VerificationStatus.Approved && db.UsersProfile.Any(u => u.Id == s.OwnerUserId && u.Status == AccountStatus.Active)), ct);
    }
    private async Task ExpireHold(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct); var o = await db.PaymentOrders.SingleAsync(o => o.Id == id, ct); await LockFinancialCampaign(o.CampaignId, ct); await db.Entry(o).ReloadAsync(ct);
        if (o.CapturedAtUtc is null && o.ExpiresAtUtc <= clock.GetUtcNow() && o.State is PaymentState.Creating or PaymentState.Pending or PaymentState.Authorized) { o.State = PaymentState.Expired; Audit(null, "PaymentHoldExpired", o); await db.SaveChangesAsync(ct); }
        await tx.CommitAsync(ct);
    }
    private async Task ApplyPayment(Guid id, ProviderPayment p, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct); var o = await db.PaymentOrders.SingleAsync(x => x.Id == id, ct); var (s, c) = await LockFinancialCampaign(o.CampaignId, ct); await db.Entry(o).ReloadAsync(ct); var now = clock.GetUtcNow();
        Require(p.OrderId == o.ProviderOrderId && p.AmountMinor == o.MaximumAmountMinor && p.Currency == "INR", "Provider amount mismatch.");
        if (o.CapturedAtUtc is not null) { Require(o.ProviderPaymentId == p.Id || p.Status != "captured", "Multiple captured payments require operator reconciliation."); await tx.CommitAsync(ct); return; }
        o.ReconciledAtUtc = now; o.ErrorCode = null;
        if (p.Status == "captured")
        {
            var hasHold = o.State is PaymentState.Creating or PaymentState.Pending or PaymentState.Authorized;
            var cancelled = o.FinalAmountMinor == 0; o.ProviderPaymentId = p.Id; o.CapturedAtUtc = now; o.State = PaymentState.Captured;
            var resourcesAvailable = Eligible(s) && await db.UsersProfile.AnyAsync(u => u.Id == s.OwnerUserId && u.Status == AccountStatus.Active, ct) && await db.UsersProfile.AnyAsync(u => u.Id == o.CustomerId && u.Status == AccountStatus.Active, ct) && await db.Variants.AnyAsync(v => v.Id == c.ProductVariantId && v.IsActive && PublicProducts.Any(p => p.Id == v.ProductId && p.SellerId == s.Id), ct) && await db.Locations.AnyAsync(l => l.Id == c.SellerLocationId && l.SellerId == s.Id && l.IsActive && l.IsPickupEnabled, ct);
            if (!hasHold || cancelled || o.ExpiresAtUtc <= now || !FinancialOpen(c) || c.ExpiresAtUtc <= now || !resourcesAvailable || c.ReservedQuantity + o.Quantity > c.TargetQuantity)
            { o.FinalAmountMinor = 0; QueueRefund(o, "late_capture", o.MaximumAmountMinor); }
            else
            {
                var r = new GroupBuyReservation { CampaignId = c.Id, CustomerUserId = o.CustomerId, Quantity = o.Quantity, UnitPriceSnapshot = o.MaximumUnitPrice, TotalPriceSnapshot = decimal.Round(o.Quantity * o.MaximumUnitPrice, 2, MidpointRounding.AwayFromZero), IdempotencyKey = "paid-" + o.Id.ToString("N"), Status = ReservationStatus.Active }; db.Reservations.Add(r); o.ReservationId = r.Id; c.ReservedQuantity += o.Quantity;
                var minimum = await db.PriceTiers.Where(t => t.CampaignId == c.Id).MinAsync(t => t.Quantity, ct); if (c.ReservedQuantity >= minimum && c.Status == CampaignStatus.Open) { c.TransitionTo(CampaignStatus.MinimumReached); Audit(null, "CampaignMinimumReached", c); }
            }
            Notify(o.CustomerId, NotificationType.PilotPaymentCaptured, o.Id); Audit(null, "PaymentCaptured", o);
        }
        else if (p.Status == "authorized" && o.State is PaymentState.Pending or PaymentState.Creating) o.State = PaymentState.Authorized;
        else if (p.Status == "failed" && o.State is PaymentState.Pending or PaymentState.Creating or PaymentState.Authorized) { o.State = PaymentState.Failed; Notify(o.CustomerId, NotificationType.PilotPaymentFailed, o.Id); Audit(null, "PaymentFailed", o); }
        o.UpdatedAt = now; await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    private void QueueRefund(PaymentOrder o, string reason, long amount)
    {
        if (amount <= 0) return; db.PaymentRefunds.Add(new() { PaymentOrderId = o.Id, Reason = reason, AmountMinor = amount, State = RefundState.Pending }); o.State = PaymentState.RefundPending; Audit(null, "RefundQueued", o);
    }
    async Task IPhaseOne.Cancel(Actor actor, Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct); var o = await OwnOrder(actor, id, ct); var (_, c) = await LockFinancialCampaign(o.CampaignId, ct); await db.Entry(o).ReloadAsync(ct); if (o.FinalAmountMinor == 0) { await tx.CommitAsync(ct); return; }
        Require(FinancialOpen(c) && c.ExpiresAtUtc > clock.GetUtcNow(), "Cancellation window is closed.");
        if (o.ReservationId is { } rid) { var r = await db.Reservations.SingleAsync(r => r.Id == rid, ct); if (r.Status == ReservationStatus.Active) { r.Status = ReservationStatus.Cancelled; r.CancelledAt = clock.GetUtcNow(); c.ReservedQuantity -= r.Quantity; } }
        o.FinalAmountMinor = 0; o.ExpiresAtUtc = clock.GetUtcNow(); if (o.CapturedAtUtc is not null) QueueRefund(o, "customer_cancellation", o.MaximumAmountMinor - o.RefundedMinor); else o.State = PaymentState.Expired;
        var minimum = await db.PriceTiers.Where(t => t.CampaignId == c.Id).MinAsync(t => t.Quantity, ct); var next = c.ReservedQuantity >= minimum ? CampaignStatus.MinimumReached : CampaignStatus.Open; if (c.Status != next) c.TransitionTo(next); Audit(actor, "PaymentOrderCancelled", o); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
}

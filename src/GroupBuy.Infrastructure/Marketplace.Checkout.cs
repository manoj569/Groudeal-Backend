using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using GroupBuy.Application;
using GroupBuy.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace GroupBuy.Infrastructure;

public sealed partial class Marketplace : ICheckout
{
    private sealed record QuotePayload(int Version, Guid Id, Guid CustomerId, Guid CampaignId,
        CheckoutQuoteRequest Selections, string Fingerprint, DateTimeOffset ExpiresAtUtc);
    private sealed record CheckoutSelection(CheckoutPackage Package, IReadOnlyList<GroupDealTier> Tiers,
        GroupDealProgress Progress, decimal CurrentPrice, decimal MaximumPrice, long Fee, long MaximumAmount,
        decimal Available, string Fingerprint);
    private static void CheckoutRequire(bool condition, string code, string message, int status = 409)
    { if (!condition) throw new BusinessException(status,code,message); }
    private static string QuoteOperation(Guid id) => "checkout-" + id.ToString("N");
    private IDataProtector QuoteProtector(Actor actor, Guid dealId) => protection.CreateProtector(
        "Groudeal.Checkout.Quote.v1",actor.UserId.ToString("N"),dealId.ToString("N"));
    private QuotePayload ReadQuote(Actor actor, Guid dealId, CheckoutConfirmRequest request)
    {
        CheckoutRequire(request.QuoteId is { Length: >= 20 and <= 10000 },"invalid_checkout_quote","Provide an issued checkout quote.",400);
        try
        {
            var quote = JsonSerializer.Deserialize<QuotePayload>(QuoteProtector(actor,dealId).Unprotect(request.QuoteId));
            if (quote is null || quote.Version != 1 || quote.CustomerId != actor.UserId || quote.CampaignId != dealId)
                throw new CryptographicException();
            return quote;
        }
        catch (Exception e) when (e is CryptographicException or JsonException or FormatException)
        { throw new BusinessException(404,"checkout_quote_not_found","Checkout quote not found."); }
    }

    // Shared by quote, quote confirmation, and the established legacy paid-order endpoint.
    // Caller holds seller -> campaign locks; no quote consumes capacity.
    private async Task<CheckoutSelection> CheckoutSelectionFor(Actor actor, Seller seller, GroupBuyCampaign c,
        CheckoutQuoteRequest request, bool strict, CancellationToken ct)
    {
        if (strict) _ = SupportedDealArea(c.ServiceArea);
        if (strict)
        {
            CheckoutRequire(config.GetValue<bool>("Pilot:Enabled") && gateway.Available,"payments_unavailable","Payments are not activated.",503);
            CheckoutRequire(Enum.IsDefined(request.FulfillmentMode),"fulfillment_unavailable","Choose a supported fulfillment mode.",400);
        }
        var now = clock.GetUtcNow();
        if (c.OriginatingDemandId is not null || !await PhysicalVariant(c.ProductVariantId,ct) || strict)
        {
            if (!strict) _ = SupportedDealArea(c.ServiceArea);
            CheckoutRequire(!await db.PaymentOrders.AnyAsync(o => o.CampaignId == c.Id && o.CustomerId == actor.UserId &&
                ((o.ReservationId != null && db.Reservations.Any(r => r.Id == o.ReservationId && r.Status == ReservationStatus.Active)) ||
                 ((o.State == PaymentState.Creating || o.State == PaymentState.Pending || o.State == PaymentState.Authorized) && o.ExpiresAtUtc > now)),ct),
                "payment_already_exists","Recover the existing checkout instead of creating another payment.");
        }
        CheckoutRequire(Eligible(seller) && FinancialOpen(c) && c.StartsAtUtc <= now && c.ExpiresAtUtc > now,
            "deal_unavailable","Deal is not accepting payments.");
        // These resource checks reuse seller/account/catalog/category/location eligibility.
        try { await CheckOperationalResources(seller,c,ct); }
        catch (BusinessException e) when (strict && e.StatusCode == 409)
        { throw new BusinessException(409,"deal_unavailable","Seller, catalog package, or fulfillment location is unavailable."); }
        var area = Area(c.ServiceArea);
        var package = await (from v in db.Variants join p in db.Products on v.ProductId equals p.Id
            where v.Id == c.ProductVariantId select new CheckoutPackage(p.Id,v.Id,p.Name,v.VariantName,
                (GroupDealType)p.ProductType,v.UnitOfMeasure,v.UnitQuantity)).SingleAsync(ct);
        CheckoutRequire(request.ProductVariantId is null || request.ProductVariantId == package.ProductVariantId,
            "invalid_package","This deal has a fixed catalog package.",400);
        CheckoutRules.ValidateQuantity(request.Quantity,c.MinimumReservationQuantity,c.MaximumReservationQuantity,package.Unit);
        var holds = await db.PaymentOrders.Where(o => o.CampaignId == c.Id &&
            (o.State == PaymentState.Creating || o.State == PaymentState.Pending || o.State == PaymentState.Authorized) && o.ExpiresAtUtc > now)
            .SumAsync(o => (decimal?)o.Quantity,ct) ?? 0;
        var available = Math.Min(c.TargetQuantity,c.CommittedQuantity) - c.ReservedQuantity - holds;
        CheckoutRequire(available >= request.Quantity,"capacity_unavailable","Campaign capacity is unavailable.");
        CheckoutRequire(package.Type == GroupDealType.Product || request.FulfillmentMode == OrderFulfillmentMode.SellerPickup,
            "fulfillment_unavailable","Service and membership entitlements use seller redemption.",400);
        long fee = 0;
        if (request.FulfillmentMode == OrderFulfillmentMode.FounderDelivery)
        {
            var a = request.Address;
            CheckoutRequire(a is not null && a.ServiceArea == c.ServiceArea && area.PostalCodes.Contains(a.PostalCode)
                && a.Recipient is { Length: >= 1 and <= 100 } && !string.IsNullOrWhiteSpace(a.Recipient)
                && a.Phone is { Length: >= 8 and <= 16 } && Regex.IsMatch(a.Phone,"^\\+?[0-9]{8,15}$",RegexOptions.CultureInvariant)
                && a.Address is { Length: >= 5 and <= 500 } && !string.IsNullOrWhiteSpace(a.Address) && (a.Landmark?.Length ?? 0) <= 200,
                "address_not_serviceable","Provide a valid delivery address in the configured service area.",400);
            fee = config.GetValue<long?>("Pilot:DeliveryFeeMinor") ?? throw new BusinessException(503,"delivery_unavailable","An approved delivery fee is required.");
            CheckoutRequire(fee is >= 0 and <= 100000,"delivery_unavailable","Delivery fee configuration is unavailable.",503);
        }
        else CheckoutRequire(request.Address is null,"invalid_address","Seller redemption/pickup does not require a delivery address.",400);
        var tiers = await db.PriceTiers.AsNoTracking().Where(t => t.CampaignId == c.Id).OrderBy(t => t.Quantity)
            .Select(t => new GroupDealTier(t.Quantity,t.UnitPrice)).ToListAsync(ct);
        CheckoutRequire(tiers.Count > 0,"deal_unavailable","Deal pricing is unavailable.");
        var maximumPrice = tiers[0].UnitPrice;
        CheckoutRequire(tiers.All(t => TierPricing.Minor(t.UnitPrice * request.Quantity) > 0),"unsupported_payment_amount","Quantity is below supported currency precision.",400);
        var maximumAmount = checked(TierPricing.Minor(maximumPrice * request.Quantity) + fee);
        TierPricing.ValidateRefundability(maximumAmount,tiers.Select(t => checked(TierPricing.Minor(t.UnitPrice * request.Quantity) + fee)),gateway.MinimumRefundMinor);
        var progress = GroupDealRules.Progress(tiers,c.ReservedQuantity);
        var fingerprint = Digest(JsonSerializer.Serialize(new { c.SellerId,c.ProductVariantId,c.SellerLocationId,c.Title,c.Description,
            c.ReferenceUnitPrice,c.MinimumReservationQuantity,c.MaximumReservationQuantity,c.TargetQuantity,c.StartsAtUtc,c.ExpiresAtUtc,
            c.ServiceArea,c.OfferTerms,c.NearTargetQuantity,c.NearTargetAccepted,package,tiers,progress.CurrentTier,fee,
            postalCodes = area.PostalCodes.Order(StringComparer.Ordinal).ToArray(),gateway.Name,gateway.CheckoutKeyId,gateway.MinimumRefundMinor }));
        return new(package,tiers,progress,progress.CurrentTier?.UnitPrice ?? maximumPrice,maximumPrice,fee,maximumAmount,available,fingerprint);
    }

    public async Task<CheckoutQuoteResponse> Quote(Actor actor, Guid dealId, CheckoutQuoteRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (seller,c) = await LockFinancialCampaign(dealId,ct);
        var selection = await CheckoutSelectionFor(actor,seller,c,request,true,ct);
        var now = clock.GetUtcNow(); var expires = now.AddMinutes(5) < c.ExpiresAtUtc ? now.AddMinutes(5) : c.ExpiresAtUtc;
        var payload = new QuotePayload(1,Guid.NewGuid(),actor.UserId,dealId,request,selection.Fingerprint,expires);
        var token = QuoteProtector(actor,dealId).Protect(JsonSerializer.Serialize(payload));
        await tx.CommitAsync(ct);
        return new(token,dealId,selection.Package,request.Quantity,selection.Progress,selection.CurrentPrice,selection.MaximumPrice,
            TierPricing.Minor(selection.CurrentPrice * request.Quantity),TierPricing.Minor(selection.MaximumPrice * request.Quantity),
            request.FulfillmentMode,selection.Fee,selection.MaximumAmount,selection.MaximumAmount,"INR",selection.Available,expires,now,
            gateway.Name,gateway.CheckoutKeyId,gateway.Sandbox);
    }

    public async Task<CheckoutStatusResponse> ConfirmCheckout(Actor actor, Guid dealId, CheckoutConfirmRequest request, CancellationToken ct)
    {
        var quote = ReadQuote(actor,dealId,request);
        var key = QuoteOperation(quote.Id);
        var existing = await db.PaymentOrders.AsNoTracking().SingleOrDefaultAsync(o => o.CustomerId == actor.UserId && o.CampaignId == dealId && o.IdempotencyKey == key,ct);
        if (existing is not null) return await CheckoutStatus(actor,existing.Id,ct);
        try
        {
            var selections = quote.Selections;
            var result = await JoinCore(actor,new(dealId,selections.Quantity,key,selections.FulfillmentMode,selections.Address),quote,ct);
            return await CheckoutStatus(actor,result.Id,ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested && (e is HttpRequestException or TaskCanceledException || e is BusinessException { StatusCode: 503 }))
        {
            // The order/hold commits before a remote call. Preserve its identity on response loss.
            var saved = await db.PaymentOrders.AsNoTracking().SingleOrDefaultAsync(o => o.CustomerId == actor.UserId && o.CampaignId == dealId && o.IdempotencyKey == key,ct);
            if (saved is null) throw;
            return await CheckoutStatus(actor,saved.Id,ct);
        }
    }

    public async Task<CheckoutStatusResponse> RecoverQuote(Actor actor, Guid dealId, CheckoutConfirmRequest request, CancellationToken ct)
    {
        var quote = ReadQuote(actor,dealId,request);
        var id = await db.PaymentOrders.AsNoTracking().Where(o => o.CustomerId == actor.UserId && o.CampaignId == dealId && o.IdempotencyKey == QuoteOperation(quote.Id))
            .Select(o => (Guid?)o.Id).SingleOrDefaultAsync(ct) ?? throw new BusinessException(404,"checkout_not_found","No confirmed checkout exists for this quote.");
        return await RecoverCheckout(actor,id,ct);
    }

    public async Task<CheckoutStatusResponse> RecoverCheckout(Actor actor, Guid orderId, CancellationToken ct)
    {
        var order = await OwnOrder(actor,orderId,ct);
        if (order.CapturedAtUtc is not null) return await CheckoutStatus(actor,orderId,ct);
        CheckoutRequire(gateway.Available && order.Provider == gateway.Name,"payments_unavailable","Original payment provider is unavailable; stored status remains readable.",503);
        if (order.CapturedAtUtc is null && (order.RetryAtUtc is null || order.RetryAtUtc <= clock.GetUtcNow()))
            await PhaseJob(m => m.ReconcileOrder(orderId,ct),"payment_reconciliation",orderId,ct);
        return await CheckoutStatus(actor,orderId,ct);
    }

    public async Task<CheckoutStatusResponse> CheckoutStatus(Actor actor, Guid orderId, CancellationToken ct)
    {
        // Status is a snapshot; it never calls a provider or allocates a new order.
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,ct);
        var order = await db.PaymentOrders.AsNoTracking().SingleOrDefaultAsync(o => o.Id == orderId && o.CustomerId == actor.UserId,ct) ?? throw Missing();
        var participated = order.CapturedAtUtc is not null && await db.Reservations.AnyAsync(r => r.Id == order.ReservationId &&
            (r.Status == ReservationStatus.Active || r.Status == ReservationStatus.Completed),ct);
        var refunds = await db.PaymentRefunds.AsNoTracking().Where(r => r.PaymentOrderId == orderId).OrderBy(r => r.CreatedAt).ThenBy(r => r.Id)
            .Select(r => new CheckoutRefund(r.Id,r.AmountMinor,r.State,r.Reason,r.ProviderRefundId,r.CreatedAt,r.UpdatedAt)).ToListAsync(ct);
        var job = await db.PilotJobs.AsNoTracking().SingleOrDefaultAsync(j => j.Stage == "payment_reconciliation" && j.ItemId == orderId,ct);
        var requiresOperator = (order.CapturedAtUtc is null && (job?.State == PilotJobState.Exhausted || order.Attempts >= 10 || order.Provider != gateway.Name))
            || refunds.Any(r => r.State == RefundState.Exhausted || (r.State != RefundState.Completed && order.Provider != gateway.Name));
        var now = clock.GetUtcNow();
        var state = CheckoutRules.Status(order.State,participated,order.ExpiresAtUtc <= now,
            order.RefundedMinor > 0 && refunds.Count > 0 && refunds.All(r => r.State == RefundState.Completed));
        var next = new[] { order.RetryAtUtc,job?.NextAttemptAtUtc }.Where(t => t.HasValue).Max();
        var response = new CheckoutStatusResponse(await OrderDto(order,ct),state.Status,state.Code,participated,
            gateway.Available && !requiresOperator && order.CapturedAtUtc is null && (next is null || next <= now),requiresOperator,next,refunds,now,"INR",gateway.Available && order.Provider == gateway.Name ? gateway.CheckoutKeyId : null);
        await tx.CommitAsync(ct); return response;
    }
}

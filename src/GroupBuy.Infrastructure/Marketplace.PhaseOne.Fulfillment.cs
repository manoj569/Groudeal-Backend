using System.Security.Cryptography;
using System.Text.Json;
using GroupBuy.Application;
using GroupBuy.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GroupBuy.Infrastructure;
public sealed partial class Marketplace
{
    public async Task<TierCampaignResponse> Finalize(Actor? actor,Guid id,CancellationToken ct)
    {
        await using var tx=await db.Database.BeginTransactionAsync(ct);var(s,c)=await LockFinancialCampaign(id,ct);var now=clock.GetUtcNow();
        if(actor is not null)Require(actor.IsAdmin || s.OwnerUserId==actor.UserId,"Resource not found.",404);
        if(c.FinalizedAtUtc is not null){await tx.CommitAsync(ct);return await TierDto(c,ct);}
        // A cancellation or extension can invalidate a worker's due-list snapshot.
        // This is a no-op, not a failed job that consumes its bounded retry budget.
        if(actor is null && (!FinancialOpen(c) || (c.ExpiresAtUtc>now && c.ReservedQuantity<c.TargetQuantity))){await tx.CommitAsync(ct);return await TierDto(c,ct);}
        Require(FinancialOpen(c),"Campaign cannot be finalized.");Require(c.ExpiresAtUtc<=now || c.ReservedQuantity==c.TargetQuantity,"Early finalization requires full capacity.");
        // Pending holds never count toward a tier. A subsequent late capture is refunded.
        var tiers=await db.PriceTiers.Where(t=>t.CampaignId==id).OrderBy(t=>t.Quantity).ToListAsync(ct);var achieved=TierPricing.Achieved(tiers.Select(t=>(t.Quantity,t.UnitPrice)),c.ReservedQuantity);
        if(achieved is null && c.NearTargetAccepted && c.NearTargetQuantity<=c.ReservedQuantity)achieved=tiers[0].UnitPrice;
        if(!Eligible(s) || !await db.UsersProfile.AnyAsync(u=>u.Id==s.OwnerUserId && u.Status==AccountStatus.Active,ct))achieved=null;
        c.TransitionTo(CampaignStatus.Finalizing);c.FinalUnitPrice=achieved;c.FinalQuantity=c.ReservedQuantity;c.FinalizedAtUtc=now;c.TransitionTo(achieved is null?CampaignStatus.Failed:CampaignStatus.Successful);c.PickupDeadlineUtc=now.AddDays(2);c.UpdatedAt=now;
        var orders=await db.PaymentOrders.Where(o=>o.CampaignId==id && o.ReservationId!=null && o.CapturedAtUtc!=null && o.FinalAmountMinor==null).ToListAsync(ct);
        var location=await db.Locations.SingleAsync(l=>l.Id==c.SellerLocationId,ct);
        var physical=await PhysicalVariant(c.ProductVariantId,ct);
        var pickupInstructions=$"Collect from {s.BusinessName}: {location.AddressLine1}, {location.AddressLine2}, {location.Area}, {location.City}, {location.State} {location.PostalCode}. Present your one-time pickup pass only at handover.";
        var notified=new HashSet<Guid>();
        foreach(var o in orders)
        {
            var r=await db.Reservations.SingleAsync(r=>r.Id==o.ReservationId,ct);if(r.Status!=ReservationStatus.Active)continue;
            o.FinalAmountMinor=achieved is null?0:checked(TierPricing.Minor(achieved.Value*o.Quantity)+o.DeliveryFeeMinor);
            QueueRefund(o,achieved is null?"minimum_not_reached":"tier_difference",o.MaximumAmountMinor-o.FinalAmountMinor.Value);
            if(achieved is null){r.Status=ReservationStatus.Expired;if(notified.Add(o.CustomerId))Notify(o.CustomerId,NotificationType.PilotCampaignFailed,c.Id);continue;}
            r.UnitPriceSnapshot=achieved.Value;r.TotalPriceSnapshot=decimal.Round(achieved.Value*r.Quantity,2,MidpointRounding.AwayFromZero);
            var pickup=new PickupRecord{ReservationId=r.Id,CampaignId=id,SellerId=c.SellerId,CustomerUserId=o.CustomerId,DeadlineUtc=c.PickupDeadlineUtc.Value,Status=PickupStatus.Pending,PaymentPolicy=PickupPaymentPolicy.VerifiedCapturedPayment,Instructions=physical && o.FulfillmentMode==OrderFulfillmentMode.SellerPickup ? pickupInstructions : ""};db.Pickups.Add(pickup);
            db.OrderFulfillments.Add(new(){PaymentOrderId=o.Id,PickupId=pickup.Id,State=FulfillmentState.CampaignConfirmed});
            if(notified.Add(o.CustomerId))Notify(o.CustomerId,NotificationType.PilotCampaignFinalized,c.Id);
        }
        if(achieved is null){c.CommittedQuantity=0;c.ReservedQuantity=0;}else c.CommittedQuantity=c.ReservedQuantity;
        Audit(actor,achieved is null?"TierCampaignFailed":"TierCampaignFinalized",c);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return await TierDto(c,ct);
    }
    public async Task<TierCampaignResponse> Rescue(Actor actor,Guid id,RescueRequest request,CancellationToken ct)
    {
        Require(request.Reason is {Length:>=5 and <=300},"A bounded rescue reason is required.",400);await using var tx=await db.Database.BeginTransactionAsync(ct);var(s,c)=await LockFinancialCampaign(id,ct);Require(s.OwnerUserId==actor.UserId || actor.IsAdmin,"Resource not found.",404);Require(Eligible(s) && FinancialOpen(c) && c.ExpiresAtUtc>clock.GetUtcNow(),"Rescue window is closed.");
        switch(request.Action)
        {
            case "extend":Require(c.ExtensionCount==0 && request.NewDeadlineUtc>c.ExpiresAtUtc && request.NewDeadlineUtc<=c.ExpiresAtUtc.AddHours(24),"Only one extension of up to 24 hours is allowed.");c.ExpiresAtUtc=request.NewDeadlineUtc!.Value;c.ExtensionCount++;break;
            case "accept-near-target":Require(c.NearTargetQuantity is not null && c.ReservedQuantity>=c.NearTargetQuantity,"Disclosed near-target threshold has not been met.");c.NearTargetAccepted=true;break;
            case "remind":
                Require(c.RescueNotifiedAtUtc is null,"A rescue reminder was already sent.");var productId=await db.Variants.Where(v=>v.Id==c.ProductVariantId).Select(v=>v.ProductId).SingleAsync(ct);
                var recipients=await db.Interests.Where(i=>i.ProductId==productId && i.ServiceArea==c.ServiceArea && i.Active && !db.PaymentOrders.Any(o=>o.CampaignId==id && o.CustomerId==i.CustomerId && o.CapturedAtUtc!=null)).Select(i=>i.CustomerId).ToListAsync(ct);
                foreach(var recipient in recipients)Notify(recipient,NotificationType.CampaignRescueReminder,c.Id);c.RescueNotifiedAtUtc=clock.GetUtcNow();break;
            default:throw new BusinessException(400,"invalid_rescue","Unsupported rescue action.");
        }
        Audit(actor,"CampaignRescue"+request.Action,c);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return await TierDto(c,ct);
    }
    private async Task<(OrderFulfillment F,PaymentOrder O,PickupRecord P,GroupBuyCampaign C,Seller S)> FinancialFulfillment(Guid id,Actor a,string view,bool locked,CancellationToken ct)
    {
        var f=await db.OrderFulfillments.SingleOrDefaultAsync(x=>x.Id==id,ct)??throw Missing();var o=await db.PaymentOrders.SingleAsync(x=>x.Id==f.PaymentOrderId,ct);Seller s;GroupBuyCampaign c;
        if(locked){(s,c)=await LockFinancialCampaign(o.CampaignId,ct);await db.Entry(f).ReloadAsync(ct);await db.Entry(o).ReloadAsync(ct);}else{c=await db.Campaigns.SingleAsync(c=>c.Id==o.CampaignId,ct);s=await db.Sellers.SingleAsync(s=>s.Id==c.SellerId,ct);}
        Require(view switch{"admin"=>a.IsAdmin,"seller"=>s.OwnerUserId==a.UserId && Eligible(s),"customer"=>o.CustomerId==a.UserId,_=>false},"Resource not found.",404);
        var p=await db.Pickups.SingleAsync(p=>p.Id==f.PickupId,ct);return(f,o,p,c,s);
    }
    public async Task<FulfillmentResponse> ConfirmChoice(Actor actor,Guid id,CancellationToken ct)
    {
        await using var tx=await db.Database.BeginTransactionAsync(ct);var(f,o,p,c,s)=await FinancialFulfillment(id,actor,"customer",true,ct);await RequireFulfillable(f,o,p,c,s,ct);Require(f.State==FulfillmentState.CampaignConfirmed && p.DeadlineUtc>clock.GetUtcNow(),"Fulfillment choice cannot be confirmed.");f.ChoiceConfirmed=true;Audit(actor,"FulfillmentChoiceConfirmed",f);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return await FulfillmentDto(f,o,p,c,s,"customer",ct);
    }
    public async Task<FulfillmentResponse> MoveFulfillment(Actor actor,Guid id,FulfillmentAction request,CancellationToken ct)
    {
        var view=actor.IsAdmin?"admin":"seller";await using var tx=await db.Database.BeginTransactionAsync(ct);var(f,o,p,c,s)=await FinancialFulfillment(id,actor,view,true,ct);
        await RequireFulfillable(f,o,p,c,s,ct);
        Require(f.ChoiceConfirmed && p.DeadlineUtc>clock.GetUtcNow(),"Invalid fulfillment transition.");
        if(FulfillmentRules.DeliveryOperation(request.State))Admin(actor);
        Require(request.Reason is null || request.Reason.Length<=300,"Reason too long.",400);
        if(request.State==FulfillmentState.DeliveryFailed)Require(FulfillmentRules.FailureReason(request.Reason),"Choose a supported delivery failure reason.",400);
        else Require(request.Reason is null,"This transition does not accept a reason.",400);
        if(f.State==request.State){await tx.CommitAsync(ct);return await FulfillmentDto(f,o,p,c,s,view,ct);}
        Require(TierPricing.CanFulfill(f.State,request.State,o.FulfillmentMode),"Invalid fulfillment transition.");
        Require(!await db.PaymentRefunds.AnyAsync(r=>r.PaymentOrderId==o.Id && r.State!=RefundState.Completed,ct),"Refund settlement is pending.");
        if(request.State==FulfillmentState.CollectedFromSeller)await ConsumeFinancialStock(f,o,c,ct);
        InvalidateFulfillmentPass(f);
        f.State=request.State;f.ExceptionReason=request.Reason;f.UpdatedAt=clock.GetUtcNow();
        if(f.State==FulfillmentState.Preparing && c.Status==CampaignStatus.Successful)c.TransitionTo(CampaignStatus.Preparing);
        if(f.State==FulfillmentState.ReadyForCollection){p.Status=PickupStatus.ReadyForPickup;NotifyFulfillment(o.CustomerId,NotificationType.PilotFulfillmentReady,f);}
        if(f.State==FulfillmentState.DeliveryFailed)NotifyFulfillment(o.CustomerId,NotificationType.DeliveryFailed,f);
        if(f.State==FulfillmentState.RescheduleRequired)NotifyFulfillment(o.CustomerId,NotificationType.DeliveryRescheduled,f);
        if(f.State==FulfillmentState.OutForDelivery)NotifyFulfillment(o.CustomerId,NotificationType.PilotOutForDelivery,f);
        Audit(actor,"Fulfillment"+request.State,f);await db.SaveChangesAsync(ct);
        if(c.Status==CampaignStatus.Preparing && !await db.OrderFulfillments.AnyAsync(x=>db.PaymentOrders.Any(y=>y.Id==x.PaymentOrderId && y.CampaignId==c.Id) && (x.State==FulfillmentState.CampaignConfirmed || x.State==FulfillmentState.Preparing),ct))c.TransitionTo(CampaignStatus.ReadyForFulfillment);
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return await FulfillmentDto(f,o,p,c,s,view,ct);
    }
    public async Task<FulfillmentPass> IssuePass(Actor actor,Guid id,CancellationToken ct)
    {
        await using var tx=await db.Database.BeginTransactionAsync(ct);var(f,o,p,c,s)=await FinancialFulfillment(id,actor,"customer",true,ct);await RequireFulfillable(f,o,p,c,s,ct);var now=clock.GetUtcNow();
        Require(f.ChoiceConfirmed && f.RedeemedAtUtc is null && p.DeadlineUtc>now && FulfillmentRules.PassReady(f.State,o.FulfillmentMode),"Pass is unavailable.");
        Require(f.SecretIssuedAtUtc is null || f.SecretIssuedAtUtc<=now.AddMinutes(-1),"Wait before regenerating the pass.",429);Require(f.LockedUntilUtc is null || f.LockedUntilUtc<=now,"Pass verification is locked.",429);
        var token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));var code=RandomNumberGenerator.GetInt32(100000,1000000).ToString(System.Globalization.CultureInfo.InvariantCulture);f.CodeVersion++;f.TokenHash=Digest(token);f.BackupCodeHash=PassHash(f,code);f.SecretIssuedAtUtc=now;f.SecretExpiresAtUtc=now.AddMinutes(10)<p.DeadlineUtc?now.AddMinutes(10):p.DeadlineUtc;f.VerifiedBy=null;f.VerifiedUntilUtc=null;
        // Regeneration never clears failed attempts or a lockout.
        Audit(actor,"FulfillmentPassIssued",f);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return new(token,code,f.SecretExpiresAtUtc.Value,f.CodeVersion);
    }
    // Key the low-entropy backup code against offline enumeration. High-entropy QR
    // tokens have a separate indexed SHA-256 lookup; the owned record binds their state.
    private string PassHash(OrderFulfillment f,string secret)
    {
        var key=System.Text.Encoding.UTF8.GetBytes(config["Jwt:SigningKey"]??throw new InvalidOperationException("Pass protection key unavailable."));
        var derived=HMACSHA256.HashData(key,System.Text.Encoding.UTF8.GetBytes("Groudeal.FulfillmentPass.Key.v1"));
        return Convert.ToHexString(HMACSHA256.HashData(derived,System.Text.Encoding.UTF8.GetBytes($"{f.Id:N}:{f.CodeVersion}:{secret}")));
    }
    public async Task<FulfillmentResponse> VerifyOpaquePass(Actor actor,SecretRequest request,CancellationToken ct)
    {
        Require(request.Secret is {Length:64} && request.Secret.All(Uri.IsHexDigit),"Scan an opaque QR token, or select the pickup reference for its backup code.",400);
        var hash=Digest(request.Secret);var id=await (from f in db.OrderFulfillments join o in db.PaymentOrders on f.PaymentOrderId equals o.Id join c in db.Campaigns on o.CampaignId equals c.Id join s in db.Sellers on c.SellerId equals s.Id where f.TokenHash==hash && (actor.IsAdmin || s.OwnerUserId==actor.UserId) select (Guid?)f.Id).SingleOrDefaultAsync(ct)??throw Missing();
        return await VerifyPass(actor,id,request,ct);
    }
    public Task<FulfillmentResponse> VerifyPass(Actor actor,Guid id,SecretRequest request,CancellationToken ct) => VerifyFulfillment(actor,id,request,false,ct);
    public Task<FulfillmentResponse> Handover(Actor actor,Guid id,SecretRequest request,CancellationToken ct) => VerifyFulfillment(actor,id,request,true,ct);
    private async Task<FulfillmentResponse> VerifyFulfillment(Actor actor,Guid id,SecretRequest request,bool complete,CancellationToken ct)
    {
        Require(request.Secret is {Length:>=6 and <=64},"Invalid pass.",400);
        var view=actor.IsAdmin?"admin":"seller";
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        var(f,o,p,c,s)=await FinancialFulfillment(id,actor,view,true,ct);
        if(o.FulfillmentMode==OrderFulfillmentMode.FounderDelivery)Admin(actor);
        await RequireFulfillable(f,o,p,c,s,ct);
        var now=clock.GetUtcNow();
        Require(f.ChoiceConfirmed && f.RedeemedAtUtc is null && p.DeadlineUtc>now && FulfillmentRules.PassReady(f.State,o.FulfillmentMode),"Handover is unavailable.");
        Require(f.LockedUntilUtc is null || f.LockedUntilUtc<=now,"Pass verification is locked.",429);
        if(f.LockedUntilUtc<=now){f.FailedAttempts=0;f.LockedUntilUtc=null;}
        var token=request.Secret.Length==64 && request.Secret.All(Uri.IsHexDigit);
        var code=request.Secret.Length==6 && request.Secret.All(char.IsAsciiDigit);
        var expected=token?f.TokenHash:f.BackupCodeHash;
        var hash=token?Digest(request.Secret):PassHash(f,request.Secret);
        var valid=(token || code) && f.SecretExpiresAtUtc>now && expected is not null && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected),Convert.FromHexString(hash));
        if(!valid){f.FailedAttempts++;if(f.FailedAttempts>=5)f.LockedUntilUtc=now.AddMinutes(15);Audit(actor,"FulfillmentPassRejected",f);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);throw new BusinessException(400,"invalid_pass","Invalid or expired pass.");}
        f.VerifiedBy=actor.UserId;f.VerifiedUntilUtc=now.AddMinutes(2)<f.SecretExpiresAtUtc?now.AddMinutes(2):f.SecretExpiresAtUtc;
        Audit(actor,"FulfillmentPassVerified",f);
        if(complete)await CompleteFulfillment(actor,f,o,p,c,ct);
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
        return await FulfillmentDto(f,o,p,c,s,view,ct);
    }
    public async Task<FulfillmentResponse> Redeem(Actor actor,Guid id,CancellationToken ct)
    {
        var view=actor.IsAdmin?"admin":"seller";await using var tx=await db.Database.BeginTransactionAsync(ct);var(f,o,p,c,s)=await FinancialFulfillment(id,actor,view,true,ct);
        if(o.FulfillmentMode==OrderFulfillmentMode.FounderDelivery)Admin(actor);
        await RequireFulfillable(f,o,p,c,s,ct);
        await CompleteFulfillment(actor,f,o,p,c,ct);
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return await FulfillmentDto(f,o,p,c,s,view,ct);
    }
    private async Task CompleteFulfillment(Actor actor,OrderFulfillment f,PaymentOrder o,PickupRecord p,GroupBuyCampaign c,CancellationToken ct)
    {
        var now=clock.GetUtcNow();
        Require(f.ChoiceConfirmed && f.RedeemedAtUtc is null && f.VerifiedBy==actor.UserId && f.VerifiedUntilUtc>now && f.SecretExpiresAtUtc>now && p.DeadlineUtc>now && FulfillmentRules.PassReady(f.State,o.FulfillmentMode),"Verify an unused pass before confirming handover.");
        var r=await db.Reservations.SingleAsync(r=>r.Id==o.ReservationId,ct);Require(r.Status==ReservationStatus.Active,"Reservation unavailable.");
        if(o.FulfillmentMode==OrderFulfillmentMode.SellerPickup)await ConsumeFinancialStock(f,o,c,ct);else Require(f.SellerHandedOverAtUtc is not null,"Founder collection must be recorded first.");
        r.Status=ReservationStatus.Completed;p.Status=PickupStatus.Collected;p.CollectedAtUtc=now;p.CollectedByUserId=actor.UserId;
        f.State=FulfillmentState.Delivered;f.RedeemedAtUtc=now;
        InvalidateFulfillmentPass(f);f.UpdatedAt=now;Audit(actor,"FulfillmentRedeemed",f);Notify(o.CustomerId,NotificationType.PilotFulfillmentCompleted,f.Id);
        await db.SaveChangesAsync(ct);
        if(!await db.OrderFulfillments.AnyAsync(x=>db.PaymentOrders.Any(y=>y.Id==x.PaymentOrderId && y.CampaignId==c.Id) && x.State!=FulfillmentState.Delivered,ct))c.Status=CampaignStatus.Completed;
    }
    private static void InvalidateFulfillmentPass(OrderFulfillment f)
    {
        f.CodeVersion++;f.TokenHash=null;f.BackupCodeHash=null;f.SecretExpiresAtUtc=null;f.VerifiedBy=null;f.VerifiedUntilUtc=null;
        // Do not clear issuance cooldown, failed attempts or verification lock.
    }
    private void NotifyFulfillment(Guid customer,NotificationType type,OrderFulfillment f) => db.Notifications.Add(new InternalNotification {
        RecipientUserId=customer,Type=type,EntityId=f.Id,CreatedAtUtc=clock.GetUtcNow(),
        DeduplicationKey=$"{type}:{f.Id:N}:{customer:N}:{f.CodeVersion}" });
    private async Task RequireFulfillable(OrderFulfillment f,PaymentOrder o,PickupRecord p,GroupBuyCampaign c,Seller s,CancellationToken ct)
    {
        var reservation=await db.Reservations.Where(r=>r.Id==o.ReservationId).Select(r=>(ReservationStatus?)r.Status).SingleOrDefaultAsync(ct);
        var outstanding=await db.PaymentRefunds.AnyAsync(r=>r.PaymentOrderId==o.Id && r.State!=RefundState.Completed,ct);
        Require(reservation is not null && FulfillmentRules.CommercialAllows(c.Status,c.FinalizedAtUtc!=null,o.State,o.CapturedAtUtc!=null,o.FinalAmountMinor,o.MaximumAmountMinor,o.RefundedMinor,reservation.Value,outstanding),"Commercial participation is not eligible for fulfillment.");
        Require(Eligible(s) && await db.UsersProfile.AnyAsync(u=>u.Id==s.OwnerUserId && u.Status==AccountStatus.Active,ct),"Seller unavailable.");
        Require(f.RedeemedAtUtc==null && f.State is not (FulfillmentState.Cancelled or FulfillmentState.RefundRequired) && p.DeadlineUtc>clock.GetUtcNow(),"Fulfillment unavailable.");
        Require(await PhysicalVariant(c.ProductVariantId,ct) || o.FulfillmentMode==OrderFulfillmentMode.SellerPickup,"Service fulfillment uses seller redemption.");
    }
    public async Task FulfillmentRefund(Actor actor,Guid id,string reason,CancellationToken ct)
    {
        Admin(actor);Require(reason is {Length:>=5 and <=300},"A bounded reason is required.",400);await using var tx=await db.Database.BeginTransactionAsync(ct);var(f,o,p,c,_)=await FinancialFulfillment(id,actor,"admin",true,ct);
        if(f.State==FulfillmentState.Cancelled){await tx.CommitAsync(ct);return;}Require(f.RedeemedAtUtc is null && f.State is FulfillmentState.DeliveryFailed or FulfillmentState.RescheduleRequired or FulfillmentState.RefundRequired,"Delivery is not eligible for refund.");
        var outstanding=await db.PaymentRefunds.Where(r=>r.PaymentOrderId==o.Id && r.State!=RefundState.Completed).SumAsync(r=>(long?)r.AmountMinor,ct)??0;QueueRefund(o,"fulfillment_failed",o.MaximumAmountMinor-o.RefundedMinor-outstanding);o.FinalAmountMinor=0;f.State=FulfillmentState.Cancelled;InvalidateFulfillmentPass(f);f.ExceptionReason=reason;p.Status=PickupStatus.Cancelled;
        var r=await db.Reservations.SingleAsync(r=>r.Id==o.ReservationId,ct);r.Status=ReservationStatus.Cancelled;c.ReservedQuantity-=o.Quantity;if(f.SellerHandedOverAtUtc is null)c.CommittedQuantity-=o.Quantity;if(c.Status!=CampaignStatus.Failed)c.TransitionTo(CampaignStatus.Failed);Audit(actor,"FulfillmentRefundRequested",f);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
    }
    public async Task<PageResponse<PaymentRefundResponse>> Refunds(Actor actor,PageRequest page,CancellationToken ct)
    {
        Admin(actor);Page(page);var q=db.PaymentRefunds.AsNoTracking();return new(await q.OrderByDescending(r=>r.CreatedAt).ThenBy(r=>r.Id).Skip(page.Offset).Take(page.PageSize).Select(r=>new PaymentRefundResponse(r.Id,r.PaymentOrderId,r.Reason,r.AmountMinor,r.State,r.Attempts,r.ErrorCode)).ToListAsync(ct),page.Page,page.PageSize,await q.CountAsync(ct));
    }
    public async Task RetryRefund(Actor actor,Guid id,int expectedRetryCount,CancellationToken ct)
    {
        Admin(actor);await using var tx=await db.Database.BeginTransactionAsync(ct);var r=await db.PaymentRefunds.SingleOrDefaultAsync(r=>r.Id==id,ct)??throw Missing();var o=await db.PaymentOrders.SingleAsync(o=>o.Id==r.PaymentOrderId,ct);await LockFinancialCampaign(o.CampaignId,ct);await db.Entry(r).ReloadAsync(ct);
        var job=await db.PilotJobs.AsNoTracking().SingleOrDefaultAsync(j=>j.Stage=="refund_processing" && j.ItemId==id,ct);
        Require(expectedRetryCount>=0,"Expected retry count must be non-negative.",400);
        if((job?.RetryCount??0)>expectedRetryCount){await tx.CommitAsync(ct);return;}
        Require((job?.RetryCount??0)==expectedRetryCount,"Retry approval version changed.");
        Require(gateway.Available && o.Provider==gateway.Name && (job is null?r.State==RefundState.Exhausted:job.State==PilotJobState.Exhausted),"Only exhausted matching-provider refunds require operator retry.");
        if(r.State!=RefundState.Completed){r.State=RefundState.Pending;r.Attempts=0;r.RetryAtUtc=null;r.ErrorCode=null;}
        await ResetPilotJob(actor,"refund_processing",id,ct);Audit(actor,"RefundRetryApproved",r);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
    }
    public async Task<PilotMetrics> Metrics(Actor actor,CancellationToken ct)
    {
        Admin(actor);var q=db.Campaigns.Where(c=>c.UsesTierPricing);var created=await q.CountAsync(ct);var completed=await q.CountAsync(c=>c.Status==CampaignStatus.Completed,ct);var interested=await db.Interests.Select(i=>i.CustomerId).Distinct().CountAsync(ct);var paid=await db.PaymentOrders.Where(o=>o.ReservationId!=null).Select(o=>o.CustomerId).Distinct().CountAsync(ct);
        var converted=await (from o in db.PaymentOrders join c in db.Campaigns on o.CampaignId equals c.Id join v in db.Variants on c.ProductVariantId equals v.Id where o.ReservationId!=null && db.Interests.Any(i=>i.CustomerId==o.CustomerId && i.ProductId==v.ProductId && i.ServiceArea==c.ServiceArea && i.CreatedAt<=o.CapturedAtUtc) select o.CustomerId).Distinct().CountAsync(ct);
        var tiers=await q.Where(c=>c.FinalUnitPrice!=null).GroupBy(c=>c.FinalUnitPrice).Select(g=>new{Price=g.Key,Count=g.Count()}).ToListAsync(ct);
        return new(created,await db.AuditLogs.Where(a=>a.Action=="CampaignMinimumReached" && a.EntityType==nameof(GroupBuyCampaign)).Select(a=>a.EntityId).Distinct().CountAsync(ct),await q.CountAsync(c=>c.FinalUnitPrice!=null,ct),await q.CountAsync(c=>c.Status==CampaignStatus.Failed,ct),completed,created==0?0:(decimal)completed/created,interested,paid,interested==0?0:(decimal)converted/interested,tiers.ToDictionary(t=>t.Price!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),t=>t.Count),await db.PaymentRefunds.CountAsync(r=>r.State!=RefundState.Completed,ct),await db.PaymentRefunds.CountAsync(r=>r.State==RefundState.Exhausted,ct));
    }
    private async Task ProcessRefund(Guid id,CancellationToken ct)
    {
        PaymentRefund r;PaymentOrder o;
        await using(var tx=await db.Database.BeginTransactionAsync(ct))
        {
            r=await db.PaymentRefunds.SingleAsync(r=>r.Id==id,ct);o=await db.PaymentOrders.SingleAsync(o=>o.Id==r.PaymentOrderId,ct);await LockFinancialCampaign(o.CampaignId,ct);await db.Entry(r).ReloadAsync(ct);
            if(r.State is RefundState.Completed or RefundState.Exhausted || r.RetryAtUtc>clock.GetUtcNow()){await tx.CommitAsync(ct);return;}
            Require(gateway.Available && o.Provider==gateway.Name,"Original payment provider must be configured for refunds.");
            r.State=RefundState.Processing;r.Attempts++;r.RetryAtUtc=clock.GetUtcNow().AddMinutes(1);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
        }
        var result=await gateway.Refund(o.ProviderPaymentId!,r.Id,r.AmountMinor,ct);
        await using var finish=await db.Database.BeginTransactionAsync(ct);await LockFinancialCampaign(o.CampaignId,ct);await db.Entry(o).ReloadAsync(ct);await db.Entry(r).ReloadAsync(ct);
        if(r.State!=RefundState.Completed){Require(result.AmountMinor==r.AmountMinor,"Refund amount mismatch.");r.ProviderRefundId=result.Id;if(result.Status=="processed"){r.State=RefundState.Completed;r.ErrorCode=null;r.RetryAtUtc=null;o.RefundedMinor+=r.AmountMinor;o.State=o.RefundedMinor==o.MaximumAmountMinor?PaymentState.Refunded:PaymentState.Captured;Audit(null,"RefundCompleted",r);Notify(o.CustomerId,NotificationType.PilotRefundCompleted,r.Id);}else if(r.Attempts>=10){r.State=RefundState.Exhausted;r.ErrorCode="provider_refund_pending";}}
        await db.SaveChangesAsync(ct);await finish.CommitAsync(ct);
    }
    async Task IPhaseOne.Process(CancellationToken ct)
    {
        await RecoverExpiredPilotLeases(ct);
        var now=clock.GetUtcNow();
        var holds=await RunnableJobs(db.PaymentOrders.AsNoTracking().Where(o=>o.CapturedAtUtc==null && o.ExpiresAtUtc<=now && (o.State==PaymentState.Creating || o.State==PaymentState.Pending || o.State==PaymentState.Authorized)).OrderBy(o=>o.ExpiresAtUtc).Select(o=>o.Id),"payment_hold_expiry",now).Take(50).ToListAsync(ct);
        foreach(var id in holds)await PhaseJob(async m=>await m.ExpireHold(id,ct),"payment_hold_expiry",id,ct);
        var scheduled=await RunnableJobs(db.Campaigns.AsNoTracking().Where(c=>c.UsesTierPricing && c.Status==CampaignStatus.Scheduled && c.StartsAtUtc<=now).OrderBy(c=>c.StartsAtUtc).Select(c=>c.Id),"campaign_opening",now).Take(50).ToListAsync(ct);
        foreach(var id in scheduled)await PhaseJob(async m=>await m.OpenTierCampaign(id,ct),"campaign_opening",id,ct);
        var orderIds=gateway.Available?await RunnableJobs(db.PaymentOrders.AsNoTracking().Where(o=>o.CapturedAtUtc==null && (o.ProviderOrderId!=null || o.State==PaymentState.Creating) && (o.RetryAtUtc==null || o.RetryAtUtc<=now)).OrderBy(o=>o.ReconciledAtUtc).ThenBy(o=>o.Id).Select(o=>o.Id),"payment_reconciliation",now).Take(50).ToListAsync(ct):[];
        foreach(var id in orderIds)await PhaseJob(async m=>await m.ReconcileOrder(id,ct),"payment_reconciliation",id,ct);
        var webhookIds=gateway.Available?await RunnableJobs(db.PaymentWebhooks.AsNoTracking().Where(w=>w.ProcessedAtUtc==null && (w.RetryAtUtc==null || w.RetryAtUtc<=now)).OrderBy(w=>w.ReceivedAtUtc).Select(w=>w.Id),"webhook_reconciliation",now).Take(50).ToListAsync(ct):[];
        foreach(var id in webhookIds)await PhaseJob(async m=>await m.ProcessWebhook(id,ct),"webhook_reconciliation",id,ct);
        var campaignIds=await RunnableJobs(db.Campaigns.AsNoTracking().Where(c=>c.UsesTierPricing && (c.WorkerRetryAtUtc==null || c.WorkerRetryAtUtc<=now) && (c.Status==CampaignStatus.Open || c.Status==CampaignStatus.MinimumReached) && (c.ExpiresAtUtc<=now || c.ReservedQuantity==c.TargetQuantity)).OrderBy(c=>c.ExpiresAtUtc).Select(c=>c.Id),"campaign_finalization",now).Take(50).ToListAsync(ct);
        foreach(var id in campaignIds)await PhaseJob(async m=>_ = await m.Finalize(null,id,ct),"campaign_finalization",id,ct);
        var expired=await RunnableJobs((from f in db.OrderFulfillments.AsNoTracking() join p in db.Pickups on f.PickupId equals p.Id where p.DeadlineUtc<=now && f.RedeemedAtUtc==null && f.State!=FulfillmentState.Cancelled select f.Id),"fulfillment_expiry",now).Take(50).ToListAsync(ct);
        foreach(var id in expired)await PhaseJob(async m=>await m.ExpireFulfillment(id,ct),"fulfillment_expiry",id,ct);
        var refundIds=gateway.Available?await RunnableJobs(db.PaymentRefunds.AsNoTracking().Where(r=>r.State!=RefundState.Completed && (r.RetryAtUtc==null || r.RetryAtUtc<=now)).OrderBy(r=>r.CreatedAt).Select(r=>r.Id),"refund_processing",now).Take(50).ToListAsync(ct):[];
        foreach(var id in refundIds)await PhaseJob(async m=>await m.ProcessRefund(id,ct),"refund_processing",id,ct);
    }
    private async Task OpenTierCampaign(Guid id,CancellationToken ct)
    {
        await using var tx=await db.Database.BeginTransactionAsync(ct);var(_,c)=await LockFinancialCampaign(id,ct);if(c.Status==CampaignStatus.Scheduled && c.StartsAtUtc<=clock.GetUtcNow()){c.TransitionTo(CampaignStatus.Open); if(c.ExpiresAtUtc>clock.GetUtcNow())await NotifyDemandOffer(c,ct);Audit(null,"CampaignOpened",c);await db.SaveChangesAsync(ct);}await tx.CommitAsync(ct);
    }
    private async Task ExpireFulfillment(Guid id,CancellationToken ct)
    {
        await using var tx=await db.Database.BeginTransactionAsync(ct);var f=await db.OrderFulfillments.SingleAsync(f=>f.Id==id,ct);var o=await db.PaymentOrders.SingleAsync(o=>o.Id==f.PaymentOrderId,ct);var(_,c)=await LockFinancialCampaign(o.CampaignId,ct);await db.Entry(f).ReloadAsync(ct);await db.Entry(o).ReloadAsync(ct);var p=await db.Pickups.SingleAsync(p=>p.Id==f.PickupId,ct);
        if(f.RedeemedAtUtc is not null || f.State==FulfillmentState.Cancelled || p.DeadlineUtc>clock.GetUtcNow()){await tx.CommitAsync(ct);return;}
        var pending=await db.PaymentRefunds.Where(r=>r.PaymentOrderId==o.Id && r.State!=RefundState.Completed).SumAsync(r=>(long?)r.AmountMinor,ct)??0;QueueRefund(o,"fulfillment_expired",o.MaximumAmountMinor-o.RefundedMinor-pending);o.FinalAmountMinor=0;f.State=FulfillmentState.Cancelled;f.TokenHash=null;f.BackupCodeHash=null;f.VerifiedUntilUtc=null;p.Status=PickupStatus.NotCollected;c.ReservedQuantity-=o.Quantity;if(f.SellerHandedOverAtUtc is null)c.CommittedQuantity-=o.Quantity;c.Status=CampaignStatus.Failed;var reservation=await db.Reservations.SingleAsync(r=>r.Id==o.ReservationId,ct);reservation.Status=ReservationStatus.Expired;Audit(null,"FulfillmentExpired",f);Notify(o.CustomerId,NotificationType.PilotFulfillmentExpired,f.Id);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
    }
    private async Task ConsumeFinancialStock(OrderFulfillment f,PaymentOrder o,GroupBuyCampaign c,CancellationToken ct)
    {
        Require(f.SellerHandedOverAtUtc is null,"Seller handover already recorded.");
        if (!await PhysicalVariant(c.ProductVariantId,ct))
        {
            Require(c.CommittedQuantity>=o.Quantity,"Committed capacity unavailable.");c.CommittedQuantity-=o.Quantity;c.FulfilledQuantity+=o.Quantity;f.SellerHandedOverAtUtc=clock.GetUtcNow();Audit(null,"SellerEntitlementRedeemed",f);return;
        }
        var inventory=await db.Inventories.SingleAsync(i=>i.ProductVariantId==c.ProductVariantId,ct);await db.Entry(inventory).ReloadAsync(ct);
        Require(inventory.AvailableQuantity>=o.Quantity && c.CommittedQuantity>=o.Quantity,"Committed inventory unavailable.");inventory.AvailableQuantity-=o.Quantity;inventory.Version++;inventory.UpdatedAt=clock.GetUtcNow();c.CommittedQuantity-=o.Quantity;c.FulfilledQuantity+=o.Quantity;f.SellerHandedOverAtUtc=clock.GetUtcNow();Audit(null,"SellerStockHandedOver",f);
    }
    private async Task ProcessWebhook(Guid id,CancellationToken ct)
    {
        var w=await db.PaymentWebhooks.SingleAsync(w=>w.Id==id,ct);var oid=await db.PaymentOrders.Where(o=>o.Provider==w.Provider && o.ProviderOrderId==w.ProviderOrderId).Select(o=>(Guid?)o.Id).SingleOrDefaultAsync(ct);Require(oid!=null,"Provider order mapping pending.");await ReconcileOrder(oid!.Value,ct);w.ProcessedAtUtc=clock.GetUtcNow();await db.SaveChangesAsync(ct);
    }
    private async Task PhaseJob(Func<Marketplace,Task> action,string stage,Guid id,CancellationToken ct)
    {
        var lease=await BeginPilotJob(stage,id,ct);if(lease is null)return;
        workerHealth.Progress(clock.GetUtcNow());
        try{await using var scope=scopes.CreateAsyncScope();await action(scope.ServiceProvider.GetRequiredService<Marketplace>());await FinishPilotJob(stage,id,lease.Value,null,ct);}
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(Exception ex)
        {
            logger.LogWarning("Phase one job {Stage} {EntityId} requires retry ({ErrorType})",stage,id,ex.GetType().Name);
            await FinishPilotJob(stage,id,lease.Value,stage+"_retry_required",ct);
        }
        finally { workerHealth.Progress(clock.GetUtcNow()); }
    }
}

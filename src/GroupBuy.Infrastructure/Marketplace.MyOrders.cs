using System.Data;
using System.Linq.Expressions;
using GroupBuy.Application;
using GroupBuy.Domain;
using Microsoft.EntityFrameworkCore;

namespace GroupBuy.Infrastructure;

public sealed partial class Marketplace : IMyOrders
{
    private sealed class OrderRow : ParticipationFacts
    {
        public Guid Id { get; init; }
        public Guid CampaignId { get; init; }
        public Guid? PaymentId { get; init; }
        public Guid ProductId { get; init; }
        public Guid VariantId { get; init; }
        public string Title { get; init; } = "";
        public string Product { get; init; } = "";
        public string Variant { get; init; } = "";
        public string Seller { get; init; } = "";
        public ProductType Type { get; init; }
        public UnitOfMeasure Unit { get; init; }
        public decimal UnitQuantity { get; init; }
        public decimal Quantity { get; init; }
        public decimal UnitPrice { get; init; }
        public decimal QuotedSubtotal { get; init; }
        public long? Maximum { get; init; }
        public long Fee { get; init; }
        public long? Final { get; init; }
        public long? Refunded { get; init; }
        public bool RefundOutstanding { get; init; }
        public bool RefundAttention { get; init; }
        public PaymentState? PaymentState { get; init; }
        public ReservationStatus? ReservationState { get; init; }
        public CampaignStatus CampaignState { get; init; }
        public decimal Target { get; init; }
        public decimal GroupQuantity { get; init; }
        public decimal GroupPrice { get; init; }
        public decimal? FinalPrice { get; init; }
        public DateTimeOffset Created { get; init; }
        public DateTimeOffset? Joined { get; init; }
        public DateTimeOffset? Captured { get; init; }
        public DateTimeOffset Ends { get; init; }
        public DateTimeOffset? Finalized { get; init; }
        public DateTimeOffset? CancelledAt { get; init; }
        public bool CanCancel { get; init; }
        public Guid? FulfillmentId { get; init; }
        public Guid? PickupId { get; init; }
        public FulfillmentState? FulfillmentState { get; init; }
        public PickupStatus? PickupState { get; init; }
        public OrderFulfillmentMode? Mode { get; init; }
        public DateTimeOffset? Deadline { get; init; }
        public DateTimeOffset? CompletedAt { get; init; }
        public DateTimeOffset? IssuedAt { get; init; }
        public DateTimeOffset? LockedUntil { get; init; }
        public bool PaymentExpired { get; init; }
    }
    private sealed class ClassifiedOrder
    {
        public OrderRow Order { get; init; } = null!;
        public ParticipationStatus Status { get; init; }
    }
    private sealed class ReplaceFact(ParameterExpression source, ParameterExpression target) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == source ? target : base.VisitParameter(node);
    }
    private IQueryable<ClassifiedOrder> OwnedOrders(Guid owner, DateTimeOffset now)
    {
        var paid = db.PaymentOrders.Where(o => o.CustomerId == owner)
            .Select(o => new { Id=o.Id,CampaignId=o.CampaignId,ReservationId=o.ReservationId,PaymentId=(Guid?)o.Id });
        var legacy = db.Reservations.Where(r => r.CustomerUserId == owner && !db.PaymentOrders.Any(o => o.ReservationId == r.Id))
            .Select(r => new { Id=r.Id,CampaignId=r.CampaignId,ReservationId=(Guid?)r.Id,PaymentId=(Guid?)null });
        var rows = from k in paid.Concat(legacy)
            join c in db.Campaigns on k.CampaignId equals c.Id
            join v in db.Variants on c.ProductVariantId equals v.Id
            join p in db.Products on v.ProductId equals p.Id
            join s in db.Sellers on c.SellerId equals s.Id
            join ro in db.Reservations.Where(r => r.CustomerUserId == owner) on k.ReservationId equals (Guid?)ro.Id into reservations
            from r in reservations.DefaultIfEmpty()
            join po in db.PaymentOrders.Where(o => o.CustomerId == owner) on k.PaymentId equals (Guid?)po.Id into payments
            from o in payments.DefaultIfEmpty()
            join fo in db.OrderFulfillments on k.PaymentId equals (Guid?)fo.PaymentOrderId into fulfillments
            from f in fulfillments.DefaultIfEmpty()
            join pk in db.Pickups.Where(x => x.CustomerUserId == owner) on k.ReservationId equals (Guid?)pk.ReservationId into pickups
            from pickup in pickups.DefaultIfEmpty()
            let outstanding = db.PaymentRefunds.Any(x => x.PaymentOrderId == k.PaymentId && x.State != RefundState.Completed)
            let qualified = r != null && (r.Status == ReservationStatus.Active || r.Status == ReservationStatus.Completed) &&
                (k.PaymentId == null || o.CapturedAtUtc != null)
            let ready = qualified && !outstanding && pickup != null && pickup.DeadlineUtc > now &&
                (k.PaymentId == null ? !c.UsesTierPricing && s.Status == ResourceStatus.Active && s.VerificationStatus == VerificationStatus.Approved &&
                    db.UsersProfile.Any(u=>u.Id==s.OwnerUserId && u.Status==AccountStatus.Active) &&
                    c.Status == CampaignStatus.FulfillmentInProgress && pickup.Status == PickupStatus.ReadyForPickup :
                 f != null && f.RedeemedAtUtc == null && (o.FulfillmentMode == OrderFulfillmentMode.SellerPickup ?
                    f.State == FulfillmentState.ReadyForCollection : f.State == FulfillmentState.OutForDelivery))
            select new OrderRow {
                Id=k.Id,CampaignId=c.Id,PaymentId=k.PaymentId,ProductId=p.Id,VariantId=v.Id,Title=c.Title,Product=p.Name,
                Variant=v.VariantName,Seller=s.BusinessName,Type=p.ProductType,Unit=v.UnitOfMeasure,UnitQuantity=v.UnitQuantity,
                Quantity=o != null ? o.Quantity : r.Quantity,UnitPrice=r != null ? r.UnitPriceSnapshot : o.MaximumUnitPrice,
                QuotedSubtotal=r != null ? r.TotalPriceSnapshot : o.MaximumUnitPrice * o.Quantity,
                Maximum=o != null ? o.MaximumAmountMinor : null,Fee=o != null ? o.DeliveryFeeMinor : 0,
                Final=o != null ? o.FinalAmountMinor : null,Refunded=o != null ? o.RefundedMinor : null,
                RefundOutstanding=outstanding,RefundAttention=db.PaymentRefunds.Any(x=>x.PaymentOrderId==k.PaymentId && x.State==RefundState.Exhausted),
                PaymentState=o != null ? o.State : null,ReservationState=r != null ? r.Status : null,
                CampaignState=c.Status,Target=c.TargetQuantity,GroupQuantity=c.FinalQuantity ?? c.ReservedQuantity,
                GroupPrice=c.GroupUnitPrice,FinalPrice=c.FinalUnitPrice,Created=o != null ? o.CreatedAt : r.CreatedAt,
                Joined=r != null ? r.CreatedAt : null,Captured=o != null ? o.CapturedAtUtc : null,Ends=c.ExpiresAtUtc,
                Finalized=c.FinalizedAtUtc,CancelledAt=r != null ? r.CancelledAt : null,
                CanCancel=k.PaymentId != null ? o.FinalAmountMinor != 0 && (c.Status==CampaignStatus.Open || c.Status==CampaignStatus.MinimumReached) && c.ExpiresAtUtc>now :
                    !c.UsesTierPricing && r.Status==ReservationStatus.Active && (c.Status==CampaignStatus.Open || c.Status==CampaignStatus.TargetReached) && c.ExpiresAtUtc>now,
                PickupId=pickup != null ? pickup.Id : null,FulfillmentId=f != null ? f.Id : null,FulfillmentState=f != null ? f.State : null,
                PickupState=pickup != null ? pickup.Status : null,Mode=o != null ? o.FulfillmentMode : null,
                Deadline=pickup != null ? pickup.DeadlineUtc : null,
                CompletedAt=f != null ? f.RedeemedAtUtc : pickup != null ? pickup.CollectedAtUtc : null,
                IssuedAt=f != null ? f.SecretIssuedAtUtc : pickup != null ? pickup.CodeIssuedAtUtc : null,
                LockedUntil=f != null ? f.LockedUntilUtc : pickup != null ? pickup.LockedUntilUtc : null,
                PaymentExpired=o != null && o.ExpiresAtUtc<=now,
                FullyRefunded=o != null && o.CapturedAtUtc != null && o.State==PaymentState.Refunded && o.RefundedMinor>=o.MaximumAmountMinor,
                Fulfilled=r != null && r.Status==ReservationStatus.Completed && pickup != null && pickup.Status==PickupStatus.Collected &&
                    (k.PaymentId==null || f != null && f.State==FulfillmentState.Delivered && f.RedeemedAtUtc != null),
                Cancelled=r != null && (r.Status==ReservationStatus.Cancelled || r.Status==ReservationStatus.Expired) ||
                    o != null && (o.FinalAmountMinor==0 || o.State==PaymentState.Expired || o.CapturedAtUtc==null && o.ExpiresAtUtc<=now) ||
                    c.Status==CampaignStatus.Cancelled || c.Status==CampaignStatus.Failed || c.Status==CampaignStatus.Expired ||
                    f != null && (f.State==FulfillmentState.Cancelled || f.State==FulfillmentState.RefundRequired),
                PaymentFailed=o != null && o.State==PaymentState.Failed,Qualified=qualified,Ready=ready,
                Confirmed=c.Status==CampaignStatus.Confirmed || c.Status==CampaignStatus.FulfillmentInProgress ||
                    c.Status==CampaignStatus.Successful || c.Status==CampaignStatus.Preparing || c.Status==CampaignStatus.ReadyForFulfillment || c.Status==CampaignStatus.Completed
            };
        var parameter=Expression.Parameter(typeof(OrderRow),"row");
        var status=new ReplaceFact(ParticipationRules.Status.Parameters[0],parameter).Visit(ParticipationRules.Status.Body)!;
        var projection=Expression.Lambda<Func<OrderRow,ClassifiedOrder>>(Expression.MemberInit(Expression.New(typeof(ClassifiedOrder)),
            Expression.Bind(typeof(ClassifiedOrder).GetProperty(nameof(ClassifiedOrder.Order))!,parameter),
            Expression.Bind(typeof(ClassifiedOrder).GetProperty(nameof(ClassifiedOrder.Status))!,status)),parameter);
        return rows.AsNoTracking().Select(projection);
    }

    public async Task<MyOrdersPage> MyOrders(Actor actor, MyOrdersFilter filter, CancellationToken ct)
    {
        Page(new PageRequest(filter.Page,filter.PageSize));
        CheckoutRequire(filter.Search?.Length is not > 100,"invalid_order_search","Search must be at most 100 characters.",400);
        var all=string.Equals(filter.Status,"all",StringComparison.OrdinalIgnoreCase);
        ParticipationStatus selected=default;
        CheckoutRequire(all || Enum.TryParse(filter.Status,true,out selected) && Enum.GetNames<ParticipationStatus>().Any(s=>string.Equals(s,filter.Status,StringComparison.OrdinalIgnoreCase)),
            "invalid_order_status","Choose a supported order status.",400);
        var now=clock.GetUtcNow();
        await using var tx=await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead,ct);
        var query=OwnedOrders(actor.UserId,now);
        if (!string.IsNullOrWhiteSpace(filter.Search)) {
            var term=filter.Search.Trim().ToLowerInvariant();
            var reference=Guid.TryParse(term,out var id) ? id : Guid.Empty;
            query=query.Where(x=>x.Order.Title.ToLower().Contains(term) || x.Order.Product.ToLower().Contains(term) || x.Order.Id==reference);
        }
        var counts=await query.GroupBy(x=>x.Status).Select(g=>new { Status=g.Key,Count=g.Count() }).ToDictionaryAsync(x=>x.Status,x=>x.Count,ct);
        var total=all ? counts.Values.Sum() : counts.GetValueOrDefault(selected);
        if (!all) query=query.Where(x=>x.Status==selected);
        var offset=new PageRequest(filter.Page,filter.PageSize).Offset;
        var rows=await query.OrderByDescending(x=>x.Order.Joined ?? x.Order.Created).ThenByDescending(x=>x.Order.Id)
            .Skip(offset).Take(filter.PageSize).ToListAsync(ct);
        var ids=rows.Select(x=>x.Order.CampaignId).Distinct().ToArray();
        var tiers=await db.PriceTiers.AsNoTracking().Where(t=>ids.Contains(t.CampaignId))
            .Select(t=>new { t.CampaignId,t.Quantity,t.UnitPrice }).ToListAsync(ct);
        await tx.CommitAsync(ct);
        var items=rows.Select(x=>OrderCard(x,tiers.Where(t=>t.CampaignId==x.Order.CampaignId).Select(t=>new GroupDealTier(t.Quantity,t.UnitPrice)),now)).ToArray();
        return new(items,filter.Page,filter.PageSize,total,(long)offset+items.Length<total,
            new(counts.Values.Sum(),Enum.GetValues<ParticipationStatus>().ToDictionary(s=>s,s=>counts.GetValueOrDefault(s))),now);
    }
    public async Task<MyOrderDetail> MyOrder(Actor actor, Guid id, CancellationToken ct)
    {
        var now=clock.GetUtcNow();
        await using var tx=await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead,ct);
        var row=await OwnedOrders(actor.UserId,now).SingleOrDefaultAsync(x=>x.Order.Id==id,ct)
            ?? throw new BusinessException(404,"order_not_found","Order not found.");
        var tiers=await db.PriceTiers.AsNoTracking().Where(t=>t.CampaignId==row.Order.CampaignId)
            .Select(t=>new GroupDealTier(t.Quantity,t.UnitPrice)).ToListAsync(ct);
        var refunds=await db.PaymentRefunds.AsNoTracking().Where(r=>r.PaymentOrderId==row.Order.PaymentId)
            .OrderBy(r=>r.CreatedAt).ThenBy(r=>r.Id)
            .Select(r=>new MyOrderRefund(r.Id,r.AmountMinor,r.State,"refund",r.CreatedAt,r.UpdatedAt)).ToListAsync(ct);
        await tx.CommitAsync(ct);
        var card=OrderCard(row,tiers,now);
        var timeline=new List<MyOrderActivity> { new(row.Order.PaymentId==null ? "reservation_created" : "checkout_created",row.Order.Created) };
        if (row.Order.Captured is {} captured) timeline.Add(new("payment_confirmed",captured));
        if (row.Order.Joined is {} joined) timeline.Add(new("joined_group",joined));
        if (row.Order.Finalized is {} finalized) timeline.Add(new(row.Order.FinalPrice != null ? "group_finalized" : "group_closed",finalized));
        if (row.Order.CancelledAt is {} cancelled) timeline.Add(new("participation_cancelled",cancelled));
        if (row.Order.CompletedAt is {} completed && row.Status==ParticipationStatus.Completed) timeline.Add(new("fulfillment_completed",completed));
        timeline.AddRange(refunds.Select(r=>new MyOrderActivity("refund_initiated",r.CreatedAtUtc,r.AmountMinor)));
        return new(card,new(card.CanCancel,row.Order.CancelledAt,row.Status==ParticipationStatus.Cancelled ? "participation_closed" : null),
            refunds,timeline.OrderBy(x=>x.AtUtc).ThenBy(x=>x.Code,StringComparer.Ordinal).ToArray());
    }
    private static MyOrderCard OrderCard(ClassifiedOrder classified,IEnumerable<GroupDealTier> tiers,DateTimeOffset now)
    {
        var r=classified.Order;
        var orderedTiers=tiers.OrderBy(t=>t.MinimumQuantity).ToArray();
        var progress=GroupDealRules.Progress(orderedTiers,r.GroupQuantity);
        var finalTier=r.FinalPrice is null ? null : progress.CurrentTier;
        var refund=r.PaymentId==null ? "not_tracked" : r.RefundAttention ? "requires_attention" : r.FullyRefunded ? "refunded" :
            r.RefundOutstanding || r.PaymentState==PaymentState.RefundPending ? "pending" : r.Refunded>0 ? "partially_refunded" : "none";
        var payment=r.PaymentState is {} state ? CheckoutRules.Status(state,r.Qualified,r.PaymentExpired,r.Refunded>0 && !r.RefundOutstanding).Status : "not_tracked";
        var kind=r.Type!=ProductType.Physical ? "seller_redemption" : r.Mode==OrderFulfillmentMode.FounderDelivery ? "founder_delivery" : "seller_pickup";
        var available=r.IssuedAt?.AddMinutes(1);
        if (r.LockedUntil>available || available==null) available=r.LockedUntil;
        var canPass=r.Ready && (available==null || available<=now);
        var action=classified.Status switch {
            ParticipationStatus.Completed => "completed",
            ParticipationStatus.Refunded => "no_action",
            _ when refund=="requires_attention" => "contact_support",
            _ when refund=="pending" => "refund_processing",
            ParticipationStatus.PaymentPending or ParticipationStatus.PaymentFailed => "check_payment",
            ParticipationStatus.Ready => kind=="seller_redemption" ? "view_redemption_pass" : kind=="founder_delivery" ? "view_delivery_pass" : "view_pickup_pass",
            ParticipationStatus.Confirmed => kind=="founder_delivery" ? "wait_for_delivery" : "wait_for_fulfillment",
            ParticipationStatus.Active => "wait_for_group",
            _ => "no_action"
        };
        return new(r.Id,r.Id.ToString("D"),r.PaymentId==null ? "reservation" : "checkout",r.CampaignId,r.Title,
            new(r.ProductId,r.VariantId,r.Product,r.Variant,(GroupDealType)r.Type,r.Unit,r.UnitQuantity),r.Seller,r.Quantity,r.Created,r.Joined,
            classified.Status,r.ReservationState,r.PaymentState,payment,r.Qualified,
            new(r.CampaignState,r.Target,progress,r.FinalPrice ?? progress.CurrentTier?.UnitPrice ?? orderedTiers.FirstOrDefault()?.UnitPrice ?? r.GroupPrice,r.FinalPrice,finalTier,r.Ends,r.Finalized),
            new("INR",r.UnitPrice,TierPricing.Minor(r.QuotedSubtotal),r.Maximum,r.Captured!=null ? r.Maximum : null,
                r.PaymentState==PaymentState.Authorized ? r.Maximum : null,r.Final,r.Fee,r.Refunded,refund),
            new(r.FulfillmentId,r.Type==ProductType.Physical ? r.PickupId : null,kind,r.FulfillmentState,r.Type==ProductType.Physical ? r.PickupState : null,r.Ready,canPass,available,r.Deadline,r.CompletedAt),
            r.CanCancel,action,now);
    }
}






using System.Data;
using System.Text.Json;
using GroupBuy.Application;
using GroupBuy.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace GroupBuy.Infrastructure;

public sealed partial class Marketplace
{
    // Legacy pickup screens are physical customer collection only. The existing
    // internal PickupRecord remains shared by paid delivery/service accounting.
    private IQueryable<PickupRecord> PhysicalPickupRows => db.Pickups.AsNoTracking().Where(p=>
        db.Campaigns.Any(c=>c.Id==p.CampaignId && db.Variants.Any(v=>v.Id==c.ProductVariantId &&
            db.Products.Any(product=>product.Id==v.ProductId && product.ProductType==ProductType.Physical))) &&
        !db.PaymentOrders.Any(o=>o.ReservationId==p.ReservationId && o.FulfillmentMode==OrderFulfillmentMode.FounderDelivery));

    // Reader projection omits pass material and seller-unneeded address ciphertext.
    private sealed class FulfillmentRow
    {
        public OrderFulfillment F { get; init; } = null!;
        public PaymentOrder O { get; init; } = null!;
        public PickupRecord P { get; init; } = null!;
        public GroupBuyCampaign C { get; init; } = null!;
        public Seller S { get; init; } = null!;
        public Product Product { get; init; } = null!;
        public ProductVariant Variant { get; init; } = null!;
        public ReservationStatus Reservation { get; init; }
        public bool RefundOutstanding { get; init; }
        public bool SellerActive { get; init; }
    }

    private IQueryable<FulfillmentRow> FulfillmentRows(Actor actor, string view) =>
        from f in db.OrderFulfillments.AsNoTracking()
        join o in db.PaymentOrders on f.PaymentOrderId equals o.Id
        join p in db.Pickups on f.PickupId equals p.Id
        join c in db.Campaigns on o.CampaignId equals c.Id
        join s in db.Sellers on c.SellerId equals s.Id
        join v in db.Variants on c.ProductVariantId equals v.Id
        join product in db.Products on v.ProductId equals product.Id
        join r in db.Reservations on o.ReservationId equals (Guid?)r.Id
        where view == "admin" && actor.IsAdmin || view == "customer" && o.CustomerId == actor.UserId ||
            view == "seller" && s.OwnerUserId == actor.UserId && s.Status == ResourceStatus.Active && s.VerificationStatus == VerificationStatus.Approved
        select new FulfillmentRow {
            // Never SELECT credentials into a broad queue, even transiently.
            F=new OrderFulfillment { Id=f.Id,State=f.State,ChoiceConfirmed=f.ChoiceConfirmed,RedeemedAtUtc=f.RedeemedAtUtc,
                SellerHandedOverAtUtc=f.SellerHandedOverAtUtc,SecretIssuedAtUtc=f.SecretIssuedAtUtc,LockedUntilUtc=f.LockedUntilUtc,
                ExceptionReason=f.ExceptionReason,UpdatedAt=f.UpdatedAt,CreatedAt=f.CreatedAt },
            O=new PaymentOrder { Id=o.Id,Quantity=o.Quantity,FulfillmentMode=o.FulfillmentMode,DeliveryFeeMinor=o.DeliveryFeeMinor,
                FinalAmountMinor=o.FinalAmountMinor,MaximumAmountMinor=o.MaximumAmountMinor,RefundedMinor=o.RefundedMinor,
                CapturedAtUtc=o.CapturedAtUtc,State=o.State,AddressCiphertext=view == "seller" ? "" : o.AddressCiphertext },
            P=new PickupRecord { DeadlineUtc=p.DeadlineUtc,Instructions=p.Instructions },
            C=new GroupBuyCampaign { Id=c.Id,SellerId=c.SellerId,ServiceArea=c.ServiceArea,Status=c.Status,FinalizedAtUtc=c.FinalizedAtUtc },
            S=new Seller { BusinessName=s.BusinessName,Status=s.Status,VerificationStatus=s.VerificationStatus },
            Product=new Product { Name=product.Name,ProductType=product.ProductType },
            Variant=new ProductVariant { VariantName=v.VariantName,UnitQuantity=v.UnitQuantity,UnitOfMeasure=v.UnitOfMeasure },
            Reservation=r.Status,RefundOutstanding=db.PaymentRefunds.Any(refund=>refund.PaymentOrderId==o.Id && refund.State!=RefundState.Completed),
            SellerActive=db.UsersProfile.Any(user=>user.Id==s.OwnerUserId && user.Status==AccountStatus.Active) };

    private FulfillmentResponse ReadFulfillment(FulfillmentRow row, string view)
    {
        var f=row.F;var o=row.O;var p=row.P;var c=row.C;var s=row.S;var product=row.Product;var variant=row.Variant;
        var reservation=row.Reservation;var outstanding=row.RefundOutstanding;var sellerActive=row.SellerActive;
        var now=clock.GetUtcNow();
        var available=f.SecretIssuedAtUtc?.AddMinutes(1);
        if (available is null || f.LockedUntilUtc>available) available=f.LockedUntilUtc;
        var eligible=FulfillmentRules.CommercialAllows(c.Status,c.FinalizedAtUtc!=null,o.State,o.CapturedAtUtc!=null,
            o.FinalAmountMinor,o.MaximumAmountMinor,o.RefundedMinor,reservation,outstanding) &&
            Eligible(s) && sellerActive && f.ChoiceConfirmed && f.RedeemedAtUtc==null && p.DeadlineUtc>now &&
            FulfillmentRules.PassReady(f.State,o.FulfillmentMode);
        DeliveryAddress? address=null;
        if (view is "admin" or "customer" && o.AddressCiphertext.Length>0)
            address=JsonSerializer.Deserialize<DeliveryAddress>(protection.CreateProtector("Groudeal.PhaseOne.DeliveryAddress.v1").Unprotect(o.AddressCiphertext));
        var label=$"{product.Name} - {variant.VariantName} ({variant.UnitQuantity.ToString(System.Globalization.CultureInfo.InvariantCulture)} {variant.UnitOfMeasure})";
        var pickup=product.ProductType==ProductType.Physical && o.FulfillmentMode==OrderFulfillmentMode.SellerPickup;
        var instructions=product.ProductType!=ProductType.Physical ? "Arrange service with the provider. Present the one-time redemption pass only when the service is completed." :
            pickup ? "Present your one-time pickup pass only at handover." : "Share your delivery code only at customer handover with Groudeal.";
        return new(f.Id,o.Id,c.Id,label,o.Quantity,s.BusinessName,o.FulfillmentMode,f.State,f.ChoiceConfirmed,o.DeliveryFeeMinor,
            o.FinalAmountMinor,p.DeadlineUtc,f.RedeemedAtUtc!=null,address,FulfillmentRules.SafeReason(f.ExceptionReason),
            FulfillmentRules.Kind(product.ProductType,o.FulfillmentMode),FulfillmentRules.Status(product.ProductType,o.FulfillmentMode,f.State),
            eligible && (available==null || available<=now),available,
            pickup && p.Instructions.Length>0 ? p.Instructions : instructions,
            product.ProductType==ProductType.Physical && o.FulfillmentMode==OrderFulfillmentMode.FounderDelivery ? f.SellerHandedOverAtUtc : null,
            f.RedeemedAtUtc,f.UpdatedAt);
    }

    private async Task<FulfillmentResponse> FulfillmentDto(OrderFulfillment f,PaymentOrder o,PickupRecord p,GroupBuyCampaign c,Seller s,string view,CancellationToken ct) =>
        ReadFulfillment(await FulfillmentRows(new(view=="seller" ? s.OwnerUserId : o.CustomerId,view=="admin","projection"),view).SingleAsync(row=>row.F.Id==f.Id,ct),view);

    public async Task<FulfillmentResponse> Fulfillment(Actor actor,Guid id,string view,CancellationToken ct) =>
        ReadFulfillment(await FulfillmentRows(actor,view).SingleOrDefaultAsync(row=>row.F.Id==id,ct) ?? throw Missing(),view);

    public async Task<PageResponse<FulfillmentResponse>> Fulfillments(Actor actor,string view,PageRequest page,string? area,
        Guid? seller,Guid? campaign,FulfillmentState? state,CancellationToken ct,string? kind=null)
    {
        Page(page);
        if (view=="admin") Admin(actor);
        else if (view=="seller") _=await OwnedSeller(actor,ct);
        else Require(view=="customer","Invalid fulfillment view.",400);
        Require(state==null || Enum.IsDefined(state.Value),"Invalid fulfillment state.",400);
        Require(kind is null or "seller_pickup" or "founder_delivery" or "seller_redemption","Invalid fulfillment kind.",400);
        await using var tx=await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead,ct);
        var q=FulfillmentRows(actor,view).Where(row=>(area==null || row.C.ServiceArea==area) && (seller==null || row.C.SellerId==seller) &&
            (campaign==null || row.C.Id==campaign) && (state==null || row.F.State==state));
        if (kind=="seller_redemption") q=q.Where(row=>row.Product.ProductType!=ProductType.Physical);
        if (kind=="seller_pickup") q=q.Where(row=>row.Product.ProductType==ProductType.Physical && row.O.FulfillmentMode==OrderFulfillmentMode.SellerPickup);
        if (kind=="founder_delivery") q=q.Where(row=>row.Product.ProductType==ProductType.Physical && row.O.FulfillmentMode==OrderFulfillmentMode.FounderDelivery);
        var count=await q.CountAsync(ct);
        var rows=await q.OrderBy(row=>row.F.CreatedAt).ThenBy(row=>row.F.Id).Skip(page.Offset).Take(page.PageSize).ToListAsync(ct);
        await tx.CommitAsync(ct);
        return new(rows.Select(row=>ReadFulfillment(row,view)).ToArray(),page.Page,page.PageSize,count);
    }
}

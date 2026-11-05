using GroupBuy.Domain;
using Microsoft.EntityFrameworkCore;
namespace GroupBuy.Infrastructure;
public sealed partial class MarketplaceDb
{
    public DbSet<PilotJob> PilotJobs => Set<PilotJob>();
    public DbSet<CustomerInterest> Interests => Set<CustomerInterest>();
    public DbSet<CampaignPriceTier> PriceTiers => Set<CampaignPriceTier>();
    public DbSet<PaymentOrder> PaymentOrders => Set<PaymentOrder>();
    public DbSet<PaymentWebhook> PaymentWebhooks => Set<PaymentWebhook>();
    public DbSet<PaymentRefund> PaymentRefunds => Set<PaymentRefund>();
    public DbSet<OrderFulfillment> OrderFulfillments => Set<OrderFulfillment>();
    public DbSet<SandboxReceipt> SandboxReceipts => Set<SandboxReceipt>();
    public DbSet<SandboxRefundReceipt> SandboxRefundReceipts => Set<SandboxRefundReceipt>();
    private static void ConfigurePhaseOne(ModelBuilder b)
    {
        b.Entity<PilotJob>(e=>{
            e.ToTable("pilot_jobs",t=>t.HasCheckConstraint("ck_pilot_job_retry","\"Attempts\" >= 0 AND \"CycleAttempts\" BETWEEN 0 AND 10 AND \"RetryCount\" >= 0 AND \"State\" BETWEEN 0 AND 4"));
            e.Property(x=>x.Stage).HasMaxLength(60);e.Property(x=>x.ErrorCode).HasMaxLength(80);
            e.HasIndex(x=>new{x.Stage,x.ItemId}).IsUnique();e.HasIndex(x=>new{x.State,x.NextAttemptAtUtc});
        });
        b.Entity<CustomerInterest>(e=>{
            e.ToTable("customer_interests",t=>t.HasCheckConstraint("ck_interest_quantity","\"DesiredQuantity\" > 0 AND \"DesiredQuantity\" <= 1000000"));
            e.HasOne<User>().WithMany().HasForeignKey(x=>x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Product>().WithMany().HasForeignKey(x=>x.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x=>x.ServiceArea).HasMaxLength(80);e.HasIndex(x=>new{x.CustomerId,x.ProductId,x.ServiceArea}).IsUnique();e.HasIndex(x=>new{x.ServiceArea,x.Active,x.UpdatedAt});
        });
        b.Entity<CampaignPriceTier>(e=>{
            e.ToTable("campaign_price_tiers",t=>t.HasCheckConstraint("ck_tier_values","\"Quantity\" > 0 AND \"UnitPrice\" > 0"));
            e.HasOne<GroupBuyCampaign>().WithMany().HasForeignKey(x=>x.CampaignId).OnDelete(DeleteBehavior.Restrict);e.HasIndex(x=>new{x.CampaignId,x.Quantity}).IsUnique();
        });
        b.Entity<GroupBuyCampaign>(e=>{e.Property(x=>x.ServiceArea).HasMaxLength(80);e.ToTable("group_buy_campaigns",t=>t.HasCheckConstraint("ck_phase_one_campaign","\"ExtensionCount\" BETWEEN 0 AND 1 AND (\"FinalUnitPrice\" IS NULL OR \"FinalUnitPrice\" > 0)"));});
        b.Entity<PaymentOrder>(e=>{
            e.ToTable("payment_orders",t=>{
                t.HasCheckConstraint("ck_payment_amounts","\"Quantity\" > 0 AND \"MaximumUnitPrice\" > 0 AND \"MaximumAmountMinor\" > 0 AND \"DeliveryFeeMinor\" >= 0 AND \"RefundedMinor\" BETWEEN 0 AND \"MaximumAmountMinor\" AND (\"FinalAmountMinor\" IS NULL OR \"FinalAmountMinor\" BETWEEN 0 AND \"MaximumAmountMinor\")");
                t.HasCheckConstraint("ck_payment_state","\"State\" BETWEEN 0 AND 7 AND \"FulfillmentMode\" BETWEEN 0 AND 1 AND \"Attempts\" >= 0");
            });
            e.HasOne<GroupBuyCampaign>().WithMany().HasForeignKey(x=>x.CampaignId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x=>x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<GroupBuyReservation>().WithMany().HasForeignKey(x=>x.ReservationId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x=>x.IdempotencyKey).HasMaxLength(100);e.Property(x=>x.Provider).HasMaxLength(24);e.Property(x=>x.ProviderOrderId).HasMaxLength(100);e.Property(x=>x.ProviderPaymentId).HasMaxLength(100);e.Property(x=>x.AddressCiphertext).HasMaxLength(12000);e.Property(x=>x.AddressDigest).HasMaxLength(64);e.Property(x=>x.ErrorCode).HasMaxLength(80);
            e.HasIndex(x=>new{x.CustomerId,x.CampaignId,x.IdempotencyKey}).IsUnique();e.HasIndex(x=>new{x.Provider,x.ProviderOrderId}).IsUnique();e.HasIndex(x=>new{x.Provider,x.ProviderPaymentId}).IsUnique();e.HasIndex(x=>x.ReservationId).IsUnique();e.HasIndex(x=>new{x.State,x.RetryAtUtc,x.ExpiresAtUtc});e.HasIndex(x=>new{x.CampaignId,x.State});
        });
        b.Entity<PaymentWebhook>(e=>{
            e.ToTable("payment_webhooks");e.Property(x=>x.Provider).HasMaxLength(24);e.Property(x=>x.EventId).HasMaxLength(120);e.Property(x=>x.PayloadHash).HasMaxLength(64);e.Property(x=>x.ProviderOrderId).HasMaxLength(100);e.HasIndex(x=>new{x.Provider,x.EventId}).IsUnique();
        });
        b.Entity<PaymentRefund>(e=>{
            e.ToTable("payment_refunds",t=>t.HasCheckConstraint("ck_refund_values","\"AmountMinor\" > 0 AND \"Attempts\" >= 0 AND \"State\" BETWEEN 0 AND 3"));
            e.HasOne<PaymentOrder>().WithMany().HasForeignKey(x=>x.PaymentOrderId).OnDelete(DeleteBehavior.Restrict);e.Property(x=>x.Reason).HasMaxLength(60);e.Property(x=>x.ErrorCode).HasMaxLength(80);e.HasIndex(x=>new{x.PaymentOrderId,x.Reason}).IsUnique();e.HasIndex(x=>new{x.State,x.RetryAtUtc});
        });
        b.Entity<OrderFulfillment>(e=>{
            e.ToTable("order_fulfillments",t=>t.HasCheckConstraint("ck_fulfillment_values","\"State\" BETWEEN 0 AND 10 AND \"FailedAttempts\" BETWEEN 0 AND 5 AND \"CodeVersion\" >= 0"));
            e.HasOne<PaymentOrder>().WithMany().HasForeignKey(x=>x.PaymentOrderId).OnDelete(DeleteBehavior.Restrict);e.HasOne<PickupRecord>().WithMany().HasForeignKey(x=>x.PickupId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x=>x.VerifiedBy).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x=>x.PaymentOrderId).IsUnique();e.HasIndex(x=>x.PickupId).IsUnique();e.Property(x=>x.TokenHash).HasMaxLength(64);e.Property(x=>x.BackupCodeHash).HasMaxLength(64);e.Property(x=>x.ExceptionReason).HasMaxLength(300);e.HasIndex(x=>x.TokenHash).IsUnique();e.HasIndex(x=>new{x.State,x.UpdatedAt});
        });
        b.Entity<SandboxReceipt>(e=>{e.ToTable("sandbox_payment_receipts",t=>t.HasCheckConstraint("ck_sandbox_amount","\"AmountMinor\" > 0 AND \"RefundedMinor\" BETWEEN 0 AND \"AmountMinor\""));e.HasIndex(x=>x.LocalOrderId).IsUnique();e.HasIndex(x=>x.ProviderOrderId).IsUnique();e.Property(x=>x.ProviderOrderId).HasMaxLength(100);e.Property(x=>x.State).HasMaxLength(24);});
        b.Entity<SandboxRefundReceipt>(e=>{e.ToTable("sandbox_refund_receipts");e.HasIndex(x=>x.RefundId).IsUnique();e.HasOne<SandboxReceipt>().WithMany().HasForeignKey(x=>x.LocalOrderId).HasPrincipalKey(x=>x.LocalOrderId).OnDelete(DeleteBehavior.Restrict);});
    }
}

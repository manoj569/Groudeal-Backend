using GroupBuy.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GroupBuy.Infrastructure;
public sealed class IdentityAccount : IdentityUser<Guid>
{
    public int TokenVersion { get; set; }
    public DateTimeOffset? MfaEnrollmentAllowedUntilUtc { get; set; }
}
public sealed partial class MarketplaceDb(DbContextOptions<MarketplaceDb> options) : IdentityDbContext<IdentityAccount, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<User> UsersProfile => Set<User>();
    public DbSet<Seller> Sellers => Set<Seller>();
    public DbSet<SellerLocation> Locations => Set<SellerLocation>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductVariant> Variants => Set<ProductVariant>();
    public DbSet<GroupBuyCampaign> Campaigns => Set<GroupBuyCampaign>();
    public DbSet<GroupBuyReservation> Reservations => Set<GroupBuyReservation>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Inventory> Inventories => Set<Inventory>();
    public DbSet<PickupRecord> Pickups => Set<PickupRecord>();
    public DbSet<InternalNotification> Notifications => Set<InternalNotification>();
    public DbSet<AdminMfaChallenge> MfaChallenges => Set<AdminMfaChallenge>();
    public DbSet<AcceptedMfaCode> AcceptedMfaCodes => Set<AcceptedMfaCode>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        b.Entity<IdentityAccount>().ToTable("identity_accounts");
        b.Entity<IdentityRole<Guid>>().ToTable("identity_roles");
        b.Entity<IdentityUserRole<Guid>>().ToTable("identity_user_roles");
        b.Entity<IdentityUserClaim<Guid>>().ToTable("identity_user_claims");
        b.Entity<IdentityUserLogin<Guid>>().ToTable("identity_user_logins");
        b.Entity<IdentityRoleClaim<Guid>>().ToTable("identity_role_claims");
        b.Entity<IdentityUserToken<Guid>>().ToTable("identity_user_tokens");
        b.Entity<IdentityAccount>().HasIndex(x => x.NormalizedEmail).IsUnique();
        b.Entity<User>(e => {
            e.ToTable("users"); e.HasOne<IdentityAccount>().WithOne().HasForeignKey<User>(x => x.Id).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.Email).IsUnique(); e.HasIndex(x => x.PhoneNumber).IsUnique();
            e.Property(x => x.Email).HasMaxLength(254); e.Property(x => x.PhoneNumber).HasMaxLength(16); e.Property(x => x.DisplayName).HasMaxLength(100);
        });
        b.Entity<Seller>(e => {
            e.ToTable("sellers"); e.HasOne<User>().WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.ReviewerUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.OwnerUserId).IsUnique(); e.HasIndex(x => new { x.VerificationStatus, x.Id });
        });
        b.Entity<SellerLocation>(e => {
            e.ToTable("seller_locations", t => { t.HasCheckConstraint("ck_location_latitude", "\"Latitude\" BETWEEN -90 AND 90"); t.HasCheckConstraint("ck_location_longitude", "\"Longitude\" BETWEEN -180 AND 180"); });
            e.HasOne<Seller>().WithMany().HasForeignKey(x => x.SellerId).OnDelete(DeleteBehavior.Restrict); e.HasAlternateKey(x => new { x.SellerId, x.Id });
        });
        b.Entity<Category>(e => {
            e.ToTable("categories", t => t.HasCheckConstraint("ck_category_parent", "\"ParentCategoryId\" IS NULL OR \"ParentCategoryId\" <> \"Id\""));
            e.HasOne<Category>().WithMany().HasForeignKey(x => x.ParentCategoryId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.Slug).IsUnique(); e.HasIndex(x => new { x.IsActive, x.SortOrder, x.Id });
        });
        b.Entity<Product>(e => {
            e.ToTable("products"); e.HasOne<Seller>().WithMany().HasForeignKey(x => x.SellerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Category>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
            e.HasAlternateKey(x => new { x.SellerId, x.Id });
            e.HasIndex(x => new { x.SellerId, x.Slug }).IsUnique(); e.HasIndex(x => new { x.Status, x.Id });
        });
        b.Entity<ProductVariant>(e => {
            e.ToTable("product_variants", t => t.HasCheckConstraint("ck_variant_quantity", "\"UnitQuantity\" > 0"));
            e.HasAlternateKey(x => new { x.SellerId, x.Id });
            e.HasOne<Product>().WithMany().HasForeignKey(x => new { x.SellerId, x.ProductId }).HasPrincipalKey(x => new { x.SellerId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.SKU).IsUnique(); e.Property(x => x.AttributesJson).HasColumnType("jsonb");
        });
        b.Entity<GroupBuyCampaign>(e => {
            e.ToTable("group_buy_campaigns", t => {
                t.HasCheckConstraint("ck_campaign_prices", "\"GroupUnitPrice\" > 0 AND \"ReferenceUnitPrice\" >= \"GroupUnitPrice\"");
                t.HasCheckConstraint("ck_campaign_capacity", "\"TargetQuantity\" > 0 AND \"ReservedQuantity\" >= 0 AND \"ReservedQuantity\" <= \"TargetQuantity\"");
                t.HasCheckConstraint("ck_campaign_limits", "\"MinimumReservationQuantity\" > 0 AND \"MaximumReservationQuantity\" >= \"MinimumReservationQuantity\" AND \"MaximumReservationQuantity\" <= \"TargetQuantity\"");
                t.HasCheckConstraint("ck_campaign_dates", "\"ExpiresAtUtc\" > \"StartsAtUtc\"");
            });
            e.HasOne<Seller>().WithMany().HasForeignKey(x => x.SellerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ProductVariant>().WithMany().HasForeignKey(x => new { x.SellerId, x.ProductVariantId }).HasPrincipalKey(x => new { x.SellerId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<SellerLocation>().WithMany().HasForeignKey(x => new { x.SellerId, x.SellerLocationId }).HasPrincipalKey(x => new { x.SellerId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.RowVersion).IsRowVersion(); e.Ignore(x => x.Progress);
            e.HasIndex(x => new { x.Status, x.ExpiresAtUtc, x.Id }); e.HasIndex(x => new { x.SellerId, x.Id });
        });
        b.Entity<GroupBuyReservation>(e => {
            e.ToTable("group_buy_reservations", t => { t.HasCheckConstraint("ck_reservation_quantity", "\"Quantity\" > 0"); t.HasCheckConstraint("ck_reservation_price", "\"UnitPriceSnapshot\" > 0 AND \"TotalPriceSnapshot\" = round(\"Quantity\" * \"UnitPriceSnapshot\", 2)"); });
            e.HasOne<GroupBuyCampaign>().WithMany().HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.CustomerUserId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.IdempotencyKey).HasMaxLength(100);
            e.HasIndex(x => new { x.CampaignId, x.CustomerUserId, x.IdempotencyKey }).IsUnique();
            e.HasIndex(x => new { x.CustomerUserId, x.CreatedAt, x.Id }); e.HasIndex(x => new { x.CampaignId, x.Status });
        });
        b.Entity<AuditLog>(e => { e.ToTable("audit_logs"); e.Property(x => x.SafeMetadataJson).HasColumnType("jsonb"); e.HasOne<User>().WithMany().HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.Restrict); e.HasIndex(x => new { x.TimestampUtc, x.Id }); });
        b.Entity<Inventory>(e => {
            e.ToTable("inventory", t => t.HasCheckConstraint("ck_inventory_available", "\"AvailableQuantity\" BETWEEN 0 AND 1000000 AND \"Version\" >= 0"));
            e.HasIndex(x => x.ProductVariantId).IsUnique(); e.HasIndex(x => new { x.SellerId, x.Id });
            e.HasOne<Seller>().WithMany().HasForeignKey(x => x.SellerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ProductVariant>().WithMany().HasForeignKey(x => new { x.SellerId, x.ProductVariantId }).HasPrincipalKey(x => new { x.SellerId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Version).IsConcurrencyToken();
        });
        b.Entity<PickupRecord>(e => {
            e.ToTable("pickup_records", t => {
                t.HasCheckConstraint("ck_pickup_status", "\"Status\" BETWEEN 0 AND 4 AND \"PaymentPolicy\" IN (0, 1) AND \"FailedAttempts\" >= 0");
                t.HasCheckConstraint("ck_pickup_collected", "(\"Status\" = 2) = (\"CollectedAtUtc\" IS NOT NULL AND \"CollectedByUserId\" IS NOT NULL)");
            });
            e.HasIndex(x => x.ReservationId).IsUnique(); e.HasIndex(x => new { x.CustomerUserId, x.Id });
            e.HasIndex(x => new { x.SellerId, x.Status, x.Id }); e.HasIndex(x => new { x.CampaignId, x.Status, x.DeadlineUtc });
            e.Property(x => x.CodeHash).HasMaxLength(64);
            e.HasOne<GroupBuyReservation>().WithMany().HasForeignKey(x => x.ReservationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<GroupBuyCampaign>().WithMany().HasForeignKey(x => new { x.SellerId, x.CampaignId }).HasPrincipalKey(x => new { x.SellerId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.CustomerUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.CollectedByUserId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<InternalNotification>(e => {
            e.ToTable("internal_notifications"); e.HasIndex(x => x.DeduplicationKey).IsUnique(); e.Property(x => x.DeduplicationKey).HasMaxLength(180);
            e.HasIndex(x => new { x.RecipientUserId, x.CreatedAtUtc, x.Id });
            e.HasOne<User>().WithMany().HasForeignKey(x => x.RecipientUserId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<GroupBuyCampaign>(e => {
            e.HasAlternateKey(x => new { x.SellerId, x.Id });
            e.ToTable("group_buy_campaigns", t => t.HasCheckConstraint("ck_campaign_commitments", "\"CommittedQuantity\" >= 0 AND \"FulfilledQuantity\" >= 0 AND \"CommittedQuantity\" + \"FulfilledQuantity\" <= \"TargetQuantity\" AND \"WorkerFailureCount\" >= 0"));
            e.Property(x => x.DecisionReason).HasMaxLength(500); e.Property(x => x.DecisionKey).HasMaxLength(100); e.Property(x => x.WorkerErrorCode).HasMaxLength(100);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.DecisionActorId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.Status, x.ConfirmationDeadlineUtc }); e.HasIndex(x => new { x.Status, x.PickupDeadlineUtc });
        });
        b.Entity<AdminMfaChallenge>(e => {
            e.ToTable("admin_mfa_challenges", t => t.HasCheckConstraint("ck_mfa_attempts", "\"FailedAttempts\" BETWEEN 0 AND 5"));
            e.HasIndex(x => x.TokenHash).IsUnique(); e.Property(x => x.TokenHash).HasMaxLength(64); e.Property(x => x.SecurityStamp).HasMaxLength(256);
            e.HasIndex(x => new { x.UserId, x.ExpiresAtUtc });
            e.HasOne<IdentityAccount>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<AcceptedMfaCode>(e => {
            e.ToTable("admin_mfa_accepted_codes"); e.HasKey(x => new { x.UserId, x.CodeHash }); e.Property(x => x.CodeHash).HasMaxLength(64);
            e.HasOne<IdentityAccount>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });
        ConfigurePhaseOne(b);
        ConfigureCustomerAuthentication(b);
        foreach (var entity in b.Model.GetEntityTypes()) foreach (var p in entity.GetProperties())
        {
            if (p.ClrType == typeof(decimal) || p.ClrType == typeof(decimal?))
            { p.SetPrecision(p.Name is "Latitude" or "Longitude" ? 10 : 18); p.SetScale(p.Name.Contains("Price") ? 2 : p.Name is "Latitude" or "Longitude" ? 7 : 3); }
            if (p.ClrType == typeof(string) && p.GetMaxLength() is null && p.GetColumnType() != "jsonb") p.SetMaxLength(4000);
        }
        b.Entity<Category>().HasData(CategorySeed.All);
        b.Entity<IdentityRole<Guid>>().HasData(new[] { "Customer", "Seller", "Admin" }.Select((name, i) => new IdentityRole<Guid> { Id = Guid.Parse($"00000000-0000-0000-0000-{i + 1:000000000000}"), Name = name, NormalizedName = name.ToUpperInvariant(), ConcurrencyStamp = $"role-{name}" }));
    }
}
public static class CategorySeed
{
    public static readonly Category[] All = Build();
    private static Category[] Build()
    {
        string[] names = ["Grocery", "Wheat & Flour", "Rice", "Dal & Pulses", "Cooking Oil", "Sugar & Salt", "Daily Essentials", "Fashion", "Accessories", "Home & Kitchen", "Electronics", "Vehicles", "Real Estate"];
        string[] slugs = ["grocery", "wheat-flour", "rice", "dal-pulses", "cooking-oil", "sugar-salt", "daily-essentials", "fashion", "accessories", "home-kitchen", "electronics", "vehicles", "real-estate"];
        return names.Select((name, i) => new Category { Id = Guid.Parse($"10000000-0000-0000-0000-{i + 1:000000000000}"), Name = name, Slug = slugs[i], ParentCategoryId = i is > 0 and < 7 ? Guid.Parse("10000000-0000-0000-0000-000000000001") : null, IsActive = i < 7, SortOrder = i, CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) }).ToArray();
    }
}
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<MarketplaceDb>
{
    public MarketplaceDb CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<MarketplaceDb>().UseNpgsql(Environment.GetEnvironmentVariable("ConnectionStrings__Database") ?? "Host=localhost;Database=groupbuy;Username=groupbuy").Options);
}

using GroupBuy.Domain;
using Microsoft.EntityFrameworkCore;

namespace GroupBuy.Infrastructure;

public sealed partial class MarketplaceDb
{
    public DbSet<DiscoveryDemand> DiscoveryDemands => Set<DiscoveryDemand>();
    private static void ConfigureDiscovery(ModelBuilder b)
    {
        b.Entity<DiscoveryDemand>(e =>
        {
            e.ToTable("discovery_demands", t =>
            {
                t.HasCheckConstraint("ck_demand_type", "\"DemandType\" IN (0,1)");
                t.HasCheckConstraint("ck_demand_lifecycle", "NOT \"IsCancelled\" OR NOT \"IsOpen\"");
                t.HasCheckConstraint("ck_demand_product_type", "\"ProductId\" IS NULL OR (\"DemandType\" = 0 AND \"NormalizedTitle\" = '')");
            });
            e.Ignore(x => x.Status);
            e.Property(x => x.LegacyIdentity).HasDefaultValue(true);
            e.Property(x => x.ClosureReason).HasMaxLength(500);
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
            e.HasAlternateKey(x => new { x.Id, x.ServiceArea });
            e.HasIndex(x => new { x.Id, x.ProductId, x.ServiceArea }).IsUnique();
            e.Property(x => x.Title).HasMaxLength(100); e.Property(x => x.NormalizedTitle).HasMaxLength(100);
            e.Property(x => x.ServiceArea).HasMaxLength(80);
            e.HasOne<Category>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.NormalizedTitle, x.ServiceArea }).IsUnique().HasFilter("\"LegacyIdentity\"");
            e.HasIndex(x => new { x.DemandType, x.CategoryId, x.ProductId, x.NormalizedTitle, x.ServiceArea }).IsUnique().AreNullsDistinct(false);
            e.HasIndex(x => new { x.ProductId, x.ServiceArea }).IsUnique().HasFilter("\"ProductId\" IS NOT NULL");
            e.HasIndex(x => new { x.ServiceArea, x.IsOpen, x.DemandType, x.CreatedAt, x.Id });
            e.HasIndex(x => new { x.CreatedBy, x.CreatedAt, x.Id });
        });
        b.Entity<CustomerInterest>(e =>
        {
            e.ToTable("customer_interests", t => t.HasCheckConstraint("ck_interest_target", "\"ProductId\" IS NOT NULL OR \"DemandId\" IS NOT NULL"));
            e.HasOne<DiscoveryDemand>().WithMany().HasForeignKey(x => x.DemandId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<DiscoveryDemand>().WithMany().HasForeignKey(x => new { x.DemandId, x.ServiceArea }).HasPrincipalKey(x => new { x.Id, x.ServiceArea }).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.RequestedQuantity).HasPrecision(18, 3);
            e.Property(x => x.RequestedUnit).HasMaxLength(24);
            e.Property(x => x.RequirementText).HasMaxLength(500);
            e.Property(x => x.RequirementDetails).HasMaxLength(1000);
            e.ToTable("customer_interests", t => t.HasCheckConstraint("ck_interest_requirement", "(\"RequestedQuantity\" IS NULL AND \"RequestedUnit\" IS NULL) OR (\"RequestedQuantity\" IS NOT NULL AND \"RequestedUnit\" IS NOT NULL AND \"RequestedQuantity\" > 0 AND \"RequestedQuantity\" <= 1000000 AND \"RequestedUnit\" IN ('kg','l','piece','pack','household','project','hour','visit','square_metre'))"));
            e.HasIndex(x => new { x.CustomerId, x.CreatedAt, x.Id });
            e.HasIndex(x => new { x.CustomerId, x.DemandId }).IsUnique().HasFilter("\"DemandId\" IS NOT NULL");
        });
    }
}

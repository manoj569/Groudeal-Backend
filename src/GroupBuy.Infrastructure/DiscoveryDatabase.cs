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
            e.ToTable("discovery_demands");
            e.Property(x => x.Title).HasMaxLength(100); e.Property(x => x.NormalizedTitle).HasMaxLength(100);
            e.Property(x => x.ServiceArea).HasMaxLength(80);
            e.HasOne<Category>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.NormalizedTitle, x.ServiceArea }).IsUnique();
        });
        b.Entity<CustomerInterest>(e =>
        {
            e.ToTable("customer_interests", t => t.HasCheckConstraint("ck_interest_target", "(\"ProductId\" IS NOT NULL) <> (\"DemandId\" IS NOT NULL)"));
            e.HasOne<DiscoveryDemand>().WithMany().HasForeignKey(x => x.DemandId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.CustomerId, x.DemandId }).IsUnique().HasFilter("\"DemandId\" IS NOT NULL");
        });
    }
}

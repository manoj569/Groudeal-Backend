using GroupBuy.Domain;
using Microsoft.EntityFrameworkCore;
namespace GroupBuy.Infrastructure;
public sealed partial class MarketplaceDb
{
    private static void ConfigureGroupDeals(ModelBuilder b)
    {
        b.Entity<Product>(e => e.ToTable("products", t => t.HasCheckConstraint("ck_product_type", "\"ProductType\" BETWEEN 0 AND 2")));
        b.Entity<GroupBuyCampaign>(e =>
        {
            e.Property(c => c.OfferTerms).HasMaxLength(4000);
            e.HasOne<DiscoveryDemand>().WithMany().HasForeignKey(c => new { c.OriginatingDemandId, c.ServiceArea })
                .HasPrincipalKey(d => new { d.Id, d.ServiceArea }).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(c => new { c.OriginatingDemandId, c.Status, c.Id });
        });
    }
}

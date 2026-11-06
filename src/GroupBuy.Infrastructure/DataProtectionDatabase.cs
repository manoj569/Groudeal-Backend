using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace GroupBuy.Infrastructure;

public sealed partial class MarketplaceDb : IDataProtectionKeyContext
{
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
    private static void ConfigureDataProtection(ModelBuilder b)
    {
        b.Entity<DataProtectionKey>(e => {
            e.ToTable("data_protection_keys");
            e.Property(x => x.FriendlyName).HasMaxLength(200);
            e.Property(x => x.Xml).HasColumnType("text");
        });
    }
}

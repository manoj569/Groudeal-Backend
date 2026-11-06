using Microsoft.EntityFrameworkCore;

namespace GroupBuy.Infrastructure;

public sealed class CustomerChallenge
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Channel { get; set; } = "";
    public string Destination { get; set; } = "";
    public string Purpose { get; set; } = "";
    public Guid? UserId { get; set; }
    public int? TokenVersion { get; set; }
    public string CodeHash { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? ConsumedAtUtc { get; set; }
    public int FailedAttempts { get; set; }
}
public sealed class UserConsent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string DocumentType { get; set; } = "";
    public string DocumentVersion { get; set; } = "";
    public DateTimeOffset AcceptedAtUtc { get; set; }
}
public sealed partial class MarketplaceDb
{
    public DbSet<CustomerChallenge> CustomerChallenges => Set<CustomerChallenge>();
    public DbSet<UserConsent> UserConsents => Set<UserConsent>();
    private static void ConfigureCustomerAuthentication(ModelBuilder b)
    {
        b.Entity<IdentityAccount>().HasIndex(x => x.PhoneNumber).IsUnique();
        b.Entity<CustomerChallenge>(e => {
            e.ToTable("customer_auth_challenges", t => t.HasCheckConstraint("ck_customer_challenge_attempts", "\"FailedAttempts\" BETWEEN 0 AND 5"));
            e.Property(x => x.Destination).HasMaxLength(254); e.Property(x => x.CodeHash).HasMaxLength(64);
            e.Property(x => x.Channel).HasMaxLength(16); e.Property(x => x.Purpose).HasMaxLength(16);
            e.HasIndex(x => new { x.Channel, x.Destination, x.CreatedAtUtc });
            e.HasOne<IdentityAccount>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<UserConsent>(e => {
            e.ToTable("user_consents"); e.Property(x => x.DocumentType).HasMaxLength(32); e.Property(x => x.DocumentVersion).HasMaxLength(100);
            e.HasIndex(x => new { x.UserId, x.DocumentType, x.DocumentVersion }).IsUnique();
            e.HasOne<IdentityAccount>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

using GroupBuy.Application;
using GroupBuy.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GroupBuy.Infrastructure;
public static class DemoSeed
{
    public static async Task Run(IServiceProvider services)
    {
        if (!services.GetRequiredService<IHostEnvironment>().IsDevelopment()) throw new InvalidOperationException("Demo seed is development-only.");
        var db = services.GetRequiredService<MarketplaceDb>();
        var config = services.GetRequiredService<IConfiguration>();
        var password = config["Demo:Password"] ?? throw new InvalidOperationException("Set Demo__Password (12+ characters with upper/lowercase, number and symbol).");
        var users = services.GetRequiredService<UserManager<IdentityAccount>>();
        // Explicit command only, after migrations; a marker account makes reruns harmless.
        if (await users.FindByEmailAsync("admin@fictional-demo.invalid") is not null) return;
        await using var tx = await db.Database.BeginTransactionAsync();
        // Seed directly within this transaction; application operations normally own their transactions.
        async Task<Guid> Account(string name, string email, string role)
        {
            var a = new IdentityAccount { Id = Guid.NewGuid(), UserName = email, Email = email, MfaEnrollmentAllowedUntilUtc = role == "Admin" ? DateTimeOffset.UtcNow.AddMinutes(15) : null };
            var result = await users.CreateAsync(a, password); if (!result.Succeeded) throw new InvalidOperationException("Demo password does not meet policy.");
            if (role == "Admin" && config["Demo:AdminAuthenticatorKey"] is string fixtureKey) {
                if (fixtureKey.Length != 32 || fixtureKey.Any(c => !(c is >= 'A' and <= 'Z' or >= '2' and <= '7'))) throw new InvalidOperationException("Synthetic fixture authenticator key shape invalid.");
                var store = (IUserAuthenticatorKeyStore<IdentityAccount>)services.GetRequiredService<IUserStore<IdentityAccount>>();
                await store.SetAuthenticatorKeyAsync(a, fixtureKey, default); SecurityTokens.Check(await users.SetTwoFactorEnabledAsync(a, true)); a.MfaEnrollmentAllowedUntilUtc = null;
            }
            await users.AddToRoleAsync(a, "Customer"); if (role != "Customer") await users.AddToRoleAsync(a, role);
            db.UsersProfile.Add(new User { Id = a.Id, Email = email.ToUpperInvariant(), DisplayName = name }); await db.SaveChangesAsync(); return a.Id;
        }
        var admin = await Account("Fictional Demo Admin", "admin@fictional-demo.invalid", "Admin");
        var customer = await Account("Fictional Demo Customer", "customer@fictional-demo.invalid", "Customer");
        for (var i = 1; i <= 3; i++)
        {
            var owner = await Account($"Fictional Owner {i}", $"seller{i}@fictional-demo.invalid", "Seller");
            var s = new Seller { OwnerUserId = owner, BusinessName = $"FICTIONAL Demo Kirana {i}", Description = "Synthetic development fixture. No real business verification.", SellerType = SellerType.Grocery, ContactEmail = $"seller{i}@fictional-demo.invalid", ContactPhone = "0000000000", FssaiRegistrationNumber = "00000000000000", VerificationStatus = VerificationStatus.Approved, ReviewerUserId = admin, ReviewedAt = DateTimeOffset.UtcNow };
            var l = new SellerLocation { SellerId = s.Id, AddressLine1 = $"{i} Fictional Demo Road", Area = "Demo Area", City = "Demo City", State = "Demo State", PostalCode = "110001" };
            db.Sellers.Add(s); db.Locations.Add(l);
            string[] names = ["Wheat", "Rice", "Dal", "Sugar", "Oil"];
            for (var j = 0; j < names.Length; j++)
            {
                var categoryIndex = new[] { 1, 2, 3, 5, 4 }[j];
                var p = new Product { SellerId = s.Id, CategoryId = CategorySeed.All[categoryIndex].Id, Name = names[j], Slug = names[j].ToLowerInvariant(), Description = "Fictional demo product" };
                var v = new ProductVariant { SellerId = s.Id, ProductId = p.Id, SKU = $"DEMO-{i}-{j}", VariantName = "1 unit", UnitOfMeasure = j == 4 ? UnitOfMeasure.Litre : UnitOfMeasure.Kilogram, UnitQuantity = 1 };
                var c = new GroupBuyCampaign { SellerId = s.Id, ProductVariantId = v.Id, SellerLocationId = l.Id, Title = $"Fictional {names[j]} Group", ReferenceUnitPrice = 100, GroupUnitPrice = 80, TargetQuantity = 100, MinimumReservationQuantity = 1, MaximumReservationQuantity = 20, StartsAtUtc = DateTimeOffset.UtcNow.AddHours(-1), ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(7), Status = CampaignStatus.Open, ReservedQuantity = 2 };
                c.CommittedQuantity = c.TargetQuantity;
                db.Inventories.Add(new Inventory { SellerId = s.Id, ProductVariantId = v.Id, AvailableQuantity = 100, Version = 1 });
                db.Products.Add(p); db.Variants.Add(v); db.Campaigns.Add(c);
                db.Reservations.Add(new GroupBuyReservation { CampaignId = c.Id, CustomerUserId = customer, Quantity = 2, UnitPriceSnapshot = 80, TotalPriceSnapshot = 160, IdempotencyKey = $"demo-{i}-{j}-reservation" });
            }
        }
        db.AuditLogs.Add(new AuditLog { ActorUserId = admin, EntityId = admin, EntityType = "Demo", Action = "FictionalDevelopmentSeed" });
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }
}

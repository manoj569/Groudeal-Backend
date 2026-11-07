using GroupBuy.Domain;

namespace GroupBuy.Infrastructure;

// Existing catalog metadata visibility; discovery adds current checkout/resource eligibility.
internal static class PublicCatalog
{
    public static IQueryable<Product> Products(MarketplaceDb db) => db.Products.Where(p => p.Status == ResourceStatus.Active
        && db.Categories.Any(c => c.Id == p.CategoryId && c.IsActive)
        && db.Sellers.Any(s => s.Id == p.SellerId && s.Status == ResourceStatus.Active && s.VerificationStatus == VerificationStatus.Approved));
}

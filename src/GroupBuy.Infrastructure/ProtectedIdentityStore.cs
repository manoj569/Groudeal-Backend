using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;

namespace GroupBuy.Infrastructure;

// Identity's supported token-store extension point protects authenticator and recovery values.
public sealed class ProtectedIdentityStore(MarketplaceDb context, IDataProtectionProvider protection)
    : UserStore<IdentityAccount, IdentityRole<Guid>, MarketplaceDb, Guid>(context)
{
    private readonly IDataProtectionProvider protection = protection;
    private IDataProtector Protector(IdentityAccount user, string provider, string name) => protection.CreateProtector("Groudeal.IdentityTokens.v1", user.Id.ToString("N"), provider, name);
    public override Task SetTokenAsync(IdentityAccount user, string loginProvider, string name, string? value, CancellationToken cancellationToken = default) =>
        base.SetTokenAsync(user, loginProvider, name, value is null ? null : Protector(user, loginProvider, name).Protect(value), cancellationToken);
    public override async Task<string?> GetTokenAsync(IdentityAccount user, string loginProvider, string name, CancellationToken cancellationToken = default)
    {
        var value = await base.GetTokenAsync(user, loginProvider, name, cancellationToken);
        return value is null ? null : Protector(user, loginProvider, name).Unprotect(value);
    }
}

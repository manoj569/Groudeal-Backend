using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using GroupBuy.Application;
using GroupBuy.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace GroupBuy.Infrastructure;
public sealed class IdentityService(MarketplaceDb db, UserManager<IdentityAccount> users, IConfiguration config, TimeProvider clock, AdminMfaService mfa) : IIdentityService
{
    public async Task<UserResponse> Register(RegisterRequest request, CancellationToken ct)
    {
        Validate.With(request, new RegisterValidator());
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var account = new IdentityAccount { Id = Guid.NewGuid(), UserName = request.Email.Trim(), Email = request.Email.Trim() };
        var result = await users.CreateAsync(account, request.Password);
        if (!result.Succeeded) throw new BusinessException(400, "registration_failed", "Registration failed. Check password requirements or use a different email.");
        result = await users.AddToRoleAsync(account, "Customer");
        if (!result.Succeeded) throw new InvalidOperationException("Customer role assignment failed.");
        db.UsersProfile.Add(new User { Id = account.Id, Email = account.Email!.ToUpperInvariant(), DisplayName = request.DisplayName.Trim() });
        db.AuditLogs.Add(new AuditLog { ActorUserId = account.Id, EntityId = account.Id, EntityType = "User", Action = "Registered" });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new(account.Id, account.Email, request.DisplayName.Trim(), ["Customer"]);
    }
    public async Task<TokenResponse> Login(LoginRequest request, CancellationToken ct)
    {
        Validate.With(request, new LoginValidator());
        var account = await users.FindByEmailAsync(request.Email.Trim());
        // Run a real hash even for unknown users to reduce account-enumeration timing differences.
        if (account is null)
        {
            _ = new PasswordHasher<IdentityAccount>().VerifyHashedPassword(new(), DummyHash, request.Password);
            throw InvalidLogin();
        }
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        account = await SecurityTokens.LockAccount(db, account.Id, ct);
        if (await users.IsLockedOutAsync(account)) throw InvalidLogin();
        if (!await users.CheckPasswordAsync(account, request.Password)) { SecurityTokens.Check(await users.AccessFailedAsync(account)); await tx.CommitAsync(ct); throw InvalidLogin(); }
        if (!await db.UsersProfile.AnyAsync(x => x.Id == account.Id && x.Status == AccountStatus.Active, ct)) throw InvalidLogin();
        TokenResponse result;
        if (await users.IsInRoleAsync(account, "Admin")) result = await mfa.Challenge(account, ct);
        else { SecurityTokens.Check(await users.ResetAccessFailedCountAsync(account)); result = await SecurityTokens.Issue(account, users, config, clock, false); }
        await tx.CommitAsync(ct); return result;
    }

    public async Task<UserResponse> Me(Guid userId, CancellationToken ct)
    {
        var profile = await db.UsersProfile.SingleAsync(x => x.Id == userId, ct);
        var account = await users.FindByIdAsync(userId.ToString()) ?? throw InvalidLogin();
        return new(userId, profile.Email, profile.DisplayName, (await users.GetRolesAsync(account)).ToArray());
    }
    public async Task Logout(Guid userId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Users.Where(x => x.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(x => x.TokenVersion, x => x.TokenVersion + 1), ct);
        SecurityTokens.Audit(db, userId, "Logout"); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    private static readonly string DummyHash = new PasswordHasher<IdentityAccount>().HashPassword(new(), Guid.NewGuid().ToString());
    private static BusinessException InvalidLogin() => new(401, "invalid_login", "Invalid credentials or account unavailable.");
}

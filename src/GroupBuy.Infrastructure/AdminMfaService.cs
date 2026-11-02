using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using GroupBuy.Application;
using GroupBuy.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace GroupBuy.Infrastructure;

public static class SecurityTokens
{
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static async Task<IdentityAccount> LockAccount(MarketplaceDb db, Guid id, CancellationToken ct)
    {
        var account = await db.Users.FromSqlInterpolated($"SELECT * FROM identity_accounts WHERE \"Id\" = {id} FOR UPDATE").SingleAsync(ct);
        await db.Entry(account).ReloadAsync(ct); return account;
    }
    public static void Check(IdentityResult result) { if (!result.Succeeded) throw new InvalidOperationException("Identity security update failed."); }
    public static void Audit(MarketplaceDb db, Guid user, string action, string? reference = null) => db.AuditLogs.Add(new AuditLog {
        ActorUserId = reference is null ? user : null, EntityId = user, EntityType = "User", Action = action,
        CorrelationId = reference ?? Guid.NewGuid().ToString("N") });
    public static async Task<TokenResponse> Issue(IdentityAccount account, UserManager<IdentityAccount> users, IConfiguration config, TimeProvider clock, bool mfa)
    {
        var roles = await users.GetRolesAsync(account);
        if (roles.Contains("Admin") && (!mfa || !account.TwoFactorEnabled)) throw new InvalidOperationException("Admin requires MFA.");
        var expiry = clock.GetUtcNow().AddMinutes(mfa ? Math.Min(10, config.GetValue("Jwt:ExpiryMinutes", 15)) : config.GetValue("Jwt:ExpiryMinutes", 15));
        var claims = new List<Claim> { new("sub", account.Id.ToString()), new("ver", account.TokenVersion.ToString()), new("jti", Guid.NewGuid().ToString()), new("amr", mfa ? "mfa" : "pwd") };
        claims.AddRange(roles.Select(role => new Claim("role", role)));
        var token = new JwtSecurityToken(config["Jwt:Issuer"], config["Jwt:Audience"], claims, clock.GetUtcNow().UtcDateTime, expiry.UtcDateTime,
            new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["Jwt:SigningKey"]!)), SecurityAlgorithms.HmacSha256));
        return new(new JwtSecurityTokenHandler().WriteToken(token), expiry);
    }
}

public sealed class AdminMfaService(MarketplaceDb db, UserManager<IdentityAccount> users, IConfiguration config, TimeProvider clock) : IAdminMfa
{
    private static BusinessException Invalid() => new(401, "invalid_mfa", "MFA challenge or verification unavailable.");
    public async Task<TokenResponse> Challenge(IdentityAccount account, CancellationToken ct)
    {
        var enrollment = !account.TwoFactorEnabled;
        if (enrollment && !(account.MfaEnrollmentAllowedUntilUtc > clock.GetUtcNow()))
            throw new BusinessException(403, "mfa_enrollment_closed", "Contact the verified operator recovery process to authorize MFA enrollment.");
        if (enrollment && await users.GetAuthenticatorKeyAsync(account) is null) SecurityTokens.Check(await users.ResetAuthenticatorKeyAsync(account));
        var now = clock.GetUtcNow();
        await db.MfaChallenges.Where(c => c.UserId == account.Id && c.ConsumedAtUtc == null).ExecuteUpdateAsync(s => s.SetProperty(c => c.ConsumedAtUtc, now), ct);
        var secret = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var challenge = new AdminMfaChallenge { UserId = account.Id, TokenHash = SecurityTokens.Hash(secret), TokenVersion = account.TokenVersion,
            SecurityStamp = account.SecurityStamp!, Enrollment = enrollment, ExpiresAtUtc = now.AddMinutes(5) };
        db.MfaChallenges.Add(challenge); SecurityTokens.Audit(db, account.Id, "AdminMfaChallengeCreated");
        await db.SaveChangesAsync(ct); return new(null, challenge.ExpiresAtUtc, secret, enrollment);
    }
    private async Task<(IdentityAccount Account, AdminMfaChallenge Challenge)> Locked(string secret, CancellationToken ct)
    {
        if (secret is null || secret.Length != 43 || secret.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')) throw Invalid();
        var hash = SecurityTokens.Hash(secret);
        var found = await db.MfaChallenges.AsNoTracking().SingleOrDefaultAsync(c => c.TokenHash == hash, ct) ?? throw Invalid();
        var account = await SecurityTokens.LockAccount(db, found.UserId, ct);
        var challenge = await db.MfaChallenges.SingleAsync(c => c.Id == found.Id, ct); await db.Entry(challenge).ReloadAsync(ct);
        if (challenge.ConsumedAtUtc != null || challenge.ExpiresAtUtc <= clock.GetUtcNow() || challenge.FailedAttempts >= 5 || challenge.TokenVersion != account.TokenVersion ||
            challenge.SecurityStamp != account.SecurityStamp || await users.IsLockedOutAsync(account) || !await users.IsInRoleAsync(account, "Admin") ||
            !await db.UsersProfile.AnyAsync(u => u.Id == account.Id && u.Status == AccountStatus.Active, ct) ||
            (challenge.Enrollment && (account.TwoFactorEnabled || !(account.MfaEnrollmentAllowedUntilUtc > clock.GetUtcNow()))) || (!challenge.Enrollment && !account.TwoFactorEnabled)) throw Invalid();
        return (account, challenge);
    }
    public async Task<MfaEnrollmentResponse> Enrollment(MfaChallengeRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (account, challenge) = await Locked(request.Challenge, ct);
        if (!challenge.Enrollment) throw Invalid();
        var key = await users.GetAuthenticatorKeyAsync(account) ?? throw Invalid();
        SecurityTokens.Audit(db, account.Id, "AdminMfaEnrollmentViewed"); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new(key, "Groudeal", account.Email!);
    }
    public async Task<MfaTokenResponse> Verify(MfaVerifyRequest request, CancellationToken ct)
    {
        if (request.Code is null || request.Code.Length is < 1 or > 32) throw new BusinessException(400, "invalid_code_shape", "Enter an authenticator or recovery code.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (account, challenge) = await Locked(request.Challenge, ct); var now = clock.GetUtcNow();
        var code = request.Code.Trim().Replace(" ", "");
        var codeHash = SecurityTokens.Hash($"{account.Id:N}:{code}");
        AcceptedMfaCode? accepted = null;
        bool valid;
        if (request.UseRecoveryCode)
            valid = !challenge.Enrollment && (await users.RedeemTwoFactorRecoveryCodeAsync(account, code)).Succeeded;
        else
        {
            accepted = await db.AcceptedMfaCodes.SingleOrDefaultAsync(c => c.UserId == account.Id && c.CodeHash == codeHash, ct);
            valid = code.Length == 6 && code.All(char.IsAsciiDigit) && !(accepted?.AcceptedAtUtc > now.AddMinutes(-10)) &&
                await users.VerifyTwoFactorTokenAsync(account, TokenOptions.DefaultAuthenticatorProvider, code);
        }
        if (!valid)
        {
            challenge.FailedAttempts++; SecurityTokens.Check(await users.AccessFailedAsync(account));
            SecurityTokens.Audit(db, account.Id, "AdminMfaVerificationFailed"); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); throw Invalid();
        }
        if (!request.UseRecoveryCode)
        {
            if (accepted is null) db.AcceptedMfaCodes.Add(new AcceptedMfaCode { UserId = account.Id, CodeHash = codeHash, AcceptedAtUtc = now });
            else accepted.AcceptedAtUtc = now;
        }
        IReadOnlyList<string> recovery = [];
        if (challenge.Enrollment)
        {
            SecurityTokens.Check(await users.SetTwoFactorEnabledAsync(account, true));
            recovery = (await users.GenerateNewTwoFactorRecoveryCodesAsync(account, 10))!.ToArray();
            account.MfaEnrollmentAllowedUntilUtc = null; account.TokenVersion++;
            SecurityTokens.Audit(db, account.Id, "AdminMfaEnrolled");
        }
        if (request.UseRecoveryCode) { account.TokenVersion++; SecurityTokens.Audit(db, account.Id, "AdminRecoveryCodeRedeemed"); }
        challenge.ConsumedAtUtc = now; SecurityTokens.Check(await users.ResetAccessFailedCountAsync(account));
        SecurityTokens.Audit(db, account.Id, "AdminMfaVerified"); await db.SaveChangesAsync(ct);
        var token = await SecurityTokens.Issue(account, users, config, clock, true); await tx.CommitAsync(ct);
        return new(token.AccessToken!, token.ExpiresAtUtc, recovery);
    }
}

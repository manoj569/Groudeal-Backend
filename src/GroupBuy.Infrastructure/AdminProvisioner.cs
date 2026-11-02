using GroupBuy.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace GroupBuy.Infrastructure;

public sealed class AdminProvisioner(MarketplaceDb db, UserManager<IdentityAccount> users, IConfiguration config, TimeProvider clock)
{
    public async Task Execute(string operation, Guid userId, string operatorId, string reference, CancellationToken ct)
    {
        if (!config.GetValue("AdminProvisioning:Enabled", false)) throw new BusinessException(403, "provisioning_disabled", "Explicit operator provisioning is disabled.");
        if (operation is not ("provision" or "revoke" or "reset-mfa") || !Safe(operatorId) || !Safe(reference)) throw new BusinessException(400, "operator_context_required", "A supported operation, named operator and change reference are required.");
        if (!await db.Users.AnyAsync(u => u.Id == userId, ct)) throw new BusinessException(404, "operator_account_missing", "Register the named operator account first; this command never creates credentials.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var account = await SecurityTokens.LockAccount(db, userId, ct);
        if (operation != "revoke" && !await db.UsersProfile.AnyAsync(u => u.Id == userId && u.Status == AccountStatus.Active, ct)) throw new BusinessException(409, "operator_inactive", "An active independently verified operator account is required.");
        var auditAction = operation switch { "provision" => "AdminProvisioned", "revoke" => "AdminRevoked", _ => "AdminMfaReset" };
        var correlation = $"{operatorId}:{reference}";
        if (await db.AuditLogs.AnyAsync(a => a.EntityId == userId && a.Action == auditAction && a.CorrelationId == correlation, ct)) { await tx.CommitAsync(ct); return; }
        var admin = await users.IsInRoleAsync(account, "Admin");
        if (operation == "provision" && !admin)
        {
            SecurityTokens.Check(await users.AddToRoleAsync(account, "Admin")); account.TokenVersion++;
            if (!account.TwoFactorEnabled) account.MfaEnrollmentAllowedUntilUtc = clock.GetUtcNow().AddMinutes(15);
            SecurityTokens.Audit(db, userId, "AdminProvisioned", $"{operatorId}:{reference}");
        }
        else if (operation == "revoke" && admin)
        {
            SecurityTokens.Check(await users.RemoveFromRoleAsync(account, "Admin")); account.TokenVersion++; account.MfaEnrollmentAllowedUntilUtc = null;
            SecurityTokens.Audit(db, userId, "AdminRevoked", $"{operatorId}:{reference}");
        }
        else if (operation == "reset-mfa")
        {
            if (!admin) throw new BusinessException(409, "admin_required", "Only an existing Admin can enter controlled re-enrollment.");
            // Independent identity verification and change approval are prerequisites to this privileged command.
            SecurityTokens.Check(await users.SetTwoFactorEnabledAsync(account, false));
            SecurityTokens.Check(await users.ResetAuthenticatorKeyAsync(account));
            _ = await users.GenerateNewTwoFactorRecoveryCodesAsync(account, 0);
            SecurityTokens.Check(await users.SetLockoutEndDateAsync(account, null)); SecurityTokens.Check(await users.ResetAccessFailedCountAsync(account));
            account.TokenVersion++; account.MfaEnrollmentAllowedUntilUtc = clock.GetUtcNow().AddMinutes(15);
            SecurityTokens.Audit(db, userId, "AdminMfaReset", $"{operatorId}:{reference}");
        }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    private static bool Safe(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 80 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '@');
}



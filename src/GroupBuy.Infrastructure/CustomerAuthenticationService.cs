using System.Security.Cryptography;
using System.Text;
using GroupBuy.Application;
using GroupBuy.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace GroupBuy.Infrastructure;

public sealed class UnconfiguredOtpDelivery : IOtpDeliveryProvider
{
    public bool Supports(string channel) => false;
    public Task Deliver(string channel, string destination, string code, CancellationToken ct) => throw new InvalidOperationException("OTP delivery is not configured.");
}

public sealed class CustomerAuthenticationService(MarketplaceDb db, UserManager<IdentityAccount> users, IConfiguration config,
    TimeProvider clock, IOtpDeliveryProvider delivery, IGoogleCredentialValidator google)
{
    private static BusinessException Invalid(string code = "authentication_invalid") => new(401, "invalid_challenge", "Authentication challenge unavailable or invalid.") { ErrorCode = code };
    private static BusinessException Conflict() => new(409, "account_conflict", "Identity unavailable. Use the existing account recovery process.") { ErrorCode = "identity_conflict" };
    private void Enabled(string channel)
    {
        if (!config.GetValue($"Authentication:{(channel == "email" ? "EmailOtp" : "MobileOtp")}:Enabled", false) || !delivery.Supports(channel))
            throw new BusinessException(503, "authentication_unavailable", "Authentication method is unavailable.") { ErrorCode = "provider_unavailable" };
    }
    // Transaction-scoped cross-instance lock also serializes absent identities. Unique indexes
    // remain the final guard against legacy registration and writes outside this service.
    private Task Lock(CancellationToken ct) => db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(714823091)", ct);
    private async Task<IdentityAccount> Customer(Guid id, CancellationToken ct)
    {
        var account = await SecurityTokens.LockAccount(db, id, ct);
        var roles = await users.GetRolesAsync(account);
        if (roles.Count != 1 || roles[0] != "Customer" || await users.IsLockedOutAsync(account) ||
            !await db.UsersProfile.AnyAsync(x => x.Id == id && x.Status == AccountStatus.Active, ct)) throw Invalid();
        return account;
    }
    private string Hash(CustomerChallenge c, string code)
    {
        var key = Encoding.UTF8.GetBytes(config["Jwt:SigningKey"]!);
        return Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"customer-otp:v1:{c.Id:N}:{c.Channel}:{c.Purpose}:{c.UserId}:{c.Destination}:{code}")));
    }
    public async Task<ChallengeResponse> Request(string channel, OtpRequest request, Guid? actor, CancellationToken ct)
    {
        Enabled(channel);
        var destination = CustomerIdentityNormalization.Normalize(channel, request.Destination);
        await using var tx = await db.Database.BeginTransactionAsync(ct); await Lock(ct);
        var account = actor.HasValue ? await Customer(actor.Value, ct) : null;
        var now = clock.GetUtcNow();
        var history = db.CustomerChallenges.Where(x => x.Channel == channel && x.Destination == destination);
        if (await history.AnyAsync(x => x.CreatedAtUtc > now.AddMinutes(-1), ct))
            throw new BusinessException(429, "challenge_rate_limited", "Try again later.") { ErrorCode = "otp_resend_cooldown" };
        if (await history.CountAsync(x => x.CreatedAtUtc > now.AddHours(-1), ct) >= 5 ||
            await history.CountAsync(x => x.CreatedAtUtc > now.AddDays(-1), ct) >= 20 ||
            (actor.HasValue && await db.CustomerChallenges.CountAsync(x => x.UserId == actor && x.CreatedAtUtc > now.AddHours(-1), ct) >= 10))
            throw new BusinessException(429, "challenge_rate_limited", "Try again later.") { ErrorCode = "rate_limited" };
        await history.Where(x => x.ConsumedAtUtc == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.ConsumedAtUtc, now), ct);
        var c = new CustomerChallenge { Channel = channel, Destination = destination, Purpose = actor.HasValue ? "AccountLink" : "Login",
            UserId = actor, TokenVersion = account?.TokenVersion, CreatedAtUtc = now, ExpiresAtUtc = now.AddMinutes(5) };
        var code = RandomNumberGenerator.GetInt32(0, 1000000).ToString("D6"); c.CodeHash = Hash(c, code);
        db.CustomerChallenges.Add(c); await db.SaveChangesAsync(ct);
        await delivery.Deliver(channel, destination, code, ct);
        await tx.CommitAsync(ct);
        return new(c.Id, c.ExpiresAtUtc);
    }
    public async Task<object> Verify(string channel, OtpVerification request, Guid? actor, CancellationToken ct)
    {
        Enabled(channel);
        await using var tx = await db.Database.BeginTransactionAsync(ct); await Lock(ct);
        var account = actor.HasValue ? await Customer(actor.Value, ct) : null;
        var c = await db.CustomerChallenges.SingleOrDefaultAsync(x => x.Id == request.ChallengeId, ct);
        var now = clock.GetUtcNow();
        if (c is null || c.Channel != channel || c.UserId != actor || c.Purpose != (actor.HasValue ? "AccountLink" : "Login") ||
            c.ConsumedAtUtc != null || (account != null && c.TokenVersion != account.TokenVersion))
        {
            db.AuditLogs.Add(new AuditLog { Action = "LoginFailed", EntityType = "AuthenticationChallenge", EntityId = request.ChallengeId });
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); throw Invalid();
        }
        if (c.FailedAttempts >= 5 || c.ExpiresAtUtc <= now)
        {
            db.AuditLogs.Add(new AuditLog { Action = "LoginFailed", EntityType = "AuthenticationChallenge", EntityId = request.ChallengeId });
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            throw Invalid(c.FailedAttempts >= 5 ? "otp_attempts_exceeded" : "otp_expired");
        }
        if (request.Code is not { Length: 6 } || !request.Code.All(char.IsAsciiDigit) ||
            !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(c.CodeHash), Convert.FromHexString(Hash(c, request.Code))))
        {
            c.FailedAttempts++;
            db.AuditLogs.Add(new AuditLog { Action = c.FailedAttempts == 5 ? "AuthenticationChallengeLocked" : "LoginFailed", EntityType = "AuthenticationChallenge", EntityId = c.Id });
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); throw Invalid(c.FailedAttempts == 5 ? "otp_attempts_exceeded" : "otp_invalid");
        }
        c.ConsumedAtUtc = now;
        var owner = channel == "email" ? await users.FindByEmailAsync(c.Destination) : await db.Users.SingleOrDefaultAsync(x => x.PhoneNumber == c.Destination, ct);
        // Unconfirmed legacy email ownership is not proof of ownership of that account.
        var conflict = actor.HasValue ? owner != null && owner.Id != actor : owner != null && !(channel == "email" ? owner.EmailConfirmed : owner.PhoneNumberConfirmed);
        if (conflict)
        {
            db.AuditLogs.Add(new AuditLog { Action = "AccountLinkConflict", EntityType = "AuthenticationChallenge", EntityId = c.Id });
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); throw Conflict();
        }
        account ??= owner == null ? await Create(ct) : await Customer(owner.Id, ct);
        await Attach(account, channel, c.Destination, ct);
        SecurityTokens.Audit(db, account.Id, actor.HasValue ? (channel == "email" ? "EmailLinked" : "MobileLinked") : "LoginSucceeded");
        await db.SaveChangesAsync(ct);
        object result = actor.HasValue ? await Me(account.Id, ct) : await Login(account, "otp", ct);
        await tx.CommitAsync(ct); return result;
    }
    private async Task<IdentityAccount> Create(CancellationToken ct)
    {
        // Identity supports passwordless accounts; validated email is assigned after creation.
        var account = new IdentityAccount { Id = Guid.NewGuid(), UserName = "customer-" + Guid.NewGuid().ToString("N") };
        SecurityTokens.Check(await users.CreateAsync(account)); SecurityTokens.Check(await users.AddToRoleAsync(account, "Customer"));
        db.UsersProfile.Add(new User { Id = account.Id, CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow() });
        SecurityTokens.Audit(db, account.Id, "CustomerRegistered"); await db.SaveChangesAsync(ct); return account;
    }
    private async Task Attach(IdentityAccount account, string channel, string destination, CancellationToken ct)
    {
        var profile = await db.UsersProfile.SingleAsync(x => x.Id == account.Id, ct);
        if (channel == "email")
        {
            if (account.Email != null && account.NormalizedEmail != destination) throw Conflict();
            account.Email = destination; account.EmailConfirmed = true; profile.Email = destination; profile.EmailVerified = true;
        }
        else
        {
            if (account.PhoneNumber != null && account.PhoneNumber != destination) throw Conflict();
            account.PhoneNumber = destination; account.PhoneNumberConfirmed = true; profile.PhoneNumber = destination; profile.PhoneNumberVerified = true;
        }
        profile.UpdatedAt = clock.GetUtcNow(); SecurityTokens.Check(await users.UpdateAsync(account));
        SecurityTokens.Audit(db, account.Id, channel == "email" ? "EmailVerified" : "MobileVerified");
    }
    public async Task<object> Google(GoogleCredential request, Guid? actor, CancellationToken ct)
    {
        if (!config.GetValue("Authentication:Google:Enabled", false)) throw new BusinessException(503, "authentication_unavailable", "Authentication method is unavailable.") { ErrorCode = "provider_unavailable" };
        GoogleIdentity identity;
        try { identity = await google.Validate(request.Credential, ct); }
        catch (BusinessException ex) when (ex.StatusCode == 401)
        {
            db.AuditLogs.Add(new AuditLog { Action = "LoginFailed", EntityType = "GoogleAuthentication", EntityId = Guid.Empty });
            await db.SaveChangesAsync(ct); throw;
        }
        await using var tx = await db.Database.BeginTransactionAsync(ct); await Lock(ct);
        var account = actor.HasValue ? await Customer(actor.Value, ct) : null;
        var owner = await users.FindByLoginAsync("Google", identity.Subject);
        var email = identity.Email == null ? null : CustomerIdentityNormalization.Normalize("email", identity.Email);
        var emailOwner = email == null ? null : await users.FindByEmailAsync(email);
        if ((actor.HasValue && owner != null && owner.Id != actor) || (owner == null && emailOwner != null && emailOwner.Id != actor))
        {
            db.AuditLogs.Add(new AuditLog { Action = "AccountLinkConflict", EntityType = "User", EntityId = actor ?? Guid.Empty });
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); throw Conflict();
        }
        account ??= owner == null ? await Create(ct) : await Customer(owner.Id, ct);
        if (owner == null)
        {
            var existing = await users.GetLoginsAsync(account);
            if (existing.Any(x => x.LoginProvider == "Google" && x.ProviderKey != identity.Subject)) throw Conflict();
            SecurityTokens.Check(await users.AddLoginAsync(account, new("Google", identity.Subject, "Google")));
            if (email != null && account.Email == null) await Attach(account, "email", email, ct);
            var profile = await db.UsersProfile.SingleAsync(x => x.Id == account.Id, ct);
            if (string.IsNullOrWhiteSpace(profile.DisplayName) && !string.IsNullOrWhiteSpace(identity.Name)) profile.DisplayName = identity.Name.Trim()[..Math.Min(100, identity.Name.Trim().Length)];
            SecurityTokens.Audit(db, account.Id, "GoogleIdentityLinked");
        }
        SecurityTokens.Audit(db, account.Id, "LoginSucceeded"); await db.SaveChangesAsync(ct);
        object result = actor.HasValue ? await Me(account.Id, ct) : await Login(account, "google", ct);
        await tx.CommitAsync(ct); return result;
    }
    private async Task<CustomerLogin> Login(IdentityAccount account, string method, CancellationToken ct) =>
        new(await SecurityTokens.Issue(account, users, config, clock, false, method), true, await Me(account.Id, ct));
    public async Task<CustomerProfile> Me(Guid id, CancellationToken ct)
    {
        var p = await db.UsersProfile.AsNoTracking().SingleAsync(x => x.Id == id, ct);
        var a = await db.Users.AsNoTracking().SingleAsync(x => x.Id == id, ct);
        var terms = config["Authentication:Consent:TermsVersion"]; var privacy = config["Authentication:Consent:PrivacyVersion"];
        var consent = await db.UserConsents.Where(x => x.UserId == id).ToListAsync(ct);
        var termsAccepted = consent.Where(x => x.DocumentType == "TermsOfService").OrderByDescending(x => x.AcceptedAtUtc).ThenByDescending(x => x.Id).FirstOrDefault()?.DocumentVersion;
        var privacyAccepted = consent.Where(x => x.DocumentType == "PrivacyPolicy").OrderByDescending(x => x.AcceptedAtUtc).ThenByDescending(x => x.Id).FirstOrDefault()?.DocumentVersion;
        var required = string.IsNullOrWhiteSpace(terms) || string.IsNullOrWhiteSpace(privacy) ||
            !consent.Any(x => x.DocumentType == "TermsOfService" && x.DocumentVersion == terms) || !consent.Any(x => x.DocumentType == "PrivacyPolicy" && x.DocumentVersion == privacy);
        var email = string.IsNullOrWhiteSpace(a.Email) ? null : a.NormalizedEmail ?? a.Email;
        var mobile = string.IsNullOrWhiteSpace(a.PhoneNumber) ? null : a.PhoneNumber;
        var emailVerified = a.EmailConfirmed && email != null;
        var phoneVerified = a.PhoneNumberConfirmed && mobile != null;
        var googleConnected = await db.UserLogins.AsNoTracking().AnyAsync(x => x.UserId == id && x.LoginProvider == "Google", ct);
        return new(id, p.DisplayName, !string.IsNullOrWhiteSpace(p.DisplayName), emailVerified, phoneVerified, required, terms, privacy,
            email, mobile, new(googleConnected, emailVerified, phoneVerified), new(termsAccepted, privacyAccepted, required));
    }
    public async Task<CustomerProfile> Profile(Guid id, ProfileCompletion request, CancellationToken ct)
    {
        var name = request.DisplayName?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || name.Any(char.IsControl)) throw new BusinessException(400, "invalid_name", "Supply a name of 1–100 characters.");
        await using var tx = await db.Database.BeginTransactionAsync(ct); await Lock(ct); await Customer(id, ct);
        var p = await db.UsersProfile.SingleAsync(x => x.Id == id, ct); p.DisplayName = name; p.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return await Me(id, ct);
    }
    public async Task<CustomerProfile> Consent(Guid id, ConsentAcceptance request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.TermsVersion) || string.IsNullOrWhiteSpace(request.PrivacyVersion) ||
            request.TermsVersion != config["Authentication:Consent:TermsVersion"] || request.PrivacyVersion != config["Authentication:Consent:PrivacyVersion"])
            throw new BusinessException(400, "invalid_consent_version", "Accept the currently configured document versions.");
        await using var tx = await db.Database.BeginTransactionAsync(ct); await Lock(ct); await Customer(id, ct);
        foreach (var (type, version) in new[] { ("TermsOfService", request.TermsVersion), ("PrivacyPolicy", request.PrivacyVersion) })
            if (!await db.UserConsents.AnyAsync(x => x.UserId == id && x.DocumentType == type && x.DocumentVersion == version, ct))
                db.UserConsents.Add(new() { UserId = id, DocumentType = type, DocumentVersion = version, AcceptedAtUtc = clock.GetUtcNow() });
        SecurityTokens.Audit(db, id, "ConsentAccepted"); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return await Me(id, ct);
    }
}

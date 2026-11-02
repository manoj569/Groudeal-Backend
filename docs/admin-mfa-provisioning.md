# Admin MFA and controlled provisioning

Sprint 5. Applies to every Admin in every environment. There is no password-only Admin bypass, public role grant, public MFA reset or automatic production administrator.

## Operator prerequisites

Use a separate privileged operational identity from a protected workstation/job. Verify the person's identity and authorization independently, record an approved change reference and arrange supervised enrollment. The account owner registers their own strong password over HTTPS as a normal Customer; the command never accepts/creates/prints a password, JWT, enrollment secret or recovery code. Do not use command-line arguments, shell history, tickets or logs for credentials.

Provisioning requires explicit `AdminProvisioning__Enabled=true` for the one-shot process and a connection permitted to assign Admin and authorize enrollment. The API host rejects this flag in Production when no operational command is present. `scripts/grant-runtime.sql` denies the runtime role Admin membership changes and MFA reset/enrollment authorization. Migration/provisioning credentials must never be mounted on the long-running API. The CLI's trust boundary is operator process/database access; it does not authenticate a human from an arbitrary operator-id string. Restrict that access and reconcile its reference with your external operator audit.

Set Production configuration, verified-TLS operator connection, signing key, issuer/audience and the same Data Protection application/key directory/certificates as the API. Deliver secrets through the operator job's secret manager or restricted process environment. `/run/secrets` key-per-file settings take precedence over environment variables: a CLI container must mount its separate operator connection file, not the API runtime file. Do not merely override the environment while retaining the runtime secret mount.

Run the published .NET 10 API assembly (replace bracketed nonsecret identifiers):

```powershell
dotnet GroupBuy.API.dll --admin-operation provision --user-id <existing-account-uuid> --operator-id <verified-operator-id> --reference <approved-change-id>
```

This command grants only the fixed Admin role to an existing active profile, increments token version, records `AdminProvisioned` transactionally and opens a 15-minute first-enrollment window if MFA is not configured. It never marks email/phone verified or treats an FSSAI value as proof. Repeat provisioning does not create an account, duplicate membership, reset MFA or extend an expired window. An existing legacy Admin without MFA requires the controlled reset operation below; rerunning provision is not an enrollment bypass.

To revoke:

```powershell
dotnet GroupBuy.API.dll --admin-operation revoke --user-id <existing-account-uuid> --operator-id <verified-operator-id> --reference <new-approved-change-id>
```

`AdminRevoked` and token-version increment invalidate outstanding tokens on subsequent requests. Already-authorized in-flight requests are not retroactively cancelled. Repeating the same operation/operator/reference is a no-op after its committed audit event, including reset. Use a new approved reference for a genuinely new operation. Disable/remove provisioning permission immediately after the one-shot job; never configure it on the normal host.

## Password and MFA protocol

`POST /api/v1/auth/login` retains Customer/Seller access-token behavior. For Admin it returns `accessToken:null`, `mfaChallenge`, `enrollmentRequired` and `expiresAtUtc`. The challenge is 256 random bits, stored as SHA-256 only, expires after five minutes and is not a JWT. It is bound to the account, token version, Identity security stamp and enrollment/login purpose. A fresh password login invalidates the previous challenge. Active status, current Admin role, lockout and security context are rechecked under an account row lock at verification.

For first enrollment, explicitly POST `{challenge}` to `/auth/mfa/enrollment`. Only an authorized, unenrolled, unexpired enrollment challenge can obtain `{sharedKey,issuer,accountName}`. Add the key manually to an authenticator app as TOTP/SHA1, six digits, 30-second period. No external QR service receives the secret. The UI displays it in memory only; cancel/navigation/expiry removes the enrollment view. Supervise initial enrollment; do not leave the short bootstrap window unattended or hand account credentials to another person.

POST `{challenge,code,useRecoveryCode:false}` to `/auth/mfa/verify`. Supported ASP.NET Identity verifies TOTP; no application OTP algorithm was implemented. Identity accepts its standard adjacent time steps. A persistent per-account accepted-code digest blocks replay for ten minutes, including enrollment and concurrent/fresh challenges. This conservative horizon may reject a rare repeated numeric code; wait for another authenticator step. Keep servers/authenticator devices time synchronized.

Success consumes the challenge and returns `{accessToken,expiresAtUtc,recoveryCodes}`. Enrollment enables Identity two-factor, closes enrollment permission, increments token version and returns ten recovery codes once. An already-enrolled login returns an empty recoveryCodes array. Admin JWTs include `amr:mfa` and last at most ten minutes (or a shorter configured JWT duration). The Admin policy requires the role and MFA claim, and JWT validation rechecks current account version/active status/role and enabled MFA. An opaque challenge or even a correctly signed Admin JWT without MFA cannot authorize Admin access.

MFA endpoints have an IP budget of 30/minute; login/register retain 10/minute, Admin operations 60/minute per authenticated actor, and global limits remain. Each challenge permits at most five failed verifications. Identity's account-wide five-failure/15-minute lockout persists across fresh password challenges; knowing the correct password does not reset failed MFA attempts. Only completed MFA resets that failure counter. All responses use no-store and secret values are never deliberately logged.

## Recovery and reset

The UI requires the operator to store enrollment recovery codes securely before installing the session/navigating. Codes are not localStorage, IndexedDB, service-worker cache, query-cache or URL data. Each code is single-use. Login with password, then POST the challenge/code with `useRecoveryCode:true`; concurrent reuse succeeds once. Redemption increments token version, invalidating earlier sessions, and records `AdminRecoveryCodeRedeemed`. Codes cannot enroll a fresh authenticator or reset MFA through HTTP.

If an enrollment success response is lost, the challenge is already consumed; do not blindly retry it. The configured authenticator still works with a fresh code/challenge. Recovery codes cannot be displayed again. For lost factors/codes, verify identity through the separately approved recovery procedure, obtain a new change reference and run:

```powershell
dotnet GroupBuy.API.dll --admin-operation reset-mfa --user-id <existing-account-uuid> --operator-id <verified-recovery-operator-id> --reference <new-approved-change-id>
```

This resets the authenticator using Identity, clears recovery codes and account lockout/failed-attempt counters, disables MFA, increments token version/security stamp, records `AdminMfaReset` and opens a new 15-minute supervised enrollment window. It creates no usable Admin JWT. Previous challenges/tokens/codes fail. A repeated reset reference does not rotate again. There is no self-service Admin disable/reset route. Lost-password recovery is a separate identity-verification/support gate; no fake email/SMS recovery provider was added.

## Secrets at rest and audit

`ProtectedIdentityStore` uses ASP.NET Data Protection through Identity's supported token-store extension points. Authenticator keys and recovery-code values are encrypted with purposes bound to application/user/provider/token name; copying ciphertext between users/token types fails. Identity's recovery-code removal is serialized by the account row lock. Passwords remain framework hashes. Challenge hashes and accepted-code digests contain no plaintext secret.

Production requires an existing persistent key directory plus a valid private-key certificate to encrypt the Data Protection master key ring. Restrict access, back up the key ring and private certificate separately from DB backups, and preserve previous decryption certificates during tested rotation. Configure `DataProtection:PreviousCertificates:<n>:Path/Password` for retained old private certificates. Never delete old keys/certificates merely because JWTs expired; enrolled authenticator ciphertext still needs them. Development key rings are intentionally separate and may be unencrypted; they are never a production template.

Events: AdminProvisioned, AdminRevoked, AdminMfaChallengeCreated, AdminMfaEnrollmentViewed, AdminMfaVerificationFailed, AdminMfaEnrolled, AdminMfaVerified, AdminRecoveryCodeRedeemed and AdminMfaReset. Audit stores subject/action/time and safe references, not factors or request bodies. Runtime credentials cannot update/delete audit rows. Audit retention is retain-by-default with no automatic purge; legal retention, restricted export/archive and access-review ownership must be approved before a live pilot.

Synthetic tests use only .invalid accounts. An optional externally generated `Demo__AdminAuthenticatorKey` is accepted only by explicit Development seeding to support real MFA automation, never Production. The real enrollment browser fixture registers a fresh synthetic Customer and invokes the actual controlled CLI; no real administrator is created.

Revocation also works for a suspended account so containment does not depend on reactivating it. Reset remains limited to an active Admin and never reactivates a suspended account.

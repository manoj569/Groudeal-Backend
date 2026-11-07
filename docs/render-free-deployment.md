# Render Free + Neon controlled deployment

Scope: backend code and local verification. No actual Neon connection, production migration, Render deployment, live provider activation, legal approval or commercial approval is performed by this task. Render Free is a controlled MVP/integration host, not an availability guarantee for payment deadlines or continuous workers.

Module 2 adds the forward-only `20261107060508_HomeDiscoveryDemands` migration after the Data Protection migration described below. Before deploying that API revision, apply the reviewed migration with the separate migrator and review/apply the updated runtime grant script. Its discovery configuration, API contracts and rollout prerequisites are documented in [Module 2 discovery](module2-discovery.md). This does not change the Render ingress contract or activate Pilot, payments or authentication providers.

## Conditional ingress resolution — verify before customer traffic

The API retains explicit trusted-proxy IPs, one symmetric `X-Forwarded-For`/`X-Forwarded-Proto` hop, HTTPS redirection, explicit hosts and explicit CORS. Forwarded headers from other peers are ignored. Unrestricted `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` is rejected in Production.

Explicit trusted proxy IPs remain preferred where a verified, supportable ingress peer set and compatible forwarding behavior are available. Enter only verified ingress addresses in `Proxy__KnownProxies__0`, etc. Outbound IP addresses, a DNS result for the public hostname, guessed private ranges and `0.0.0.0/0` are not substitutes.

Render Free public-edge mode is a separate, explicit alternative. Render documents that public HTTP is redirected to HTTPS before forwarding, the application's listening port is not directly public, TLS terminates at Render, and Free web services cannot receive private-network traffic. Under this reviewed topology, the adapter reconstructs the external HTTPS scheme without reading forwarded headers. This is a platform-isolation assumption, not per-request authentication. `RENDER=true`, Host and other environment markers do not independently prove that isolation or the Free plan. The operator must verify and acknowledge it; the application cannot detect a later plan/network change.

Set these non-secret application variables for the current deployment:

```text
Hosting__RenderFreeIngress__Enabled=true
Hosting__RenderFreeIngress__PublicEdgeIsolationAcknowledged=true
Hosting__RenderFreeIngress__ExternalHostname=groudeal-backend.onrender.com
AllowedHosts=groudeal-backend.onrender.com
PORT=10000
```

Retain the existing explicit CORS origin `https://groudeal-frontend.vercel.app` in `Cors__Origins__0`. Render must supply `RENDER=true`, `RENDER_SERVICE_TYPE=web`, `RENDER_EXTERNAL_HOSTNAME=groudeal-backend.onrender.com`, and `RENDER_EXTERNAL_URL=https://groudeal-backend.onrender.com`. Do not forge platform markers to pass validation. Mode activation requires Production, all matching settings, a single exact onrender.com AllowedHosts hostname, and no `Proxy__KnownProxies` entries. Unrestricted forwarding remains forbidden. Invalid enabled configuration fails during startup; the mode defaults to disabled.

Only HTTP with a present socket peer on the configured listener and that exact public Host (no port or port 443) is normalized. Both loopback and non-loopback delivery are supported. Health paths are excluded regardless of peer; local CLI readiness remains HTTP. HSTS and HTTPS redirection remain installed. Explicit trusted-proxy forwarding remains unchanged in deployments with this mode disabled. Do not combine the two modes.

Production diagnostics supplied by the operator showed public requests arriving as HTTP, the exact configured public Host, no Host port, LocalPort 10000 and a loopback peer. The earlier non-loopback requirement therefore rejected legitimate edge delivery and caused the remaining HTTPS self-redirect. Some health probes had non-loopback peers; peer classification is not a public-request/health-probe discriminator. This loopback behavior is an observation of this service, not a documented guarantee of Render's future socket implementation. The final adapter does not classify peer addresses or read forwarded headers, and never changes RemoteIpAddress.

**The acknowledged isolation contract includes a trusted local runtime.** There is no request-level proof that distinguishes an edge-delivered loopback request from a local process making the identical HTTP request with the configured public Host and listener port. Ordinary health probes are distinguished by the two exempt paths. Other requests with a different Host are rejected by host filtering; different listener ports or non-public Host ports do not reconstruct the scheme. A trusted local tool deliberately using the public host/port contract on a normal API path will receive reconstruction too; authentication and authorization still apply. Host/port are constraints, not credentials. Do not run untrusted co-resident processes, expose a local request relay/SSRF path, or treat the adapter as protection from container compromise. If isolation from arbitrary local callers is required, use authenticated ingress or an independently isolated listener; this mode cannot supply that boundary.

The security rationale is conditional: Render enforces public HTTP-to-HTTPS redirection before forwarding; the application port is not directly public; Free services cannot receive private ingress; and the application's local runtime is trusted. Thus the HTTP socket is an internal transport for the externally HTTPS service. Startup marker checks detect inconsistent configuration but do not cryptographically attest ingress or prove the Free plan. The explicit mode must remain disabled in local development; opting in outside Production fails startup. Temporary per-request Information diagnostics have been removed entirely after reproducing the observed condition in regression tests.

No original-client-IP trust is established: the socket peer remains the client IP. IP-based rate budgets can consequently be shared behind Render; forged X-Forwarded-For does not create new budgets. Destination/user quotas and authentication controls remain unchanged.

**Changing plan, local-process trust or infrastructure to permit inbound private/direct traffic requires a new ingress review before this mode remains enabled.** If that isolation cannot be established, the ingress blocker remains and an authenticated/trusted ingress architecture is required. Any direct caller reaching this listener with the expected Host would be indistinguishable from the edge; Host/port checks do not solve that problem.

Rollback: set `Hosting__RenderFreeIngress__Enabled=false` and restart/redeploy the reviewed version. This restores the previous strict scheme/HTTPS behavior; without verified trusted proxies, API requests through Render will again redirect repeatedly. Health exemptions remain. Do not disable redirection, clear trust lists or switch to Development.

After controlled deployment, verify public HTTP redirects at the edge, HTTPS API requests do not self-redirect, expected 200/401/403 responses, HSTS, exact allowed/disallowed CORS and preflight behavior, both health paths, and the Free-plan/no-direct/no-private-ingress assumptions. Local tests cannot prove the deployed platform boundary. No deployment is performed by the local verification script.

References: [Render web services: ports and TLS termination](https://render.com/docs/web-services), [Render private networking: Free inbound restriction](https://render.com/docs/private-network), [Render environment variables](https://render.com/docs/environment-variables), [Microsoft forwarded-header security guidance](https://learn.microsoft.com/en-us/aspnet/core/breaking-changes/8/forwarded-headers-unknown-proxies?view=aspnetcore-10.0).

## 1. Prepare the database and deployment order

Target context supplied by the owner: Neon project **Groudeal**, production branch, Singapore region; Render service Singapore. Actual host/database/role values must come from the approved target. No example domain or credential here is active.

1. Keep customer traffic and automatic deployment disabled while preparing schema and secrets. Back up the intended database before upgrades.
2. Bootstrap with an authorized Neon administrator. Create a distinct migration identity that owns application schema objects, and a standalone runtime LOGIN role without superuser, database/role creation, bypass-RLS or inherited administrative memberships. Neon console-created administrative roles may have extra membership; inspect actual privileges. Do not use that administrator/owner as the permanent API role.
3. Generate/review an idempotent migration script locally with the pinned .NET SDK/tool:

   ```powershell
   dotnet tool restore
   dotnet restore GroupBuy.sln
   dotnet ef migrations script --idempotent --project src/GroupBuy.Infrastructure --output .local/render-migrations.sql
   ```

4. Apply reviewed SQL using a **direct Neon endpoint** and the migration identity, with TLS hostname verification. Supply `PGHOST`, `PGDATABASE`, `PGUSER`, `PGPASSWORD` and `PGSSLMODE=verify-full` through the approved secret mechanism in a dedicated operator shell; do not put passwords on command lines or in committed files.

   ```powershell
   psql --set ON_ERROR_STOP=1 --file .local/render-migrations.sql
   psql --set ON_ERROR_STOP=1 --set app_role=<ACTUAL_STANDALONE_RUNTIME_ROLE> --file scripts/grant-runtime.sql
   ```

   Replace the angle-bracket role placeholder before execution. The grant template is only for a dedicated Groudeal database and changes PUBLIC/schema grants, RLS and security triggers. Review it before running. Its ownership/membership checks intentionally reject privileged runtime roles. No script is applied to Neon by Codex.
5. Verify applied migration history, runtime permissions, backups and the intended branch/database. The new additive migration is `20261106155504_PersistDataProtectionKeys`, following `20261106104909_CustomerPasswordlessAuthentication`.
6. Configure Render with runtime credentials only. Migration/bootstrap credentials must not be available to the web service. Deploy the reviewed commit after the migration/grants succeed. The API never runs EF migrations on startup. Do not rely on a web-start command as a migration job; execute the operator step separately for Free hosting.

Neon pooled endpoints use transaction pooling. Runtime code uses transaction-scoped advisory locks and ordinary EF operations; it does not depend on session-scoped advisory locks. Use direct connections for migrations, role administration and logical backups. [Neon pooling documentation](https://neon.com/docs/connect/connection-pooling).

## 2. Configure the Npgsql runtime connection

Set `ConnectionStrings__Database` as a Render secret, using **Npgsql semicolon syntax**, not an unparsed `postgres://` URL:

```text
Host=<NEON_POOLER_HOST>;Database=<ACTUAL_DATABASE>;Username=<RUNTIME_ROLE>;Password=<SECRET>;SSL Mode=VerifyFull;Pooling=true;Maximum Pool Size=20;Timeout=15;Command Timeout=30;Include Error Detail=false;
```

The example pool/timeouts are supported technical settings, not a statement of Neon plan capacity. Account for all API instances/workers within the actual database connection budget. Production permits pool size 1–200, connect timeout 1–30 seconds, command timeout 1–60 seconds, requires pooling and rejects error detail. EF commands retain the existing 30-second bound. Use the actual Neon hostname matching its public TLS certificate and trusted CA chain. Never set `Trust Server Certificate`, disable hostname validation, or substitute `SSL Mode=Require`.

## 3. Configure encrypted PostgreSQL Data Protection

Required Render secrets/settings:

```text
ASPNETCORE_ENVIRONMENT=Production
DataProtection__Persistence=Postgres
DataProtection__CertificateBase64=<BASE64_PKCS12_SECRET>
DataProtection__CertificatePassword=<PFX_PASSWORD_SECRET>
```

`DataProtection__KeyPath` and `DataProtection__CertificatePath` are **not needed in Postgres mode with a base64 certificate**. Configure exactly one current certificate source. Microsoft’s official `Microsoft.AspNetCore.DataProtection.EntityFrameworkCore` package is pinned to `10.0.14`, matching the existing ASP.NET/EF serviced version. It supplies `PersistKeysToDbContext<MarketplaceDb>`/`IDataProtectionKeyContext`; no custom cryptographic format is used.

The table `data_protection_keys` contains the official `Id`, `FriendlyName`, `Xml` fields. XML is encrypted with `ProtectKeysWithCertificate`; database access alone does not reveal the master key. Current PFX bytes load directly in memory with `X509CertificateLoader` and `EphemeralKeySet`. Current certificates need an RSA private key of at least 2048 bits and a valid current date interval. Missing, ambiguous, malformed, wrong-password, public-only, weak, expired or future certificates fail closed. Base64/password/private keys are never logged. Production rejects existing unencrypted key XML rather than silently reusing it. Startup validates existing usable key encryptors and performs the existing protect/unprotect probe; inaccessible key storage, missing schema/permissions or undecryptable old keys prevent startup with a sanitized error.

Runtime grants allow SELECT and INSERT on the key table plus necessary sequence privileges. UPDATE/DELETE/DDL are not granted for this table; the official repository is append-only. The same existing `ConnectionStrings__Database` and application name `Groudeal.Identity.v1` are reused across instances.

Reference: [Microsoft Data Protection key persistence](https://learn.microsoft.com/aspnet/core/security/data-protection/implementation/key-storage-providers), [key encryption and certificate rotation](https://learn.microsoft.com/aspnet/core/security/data-protection/configuration/overview).

### Existing disk-backed environments

Omitted persistence mode continues to mean `FileSystem` for backward compatibility. That production mode still requires an existing persistent absolute `DataProtection__KeyPath` and an encryption certificate. Existing `DataProtection__CertificatePath` and `DataProtection__PreviousCertificates__n__Path`/`Password` remain supported.

Changing the storage mode does not import an existing key ring. Before moving an environment with encrypted MFA/delivery data from files to PostgreSQL, perform a separately approved backup/import of its **original encrypted key XML and revocation records**, retaining IDs, application name and decryption certificates, and prove old payload decryption. Do not replace the ring with fresh keys or reset MFA to hide an absent-key problem. Keep existing staging in FileSystem mode until that transfer is approved and verified. No staging key material is read or moved in this task. A fresh Neon installation has no old ring to transfer.

### Certificate generation — owner executes separately

Use PowerShell 7+ on a trusted workstation and the approved secret manager. These commands are instructions only; Codex does not generate a production certificate.

```powershell
$dpWork = Join-Path $env:USERPROFILE '.groudeal-certificate-preparation'
New-Item -ItemType Directory -Path $dpWork -Force | Out-Null
$dpIdentity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
icacls $dpWork /inheritance:r /grant:r "${dpIdentity}:(OI)(CI)F" | Out-Null
$dpRsa = [Security.Cryptography.RSA]::Create(3072)
$dpRequest = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
    'CN=Groudeal Data Protection', $dpRsa,
    [Security.Cryptography.HashAlgorithmName]::SHA256,
    [Security.Cryptography.RSASignaturePadding]::Pkcs1)
$dpCertificate = $dpRequest.CreateSelfSigned(
    [DateTimeOffset]::UtcNow.AddMinutes(-5), [DateTimeOffset]::UtcNow.AddYears(2))
$dpPassword = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$dpBytes = $dpCertificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $dpPassword)
$dpBase64 = [Convert]::ToBase64String($dpBytes)
$dpPfxPath = Join-Path $dpWork 'groudeal-data-protection.pfx'
[IO.File]::WriteAllBytes($dpPfxPath, $dpBytes)
```

Transfer `$dpBase64` and `$dpPassword` directly into the corresponding approved secret fields without printing them, committing them, putting them in command arguments, or saving them in shell history/transcripts. Keep the encrypted PFX and its password in separately protected backup/secret storage. This is a Data Protection wrapping certificate, not the public HTTPS certificate; Render manages public HTTPS separately.

After verified secret/backup storage, dispose `$dpCertificate`/`$dpRsa`, clear the byte array with `[Array]::Clear($dpBytes,0,$dpBytes.Length)`, remove sensitive variables and close the preparation shell. Delete preparation copies only with `Remove-Item -LiteralPath <VERIFIED_EXACT_FILE_PATH>` after checking the resolved file stays inside the preparation directory; no recursive cleanup command is needed. Never commit PFX/PEM/private-key/password/base64 material or `.env` files. Keep original certificates for recovery until all protected payloads/backups requiring them have expired or been reprotected under a reviewed procedure.

### Rotation

1. Prepare a new currently valid wrapping certificate through the same owner procedure.
2. Retain the old certificate as secret configuration:

   ```text
   DataProtection__PreviousCertificates__0__Base64=<OLD_PFX_BASE64_SECRET>
   DataProtection__PreviousCertificates__0__Password=<OLD_PFX_PASSWORD_SECRET>
   ```

3. Replace the current certificate secrets and restart all instances with the same current/previous set. New key material uses the current certificate; existing XML remains protected with the earlier certificate. Retain previous certificates for historical decryption, including after their issuance validity expires. A previous certificate still needs its supported RSA private key.
4. Verify startup and decryption of an older representative protected payload. Back up the ring and certificates together under the approved retention policy. Existing tests exercise cross-instance decryption and rotation. Never delete old rows/certificates merely because a new certificate is active.

## 4. Render service configuration

Choose Web Service → connected backend GitHub repository → Docker runtime → actual reviewed branch → **Singapore** → Free compute. Use repository-root `Dockerfile`, build context `.`, and its default entrypoint `dotnet GroupBuy.API.dll`. No frontend directory is involved. Keep automatic deployment off while applying migrations/configuration. Do not configure a persistent disk.

The app respects Render's `PORT` and binds `http://0.0.0.0:<PORT>`. Use Render's default `10000` unless deliberately approved otherwise. Supported unprivileged ports are 1024–65535; avoid Render-reserved ports. With no PORT the existing Docker/local 8080 behavior remains. Docker's EXPOSE is informational; it does not override PORT. The existing `--healthcheck` command now probes the configured PORT.

**Render Health Check Path: `/health/ready`.** It is unauthenticated and returns status only. It checks the existing worker heartbeat, database connectivity and migration state with a two-second dependency budget. `/health/live` is a lighter liveness probe. Both allow internal HTTP probes; business endpoints continue HTTPS enforcement. Health is not legal/provider/payment acceptance.

### Initial environment matrix

| Variable | Initial safe value / rule |
| --- | --- |
| `ASPNETCORE_ENVIRONMENT` | `Production` |
| `ConnectionStrings__Database` | Secret runtime Npgsql string above |
| `Jwt__SigningKey` | Secret generated from 64 cryptographically random bytes; stable across restarts/instances |
| `Jwt__Issuer` | Exact approved issuer, e.g. existing `GroupBuy`; choose consistently with token consumers |
| `Jwt__Audience` | Exact approved audience, e.g. existing `GroupBuy.Client` |
| `Jwt__ExpiryMinutes` | `15`; supported 1–60, Admin capped at 10 |
| `AllowedHosts` | `<ACTUAL_RENDER_HOST>`; hostnames only, semicolon-separated for multiple approved hosts; no `*`, scheme or port |
| `Cors__Origins__0` | `<APPROVED_HTTPS_FRONTEND_ORIGIN>`; additional approved origins use indices 1, 2; no wildcard, trailing slash, path, credentials, query or fragment |
| `DataProtection__Persistence` | `Postgres` |
| `DataProtection__CertificateBase64` | Secret PKCS#12 contents |
| `DataProtection__CertificatePassword` | Secret PFX password |
| `DataProtection__PreviousCertificates__n__Base64` / `Password` | Retained decryption certificates when rotating |
| `Proxy__KnownProxies__0` | Verified ingress peer only in explicit-proxy mode; absent in Render Free public-edge mode |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED` | Omit or `false`; unrestricted `true` is rejected |
| `PORT` | Render-supplied, normally `10000` |
| `HealthChecks__Host` | Optional actual allowed hostname for internal CLI health probe when needed |
| `Operations__WorkerMaxAttempts` | `10`; supported 1–100 |
| `Operations__ConfirmationMinutes` | `60`; supported 1–1440 |
| `Operations__PickupHours` | `48`; supported 1–168 |
| `AdminProvisioning__Enabled` | `false`; never leave one-shot provisioning authority enabled on the API |
| `Demo__Password`, `Demo__AdminAuthenticatorKey` | **Absent**, not fixture values |
| `Pilot__Enabled` | `false` |
| `Payments__Enabled` | `false` |
| `Payments__SandboxEnabled` | `false` |
| `Payments__Provider` | Omit until approved activation; never `Sandbox` in Production |
| `Authentication__Google__Enabled` | `false` |
| `Authentication__MobileOtp__Enabled` | `false` |
| `Authentication__EmailOtp__Enabled` | `false` |
| `Authentication__Consent__TermsVersion` | Omit until approved; later use `<APPROVED_TERMS_VERSION>` |
| `Authentication__Consent__PrivacyVersion` | Omit until approved; later use `<APPROVED_PRIVACY_VERSION>` |

Do not paste placeholder strings as approved legal versions or active origins/hosts. Missing legal versions are allowed for an inactive initial host, return `consentRequired=true`, and cannot be accepted. Production customer-provider or pilot activation requires both legal versions; the backend does not approve legal text.

Production can also load operator-mounted `/run/secrets` key-per-file configuration; those settings override environment configuration. Render's recommended setup here uses environment secrets, without secret files or persistent local paths. Do not configure both current certificate sources.

## 5. Separate activation gates

**Payments:** Initial provider is the existing disabled gateway; no fake Test or Live fallback. Later controlled pilot requires `Pilot__Enabled=true`, `Payments__Enabled=true`, `Payments__Provider=Razorpay`, `Payments__Razorpay__Mode=Test`, `Payments__Razorpay__KeyId`, `KeySecret`, `WebhookSecret`; optional `Payments__Razorpay__AccountId` binds webhook account when provided. Real secrets come only from the authorized account. Although the lower-level Razorpay validator recognizes `Mode=Live` with matching key prefix and `Payments__Razorpay__LiveApproved=true`, the current Production controlled-pilot gate additionally requires Test mode. Live activation is outside this supported controlled-pilot path and needs separate review. No acceptance is claimed.

**Pilot/delivery:** Paid pilot activation also requires `Pilot__Approved=true`, `Pilot__ServiceAreas__0__Id`, `Name`, `PostalCodes__0` (additional indexed areas/postal codes), and `Pilot__DeliveryFeeMinor` plus `Pilot__DeliveryFeeApproved=true`. Areas must pass existing uniqueness/length/real-postal checks. No real area or delivery fee is approved yet: leave pilot disabled and omit those values. Founder delivery stays unavailable. Legacy non-financial seller pickup remains available under existing seller/campaign rules and is free. Paid pilot pickup remains subject to its existing payment/pilot prerequisites; do not invent delivery settings merely to enable it.

**Google:** Activate only with approved `Authentication__Google__ClientId`, enabled flag, approved legal versions, approved frontend origin/OAuth configuration and real end-to-end acceptance. No client secret is needed for backend ID-token verification; issuer/audience/signature/expiry/subject checks remain. Actual external-login membership determines the connected flag.

**OTP:** An enabled email/mobile feature requires a reviewed registered `IOtpDeliveryProvider` supporting that channel, bounded delivery timeout, secret-managed provider configuration and sandbox acceptance. The default provider supports neither channel and startup rejects enabled-but-unconfigured methods. No console/log OTP fallback exists. There are no invented SMTP/SMS secret keys: provider-specific names are defined only when a real adapter is approved.

**Legal:** Publish approved documents separately; configure their exact versions. No document content or acceptance approval is fabricated. Persisted acceptance remains auditable and `/me` reports historical accepted and configured current versions separately.

## 6. Acceptance and limitations

Before enabling customer traffic: resolve ingress trust; verify real hostname/HTTPS redirects and spoofed-header rejection; apply schema/grants with separate credentials; verify runtime connection TLS and DP restart/decryption; confirm exact CORS; approve legal content; then accept each enabled provider separately. Keep payments, pilot, Google and OTP disabled until their own gates pass. Do not expose bootstrap/migrator secrets to Render runtime.

Render Free can spin down after idle time, has cold starts/resource constraints and loses local filesystem changes on restart/redeploy. PostgreSQL key persistence removes the key-ring disk dependency but cannot provide continuous worker uptime or production-grade availability. Neon cold starts, connection budgets, backups, monitoring, certificate/JWT rotation and geographic/data-processing approval remain operational responsibilities. Do not keep an instance awake with a workaround or claim durable background processing availability from Free hosting.

No protected Sprint 5.1 container, volume, database, network or port is used by these procedures. Never run test commands or migration examples against those resources or recovered/staging databases. Local validation uses disposable PostgreSQL, including a synthetic TLS fixture with private keys confined to its disposable container. No actual Neon/Render acceptance is implied by local tests.

## 7. Local verification record

The final frozen-code gate completed on 2026-11-06 with `scripts/verify-render-readiness.ps1`. Evidence is retained locally in `.local/render-final-2fcfbf1493a74b5690f67fd94cc17be1` and is intentionally excluded from Git.

| Ordered check | Result |
| --- | --- |
| Restore, including package metadata audit | Passed |
| Solution build | Passed; zero warnings/errors |
| New focused unit tests | 13 passed |
| Complete unit suite | 84 passed |
| New PostgreSQL integration tests | 29 passed |
| Authentication integration subset | 30 passed |
| Admin MFA/security subset | 46 passed |
| Complete PostgreSQL integration suite | 172 passed; 8.9138 minutes |
| Migration from empty isolated PostgreSQL | Passed |
| Upgrade from previous authentication schema | Passed; accounts/profiles/consent preserved |
| EF model consistency | No pending model changes |
| Cross-instance/restart/unencrypted-ring checks | 3 passed |
| Render-production configuration/Kestrel checks | 16 passed |
| `git diff --check` | Passed; line-ending notices only |
| Secret-pattern scan and manual review | 24 files reviewed; zero credential-pattern findings or real secrets added |
| Changed-file review | Completed; exact manifest below |
| Docker/runtime cleanup verification | Zero new Testcontainers resources or backend API processes remaining |

All test-stage TRX counters were read back: zero failed, skipped or aborted tests. The two complete suites cover **256 distinct tests** (84 unit + 172 integration); focused subsets overlap those suites and are not added to that total. The real Production process used synthetic TLS PostgreSQL with `VerifyFull`, a base64 PFX, no key directory and disabled external features. Its first focused frozen-code startup reported three unavailable-connection probes and zero timed-out probes within the unchanged 30-second startup budget / three-second probe budget. The final gate completed every ordered stage without an interruption or a retry wrapper.

Development failures were not counted as passing validation: the first development run had a duplicate synthetic contact value across two metadata cases and a real-process readiness failure; the contact values were made unique. A subsequent diagnostic run exposed an unhandled warm-up HTTP timeout in readiness polling. The loop now records initial connection unavailability/timeouts as non-ready probes while retaining its original deadline and per-request timeout; failure to become ready remains fatal and returns sanitized diagnostics. The production loopback CLI probe was hardened to avoid environment proxies and redirects. No production timeout or existing regression assertion was weakened. These development logs remain under `.local/render-development-*` / `.local/render-kestrel-diagnostic.log`; none is committed.

The preceding results describe the earlier Data Protection/authentication revision, not validation of later ingress changes. **Render ingress is conditionally resolvable using the mode above; actual customer traffic still requires deployed ingress/TLS acceptance and any external activation gates.** That earlier local gate did not perform actual Neon migration, Render deployment, ingress/TLS acceptance, Google/SMS/email/Razorpay acceptance or legal approval. Existing disk-backed environments retain their mode until separately approved key-ring transfer.

## Render Free ingress revision validation — 2026-11-07

Historical result for the initial adapter, before the observed loopback correction below. Its synthetic non-loopback edge tests did not establish live ingress acceptance.

The complete `scripts/verify-render-readiness.ps1` gate passed with evidence in `.local/render-ingress-final/` (local ignored artifacts): build zero warnings/errors; all 84 unit tests; all 212 integration tests; 40 focused ingress cases; 31 focused authentication cases; 46 Admin/security cases; empty and upgrade migrations; no EF pending-model changes; three restart-protection cases; and 16 existing production/explicit-proxy cases. Focused subsets overlap the complete suites: **296 distinct tests**, zero failed/skipped/aborted.

The new ingress tests cover no-header and spoofed-header HTTPS reconstruction, unchanged peer/host, exact CORS and preflights, unauthenticated access denial, forged-IP rate-budget resistance, direct/local HTTP redirection, arbitrary-host rejection, unchanged health-path schemes and startup configuration rejection. They simulate edge delivery through server-controlled connection metadata; they do not prove Render's deployed network isolation. Existing real-Kestrel HTTP/spoof tests retain their redirect requirement.

The final gate reported zero secret-pattern findings across the six changed files, zero new Testcontainers resources remaining and zero new backend API processes remaining. Only this documentation result was appended after the gate; application/test/script sources were unchanged. No frontend, schema, activation settings, protected Sprint 5.1 resources or production secrets were changed. No commit, push or deployment was performed. The fix is ready for a reviewed commit/push and controlled deployment acceptance under the conditional ingress requirements above.

## Render loopback ingress correction validation — 2026-11-07

The complete `scripts/verify-render-readiness.ps1` gate passed again; local ignored evidence is in `.local/render-loopback-final/`, including `COMPLETE.txt` and per-stage TRX files. Build: zero warnings/errors. Complete suites: 84 unit tests and 239 integration tests, **323 distinct passes**, zero failures/skips/aborts. Focused subsets (overlapping those suites): 67 ingress cases, 29 PostgreSQL/configuration cases, 31 authentication cases, 46 Admin/security cases, three restart-protection cases and 16 existing production/explicit-proxy cases. Empty-database and upgrade migrations passed; EF reported no pending-model changes.

The ingress cases reproduce HTTP on listener port 10000 with the configured production hostname and IPv4, IPv6 and mapped-IPv6 loopback peers, without forwarded headers. They retain spoofing, restricted CORS/preflight, HSTS, authentication denial, unchanged peer and rate-budget assertions. Negative coverage includes disabled mode, wrong host/listener/public port, non-Production, absent acknowledgement and missing/mismatched Render identity. Both health paths remain HTTP and unredirected for loopback and non-loopback delivery. Adapter-specific per-request logging is asserted absent. The gate's minimum test counts were raised to include the expanded coverage.

The final gate reported zero secret-pattern findings across four changed files, zero new Testcontainers resources remaining and zero new backend API processes remaining. Only this documentation result was appended after the gate; application/test/script sources were unchanged. No frontend, database schema/migrations, protected Sprint 5.1 resources, production secrets or external activation settings were changed. No commit, push or deployment was performed. Ready for reviewed commit/push and controlled Render redeployment under the public-edge isolation and trusted-local-runtime contract above; actual post-deployment HTTP/HTTPS, CORS, health and isolation acceptance remains required. No new environment variables are needed for this correction.

### Exact task file manifest

All paths are relative to `D:\Manoj\Group-Buy\Group-Buying Backend`; the frontend is untouched:

```text
docs/customer-authentication.md
docs/render-free-deployment.md
scripts/grant-runtime.sql
scripts/verify-render-readiness.ps1
src/GroupBuy.API/CustomerAuthenticationEndpoints.cs
src/GroupBuy.API/Errors.cs
src/GroupBuy.API/ProductionConfiguration.cs
src/GroupBuy.API/Program.cs
src/GroupBuy.Application/CustomerAuthentication.cs
src/GroupBuy.Domain/Models.cs
src/GroupBuy.Infrastructure/CustomerAuthenticationService.cs
src/GroupBuy.Infrastructure/Database.cs
src/GroupBuy.Infrastructure/DataProtectionDatabase.cs
src/GroupBuy.Infrastructure/DataProtectionSetup.cs
src/GroupBuy.Infrastructure/GroupBuy.Infrastructure.csproj
src/GroupBuy.Infrastructure/Migrations/20261106155504_PersistDataProtectionKeys.cs
src/GroupBuy.Infrastructure/Migrations/20261106155504_PersistDataProtectionKeys.Designer.cs
src/GroupBuy.Infrastructure/Migrations/MarketplaceDbModelSnapshot.cs
tests/GroupBuy.IntegrationTests/CustomerAccountContractTests.cs
tests/GroupBuy.IntegrationTests/CustomerAuthenticationTests.cs
tests/GroupBuy.IntegrationTests/DataProtectionPersistenceTests.cs
tests/GroupBuy.IntegrationTests/RenderProductionConfigurationTests.cs
tests/GroupBuy.IntegrationTests/RenderTlsFixture.cs
tests/GroupBuy.UnitTests/DataProtectionConfigurationTests.cs
```

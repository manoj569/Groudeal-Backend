# Phase 1 customer authentication

The Render/account-contract extension is documented in [Render Free deployment](render-free-deployment.md). Earlier validation history and the original implementation manifest below describe the authentication foundation; the current deployment report records the extension's own validation and file set.

Implementation and local validation only. All new login methods are disabled by default. No live credentials or providers are provisioned.

## Architecture and identity

The existing ASP.NET Core Identity account, Customer role, marketplace `users` profile, JWT validation and token-version revocation are reused. Google subjects use Identity's `identity_user_logins` primary key `(LoginProvider, ProviderKey)`. Email confirmation and phone confirmation in Identity are authoritative; existing profile verification fields are synchronized for compatibility. Profile completion is derived from a nonempty display name. Existing profile timestamps and account status are reused.

Passwordless accounts use random internal usernames and no password. Identity's optional-email validation setting permits mobile-only accounts; the existing unique normalized email index still enforces uniqueness for all accounts. Password registration retains its existing email/password validators and database constraints. No automatic identity merging occurs, including with an unconfirmed legacy email. Passwordless authentication is restricted to accounts whose sole role is Customer. Seller and Admin must use their existing credentials; Admin MFA remains mandatory.

## API contract

All routes are under `/api/v1`:

| Method | Route | Body |
| --- | --- | --- |
| POST | `/auth/google` | `{ "credential": "Google ID token" }` |
| POST | `/auth/email/request-otp`, `/auth/mobile/request-otp` | `{ "destination": "email or E.164 phone" }` |
| POST | `/auth/email/verify-otp`, `/auth/mobile/verify-otp` | `{ "challengeId": "uuid", "code": "six digits" }` |
| GET | `/me` | None; existing Customer bearer token required |
| PATCH | `/me/profile` | `{ "displayName": "Customer name" }` |
| POST | `/me/consent` | `{ "termsVersion": "approved-version", "privacyVersion": "approved-version" }` |
| POST | `/me/link/google` | `{ "credential": "Google ID token" }` |
| POST | `/me/link/email/request`, `/me/link/mobile/request` | Same destination body; Customer authentication required |
| POST | `/me/link/email/verify`, `/me/link/mobile/verify` | Same verification body; original Customer authentication required |
| POST | `/auth/logout` | Existing authenticated endpoint; revokes all JWTs for the account |

Login returns `{ session: { accessToken, expiresAtUtc }, authenticated: true, user: <CustomerProfile> }`. `/me`, profile/link/consent responses return that same CustomerProfile directly. Existing fields are retained and the account contract adds `email`, `mobile`, `connectedMethods` and `consent`:

```json
{
  "id": "00000000-0000-0000-0000-000000000001",
  "displayName": "Customer",
  "profileCompleted": true,
  "emailVerified": false,
  "phoneVerified": false,
  "consentRequired": true,
  "termsVersion": null,
  "privacyVersion": null,
  "email": null,
  "mobile": null,
  "connectedMethods": { "google": true, "email": false, "mobile": false },
  "consent": { "termsAcceptedVersion": null, "privacyAcceptedVersion": null, "consentRequired": true }
}
```

`phoneVerified` remains the established field name; `mobile` is the contact value. Email uses Identity's normalized email (falling back to its email when normalization is absent); mobile uses Identity's canonical phone. Verification flags come from Identity, not marketplace copies. Missing contacts are null and cannot be reported verified. Connected Google requires actual `Google` external-login membership, including for Gmail addresses. Connected email/mobile require their nonempty verified Identity contacts; these flags describe linked methods independently of whether a delivery feature is operationally enabled.

`termsVersion` and `privacyVersion` remain the configured **current** versions. Nested consent exposes each document's latest persisted accepted version, ordered by acceptance time with a deterministic ID tie-break. `consentRequired` checks acceptance of the configured current versions; missing versions require consent and cannot be accepted. Production customer-provider/pilot activation additionally requires both approved version identifiers. No legal text is generated. Linking returns freshly queried account metadata and a subsequent authenticated `/me` immediately reflects persisted state.

Challenge responses contain only challenge ID and expiry. Invalid or expired credentials return 401; unavailable methods 503; identity conflicts 409; throttles 429. No endpoint performs redirects or accepts return URLs. Unknown JSON properties are rejected by the existing strict serializer, including role and return URL injection.

### Machine-readable errors

Problem Details retains the existing `title`, status, generic detail and trace ID. The additive `code` extension is the machine-readable contract. Existing titles such as `invalid_challenge`, `challenge_rate_limited`, `account_conflict`, and `authentication_unavailable` remain unchanged.

| `code` | Meaning |
| --- | --- |
| `otp_invalid` | Wrong/malformed code for an eligible challenge |
| `otp_expired` | Eligible matching-purpose/channel/account challenge has expired |
| `otp_attempts_exceeded` | That eligible challenge exhausted its five attempts |
| `otp_resend_cooldown` | Destination has a challenge within the resend window |
| `rate_limited` | Durable request budget or HTTP limiter exhausted |
| `identity_conflict` | After proof of control, identity cannot be attached; other-account data is not returned |
| `provider_unavailable` | Requested customer authentication method disabled/unconfigured |
| `authentication_invalid` | Intentionally generic unknown, consumed/replayed, wrong-purpose/channel/account or invalidated-session challenge; also unavailable account context |

Expiry/attempt details are returned only after matching the unguessable challenge ID to the expected purpose, channel, account and session version. An unrelated challenge never exposes that state. Request cooldowns/budgets depend on challenge history, not whether an account exists. Existing Google validation errors retain their stable `invalid_google_credential`/`google_unavailable` codes. No error returns another account's identity, remaining code, OTP/hash, token, provider subject or security state.

## Google validation

An injectable validator uses Microsoft's JWT library and RSA public keys fetched only from Google's fixed HTTPS JWKS endpoint. Keys are cached for five minutes under a shared lock; a new Google key may temporarily fail closed until the next refresh. Validates RS256 signature, issuer, configured audience, expiry, not-before when present, subject, issued-at and authorized-party when present. No Google client secret is needed for ID-token validation. Immutable `sub` is the provider key. Token, name, email or roles supplied separately by a client are never trusted.

Google email is attached only when verified and Google is authoritative (Gmail or a hosted-domain claim). Other third-party mailbox addresses require a separate email OTP. Matching any existing email never silently logs into or merges an unlinked account. Existing subject login does not follow subsequent provider email changes.

Reference: [Google server-side token verification](https://developers.google.com/identity/gsi/web/guides/verify-google-id-token) and [Google backend authentication and mailbox authority](https://developers.google.com/identity/sign-in/web/backend-auth).

## OTP lifecycle and abuse controls

Email is trimmed, Unicode NFC normalized, validated and invariant-uppercase normalized. Canonically equivalent Unicode spellings share identity lookup and request quotas. Phones accept explicitly international E.164-form numbers, including +91, with spaces, parentheses and hyphens removed. No implicit country guessing or provider-specific mailbox alias collapsing occurs.

Each durable challenge has a random UUID, channel, normalized destination, Login/AccountLink purpose, optional account/token version, creation/expiry, HMAC hash, failed attempts and consumption time. Codes are generated with the cryptographic RNG, expire in five minutes, and allow at most five incorrect attempts. Hashes use the existing secret-managed JWT signing key with a separate domain prefix and bind challenge, purpose, channel, account and destination. Database disclosure without the key does not permit offline enumeration of six-digit codes. Signing-key rotation invalidates outstanding challenges.

New requests consume earlier outstanding challenges for the destination. Shared PostgreSQL transaction advisory locking serializes issuance and consumption across instances, including identities not yet created. Unique email/phone/provider constraints remain the final concurrency guard, including legacy registration. Locks intentionally trade throughput for a small, deterministic Phase 1 implementation; monitor before scaling. Delivery occurs before commit with a bounded provider timeout required by each delivery adapter. A database failure after delivery can produce an unusable code, never an authenticated session.

Durable destination limits: one request per minute, five per hour, twenty per day across login and linking. Authenticated link request limit: ten per account per hour. Customer login endpoints additionally allow thirty requests per source IP per minute, account endpoints thirty per account per minute, and existing global IP limits remain. Failed verification persists before returning 401. Identity existence is not disclosed by request responses. Provider errors must not expose secrets or destinations. No codes are persisted, logged or returned. Tests inject a capture provider defined only in the test assembly.

## Linking, conflicts and sessions

Linking requires a valid authenticated Customer session plus a fresh OTP bound to that account, purpose, channel and token version, or a validated Google credential. A method owned by another account returns a conflict, never a merge. Existing email/phone cannot be replaced by these endpoints; replacement/recovery is intentionally outside scope. Unconfirmed legacy identities require the existing recovery path rather than allowing OTP to authenticate a possibly preclaimed account. Races are serialized and database constraints prevent duplicate ownership.

Existing short-lived bearer JWTs are returned over the API; no refresh tokens, cookies or second session system were added. Existing issuer, audience, signature, expiry, current-role, account-status, token-version and Admin MFA checks remain. Passwordless tokens carry `amr=otp` or `amr=google`. Logout atomically increments token version and writes an audit entry, revoking all previously issued Groudeal tokens for that account. Later authentication can create a new session. Google ID credentials remain bearer credentials valid for their provider lifetime; logout revokes Groudeal sessions, not Google's session. Client-side token handling and Google sign-out remain external integration responsibilities.

Display name and consent are routing requirements reported by the API; existing marketplace endpoints are not newly gated on profile/consent, to preserve legacy flows. Consent persists document type/version/time with unique per-user document/version constraints; repeated acceptance is idempotent. New document versions require new acceptance. Missing configured legal versions reports consent required and prevents valid acceptance. No legal text has been fabricated.

Audit entries cover registration, successful login, failed code verification, locked challenge, verified/linked methods, conflicts, consent and logout. Entries contain IDs and event names, never OTPs, Google credentials, JWTs or secrets.

## Configuration and provider activation

| Environment key | Default / requirement |
| --- | --- |
| `Authentication__Google__Enabled` | false |
| `Authentication__Google__ClientId` | Required when Google enabled; approved Google OAuth web client audience |
| `Authentication__MobileOtp__Enabled` | false; requires an explicitly registered delivery provider |
| `Authentication__EmailOtp__Enabled` | false; requires an explicitly registered delivery provider |
| `Authentication__Consent__TermsVersion` | Approved document version, 1–100 characters |
| `Authentication__Consent__PrivacyVersion` | Approved document version, 1–100 characters |
| `Jwt__SigningKey`, `Jwt__Issuer`, `Jwt__Audience`, `Jwt__ExpiryMinutes` | Existing JWT configuration and secret-management rules unchanged |
| `ConnectionStrings__Database` | Existing database configuration; apply migration and runtime grants through approved operations |

`IOtpDeliveryProvider` is the delivery extension point. Default `UnconfiguredOtpDelivery` supports neither channel. Startup fails if OTP is enabled without a provider, in every environment. No logging/dev fallback is reachable in production. Implement and register a reviewed adapter before activation; choose provider-specific secret keys only when an actual provider is approved. No invented SMS/email credentials or paid integration are included.

External acceptance requires approved legal documents/versions, Google OAuth audience and frontend origins, Google credential handling/HTTPS integration, an approved SMS and/or email provider adapter with bounded timeout and secret-managed configuration, sandbox delivery and abuse testing, data retention/expired challenge cleanup policy, deployment migration preflight for preexisting phone duplicates, runtime grant application and controlled end-to-end verification. Do not enable live services as part of this code task. Never apply migrations to protected Sprint 5.1 resources or use recovered/staging infrastructure for experimentation.

## Migration and local verification

Migration: `20261106104909_CustomerPasswordlessAuthentication`. Adds `customer_auth_challenges`, `user_consents`, their foreign keys/indexes, and `IX_identity_accounts_PhoneNumber`. Existing migrations are unchanged; the generated snapshot includes the new model. Existing provider uniqueness uses Identity's external-login primary key. This migration does not rewrite customer or marketplace data; duplicate existing phone values cause migration failure and require an explicit operator review rather than deletion or merging.

`scripts/verify-customer-authentication.ps1` builds with one MSBuild worker, runs new units, creates an isolated portable PostgreSQL cluster on a free loopback port for focused integration/security tests, runs all units, and then removes its connection override so the full integration suite uses its existing disposable Testcontainers fixtures (including the logical backup/restore test). xUnit collection parallelism is disabled with `scripts/customer-authentication.runsettings`. It then checks pending model changes and whitespace. The runner restores caller environment variables and stops only the cluster it created. It does not restore packages automatically: restore dependencies and vulnerability metadata before running. Windows sandbox restrictions can prevent VSTest test-host IPC, Docker pipe access or PostgreSQL startup; run authorized local validation outside that sandbox without increasing timeouts or weakening checks.

The existing five-minute test hang guard is retained, without dumps. The focused security selection includes real Admin MFA and the marketplace confirmation/cancellation race. The test API factory additionally captures the existing error handler's redacted diagnostics; the marketplace race's HTTP 200 assertion retains the same condition and now reports the safe diagnostics on failure. No marketplace production code or regression assertion was weakened.

### Validation history retained

- An initial sandbox execution aborted before any new unit test executed because VSTest could not connect to its test host. Validation was moved to authorized local execution outside that sandbox; the timeout was not increased. NuGet restore succeeded with vulnerability metadata auditing enabled.
- Completed pre-final full run: `.local/customer-auth-validation-1767a6a53f8b42a6a0470b1180cff348`: 69 unit passes; 141 PostgreSQL integration tests executed, 140 passed, one failed, zero skipped. The failure was `Distinct_customers_race_capacity_then_real_confirmation_races_cancellation`, which received HTTP 500 from seller confirmation after a 5m6s test execution. PostgreSQL recorded canceled inventory aggregate and worker queries at the matching time. About 520 MB of 7,886 MB physical RAM was free during investigation. This is evidence of resource pressure, not proof of an authentication defect or a completed root-cause fix.
- Intermediate run `.local/customer-auth-validation-4bc15f3e3454454d8eff929c7b5e59c6`: clean build, 17 new unit passes, 16 new authentication integration passes, 38 focused security/marketplace passes (including the previously failing race), and 69 complete unit passes. Its full integration stage was deliberately stopped after 30 passes when final review found the Unicode email quota-normalization edge case. That aborted stage is not counted as a passing regression run.
- Unicode normalization was corrected and two unit cases plus a PostgreSQL cooldown-collision test were added. The final ordered gate uses `.local/customer-auth-validation-e1b063c4cd144b50bf28ed2876bcb861`. Final results are recorded below after completion.
- On that final code, the build, 19 new units, 17 new PostgreSQL authentication tests, 39 focused security/marketplace checks and 71 complete units passed. A tool-session reset interrupted the runner after the focused stage; the remaining stages resumed from saved evidence. The first resumed full suite discovered 143 tests, with 31 non-database passes and 112 fixture setup failures, all reporting `DockerUnavailableException`: Docker's engine pipe was no longer present. This is a blocked infrastructure run, not a passing regression result. The model consistency check and `git diff --check` passed independently. The user offered to restart Docker before full PostgreSQL validation resumes.

### Final verification result

Final code validation completed on 2026-11-06. Docker availability was confirmed before restarting the blocked full PostgreSQL stage. Final evidence is retained in `.local/customer-auth-validation-e1b063c4cd144b50bf28ed2876bcb861`:

| Check | Result |
| --- | --- |
| Solution build | Passed; zero warnings, zero errors |
| New authentication unit tests | 19 passed |
| New authentication PostgreSQL tests | 17 passed |
| Focused security/MFA/marketplace checks | 39 passed |
| Complete unit suite | 71 passed, zero failed/skipped |
| Complete PostgreSQL suite | 143 passed, zero failed/skipped; 11.2374 minutes |
| Combined distinct complete-suite tests | 214 passed |
| Previously failing marketplace race | Passed in the focused gate and final full suite; final execution 6 seconds |
| EF model/snapshot consistency | No pending model changes |
| `git diff --check` | Passed; line-ending normalization notices only |
| Secret review | 21 task files reviewed; zero credential-pattern findings; manual review found no added secrets or production token/OTP logging |

The complete-suite TRX files are `Ashish_LAPTOP-SH2NN93A_2026-11-06_17_28_44_net10.0.trx` (71 unit tests) and `Ashish_LAPTOP-SH2NN93A_2026-11-06_17_32_55_net10.0.trx` (143 integration tests). Their counters were read back and confirm zero failures, aborts, timeouts or unexecuted tests. The final full run is `regression-final.log`; `model-final.log` records model consistency. Earlier failed/blocked/aborted evidence above remains intact and is not included as passing validation. No marketplace implementation change, timeout increase, retrying test wrapper, skipped case or weakened assertion was used to obtain the final result.

Code is ready for controlled integration. Live provider activation and external acceptance remain outstanding. Operational limitations include the serialized Phase 1 authentication lock, five-minute Google key-cache refresh interval, and the need for an approved expired-challenge retention/cleanup policy. Passwordless sign-in is intentionally limited to Customer-only accounts: promotion of a passwordless-only account to a privileged role requires separately approved privileged credential provisioning; existing password and Admin MFA flows remain available and unchanged. Profile/consent routing flags do not newly gate legacy marketplace endpoints. No automatic account merge, existing-method replacement or account recovery flow was added.

Frontend and protected Sprint 5.1 resources were untouched by this task. No code was committed, pushed or deployed; no live provider, payment, Google credential or production secret was enabled or created.

## Exact implementation file manifest

All files are within `D:\Manoj\Group-Buy\Group-Buying Backend`; frontend and staging resources are outside this task. The following is the complete task file set (including this document):

```text
docs/customer-authentication.md
scripts/customer-authentication.runsettings
scripts/verify-customer-authentication.ps1
scripts/grant-runtime.sql
src/GroupBuy.API/CustomerAuthenticationEndpoints.cs
src/GroupBuy.API/Program.cs
src/GroupBuy.Application/CustomerAuthentication.cs
src/GroupBuy.Infrastructure/CustomerAuthenticationDatabase.cs
src/GroupBuy.Infrastructure/CustomerAuthenticationService.cs
src/GroupBuy.Infrastructure/GoogleCredentialValidator.cs
src/GroupBuy.Infrastructure/AdminMfaService.cs
src/GroupBuy.Infrastructure/Database.cs
src/GroupBuy.Infrastructure/IdentityService.cs
src/GroupBuy.Infrastructure/Migrations/20261106104909_CustomerPasswordlessAuthentication.cs
src/GroupBuy.Infrastructure/Migrations/20261106104909_CustomerPasswordlessAuthentication.Designer.cs
src/GroupBuy.Infrastructure/Migrations/MarketplaceDbModelSnapshot.cs
tests/GroupBuy.IntegrationTests/CustomerAuthenticationTests.cs
tests/GroupBuy.IntegrationTests/BackendTests.cs
tests/GroupBuy.IntegrationTests/MarketplaceJourneyTests.cs
tests/GroupBuy.UnitTests/CustomerAuthenticationTests.cs
tests/GroupBuy.UnitTests/GroupBuy.UnitTests.csproj
```

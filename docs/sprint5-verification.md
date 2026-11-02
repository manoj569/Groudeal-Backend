# Sprint 5 verification and change record

Verification date: 2026-11-02 (Asia/Calcutta). All database executions used newly initialized loopback-only disposable PostgreSQL 17.6 clusters. No production connection, live Admin credential, automatic deployment or push occurred. This report distinguishes local code verification from external release acceptance.

## Runtime and compatibility — PASS

The workspace-local Microsoft SDK 10.0.403 download was SHA-512 checked against official release metadata. Actual shared runtimes are Microsoft.NETCore.App and Microsoft.AspNetCore.App 10.0.14. `global.json` pins 10.0.403 with latestPatch and no prerelease; all six projects target net10.0. Microsoft/ASP.NET/Identity/EF references are 10.0.14, Npgsql EF 10.0.3 and Serilog ASP.NET 10.0.0. Existing unrelated dependency versions remain in place; Otp.NET (backend tests) and OTPAuth (frontend test tooling) support real TOTP fixtures only.

OpenAPI namespace/security-scheme changes for the serviced package were applied; actual `/openapi/v1.json` is exercised by retained API tests. Full build: **0 warnings, 0 errors**. The local SDK does not install or certify a global runtime or target container.

An existing background SDK 9 restore overwrote ordinary `obj/project.assets.json` during verification. The verified runner isolates its intermediate output under `obj/sprint5/`, checks fresh nonempty TRX files and minimum retained baseline counts, and invokes the explicit workspace SDK. A failed exploratory command using the default tool home was corrected to use `DOTNET_CLI_HOME=.local`; no migration success is inferred from that failed command.

## Backend regression and migrations — PASS

Command: `./scripts/verify-sprint5-backend.ps1`.

- **37/37 unit tests passed; 66/66 PostgreSQL integration tests passed; 0 failed/skipped.** Original 37/49 baseline retained.
- New coverage: explicit disabled/idempotent provisioning, suspended-account revocation, no pre-MFA Admin privilege, encrypted enrollment/recovery storage, bad/expired/context-invalid challenges, persistent lockout, replay, parallel single-use recovery, controlled reset and session revocation, signed Admin JWT without MFA, certificate-protected keys, safe failed key-protection startup, explicit proxy trust, safe readiness/liveness, DB outage/recovery, graceful shutdown, retry exhaustion/resume/idempotency, actual restricted-role SQL grants, migration preservation and authentication quota boundaries.
- Additive migration `20261102012900_AdminMfaSecurity`: nullable enrollment-window column, two security tables, indexes/checks/restricting FKs. No business-data backfill or token-column convenience alteration.
- Fresh three-migration schema exercised. Separate synthetic legacy-audit fixture upgraded from migration 2 to 3, rolled back empty security structures to 2, then upgraded again with audit preserved. This is not approval for a populated live security rollback; production recovery is forward-first and backup-driven.
- `dotnet ef migrations has-pending-model-changes`: no model changes. Fresh `docs/schema.sql` and ignored idempotent SQL regenerated with the verified SDK. No automatic API migration.
- Actual `scripts/grant-runtime.sql` tested with an unprivileged login: no DDL, audit update/delete, migration-history writes, Admin membership escalation or MFA reset. Application queries/transactions remain functional. Production grants/TLS/network remain NOT VERIFIED.

Local evidence: `.local/sprint5-build.log`, `sprint5-test.log`, `sprint5-model.log`, `sprint5-idempotent.sql` and fresh project `TestResults/sprint5-results.trx`. Ignored artifacts are local evidence, not public attachments; do not publish synthetic enrollment traces or DB dumps.

## Frontend, real API and restore verification

Executed browser and restore results are recorded below. Earlier exploratory failures are not counted as passes. They identified and corrected an auth-quota regression, a stale-401 session race and test selectors for required MUI field labels; no existing tests were removed or security rate limit disabled.

## Dependency and deployment checks

- NuGet direct/transitive vulnerable-package audit succeeded with no reported vulnerable packages across all six projects (`.local/sprint5-nuget-audit.json`). This reflects the current feed, not a guarantee against unknown vulnerabilities.
- npm production dependency audit: **0 vulnerabilities**. Full graph: **20 (18 Moderate, 2 Low; 0 High/Critical)**, in development tooling. Compatible remediation or accountable build-system risk acceptance remains required. Evidence is in frontend `.local/sprint5-npm-{audit,production-audit}.json`.
- Development and production `docker compose config --quiet`: **PASS**, with syntax-only nonsecret path placeholders. `docker info`: engine unavailable (missing named pipe). **Docker image build/start, nginx runtime, health transition, volume persistence and TLS behavior BLOCKED / NOT VERIFIED.** No claim of Compose runtime execution.
- GitHub Actions workflow prepared, with read-only permissions and no deployment job. Remote CI NOT VERIFIED; no push/remote run performed.
- Production configuration locally exercises fail-closed key/certificate/TLS prerequisites, HTTPS redirect, explicit hosts/CORS/proxy trust and actual protected key-ring creation with a synthetic certificate. Actual DNS, proxy, HTTPS, key ACLs/rotation, SCRAM/CA/network and secret manager require target-host acceptance.

## Files changed / added

Paths below are repository-relative; generated `bin`, `obj`, `.local`, test results and IDE files are not source deliverables. The frontend paths are relative to the adjacent Group-Buying Frontend repository. This is a task manifest, not a claim of a clean Git checkout.

| Area | Files |
| --- | --- |
| Runtime/build | `global.json`, `Directory.Build.props`, `.config/dotnet-tools.json`, all six `src/*/*.csproj` / `tests/*/*.csproj` |
| Domain/application | `src/GroupBuy.Domain/AdminSecurity.cs`; `src/GroupBuy.Application/AdminSecurityContracts.cs`, `Contracts.cs`, `OperationsContracts.cs`, `WorkerHealth.cs` |
| Identity/security persistence | `src/GroupBuy.Infrastructure/IdentityService.cs`, `AdminMfaService.cs`, `AdminProvisioner.cs`, `ProtectedIdentityStore.cs`, `Database.cs`, `DemoSeed.cs` |
| Worker processing | `src/GroupBuy.Infrastructure/Marketplace.cs`, `Marketplace.Operations.cs`; `src/GroupBuy.API/ExpiryWorker.cs` |
| API/config | `src/GroupBuy.API/Program.cs`, `Endpoints.cs`, `AdministrationEndpoints.cs`, `OperationsEndpoints.cs`, `AdminSecurityEndpoints.cs`, `ProductionConfiguration.cs`, `OperationalHealthEndpoints.cs` |
| Migration | `src/GroupBuy.Infrastructure/Migrations/20261102012900_AdminMfaSecurity.cs`, companion `.Designer.cs`, `MarketplaceDbModelSnapshot.cs`; `docs/schema.sql` |
| Backend regressions | `tests/GroupBuy.IntegrationTests/BackendTests.cs`, `MfaTestSupport.cs`, `AdminSecurityTests.cs`, `OperationalSecurityTests.cs`, `ProductionSecurityTests.cs`, `DatabaseHardeningTests.cs`, `MigrationSecurityTests.cs`, `AuthenticationRateTests.cs` |
| Operations/templates | `scripts/verify-sprint5-backend.ps1`, `scripts/start-local.ps1`, `scripts/grant-runtime.sql`, `Dockerfile`, `.dockerignore`, `compose.production.yaml`, `.env.production.example`, `deployment/nginx.conf`, `.github/workflows/backend.yml`, `.gitignore` |
| Documentation | `README.md`, `docs/sprint5-security-hardening.md`, `docs/sprint5-verification.md`, `docs/admin-mfa-provisioning.md`, `docs/production-operations-runbook.md`, `docs/security-model.md`, `docs/api-contracts.md`, `docs/deployment-plan.md`, `docs/production-readiness-checklist.md` |
| Frontend integration | `README.md`, `package.json`, `package-lock.json`; `src/auth.tsx`, `src/api.ts`, `src/types.ts`, `src/account.tsx`, `src/admin-mfa.tsx`, `src/admin/types.ts`, `src/admin/screens.tsx` |
| Frontend regression/harness | `tests/mfa.test.tsx`, `tests/auth-race.test.ts`, `integration/admin-auth.ts`, `integration/seller-backend.spec.ts`, `integration/zz-admin-backend.spec.ts`, `integration/zzzz-marketplace.spec.ts`, `integration/zzzzz-admin-mfa.spec.ts`, `scripts/test-backend.ps1`, `scripts/test-backend-isolation.ps1` |

## Remaining risks and verdict

**NO-GO for a real-customer controlled pilot.** Local code is eligible for controlled synthetic staging validation. See the current PASS/FAIL/BLOCKED/NOT VERIFIED [checklist](production-readiness-checklist.md) and [hardening findings](sprint5-security-hardening.md). External hosting/Docker/CI, named operator IAM and recovery identity checks, production database/key permissions, measured PITR/key restoration, alerting, legal retention/stock cutover and customer recovery/support acceptance remain gates. No payment, wallet, delivery or unrelated category workflow was introduced.

### Executed real API / PostgreSQL browser and restore result — PASS

`./scripts/test-backend-isolation.ps1`: **5/5 passed** (2.4 minutes), serial Chromium workflows: customer registration/reservation/cancellation/verified pickup; seller product/inventory/campaign/collection; Admin seller/campaign review; continuous onboarding-to-completion; actual CLI-authorized Admin enrollment, wrong-factor rejection and recovery-code login. Runtime API uses the actual restricted SQL-template role; migrations and provisioning use the separate fixture owner.

The final fresh fixture was `Group-Buying Frontend/.local/integration-30831d07d4f145eca538629f117ac647`. Its logical dump/restore matched selected counts and all checked invariants: 20 campaigns, 30 reservations, 9 pickups, 36 notifications, 92 audits and all three migration IDs. Campaign bounds, reservation snapshot arithmetic, collected pickup-secret clearing and notification-duplicate violations were all zero. Representative query plans captured. All twelve caller environment variables restored, and local fixture API/database stopped.

Generated synthetic password, signing key and authenticator key were absent from API, seed and provisioning logs. This scan is deliberately bounded; raw test artifacts remain restricted/ignored and are not suitable public evidence.

The first post-fix run passed four workflows but timed out while the cold public catalog still displayed Loading before customer login. The harness now explicitly asserts that the seeded catalog is readable before starting the browser suite; all five then passed without changing UI assertion timeouts or marketplace guards. An exploratory outer PowerShell redirection also held database startup output open; that disposable fixture was stopped and rerun directly. These failed/interrupted runs are not counted as passes.

Bounded local measurements from the continuous workflow: 20 listing requests, concurrency 4, median 49.6 ms/p95 80.3 ms; 20 detail requests, concurrency 4, median 37.5 ms/p95 46.1 ms. Twelve simultaneous reservation attempts yielded six accepted and six capacity conflicts in 753 ms, preserving bounds. Small synthetic loopback timings are not production capacity or SLA evidence. Existing artifact filename remains `.local/sprint4-performance.json` for compatibility; this result came from the fresh Sprint 5 run.

### Final retained frontend suites — PASS

The final serial command ran `npm run typecheck`, `npm run lint`, `npm test`, `npm run build`, `npx playwright test` and `npx playwright test --config playwright.pwa.config.ts`, checking each exit code. Results: **33/33 unit/component tests (6 files), 60/60 desktop/mobile browser checks and 1/1 production PWA offline test**, zero failures/skips. TypeScript, ESLint and production build passed. Existing main chunk 542.92 kB and Zod annotation warnings remain; no warning suppression was added. PWA checks static-only caching and unavailable private views offline; Chromium emulation is not a real-device accessibility certification.

Final combined totals: backend **37 unit + 66 real PostgreSQL integrations**; frontend **33 unit/component + 60 browser + 5 real-backend workflows + 1 PWA**. All original baselines retained. Isolated API/database ports 5081, 55433 and 55434 were checked closed after fixture completion. No source changes followed these checks except documentation/status records.

# Sprint 5.2 production readiness report

Status: **NO-GO for real customers or deployment**. This report separates code-level evidence from target infrastructure evidence. The recovered database `groupbuy_sprint51`, its container `groudeal-sprint51-postgres`, and volume `groudeal-sprint51-postgres-data` are protected and were not connected, changed, restarted, backed up, or used as a test target.

## Findings and dispositions

### Previous November 3 audit findings (11)

The earlier audit classified F-01/F-02/F-03/F-04 as fixed and verified; F-05 as fixed but not verified; F-06/F-07/F-08 as expected negative-test behavior; F-09/F-10 as externally blocked; and F-11 as open customer recovery/support work. F-05 is addressed here by removing arbitrary exception messages from diagnostic logs and adding synthetic secret-canary tests. F-03's prior migration coverage was insufficient for representative historical rows; the new migration-upgrade regressions cover those cases, subject to the final test results below.

### New issues found in Sprint 5.2

| Finding | Severity | Disposition |
| --- | --- | --- |
| The prior regex-redacted exception logger could leak free-form secrets in unfamiliar formatting or nested provider messages. | High | Fixed in `Errors`: message text is never logged; bounded type chain plus allowlisted PostgreSQL metadata remains. Synthetic secret-canary regression added. |
| The prior staging verifier required the exact protected Sprint 5.1 PostgreSQL container and network, did not prove the mounted data volume was unique, and used the production subnet/port. | Critical | Fixed fail-closed checks: unique Sprint 5.2 DB/container/volume/network/runtime role, separate private /24 and localhost port, staging labels, and actual database volume mount. Synthetic guard tests added. Live staging remains blocked and was not run. |
| Production Compose used a mutable local API image tag and build directive. | High | Fixed: production requires `API_IMAGE` by immutable digest and no longer builds the API image from source. Staging override is the only build path. Static Compose validation is recorded below. |
| API latency and authentication/worker event metrics were not present. | Medium | Fixed at code level with bounded route-template, method, status and exception-type metrics. No exporter/provider is configured; external collection remains blocked. |

## Verification

Results are completed after the final code change. A missing result is explicitly not verified.

| Check | Result |
| --- | --- |
| Full solution restore, normal NuGet audit enabled | **Blocked, exit 1.** `NU1900`: unable to load `https://api.nuget.org/v3/index.json`; audit was not disabled. |
| Full solution build, warnings as errors | **Pass, exit 0** after final code changes, .NET 10 SDK 10.0.403, Release, 0 warnings/errors, 2m57s. Command used the earlier normally audited `obj/sprint5` assets and isolated `bin/sprint52-final` output with `--no-restore`; no audit was disabled. A normal current restore remains blocked by `NU1900`. |
| Unit tests | **0 executed.** VSTest failed to connect to testhost after 180 seconds and aborted (exit 1). |
| PostgreSQL integration suite | **Not run.** `GROUPBUY_TEST_POSTGRES` was unset, but Docker inventory was unavailable (named-pipe permission denied), so no Testcontainers infrastructure was started. |
| Exception redaction and metrics tests | Added and compiled, but the focused non-database VSTest run also failed to connect to testhost after 180 seconds (0 tests executed). |
| Fresh and populated upgrade migrations | Test cases added and compiled; **not executed** because the disposable Docker resource inventory could not be inspected. EF pending-model-change command also stopped at `NU1900` project metadata failure. No migration was applied to any database. |
| Synthetic logical backup/restore | Test added and compiled; **not executed** because Docker access and disposable resource inventory could not be proven. |
| Staging guard tests and parser | **Pass.** Windows PowerShell parser reports zero errors for script and test; synthetic protected-resource, env-override, Compose argument-order, diagnostic-redaction cases pass. Mojibake scan 0; non-ASCII count 0. The verifier itself was not run. |
| Production Compose static validation | **Pass, exit 0** for base production Compose and synthetic Sprint 5.2 override using placeholder images/files; no engine or containers used. |
| Vulnerability audit | **Blocked** by the NuGet advisory feed outage; no new current-feed result is claimed. The previous November 3 audit is historical evidence only. |
| Secret exposure review | No real credentials were added; test fixtures contain synthetic canaries only. No connection string or secret material was printed or added to documentation. |
| Diff whitespace | **Pass.** `git diff --check` exit 0. Git emitted only line-ending normalization warnings. |

The read-only Docker inventory attempt (`ps -a`, `volume ls`, `network ls`) failed with named-pipe permission denied. Because protected resources could not be inventoried, this task did not start Testcontainers, did not run the staging verifier, and did not attempt database migrations or backup/restore.

## Release gates still open

- Staging API/edge/PostgreSQL startup, TLS, restart, persistent-volume behavior, and operational log review on separately approved Sprint 5.2 assets.
- Production database schema/grants/TLS, migration identity execution, offsite encrypted backup, PITR, and restore/key recovery with measured RPO/RTO.
- Configured metrics exporter/collector, alert routing, on-call ownership, and synthetic alert delivery.
- Named release/security/data owner acceptance, customer account recovery and support policy, seller fulfillment dispute handling, retention/legal approval, and pilot load/accessibility review.

Passing local tests are necessary but do not establish production or pilot readiness. No deployment, commit, or push is authorized by this report.

## Required completion summary

- **Initial audit findings:** 11.
- **New Sprint 5.2 issues discovered:** 4 (free-form exception-message leakage; unsafe Sprint 5.1 resource assumptions in the verifier; mutable/build-time production API image; missing API/authentication/worker metrics).
- **Fixed and verified:** fail-closed synthetic staging guards and Windows PowerShell parser; production Compose base and staging overlay static configuration; production API image now requires a digest and no production build; `git diff --check`.
- **Fixed but not runtime-verified:** exception message suppression and structured PostgreSQL diagnostic metadata; latency/auth/worker metrics and their C# regression tests; populated migration upgrade and backup/restore regression tests. C# changes compiled, but VSTest could not start its testhost.
- **Accepted/expected:** previously documented worker/database, injected Data Protection, and campaign retry exceptions are intentional negative/recovery scenarios (the prior integration run passed them); no new runtime test evidence is claimed here.
- **Blocked:** fresh current-feed NuGet audit (feed unavailable); unit, focused, and PostgreSQL integration test execution (VSTest startup and Docker engine access); fresh/upgrade migration execution; backup restore drill; live staging runtime, target TLS/grants, production deployment, monitoring-provider and backup-store verification.
- **Open remediation:** customer/seller password recovery and verified email flow; support/dispute policy; approved RPO/RTO, offsite encrypted backup/KMS/PITR; metrics exporter and alert routing; named on-call/release/data/security owners; legal/retention and pilot load/accessibility sign-off.

**Final verdict: NO-GO for a real-customer pilot.** Before a controlled pilot, restore testhost and Docker access in a trusted host context, rerun the full audited restore/build/test matrix on proven disposable resources, execute the migration and backup drills, provision independently reviewed Sprint 5.2 staging TLS/grants/secrets/volume/network, verify API/edge/DB restart and recovery, configure and test alerts/backups, and obtain accountable product, privacy, data, security and release-owner approval for the recovery/support gaps. Do not target the protected recovered database or use this report as deployment authorization.

## Sprint 5.2 files changed

- `.env.production.example`, `compose.production.yaml`
- `src/GroupBuy.API/Errors.cs`, `src/GroupBuy.API/Program.cs`, `src/GroupBuy.API/ExpiryWorker.cs`
- `src/GroupBuy.Application/WorkerHealth.cs`, `src/GroupBuy.Infrastructure/Marketplace.cs`
- `tests/GroupBuy.IntegrationTests/ExceptionRedactionTests.cs`, `OperationalMetricsTests.cs`, `MigrationUpgradeTests.cs`, `DisposableBackupRestoreTests.cs`, and `BackendTests.cs`
- `scripts/verify-sprint51-staging.ps1`, `scripts/tests/verify-sprint51-staging-diagnostics.Tests.ps1`
- `docs/sprint51-staging-verification.md`, `docs/production-readiness-checklist.md`, and all six new Sprint 5.2 readiness documents.

Other working-tree entries from prior tasks were preserved and are listed by `git status --short`; they were not reset, staged, or committed.

### Staging provisioning follow-up — 2026-11-03

A pure PowerShell safety helper and synthetic regression script were added for unique resource naming, protected-resource/port/subnet rejection, collision decisions, secret-safe evidence projection, and cleanup identity eligibility. The tests pass. These helpers do not perform provisioning or cleanup.

Live Sprint 5.2 provisioning remains **BLOCKED**. Docker CLI is available, but this execution context cannot access the Windows Docker Desktop engine named pipe (`npipe:////./pipe/dockerDesktopLinuxEngine`); read-only Docker container, volume, and network inventory therefore cannot be established. Host OpenSSL and psql commands are also unavailable, and reviewed immutable image digests were not supplied in the checkout. No staging resources were created or removed; no PostgreSQL connection, migration, backup, restore, or staging verifier run occurred. The protected Sprint 5.1 resources remain untouched by this execution. Current Sprint 5.2 gate remains **NO-GO** until provisioning, TLS, grants, migrations, the existing verifier, and exact cleanup are run in a host context that can prove identity and isolation.

### Staging verifier execution follow-up — 2026-11-04

The preceding 2026-11-03 blocked note is historical. In the current authorized host context, run `41710b322829` was inspected and verified without touching Sprint 5.1. The staging verifier passed configuration/isolation checks, API and edge startup, strict in-container TLS liveness/readiness (HTTP 200 before and after both API and edge restarts), and exact API/edge project cleanup. Its recorded overall state is **NOT VERIFIED**, because migration/runtime separation, recovery, Admin MFA persistence, PostgreSQL restart and backup/restore, worker recovery, and restricted log review remain manual/unverified gates.

Windows host TLS inspection replaced the loopback endpoint certificate with one issued by `Avast Web/Mail Shield Untrusted Root`. The verifier does not trust or bypass that certificate: it records the host result as a sanitized diagnostic and performs strict CA/hostname checks from inside the edge container using its mounted public certificate chain. Host port 18443 was TCP reachable. This local inspection condition and the remaining manual gates keep the production verdict **NO-GO**.

PowerShell validation passed: all three `scripts/tests/*.Tests.ps1` scripts, zero verifier parser errors, zero non-ASCII/mojibake characters in the verifier and its diagnostics tests, and `git diff --check`. The Release integration-test project built successfully and **77/77 PostgreSQL Testcontainers integration tests passed** with `GROUPBUY_TEST_POSTGRES` unset; the fixture removed its disposable container. The 37-test unit suite could not start: VSTest failed to connect to the testhost at both 90-second and 180-second connection timeouts, reporting no executed tests. No C# source was changed in this continuation. A separate solution build attempt ended with `Build FAILED` and zero warnings/errors, but the integration project build and tests passed.

### Sprint 5.2 continuation — 2026-11-04

This continuation supersedes the earlier unit/build/integration status above where later evidence conflicts. It does not authorize production use.

| Gate | Result | Evidence and limits |
| --- | --- | --- |
| Migration/runtime separation | **PASS** | Read-only TLS `VerifyFull` checks against only `groupbuy_sprint52` showed 5 migration-history rows, 22 public tables, and 71 constraints. The runtime role was not superuser, did not own relations, had no role memberships or DDL flags, and the application readiness checks used the runtime login. The migrator owned 24 relations and alone had schema CREATE. Migration history was unchanged through the restart. |
| PostgreSQL restart/recovery | **PASS** | Captured pre-restart state, restarted only `groudeal-sprint52-postgres-41710b322829`, waited for `running|healthy`, and reconnected with runtime `VerifyFull`. Post-restart state remained 5 migrations / 22 tables / 71 constraints; the API/edge strict TLS probes had previously passed after API and edge restarts. `groudeal-sprint51-postgres` was not restarted. |
| Backup/restore | **PASS** | Source was only `groupbuy_sprint52`. Created a separate random restore database `groupbuy_sprint52_restore_67d1c1dde773`, used custom-format `pg_dump -Fc --no-owner --no-privileges` and `pg_restore --exit-on-error`, verified restore listing and matching 5 migration rows / 22 tables / 71 constraints, then the drill's cleanup trap dropped the target and removed its temporary files. Representative source row counts were category/product/seller/campaign/pickup = 13/0/0/0/0. No Sprint 5.1 resource was a source or target. |
| Worker retry/recovery | **PARTIAL** | The earlier full disposable PostgreSQL integration run passed 77/77, including bounded retry, poison-campaign isolation, new-scope recovery, cancellation, and exhaustion/reset coverage. A pending worker item across an actual API process/container restart was not demonstrated. |
| Admin MFA persistence | **NOT VERIFIED** | Existing tests passed enrollment, encrypted persistence, recovery-code, challenge, and no-bypass checks within a test host. A new focused restart regression compiled, but its run failed before enrollment because `/api/v1/auth/register` returned HTTP 500; fixture cleanup then timed out connecting to its disposable PostgreSQL. It produced 0 passing tests and does not establish an MFA defect or a restart pass. The exact underlying registration exception was not available in test output. |
| Restricted logs | **PARTIAL / NOT VERIFIED** | Recent Sprint 5.2 PostgreSQL logs were scanned in memory; counts were zero for password/connection-string, bearer/JWT, private-key, MFA/recovery-code, and email patterns. The 24 broad phone-pattern matches were all ISO date stamps; no matched values were emitted. Source review found API/error/worker logging limited to bounded exception types, sanitized PostgreSQL metadata, trace IDs, campaign IDs, and worker status. API/edge logs from the prior run were no longer available after exact cleanup, so a full operational log review remains open. |
| Unit tests | **PASS** | Full Release unit suite: 37 passed, 0 failed, 0 skipped, 37 total. The execution required the elevated host context; prior testhost connection failures were environmental. |
| Full solution build | **PASS (serial)** | `dotnet build GroupBuy.sln --no-restore --configuration Release --verbosity minimal -m:1 /nodeReuse:false`: exit 0, 0 warnings, 0 errors, 2m28.94s. A parallel attempt first failed with MSBuild `OutOfMemoryException` / insufficient system resources; serial build completed successfully. |
| Focused MFA restart test | **FAIL / NOT VERIFIED** | `dotnet test tests/GroupBuy.IntegrationTests/GroupBuy.IntegrationTests.csproj --no-restore --configuration Release --filter 'FullyQualifiedName~Admin_MFA_persists_and_cannot_be_bypassed_after_API_host_restart' --verbosity minimal`: 0 passed; the test failed and test-class cleanup also failed (2 reported failures). Registration returned sanitized HTTP 500 before MFA enrollment, and fixture cleanup's database connection timed out. `GROUPBUY_TEST_POSTGRES` was explicitly unset. The exact disposable `agitated_sinoussi` Testcontainers PostgreSQL container was removed; it is not a product database. |
| Integration suite | **PASS historically; current added regression failed** | The previously completed suite passed 77/77 on its disposable Testcontainers PostgreSQL. The focused test above is a later failed run and means MFA restart coverage is not presently verified. |
| Final staging verifier | **FAIL** | The first final attempt hit the verifier's bounded `docker info` preflight timeout. A single retry (generated project ID `groudeal-sprint52-staging-20083b6b4b89`) passed Docker/Compose connectivity and version checks, then failed closed because `.local/sprint52/staging.env` is missing; no required staging values were created or supplied by this task. Latest sanitized evidence is `.local/sprint52/staging-verification-result.txt`, overall `FAIL`. It stopped before Compose startup. |
| Docker cleanup/isolation | **PASS for observed resources** | Final inventory showed `groudeal-sprint52-postgres-41710b322829` and `groudeal-sprint51-postgres` running and healthy. Both Sprint 5.1/5.2 network names and the protected Sprint 5.1 data volume plus Sprint 5.2 data/TLS volumes remained present. No verifier API/edge project was created. The failed Testcontainers container was removed by its exact name. No global prune or volume removal was used. |

#### Host TLS interception disposition

The prior host loopback HTTPS inspection observed issuer `Avast Web/Mail Shield Untrusted Root`, while the edge's own strict CA-chain and hostname verification passed from inside the isolated service network. This is strong evidence that host HTTPS inspection is substituting a certificate on that path. The evidence does not justify trusting the substituted root or weakening TLS. An operator should review Avast Web/Mail Shield HTTPS inspection policy and either approve a narrowly scoped loopback exclusion or perform the host probe from an approved non-intercepting environment, then repeat strict validation. No trusted-root or antivirus setting was changed.

#### Remaining NO-GO gates

- Diagnose the registration HTTP 500 in the focused disposable MFA restart test, rerun it successfully, and prove post-restart MFA remains mandatory.
- Exercise pending worker work across an actual API restart and verify no duplicate business mutation.
- Capture and review API/edge/worker logs while a staging verifier project is running, with secret-safe scanning.
- Provision the ignored local `.local/sprint52/staging.env` safely and rerun the final verifier. The latest 2026-11-04 verifier result is **FAIL** because that required file is absent, not a pass or manual-only `NOT VERIFIED` state. Its earlier retry also demonstrated transient Docker `info` latency.
- Keep the host TLS interception disposition under operator review.

**Final verdict: NO-GO.** The build, unit suite, prior integration suite, database separation/restart, and backup/restore evidence passed, but MFA restart, worker restart, full live log review, and the latest final verifier remain unresolved. Do not deploy or start Sprint 6 on this evidence.

### Final closure update — 2026-11-04

This section is the current disposition for the final closure task and supersedes prior statements that `staging.env` was absent or that the MFA and worker restart regressions had not been exercised.

| Gate | Result | Evidence and limits |
| --- | --- | --- |
| Admin MFA persistence | **PASS** | The focused restart regression reported 1 passed, 0 failed, 0 skipped. It exercised registration/setup, enrollment, the initial TOTP confirmation, MFA-protected authentication, API factory restart, persistence of the protected Data Protection key, and denial of password-only access after restart. The original registration HTTP 500 did not reproduce in the corrected end-to-end test. Investigation found test setup defects: the Data Protection path was applied too late for production configuration startup, and the test asserted completed enrollment without first confirming the initial TOTP. No application defect or exact PostgreSQL error was established for the earlier transient 500; do not attribute that historical symptom to an application root cause. |
| Worker recovery after API restart | **PASS** | `Expired_campaign_is_processed_once_after_API_host_restart` reported passed (2m35s) against disposable Testcontainers PostgreSQL. The test creates and approves a real campaign through APIs, lets its expiry occur while the first API host is stopped, starts a fresh host, waits for a worker batch, verifies expiration and exactly one expiry audit, then checks state/version stability and retry state. No direct DB mutation manufactured the final state. |
| Full integration suite | **FAIL (environmental startup)** | Final run totals: 7 passed, 72 failed, 0 skipped, 79 total. The 72 failures were Testcontainers fixture startup failures because Docker endpoint `npipe://./pipe/docker_engine` timed out/unavailable; they did not reach their test bodies. The focused worker regression passed in a separate run when Docker was accessible. Do not treat the full-suite attempt as a product test pass. |
| Unit tests | **PASS (prior evidence)** | 37 passed, 0 failed, 0 skipped from the previously completed run; not rerun because no application source changed in this closure continuation. |
| Serial build | **PASS (prior evidence)** | The prior Release serial solution build passed with 0 warnings and 0 errors. The current full integration invocation also rebuilt the API and integration-test projects successfully before fixture startup failed. |
| API/edge restricted log review | **NOT VERIFIED** | The new verifier-side scan and synthetic secret-sanitization tests passed, but no controlled API/edge Compose run completed in this closure, so no live API/edge logs were available for review. Prior PostgreSQL restricted-log scan remains PASS for its recorded patterns only. |
| Staging configuration | **PASS** | Root `.local/sprint52/staging.env` was reconstructed byte-for-byte from the already-existing authorized `.local/sprint52/41710b322829/staging.env`. It passed the verifier's local validation and identifier/secret-file checks without printing values. The root file remains ignored and untracked. No credential was invented. |
| Final staging verifier | **NOT VERIFIED** | Not run in this closure because Docker Engine is unavailable: `docker context show` reported `desktop-linux`, but Docker reported `server-unavailable` and the `dockerDesktopLinuxEngine` named pipe was absent. No final API/edge Compose project was created by this run. |
| PowerShell diagnostics and sanitization tests | **PASS** | Synthetic verifier tests passed. Windows PowerShell parser reported zero errors for the complete verifier and diagnostics test script; both files are ASCII-only with zero mojibake matches. |
| Isolation and cleanup | **PARTIAL** | No final verifier resources were created. The focused Testcontainers worker test created its disposable PostgreSQL/Ryuk containers and reported success; fixture cleanup was expected, but a fresh inventory could not be obtained after Docker became unavailable. The full-suite fixture failures occurred before containers could be created. No persistent Sprint 5.2 resources or protected Sprint 5.1 resources were intentionally changed. The final current Docker inventory could not be inspected. |
| Avast TLS interception | **PARTIAL** | Earlier controlled verification recorded Avast Web/Mail Shield certificate substitution on host loopback and strict in-container CA/hostname checks passed. No TLS bypass or trust-store modification was made. The final verifier did not rerun, so its current TLS gate remains unverified. |

`git diff --check` and the PowerShell synthetic checks pass. The earlier 37-test unit result and serial build result remain historical evidence and were not repeated because this continuation changed test harnesses and verifier diagnostics, not application source.

#### Current decisions

- **Backend staging-integration readiness: NO-GO.** Authentication/MFA and worker recovery have focused passes, the local configuration is legitimately present, and no new isolation violation is known. However the final staging verifier and live API/edge log review remain unverified, and the full integration suite could not start its Testcontainers fixture because Docker became unavailable.
- **Production readiness: NO-GO.** Production operational gates remain open, including configured monitoring/alerts, approved RPO/RTO and offsite recovery, owner approvals, support/recovery policy, and host TLS interception disposition.

#### Next step

Restore Docker Desktop Engine availability in the authorized host session, confirm the `desktop-linux` server responds, then run `.\scripts\verify-sprint51-staging.ps1`. Review its sanitized evidence and API/edge log-scan result before cleanup. After that verifier run, rerun the full integration suite once against its disposable Testcontainers fixture and record its exact totals. Do not target or restart any Sprint 5.1 resource.


### Docker-restored final closure — 2026-11-04

This is the latest status after restoring Docker access. It supersedes the immediately preceding closure update's full-suite and verifier-unavailable results. No application, production Compose, PostgreSQL, migration, or protected Sprint 5.1 resource was changed in this continuation.

| Gate | Result | Evidence and limits |
| --- | --- | --- |
| Final staging verifier | **NOT VERIFIED overall; automated gates PASS** | Run `groudeal-sprint52-staging-c1fa09cb5db9` passed configuration/isolation preflight, API healthy + edge running, strict in-container TLS liveness/readiness HTTP 200, API restart probes, edge restart probes, and exact scoped Compose cleanup. Host TCP 18443 was reachable. The verifier overall remains NOT VERIFIED because its template records separate manual gates even when those have independent PASS evidence elsewhere in this report. |
| API logs | **PARTIAL** | The bounded Docker log scan found 0 lines and 0 sensitive-pattern matches. There was no API log content to inspect in this run. The API uses normal framework logging; the app logs authentication/500 events, while no such error occurred during this healthy run. |
| Worker logs | **PARTIAL** | There is no separate worker container: expiry processing is hosted in the API. The API log stream was empty, so no live worker event lines were available. Source review confirms successful batches are not logged; only batch failures emit an error with a bounded exception type. Worker recovery behavior is independently covered by the passing real API-restart integration regression. |
| Edge logs | **PASS** | 22 bounded lines scanned in memory; 0 password/credentialed-connection-string, authorization/bearer/JWT, private-key, MFA/recovery, security-token, email, or context-labeled phone matches. Raw lines were withheld. |
| PostgreSQL logs | **PASS** | 6 bounded recent lines from only `groudeal-sprint52-postgres-41710b322829`; 0 sensitive-pattern matches. Raw lines were withheld. Phone matching required a phone/mobile field, so timestamps were not treated as phone leakage. |
| Full integration suite | **PASS** | **79 passed, 0 failed, 0 skipped, 79 total**, 8m53s, .NET 10.0.14, disposable Testcontainers PostgreSQL, `GROUPBUY_TEST_POSTGRES` unset. TRX: `tests/GroupBuy.IntegrationTests/TestResults/sprint52-integration-v416-final.trx`. The earlier 4.5.0 attempts hit Testcontainers' intermittent compiled image-regex timeout before tests; after the test-only dependency update to 4.16.0, the complete suite passed. |
| Testcontainers regression fix | **PASS** | Updated `Testcontainers.PostgreSql` 4.5.0 to 4.16.0 and changed the fixture to pass its existing `postgres:17-alpine` image through the explicit builder constructor. The updated constructor compiled with warnings-as-errors. Earlier isolated failures in seller dashboard, readiness recovery, and admin reads all passed individually; the final full run passed all 79. The upstream project documents the intermittent `MatchImage` regex timeout and the 4.16.0 source uses a 5-second timeout ([issue #1507](https://github.com/testcontainers/testcontainers-dotnet/issues/1507), [4.16.0 MatchImage source](https://raw.githubusercontent.com/testcontainers/testcontainers-dotnet/4.16.0/src/Testcontainers/Images/MatchImage.cs)). |
| Integration-test project build | **PASS** | Release, serial, no restore: 0 warnings, 0 errors after the package update. Full solution build remains prior PASS at 0 warnings/errors; no application source changed here. |
| MFA persistence | **PASS** | Focused restart regression: 1 passed. The final 79-test suite also passed all MFA coverage. |
| Worker real API restart recovery | **PASS** | Focused restart regression passed and is included in the 79/79 suite. It observes natural expiry with the first API host stopped, then verifies the fresh host processes it once and leaves stable state. |
| Unit tests | **PASS (prior evidence)** | 37 passed, 0 failed, 0 skipped; not rerun because this continuation changed only test infrastructure, the verifier scanner, and documentation. |
| Cleanup and isolation | **PASS** | The exact verifier Compose project and all Testcontainers/Ryuk resources were absent after cleanup. The Sprint 5.2 PostgreSQL container remained `running|healthy` on only `groudeal-sprint52-network-41710b322829`, with its labeled data/TLS volumes intact. Protected `groudeal-sprint51-postgres` remained `running|healthy` on only `groudeal-sprint51-network`; its labeled data volume remained. No cross-network attachment occurred. No database restart, migration, volume deletion, or global prune was performed. |
| TLS interception | **PARTIAL** | Host loopback presented an untrusted issuer consistent with Avast Web/Mail Shield interception. The verifier did not trust or bypass it. Strict in-container CA/hostname validation passed on every probe. |
| Scanner precision | **PASS** | Tightened MFA/recovery detection to require a labeled value or `otpauth://`, and added a negative regression for test method names. The synthetic diagnostics suite passed; both PowerShell files parsed with zero errors and remained ASCII-only/no-mojibake. The captured local integration artifacts were rescanned with 0 matches and then removed. |

#### Current decisions

- **Backend staging-integration readiness: GO FOR STAGING INTEGRATION.** The staging configuration and verifier work; strict TLS, health, restart, cleanup, and isolation checks pass; MFA and worker restart coverage pass; the full integration suite passes; no sensitive-pattern leak was found in available live logs. The empty API/worker log stream is recorded as limited observability evidence, not a detected leak.
- **Production readiness: NO-GO.** Production monitoring/exporter and alert delivery, on-call ownership, approved RPO/RTO and independently stored recovery material, customer recovery/support policy, named release/security/data/privacy approvals, and the host TLS interception disposition remain operator gates.

The final verifier's overall status intentionally remains **NOT VERIFIED** because it includes manual operational rows. This does not override the independent PASS evidence above or convert its manual rows to PASS. The production verdict remains NO-GO.

#### Exact next step

Begin the next separately controlled frontend/backend integration phase. Do not start Sprint 6, payments, tiered campaigns, Pickup Pass, RFQ, or delivery work as part of this closure.

# Sprint 5.2 staging isolation

## Safety boundary

The recovered database `groupbuy_sprint51`, container `groudeal-sprint51-postgres`, volume `groudeal-sprint51-postgres-data`, and network `groudeal-sprint51-network` are hard-protected. The verifier is not to run against any of them. It was not run in this task.

The host verifier now requires a separate Sprint 5.2 database name, PostgreSQL container, runtime login, named PostgreSQL data volume, external network, edge host port, and private subnet. The container, volume, and network must carry `groudeal.sprint52.staging=true`; it checks that the configured unique volume is actually mounted at PostgreSQL's data directory and that the isolated network has only the expected database container attached before the API is started. The private subnet must be a non-production private `/24`; the edge proxy address must be its `.10` address; the edge is bound only to a unique loopback host port in 18000-29999. Runtime connection uses `VerifyFull`. API/edge images are immutable digests; the verifier's local API build is a separate override.

It rejects protected identifiers in settings and runtime connection strings before any Docker resource inspection or Compose operation, rejects matching process-environment overrides, checks project collisions, and uses a unique random project. Cleanup is limited to the project created by that invocation and never removes volumes or the externally managed database/network.

## Verification state

- Added a pure provisioning-safety helper and synthetic tests for random run IDs, verifier-compatible unique names, protected-resource rejection, protected port/subnet rejection, collision handling, safe evidence projection, and identity-proven cleanup eligibility. The helper performs no file, Git, Docker, or database I/O.
- Provisioning remains blocked in this Codex execution context: Docker CLI 29.7.2 is present, but Docker Engine operations (`docker version`, container/volume/network inventory) fail with permission denied on `npipe:////./pipe/dockerDesktopLinuxEngine`. OpenSSL/psql are not installed as host commands. No provisioning script has created resources, no database was contacted, and the verifier was not run. Since actual IDs, mounts, subnet occupancy, host port availability, and reviewed immutable image digests cannot all be proven here, do not provision or clean resources from this context.
- Implemented: fail-closed resource-name, protected-endpoint, role, port, subnet, and override checks.
- Implemented: actual container name, health, staging-label, network alias/subnet, mounted volume identity, and volume/network label checks.
- Implemented: PowerShell synthetic tests for valid Sprint 5.2 inputs and protected DB/container/network/volume/endpoint references.
- Not verified: live Sprint 5.2 DB/network/volume labels, PostgreSQL TLS certificates, runtime grants, external network isolation, actual Compose startup, TLS probes, restart behavior, and scoped cleanup.
- Blocked: the required dedicated synthetic database, certificate assets, fresh random credentials, labeled volume/container/network, and target-host isolation inventory are not established in this workspace. Do not run the verifier until those are provisioned and independently reviewed.

The helper tests are not a substitute for a host provisioning run. The next safe step is to restore Docker Engine access in the authorized host PowerShell context, inspect the full Docker inventory read-only, provide reviewed API/edge/PostgreSQL image digests, then implement/execute the destructive-resource provisioning and exact identity-scoped cleanup under an approved operator session. Never infer that an inaccessible resource is absent.

The verifier requires all sensitive paths to be absolute and local staging inputs to be ignored/untracked. Never put passwords, keys, connection strings, tokens, or certificates in tracked files or reports.

## Sprint 5.2 run 41710b322829 — 2026-11-04

This section supersedes the earlier blocked verification notes above for this run only. It does not change the protected Sprint 5.1 resources or authorize production deployment.

- The isolated PostgreSQL container remained healthy, published no host port, and stayed on `groudeal-sprint52-network-41710b322829`. The verifier created and removed only its unique API/edge Compose project; cleanup used `down --remove-orphans` without `-v`. The Sprint 5.2 database, network, and data/TLS volumes were retained.
- TLS liveness and readiness both passed inside the isolated edge container with certificate-chain and configured hostname verification enabled. Both returned HTTP 200 (`live` and `ready`). The edge listened on container port 443, mapped only to `127.0.0.1:18443`; host TCP reachability passed.
- The Windows host loopback TLS diagnostic received a certificate issued by `Avast Web/Mail Shield Untrusted Root`, rather than the staged edge CA. Treat this as local HTTPS inspection; the verifier records it as a sanitized diagnostic and does not trust, install, or bypass that certificate. The in-container probe validates the actual edge certificate chain independently.
- API restart and edge restart passed. Strict TLS liveness/readiness probes returned HTTP 200 before restarts, after the API restart, and after the edge restart. The edge restart wait accepts `running` because the production edge image has no Docker `HEALTHCHECK`; the subsequent strict edge TLS probes validate service health.
- The verifier overall result is **NOT VERIFIED**, because migration/runtime boundary, database and worker recovery, worker restart with pending work, Admin MFA persistence, PostgreSQL restart/volume/backup/restore, full-stack restart, and restricted log review remain operator checks. This is **NO-GO for production** until those checks and the local host TLS path disposition are reviewed.
- The verifier now retries transient Compose container lookups within the existing bounded health wait. API/edge TLS probes run with `curl` inside the edge container and trust only its mounted public certificate chain; the Windows host TLS result remains diagnostic. Secret files, private keys, PostgreSQL configuration, migrations, and production Compose were not changed by this verifier hardening.

Automated evidence is stored at the ignored local path `.local/sprint52/staging-verification-result.txt`. It contains sanitized stage results and the ephemeral Compose project identifier; it contains no credentials or raw container logs.

## Continuation evidence — 2026-11-04

The isolated Sprint 5.2 PostgreSQL restart and backup/restore drill completed successfully as recorded in the production-readiness report. The protected Sprint 5.1 container, database, data volume, and network remained separate and were not restarted, attached, migrated, backed up, restored into, or deleted.

The first final verifier attempt generated project identifier `groudeal-sprint52-staging-213890fd4d4b` but failed its Docker `info` preflight after 30 seconds, before Compose startup. One retry passed Docker/Compose connectivity and version checks, then failed closed because the ignored local `.local/sprint52/staging.env` file is absent. Latest sanitized evidence records `FAIL - Required staging configuration - Missing file .local/sprint52/staging.env`; this task did not create or populate that local secret-bearing configuration. A subsequent read-only inventory confirmed that no verifier API/edge containers remained and both database containers were running healthy; the expected Sprint 5.1/5.2 networks and volumes remained present.

A separate focused Admin MFA restart integration regression compiled but did not pass: account registration returned HTTP 500 before MFA enrollment and the disposable test database cleanup connection timed out. That failed Testcontainers container was removed by exact name. No MFA restart pass is claimed. See the production-readiness report for test counts, log scan, build results, failure details, and remaining gates.

## Final closure update — 2026-11-04

This update supersedes the earlier “staging.env missing” statement for the final closure. The root `.local/sprint52/staging.env` was reconstructed from the already-authorized ignored run configuration at `.local/sprint52/41710b322829/staging.env`; validation confirmed it is ignored and untracked. No values were generated or printed.

The final verifier was **NOT VERIFIED** in this closure. Read-only inspection reported Docker context `desktop-linux`, but the server endpoint was unavailable and the Docker Desktop named pipe was absent. Therefore this run created no API/edge Compose project, collected no live API/edge logs, and performed no verifier cleanup. The restricted-log scanner's synthetic tests passed, but live API/edge log review remains **NOT VERIFIED**.

The focused worker restart regression reported pass against disposable Testcontainers PostgreSQL. A later full integration attempt reported 7 passed, 72 failed, 0 skipped; all 72 failures were fixture startup failures because the Docker named pipe became unavailable. The focused test result is not a substitute for a complete suite run.

The focused Testcontainers worker run created disposable PostgreSQL/Ryuk containers and reported its test passed; its fixture cleanup is expected, but the final Docker inventory could not be inspected after the engine became unavailable. The full-suite attempt failed at fixture startup and created no Testcontainers database. No persistent Sprint 5.2 resource or protected Sprint 5.1 container, database, volume, or network was intentionally changed. A fresh final inventory cannot be claimed. Earlier documented Sprint 5.1/5.2 isolation evidence remains historical evidence.

Current decision: **NO-GO for staging integration** until Docker is available, the final verifier completes its isolated startup/TLS/restart/log-scan/cleanup sequence, and the full integration suite completes. Production remains **NO-GO** for the operational gates listed in the readiness report.


## Docker-restored final closure — 2026-11-04

The final verifier ran with project `groudeal-sprint52-staging-c1fa09cb5db9`. Configuration/isolation preflight, API health, edge running state, strict in-container TLS liveness/readiness (HTTP 200), API restart probes, edge restart probes, and scoped API/edge cleanup passed. Host TCP 18443 was reachable. The verifier's overall result remains **NOT VERIFIED** because it still records manual operational checklist rows; the related migration/runtime, MFA, worker restart, PostgreSQL restart, and backup/restore gates have separate evidence in the readiness report.

Restricted-log review, with all raw lines withheld:

- API: 0 lines and 0 pattern matches; there was no API content to inspect in the bounded window.
- Worker: hosted in the API, with no separate worker log stream. Successful expiry batches are not logged; failures are logged with a bounded exception type. Worker restart behavior passed its dedicated integration regression and the full suite.
- Edge: 22 lines, 0 sensitive-pattern matches.
- Sprint 5.2 PostgreSQL: 6 recent lines, 0 sensitive-pattern matches. Phone detection was context-bound, so timestamp strings were not treated as phone leakage.

The full integration suite passed **79/79** with `GROUPBUY_TEST_POSTGRES` unset. The first attempt using Testcontainers 4.5.0 hit an intermittent upstream image-regex timeout; the test-only package was updated to 4.16.0, the PostgreSQL builder was adjusted to the explicit image constructor, and the final full suite passed. No application source or production Compose configuration changed.

Post-run inventory showed no verifier Compose project and no Testcontainers/Ryuk resources. `groudeal-sprint52-postgres-41710b322829` remained healthy on only the Sprint 5.2 network, with its labeled data/TLS volumes present. `groudeal-sprint51-postgres` remained healthy on only `groudeal-sprint51-network`, with its protected data volume present. No Sprint 5.1 resource was restarted, mounted into another network, or modified; no global prune or migration was run.

The host TLS path still presented an Avast Web/Mail Shield untrusted issuer. Strict CA and hostname verification inside the isolated edge passed; no certificate trust/bypass was added. Production remains **NO-GO** for the operational gates in the readiness report. Staging-integration decision: **GO FOR STAGING INTEGRATION**. The next phase must be separately controlled; this continuation does not start frontend integration or Sprint 6.

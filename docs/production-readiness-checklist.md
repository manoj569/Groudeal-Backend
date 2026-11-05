# Production readiness statuses — Sprint 5

Status date: 2026-11-02 (Sprint 5.1 backend staging verification). PASS requires local code evidence; it is not a target-host security certification. BLOCKED identifies an unavailable prerequisite, NOT VERIFIED requires external evidence, and FAIL records known absent behavior. No deployment or live pilot is approved.

| Requirement | Status | Evidence / condition |
| --- | --- | --- |
| .NET 10 LTS/package upgrade | PASS | Verified official SDK download/hash and supported package versions; build/tests in sprint5-verification.md |
| Runtime/global host patch deployment | NOT VERIFIED | Workspace SDK is verified; actual target host/image digest and servicing acceptance still required |
| Secure Admin provisioning/revocation | PASS | Explicit fixed-role CLI, idempotency/audit/session invalidation, suspended-account revocation regressions |
| Admin TOTP MFA/replay/lockout/recovery/reset | PASS | Real PostgreSQL tests and browser journey; no password-only Admin bypass |
| Protected authenticator/recovery storage | PASS | Identity token-store encryption and certificate-encrypted production key-ring proof with synthetic certificate |
| Actual persistent keys/certificate/rotation/backup ACLs | NOT VERIFIED | Operator-provided infrastructure and key recovery drill required |
| JWT current role/status/version enforcement | PASS | Retained revocation/account tests and signed Admin-without-MFA rejection |
| Customer/Seller/Admin ownership and pickup secrecy | PASS | Existing marketplace regressions retained |
| Production configuration validation and proxy/CORS limits | PASS | Fail-closed config, explicit one-hop trust, API HTTP redirect/health exceptions and regression evidence |
| Actual HTTPS ingress, proxy identity, CORS and frontend headers | NOT VERIFIED | Real certificates/domains/host must be exercised externally |
| Runtime DB grants and separate operator privilege template | PASS | Actual SQL template tested; no DDL/audit tampering/migration writes/Admin grants/MFA reset from runtime |
| Actual production DB TLS/SCRAM/grants/network/pool sizing | NOT VERIFIED | Isolated fixture uses loopback trust; real target credentials/CA/permissions must be inspected |
| Additive migrations and recovery strategy | PASS | Fresh migrations, legacy-audit-preserving upgrade/empty-security rollback, no pending model changes |
| Legacy live campaigns and physical stock cutover | NOT VERIFIED | No production data supplied; separate per-row review/sign-off, no automatic backfill |
| Worker restart/retry/exhaustion/cancellation/shutdown | PASS | Persisted failure/restart/idempotency tests, finite attempt cap, per-item progress and graceful host disposal |
| Actual container/host kill, restart and DB failover behavior | NOT VERIFIED | Local host disposal/outage tests do not prove OS/container/PITR failure modes |
| Safe readiness/liveness and diagnostics | PASS | Public status-only probes, bounded dependency checks and safe errors; protected detailed health |
| External monitoring/alerts/on-call response | NOT VERIFIED | Need target alerting, named incident/release owner and tested escalation |
| Backend/frontend builds and marketplace compatibility | PASS | Backend 37 unit/66 PostgreSQL; frontend 33 unit/60 browser/5 real workflows/1 PWA; typecheck/lint/build pass; exact evidence in sprint5-verification.md |
| Dependency vulnerability audits | PASS | NuGet direct/transitive and npm production report zero; full npm has 18 Moderate/2 Low development advisories requiring separate review below; current-feed result only |
| Moderate/Low frontend development-tool advisories | NOT VERIFIED | Remediate or obtain documented build-system risk acceptance; no forced unrelated upgrade |
| Docker Compose configuration | PASS | Development and production config commands exit 0 with configuration-only placeholders; syntax only |
| Docker image build/start/health/volume/TLS execution | NOT VERIFIED | Docker CLI selects `desktop-linux`; named pipe exists but Codex is denied access. User reports host PowerShell works; no runtime claim. |
| Sprint 5.1 Docker backend/PostgreSQL staging runtime | NOT VERIFIED | Tooling execution-context limitation, not a demonstrated backend defect. A separate synthetic PostgreSQL container can share the existing production Compose bridge without changing production topology; host-only setup and verifier are documented in sprint51-staging-verification.md. No Docker runtime was invoked from Codex. |
| Clean isolated PostgreSQL migration and runtime grants | PASS | 37 unit/66 PostgreSQL integration tests on fresh local PostgreSQL 17.6; latest migration and restricted-role tests passed; this is not a persistent staging service |
| API/MFA/worker/DB state across real process and volume restart | NOT VERIFIED | Integration tests cover DB-backed MFA, retries, health and recovery; no container/API process or persistent DB volume restart after enrollment |
| Persistent staging backup/restore and app against restored database | NOT VERIFIED | Sprint 5 synthetic fixture backup/restore exists; no Sprint 5.1 staging dump/restore drill |
| GitHub CI execution | NOT VERIFIED | Workflow added; no push or remote run performed |
| Logical fixture backup/restore and invariants | PASS | Fresh five-workflow fixture dumped/restored; counts, three migrations and selected zero-violation checks match; no production/PITR claim |
| Production encrypted offsite backups, PITR, RPO/RTO/restore | NOT VERIFIED | Target-specific recovery objectives and measured restore/key recovery required |
| Audit retention/no silent purge | PASS | Retain-by-default code/policy; runtime audit INSERT/SELECT only |
| Legal retention, immutable archive/export/access review | NOT VERIFIED | Owner/legal approval and storage evidence required |
| Customer self-service password recovery/email ownership | FAIL | No delivery/verification provider or self-service recovery implemented; review/support acceptance or scoped implementation required |
| Real named-operator IAM, supervised onboarding and MFA recovery | NOT VERIFIED | CLI operator strings are audit context, not human authentication; restrict operational credential/job access |
| Seller legal/FSSAI/commercial review | NOT VERIFIED | Syntactic fixture values are not business/legal verification |
| Realistic pilot load/accessibility/device validation | NOT VERIFIED | Small local measurements and Chromium emulation are not production capacity/device certification |
| Production data mutation/deploy/push by this task | PASS | None performed; all accounts/databases are synthetic isolated fixtures |

Sprint 5.1 backend verdict: **BACKEND NOT READY — BLOCKERS REMAIN** until Docker staging runtime, persistent MFA/key restart and staging restore are verified.

### Sprint 5.2 staging provisioning follow-up — 2026-11-03

| Requirement | Current state | Evidence / remaining action |
| --- | --- | --- |
| Provisioning safety helpers | PASS (synthetic only) | Pure helper tests cover unique names, protected identifiers/port/subnet, collision decisions, redacted evidence projection and cleanup identity predicates. No Docker resources are created by these tests. |
| Live staging provisioning and cleanup | BLOCKED | Docker Engine named pipe access is denied in the Codex execution context, so resource IDs, mounts, membership, host port availability, and exact cleanup identity cannot be proven. No provisioning, cleanup, migration, or verifier run occurred. Continue only in an authorized host context with read-only inventory and reviewed immutable image digests. |
| PostgreSQL TLS and runtime grants | NOT VERIFIED | No staging database was created or contacted; generated TLS and role setup must be validated only on a distinct Sprint 5.2 target. |

Current Sprint 5.2 verdict remains **NO-GO** for the isolated staging verification gate. Do not treat the synthetic guard suite as evidence that a live target is isolated, and do not run the verifier until the host-side identity checks and setup are complete.

Decision: **NO-GO for real-customer controlled pilot until external gates and known recovery/support limitations are resolved or explicitly accepted by the responsible release/security/data owners.** Code may proceed to controlled synthetic staging verification. Do not reinterpret local PASS as deployment authorization.

### Sprint 5.1 recovery status update — 2026-11-03

The current execution built the full solution successfully with .NET SDK 10.0.403 (`--no-restore`, isolated build paths; 0 warnings/errors). Restore and EF model metadata checks remain blocked by unavailable NuGet vulnerability-feed metadata (`NU1900`). Unit tests and the focused test-database safety tests did not execute: VSTest failed to connect to the testhost after 90 seconds (0 tests reported). The full PostgreSQL integration suite was also attempted with a 20-second testhost bound and failed to connect before executing tests (0 tests reported). PowerShell synthetic verifier diagnostics tests passed; the verifier and test script parsed with zero errors.

The local Sprint 5.1 configuration points to the recovered `groupbuy_sprint51` database. Since the API starts a periodic state-mutating `ExpiryWorker`, the staging verifier was not run. It now rejects this protected database target before starting Compose. Docker access was denied in this execution context, so database/container health, migration history, live schema/roles, backup/restore, and process/volume recovery are NOT VERIFIED. No Docker or database operation was performed.

This current status remains **NO-GO**. It supersedes any earlier historical PASS entries for tests or local PostgreSQL checks as evidence for this recovery execution; those earlier results remain historical and do not establish the current recovered database’s integrity or recoverability.

### Comprehensive backend audit update — 2026-11-03

Current audit verification supersedes the 2026-11-03 recovery-execution test/restore status above for local code evidence: full .NET 10 build passed with 0 warnings/errors; 37/37 unit tests and 72/72 PostgreSQL integration tests passed against fresh disposable Testcontainers PostgreSQL with `GROUPBUY_TEST_POSTGRES` unset. Normal NuGet restore with vulnerability auditing enabled succeeded from the host network; the current direct/transitive audit reports no vulnerable packages in all six projects. EF reports no pending model changes after two additive seller-ownership migrations; `docs/schema.sql` was regenerated. Docker access was used only by Testcontainers for disposable test containers. No production Compose stack, staging verifier, recovered database, or persistent volume was accessed.

The integration suite now verifies that PostgreSQL rejects campaigns and inventory rows that reference another seller’s product variant. Runtime-role test credentials and repository-root test asset paths are corrected. Expected expiry-worker `NpgsqlException`, injected Data Protection `CryptographicException`, and campaign retry `DbUpdateException` logs were each traced to passing negative/recovery tests. Exception diagnostics now include redacted PostgreSQL details, but a dedicated arbitrary-message redaction regression remains open.

Current release decision remains **NO-GO**. Passing tests do not verify the recovered database’s schema/grants, production TLS, Compose edge/API runtime, backup/PITR, key restoration, real process/volume restart, target-host ACLs, monitoring, or operator IAM. Do not run the new migrations against the recovered database without separate approval and a reviewed backup-backed plan. See [comprehensive backend audit](comprehensive-backend-audit-2026-11-03.md) for findings, classifications, commands, counts, and remaining actions.

### Sprint 5.2 production-readiness update — 2026-11-03

This section supersedes earlier Sprint 5.1 descriptions where they conflict. The verified Sprint 5.1 baseline remains historical evidence only; this update reports only Sprint 5.2 checks recorded in [the Sprint 5.2 report](sprint52-production-readiness-report.md).

| Requirement | Current Sprint 5.2 state | Evidence / remaining action |
| --- | --- | --- |
| Arbitrary exception-message secrecy | Implemented; tests unverified | Messages are no longer logged; bounded type chain and safe PostgreSQL classifications remain. C# canary test compiled, but VSTest failed to connect to testhost (0 tests). |
| Production API artifact | Implemented and static-config verified | Production Compose requires an API image pinned by digest and does not build from source; base and synthetic overlay `compose config --quiet` both exit 0. |
| Metrics | Implemented; tests/exporter blocked | Route-template request counts/latency, authentication failures, worker failures/retries/exhaustion are instrumented with bounded tags. Test compiled; no VSTest execution or approved collector/alert delivery. |
| Populated ownership-migration upgrade | Tests implemented, not executed | Synthetic valid historical rows and invalid cross-seller campaign data are covered by compiled integration tests. Docker inventory unavailable; no database opened. |
| Backup/restore | Test implemented, not executed | Compiled Testcontainers `pg_dump`/`pg_restore` drill. Docker inventory unavailable; no backup/restore operation ran. Production offsite/KMS/PITR remains blocked. |
| Staging isolation | Guarded; live staging blocked | Windows PowerShell parser and synthetic guard tests pass. Verifier requires unique Sprint 5.2 names, volume/network/container labels, port/subnet, runtime identity, and rejects protected endpoints. It was not run. |
| Customer account recovery/support | Open | Password/email verification provider and privacy/support policy are unresolved. No payment/refund behavior was introduced. |
| Production infrastructure | Blocked | Real TLS, grants, KMS, persistent key storage, external monitoring, offsite backups, restart behavior, operator IAM, and pilot acceptance remain unverified. |

Sprint 5.2 verdict remains **NO-GO** until final results are recorded and all external pilot gates are closed or explicitly accepted by accountable owners. This update does not authorize deployment, migration against recovered data, or staging execution.

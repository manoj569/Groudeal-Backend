# Production operations and recovery runbook

Sprint 5 code/templates are not deployment approval. Require the status/evidence in production-readiness-checklist.md and the separate legacy plan in deployment-plan.md. Do not connect these verification scripts to production.

## Configuration and startup

Use .NET 10.0.403 SDK / serviced 10.0.14 runtime baseline or a newly reviewed compatible patch. Build immutable release artifacts and scan/pin image digests. Dockerfile/compose.production.yaml provide a non-root API, read-only root filesystem, writable bounded /tmp, persistent encrypted key-ring location, process/resource limits, graceful stop and an explicit trusted reverse proxy. External PostgreSQL is required; production Compose publishes no database port. The edge binds loopback 8443 by default. Adapt ingress/DNS/certificates/addresses deliberately; do not expose the private API directly. The fixed example proxy IP/subnet must not collide with existing networks. Replace EDGE_IMAGE with a reviewed immutable nginx digest.

The templates need real protected files and target-host verification. No image build/start, TLS handshake, volume persistence or orchestrator restart is implied by YAML validation. CI workflow exists but has not run on GitHub until separately authorized publication.

| Variable | Requirement |
| --- | --- |
| ASPNETCORE_ENVIRONMENT | Production; no Demo:* values |
| AllowedHosts | Explicit hostnames separated by semicolons; no wildcard |
| ConnectionStrings__Database | Dedicated runtime identity; SslMode=VerifyFull, trusted CA/hostname, Pooling=true, Maximum Pool Size 1–200, Timeout 1–30s, Command Timeout 1–60s, Include Error Detail=false |
| Jwt__SigningKey | External cryptographically random secret, at least 32 UTF-8 bytes; never a sample/password |
| Jwt__Issuer / Jwt__Audience | Exact expected values |
| Jwt__ExpiryMinutes | 1–60; Admin access additionally capped at 10 minutes |
| DataProtection__KeyPath | Existing persistent absolute directory writable only by API identity/controlled backup operator |
| DataProtection__CertificatePath / CertificatePassword | Valid private-key PFX and external password; files mounted read-only |
| DataProtection__PreviousCertificates__n__Path / Password | Retained private certificates for decrypting earlier master keys during rotation |
| Proxy__KnownProxies__n | Explicit IPs only; no forwarding enabled without them, one symmetric X-Forwarded-For/Proto hop |
| Cors__Origins__n | Exact HTTPS origins if cross-origin UI is used; same-origin preferred, no wildcard/path/query/user-info |
| Operations__WorkerMaxAttempts | 1–100, default 10; finite automatic attempts |
| Operations__ConfirmationMinutes / PickupHours | Existing 1–1440 / 1–168 bounds; persisted existing deadlines do not change |
| AdminProvisioning__Enabled | Temporary one-shot operator job only; forbidden on normal Production API |
| HealthChecks__Host | Optional probe Host override when allowed-host configuration is file-only; container template otherwise uses first AllowedHosts value |

/run/secrets key-per-file configuration is supported in Production; Development does not implicitly load that directory. Double underscores in filenames map to configuration sections. Production file settings override process environment. Keep migration/provisioning connection files separate from the runtime mount. Do not put secrets in VITE variables, arguments, repo .env files, console logs or CI artifacts. `.env.production.example` contains paths/placeholders only. Set the persistent directory ownership for container UID/GID 1654 before startup; do not grant world-write to make a probe green.

Startup performs an actual Data Protection protect/unprotect probe and fails with a safe configuration error if key storage or protection is unusable. Raw framework cryptographic diagnostics are suppressed; retain the safe error type and inspect restricted infrastructure access separately. A probe does not prove old-factor decryption after rotation; perform that restore drill explicitly.

Migrations are an explicit operator step using the migration identity and reviewed idempotent SQL. The API never automatically migrates. Apply the additive MFA migration and review/reapply scripts/grant-runtime.sql for the standalone runtime role. That template removes public table/temporary-create permissions in a dedicated database, grants necessary runtime DML, leaves migration history/audit read-only except audit INSERT, applies Admin membership RLS and blocks runtime MFA reset/enrollment authorization. Review existing policies/grants before use; never run it blindly in a shared database. Prevent runtime login to other databases with database-specific pg_hba/network rules. Provision/reset uses the separate privileged operator identity, not runtime credentials.

## Health and alerts

`GET /health/live` reports only process liveness. `GET /health/ready` returns 200 only with recent successful worker activity, database connectivity and no pending migrations; dependency checks are cancellation-bounded to two seconds. It returns safe 503 `not_ready`, never connection details. These two status-only paths may be probed internally over HTTP; application APIs still redirect to HTTPS outside Development. Ingress must restrict probe access as required. Docker's --healthcheck command targets internal 8080 and supplies an allowed Host. Health does not certify TLS, backups, seller legality, privilege grants or image safety.

Protected `/api/v1/admin/operations/health` exposes persisted overdue/retry/legacy/exhausted counts and process-local batch/progress heartbeat, with normalized errors. Monitor external liveness, readiness, DB saturation, lock waits, request failures/latency and retry age. Alert immediately on exhausted work, stale progress, rising auth failures or unexpected Admin security events. A healthy process can still contain a failed campaign requiring review. Docker health status alone does not automatically restart an unhealthy container; configure the orchestrator/incident response deliberately.

The existing worker runs every 15 seconds, 100 candidates/batch, seller->campaign->pickup locks, bounded per-command/lock waits, exponential retry capped at 900 seconds. After WorkerMaxAttempts it pauses automatic processing of that item while retaining state/stock/reservations and exposing exhaustion. It never fabricates success, silently releases evidence or creates a second notification dispatcher. Long batches report item progress. Host shutdown allows 30 seconds; container stop grace is 40 seconds. SIGKILL/disk/network failure modes still need target-host rehearsal.

## Database outage or exhausted work

Keep failed requests closed; do not disable ownership/MFA or shorten validations to recover service. Readiness becomes 503 while liveness remains available. Restore database connectivity/permissions/TLS and verify the existing worker resumes; persisted business effects/audits/notifications must remain idempotent. Examine safe campaign IDs/error types through protected health and restricted logs, not customer secrets.

For exhausted items, pause relevant intake if commitments are uncertain. Determine and fix the root cause, back up the affected rows, reconcile stock/reservations/pickups and obtain an approved operator change. Only then reset the specific persisted retry counter/timestamp through reviewed transactional operator SQL under a row lock; record an operator audit/reference separately. No public force-retry endpoint exists. Verify one terminal audit/notification and unchanged price snapshots after resumed processing. Never mass-reset all failures or rerun collection to clear a red dashboard.

## Backup and restore

Define and approve RPO/RTO, encryption, storage location, retention and restore owner before pilot. Use pg_dump/pg_restore for a consistent logical backup and WAL/PITR where the agreed RPO requires it. Use PGPASSFILE/service/secret-manager injection; never put passwords in command arguments. Back up database, migration history, DP key ring, current/previous private encryption certificates and required key metadata as separately access-controlled assets. Keep signing secrets in the secret manager, not DB dumps.

Restore only into a separate isolated database first. Apply no destructive schema reset. Verify migration history, constraints, campaign commitments, reservation snapshot arithmetic, pickup collection/code clearing, notification uniqueness and audit counts; compare selected totals to the backup record and run the real marketplace/MFA smoke journey. The Sprint 5 test harness performs a disposable logical dump/restore and selected-invariant comparison; it is not production PITR or an RPO/RTO drill.

A live restore requires incident/data-owner approval and a write freeze. Reconcile any physical pickups and changes after the recovery point before reopening: raw replay of old data could resurrect consumed codes or stock. Preserve post-backup audit/evidence. Prefer a forward fix; do not run the MFA Down migration on enrolled accounts or downgrade to password-only Admin code. The additive migration's Down drops challenge/replay context and cannot revoke knowledge of already revealed factors. Use reviewed artifact rollback only when compatible; protect enrollment/security state.

## Secret compromise and identity recovery

For signing-key compromise rotate via secret manager and coordinate all instances; the single-key validator immediately invalidates earlier JWTs. Require new login/MFA. Zero-downtime multi-key rotation is not implemented or claimed. For factor loss follow admin-mfa-provisioning.md with independent identity verification and a fresh approved reference. Do not send a setup key/recovery code in a ticket or bypass MFA temporarily.

For DP certificate rotation retain old private decryption certificates and all still-needed master keys, configure PreviousCertificates and test decryption of existing factors on a separate restore before rollout. Losing all decrypting keys requires controlled per-Admin reset/re-enrollment, never plaintext fallback. Time synchronization, certificate expiry and key-directory access must be monitored.

## Logs, retention and incident handling

Application logs are structured JSON. Request errors, worker failures and authentication diagnostics use safe type/trace/identifiers; framework raw EF/JWT exception streams are suppressed in favor of these safe events. Keep EF sensitive logging off and database detailed errors disabled. Nginx access format omits query strings, bodies and Authorization; restrict/review proxy error logs and all downstream sinks. Never enable debug body/header logging during an MFA/pickup incident. Synthetic verification scans server/seed/provision logs for its generated secrets; test failure browser artifacts can contain synthetic enrollment-screen content and must remain restricted/ignored, never public uploads.

Policy implemented now: retain all audit/security history; no automated purge or audit UPDATE/DELETE permission for runtime. Approved legal/commercial retention duration, archive immutability, export/redaction and access-review owner remain external release gates. Proposed cleanup schedules must be reviewed before code/SQL is introduced; do not silently delete historical audit rows, MFA security events or fulfillment evidence.

Incident owner records start time, safe trace/entity IDs, affected operations, customer impact, containment, recovery changes and post-incident integrity results. Stop new reservations/publication if inventory correctness, identity isolation or pickup evidence is uncertain; keep the recovery Admin path and restricted observability available. No real payment operations exist. Release owner signs off the production checklist after staging failover/restore/shutdown, TLS/proxy, MFA/operator, least-privilege and realistic-load drills.


Credential-attempt limits apply to login and registration (10/IP/minute); authenticated profile/logout calls retain the global limit and do not consume this credential quota. MFA endpoints have their own 30/IP/minute limit plus persistent account/challenge lockouts. Admin operations have a 60/actor/minute limit. The nginx template adds a pre-authentication IP budget; its runtime behavior remains unverified without Docker.
The legacy retryingCampaigns health field retains its historical count of all persisted failures, including exhausted entries. Use exhaustedCampaigns and workerMaxAttempts to identify work that will no longer retry automatically.

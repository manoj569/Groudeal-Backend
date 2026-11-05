# Sprint 5.1 — Backend staging verification

> **Historical instructions: do not execute the Sprint 5.1 setup commands below.** Their resource names include `groudeal-sprint51-postgres`, `groudeal-sprint51-network`, and `groudeal-sprint51-postgres-data`, which are protected recovered resources. Sprint 5.2 replaced the verifier defaults and requires uniquely named Sprint 5.2 synthetic assets. Follow [Sprint 5.2 staging isolation](sprint52-staging-isolation.md); the verifier remains unexecuted until the separate inventory and staging assets are proven safe.

**Verification date:** 2026-11-02 (Asia/Calcutta)
**Repository:** Groudeal Backend
**Branch / commit:** `main` / `0808f0e73fd1f846f15c45c8495e2af9cf63500d`
**Initial Git state:** clean; no code changes were present before this report.
**Final verdict:** **BACKEND NOT READY — BLOCKERS REMAIN**

This report records local backend evidence and prepares (but does not execute) a host-side Docker staging workflow. It does not claim a Docker deployment or production-like staging certification. No frontend repository was accessed, no production database or live credential was used, and no commit or push was made.

## Environment

| Item | Result |
| --- | --- |
| Host | Windows 11 x64 (build 26200); disposable local files confined to ignored `.local` test paths |
| .NET SDK | 10.0.403 from the repository-local SDK selected by `global.json` |
| .NET / ASP.NET runtime | 10.0.14 |
| PostgreSQL | Portable PostgreSQL 17.6; a fresh loopback-only cluster was started for the test runner and stopped at completion |
| Docker CLI / Compose | Docker 29.7.2; Compose v5.5.0 |
| Docker Engine from Codex | **INACCESSIBLE FROM THIS EXECUTION CONTEXT**: `desktop-linux` is selected and the named pipe exists, but `docker version`, `docker info`, and `docker ps` return `permission denied` for `npipe:////./pipe/dockerDesktopLinuxEngine`. WSL reports `Wsl/E_ACCESSDENIED`. |
| Docker Desktop on host | User reports Docker Desktop works from normal Windows PowerShell. This was not independently executable from Codex. The evidence supports a Codex/host execution-context boundary, not a demonstrated Groudeal defect. |

## Results

| Check | Status | Evidence |
| --- | --- | --- |
| Clean Git start and source inspection | PASS | `git status --short` was empty; branch and commit above. Reviewed Docker/Compose, SQL grants, migration, Identity/MFA, health/worker, runtime configuration and operations docs. |
| Tracked secret scan | PASS | No real credentials, signing key, authenticator seed, recovery code, certificate or private key found. The findings register below records candidate settings without values. |
| Development/production Compose syntax | PASS | `docker compose --env-file .local/sprint51-compose.env -f compose.yaml config --quiet` and the corresponding `compose.production.yaml` command both exited 0. The env file contained only configuration placeholders. This is syntax validation, not image or runtime validation. |
| Docker image build/start, container health, volume, TLS, container restart | **NOT VERIFIED — Docker runtime inaccessible from Codex** | Per task instruction, no Docker commands were run after confirming the named-pipe access denial. Host-only script is syntax-checked but has not been executed; no runtime passes are claimed. |
| .NET build | PASS | `scripts/verify-sprint5-backend.ps1`: build succeeded, zero warnings/errors. `.local/sprint5-build.log`. |
| Unit tests | PASS | 37 passed, 0 failed/skipped. |
| PostgreSQL integration tests | PASS | 66 passed, 0 failed/skipped on the fresh isolated PostgreSQL 17.6 cluster; this includes clean migrations, MFA/auth flows, runtime-role restrictions, health/readiness/outage recovery, worker retry/recovery and migration rollback-preservation checks. `.local/sprint5-test.log` and fresh TRX. |
| EF migration/model check | PASS | Latest migration `20261102012900_AdminMfaSecurity`; no pending model changes. Database fixture uses `Database.MigrateAsync` against newly created test databases. `.local/sprint5-model.log`. |
| Current actual staging DB migration state | **NOT VERIFIED** | There is no Docker Engine or separately provisioned staging database. The passing migrations above are isolated test databases, not a persistent staging service. |
| Runtime DB permissions | PASS (isolated PostgreSQL test) | `DatabaseHardeningTests` and `RuntimePermissionsTests` applied/exercised `scripts/grant-runtime.sql` with a standalone login. It has no superuser/CREATEDB/CREATEROLE/BYPASSRLS, group membership or schema/database ownership; tested rejection of DDL, Admin role assignment, audit deletion and migration-history writes, while application registration/login work. This is not a production grant review. |
| PostgreSQL TLS, actual secret mounts and production host configuration | **NOT VERIFIED** | Production configuration guards and synthetic-certificate behavior are covered by tests; a real TLS PostgreSQL endpoint, real secret files, target host ACLs and certificates were not available or used. |
| Liveness/readiness and DB failure/recovery | PASS (API integration test) | Liveness remains safe; readiness reports bounded `not_ready` during DB outage and recovers after DB connectivity returns. No container health transition was run. |
| MFA login, encryption and Admin authorization | PASS (API + PostgreSQL integration) | Real HTTP login/enrollment/TOTP verification and Admin authorization run against PostgreSQL; encrypted Identity values, replay/lockout/recovery and signed Admin-without-MFA rejection are exercised. Production key-ring encryption is tested with a synthetic certificate. |
| MFA factor usability after actual API process/container restart | **NOT VERIFIED** | No application process/container was restarted after enrollment against a persistent stage database/key volume. Existing tests prove encrypted persistence and startup key protection, not survival of this operational restart scenario. |
| Worker retries/exhaustion and restart recovery | PASS (integration tests); actual process restart **NOT VERIFIED** | Persisted backoff, a new processing scope, safe recovery, exhaustion and reviewed resumption are tested against PostgreSQL. No backend process/container was killed/restarted with staged pending work. |
| PostgreSQL restart and persistent-volume retention | **NOT VERIFIED** | The disposable test cluster ran and stopped; there was no container volume or staging DB restart drill. |
| Backup/restore | Previous baseline PASS; Sprint 5.1 stage drill **NOT VERIFIED** | Sprint 5 verification recorded a logical dump/restore of a synthetic integration fixture. This run did not back up a persistent staging DB or restore/start the backend against a second staging database. No backup file was committed. |
| Structured logs / secret exposure under a staging workload | Partial PASS; container log inspection **NOT VERIFIED** | Safe logging configuration and secret-storage/error regressions are covered. No actual container log stream was produced in this run. Tracked files were inspected without printing candidate values. |
| NuGet vulnerability audit | PASS | `dotnet list GroupBuy.sln package --vulnerable --include-transitive --no-restore`: all six projects reported no vulnerable packages from the current configured sources. |

## Host-side verification script

[`scripts/verify-sprint51-staging.ps1`](../scripts/verify-sprint51-staging.ps1) is prepared for normal Windows PowerShell. It uses the existing production Compose file (`compose.production.yaml`) with a local ignored image-tag override only; it does not create a new service topology. It verifies Docker CLI/Engine/Compose access, validates an untracked ignored staging env file and operator-managed files, checks the Compose configuration, builds/starts only the `api` and `edge` services under a unique `groudeal-sprint51-staging-<run-id>` project and unique API image tag, waits for container health, makes TLS-validated requests to `/health/live` and `/health/ready`, restarts API and edge, then removes only that fresh project's containers/network without `-v`. It emits a sanitized result under `.local/sprint51/staging-verification-result.txt` and returns exit 2 while manual checks remain.

The script deliberately cannot finish the full requested Sprint 5.1 drill. Production Compose has no PostgreSQL service or migration/admin connection, but it can use a separately provisioned synthetic PostgreSQL container on the dedicated bridge described below. The verifier checks that database container's label, health, bridge, and DNS alias, then uses API readiness to establish DB connectivity and no pending migrations. It does not apply migrations, inspect table data, test DB permissions, restart PostgreSQL, or perform backup/restore. It does not read an admin connection string, MFA seed or token, or inspect/print logs. Existing runtime privilege and worker behavior tests are `RuntimePermissionsTests`, `DatabaseHardeningTests`, `OperationsTests`, and `OperationalSecurityTests`; these are regression tests, not substitutes for live restart/restore evidence.

The ignored `.local/sprint51/staging.env.example` and `.local/sprint51/staging.env` are prepared with synthetic identifiers, local absolute paths and an immutable edge image reference. `.local/` is ignored by Git. Secret files and certificates are created only when the operator runs the generator in the procedure below; no secret values are embedded in these env files. Do not copy production secrets, use a production DB, or commit any `.local` file. Run from the repository root only after completing the database bootstrap, migration and certificate setup below:

```powershell
cd "D:\Manoj\Group-Buy\Group-Buying Backend"
.\scripts\verify-sprint51-staging.ps1
```

The script's preflight prints required variable names, never values. Exit 0 means its automated checks pass; exit 1 means an automated prerequisite/runtime check failed; exit 2 means it completed its API/edge checks but Sprint 5.1 manual DB/MFA/worker/restore checks remain.

Manual actions remain mandatory: apply migrations as the separate migration identity, apply/review `scripts/grant-runtime.sql` as the database owner for the staging-only DB, verify DB privileges and schema state, provision safe representative records, and exercise DB outage/restart/volume retention and pg_dump/restore into a second isolated DB. **MANUAL ACTION REQUIRED — ADMIN MFA RESTART VERIFICATION:** use an already enrolled synthetic Admin and the trusted staging client: `POST /api/v1/auth/login`, then submit its challenge and a fresh authenticator code to `POST /api/v1/auth/mfa/verify`. Confirm Admin access. Restart only the `api` service while preserving the same PostgreSQL database and Data Protection key directory, repeat login with a fresh challenge and a new TOTP, and confirm Admin access and enabled MFA. Never print/copy the seed or recovery codes; keep any one-time enrollment response private and do not place response bodies in evidence. For worker recovery, use persisted campaign retry state and the safe worker behavior described in `docs/production-operations-runbook.md`; regression coverage lives in `OperationsTests` and `OperationalSecurityTests`. Capture before/after aggregate state and review restricted logs locally for leakage without pasting values into the result file. The verdict remains NOT READY until these actions have actual recorded evidence.

The full integration run took longer than the Sprint 5 run but completed with all 66 tests passing. The initial unprivileged NuGet audit attempt could not resolve `api.nuget.org`; the exact audit completed successfully on the read-only network-enabled retry above. The current Codex execution context has only .NET SDK 9.0.302 installed while `global.json` requests 10.0.403, so the synthetic asset generator could not be rebuilt in this preparation turn. It was not executed. Install/select the repository SDK on the host before using the commands below.

## Tracked secret candidates

Values are deliberately omitted. The scan reviewed tracked text/config files and manually checked these candidates; no actual credential material was found.

| File | Setting/material | Status |
| --- | --- | --- |
| `.env.production.example` | DB connection, JWT key, DP certificate/password, TLS certificate/private-key file variables | PASS — external paths/placeholders only; no secret value or file committed |
| `compose.production.yaml` | Docker secret file references | PASS — file references only; actual secret files are operator-managed and absent from this verification |
| `compose.yaml` | `POSTGRES_PASSWORD` | PASS — required environment-variable interpolation with no committed value; development-only PostgreSQL service |
| `src/GroupBuy.API/appsettings.json` | JWT issuer/audience, allowed-hosts | PASS — non-secret settings only; no signing key or database password present |
| `src/GroupBuy.API/appsettings.Development.json` | Credentials / MFA | PASS — logging configuration only; no demo password or authenticator seed present |
| `tests/GroupBuy.IntegrationTests/*` | Password and TOTP fixtures | PASS — test-only account material used with isolated `.invalid` identities and generated keys; no live account or credential |
| Tracked `.env*`, certificates, private keys, backups | Secret artifacts | PASS — no such credential-bearing artifacts were identified as tracked |

## Runtime privilege summary

The reviewed SQL template expects a pre-created standalone runtime `LOGIN` role and refuses the current owner, a superuser, database/role creation privilege, `BYPASSRLS`, role memberships, database ownership or schema object ownership. It grants `CONNECT`, schema `USAGE`, table `SELECT` plus limited required DML. Audit rows and EF migration history remain read-only to runtime except audit `INSERT`. RLS denies runtime changes to the fixed Admin role; an account trigger blocks runtime MFA reset or fresh enrollment authorization. Migration and operator identities must remain separate and were not provisioned against any shared database during this verification.

## Findings (P0–P3)

No P0 security/data-loss/customer-safety defect was demonstrated in the available local verification. A P0 conclusion about restart-time key loss cannot be made without the required persistence drill.

| Priority | Finding and evidence | Risk / root cause | Fix / verification |
| --- | --- | --- | --- |
| **P1** | Docker runtime and hardened container deployment are not verified from Codex. Docker CLI selects `desktop-linux`; the pipe exists, but Docker operations are denied and WSL access is denied. User reports normal host PowerShell works. | Host/session pipe ACL or execution-context isolation prevents Codex from controlling the engine. This is a tooling boundary; it is not evidence of an application defect. Container behavior remains unverified. | Do not change Docker permissions. Run `scripts/verify-sprint51-staging.ps1` from normal host PowerShell with synthetic isolated staging configuration; review sanitized evidence. **Status: NOT VERIFIED.** |
| **P1** | MFA usability after API restart, PostgreSQL persistent-volume restart, and stage backup/restore were not executed in this run. | A bad/missing mounted Data Protection key-ring or loss of DB storage could make enrolled MFA unusable or lose commitments. Codex cannot access Docker, and the production Compose intentionally externalizes PostgreSQL. | Use a synthetic Admin, preserve the same DB/key directory, complete TOTP, restart API and verify a fresh login/TOTP; restart staging DB using its provider, verify state, dump/restore into a different isolated DB and verify migration IDs/counts/MFA/campaign state. The host script automates only API/edge checks; manually record database evidence. **Status: NOT VERIFIED.** |
| **P2** | Real production TLS/SCRAM/CA, host ACLs, alerting/on-call, IAM-supervised Admin recovery and legal/business approvals remain external. | Local tests cannot establish cloud ingress, least-privilege infrastructure, key custody, response ownership or marketplace legal readiness. | Complete and record target-platform checks and responsible-owner approvals before pilot. **Status: NOT VERIFIED.** |
| **P2** | Customer self-service password/email recovery remains absent (existing known product limitation). | Account recovery depends on a documented, staffed support procedure. | Assign/approve a secure recovery process or implement an approved delivery/ownership workflow before customer launch. Existing Sprint 5 checklist records this limitation. |

## Git and files

No application, schema, Compose or credential code change was justified by the checks available here. The tracked changes are limited to: `docs/sprint51-staging-verification.md` (new execution evidence and blockers) and `docs/production-readiness-checklist.md` (Sprint 5.1 statuses and verdict). No commit, push or deployment was performed.

## Verdict and next action

**BACKEND NOT READY — BLOCKERS REMAIN** for Sprint 5.1 because the required Docker runtime, API/DB restart, MFA/key persistence and persistent staging backup/restore checks are not verified. The previous Sprint 5 code-level and isolated PostgreSQL tests pass; Compose syntax passes; these do not satisfy the requested staging proof.

Next action: run `scripts/verify-sprint51-staging.ps1` from normal Windows PowerShell on an isolated host staging setup. The existing production Compose uses external PostgreSQL and has no DB service or migration command; the script will not invent those. Separately provision a disposable staging database with distinct migration/admin and runtime identities, apply migrations and the reviewed grants, then perform DB restart, persistence, backup/restore and MFA/worker recovery steps in the provider's supported manner. Review the sanitized result before changing any status. Never connect to production or reuse real production data.

## Host-only synthetic setup (prepared, not executed)

Run each step from a normal Windows PowerShell session on the Docker Desktop host. These commands are instructions only; this Codex run did not invoke Docker or the verifier. Stop on any failed assertion. Use only an isolated, disposable local engine/database. The PostgreSQL container uses the repository's test image family `postgres:17-alpine`; production Compose remains unchanged and connects to this container over a dedicated external bridge.

1. Confirm the repository and ignored local directory, and stop if any exact-name resources already exist. Do not reuse them:

   ```powershell
   Set-Location 'D:\Manoj\Group-Buy\Group-Buying Backend'
   git check-ignore -v .local/sprint51/staging.env
   docker ps -a --filter 'name=^/groudeal-sprint51-postgres$' --format '{{.Names}}'
   docker network ls --filter 'name=^groudeal-sprint51-network$' --format '{{.Name}}'
   docker volume ls --filter 'name=^groudeal-sprint51-postgres-data$' --format '{{.Name}}'
   ```

   Expected: ignore output names `.local/`; each Docker query is empty. If any resource is listed, stop and inspect it manually; never delete/reuse it as part of setup.

2. Use the SDK pinned by `global.json` (10.0.403) and generate random local credentials, PostgreSQL CA/server TLS, edge TLS, a password-protected Data Protection PFX, two connection-string files, and role bootstrap SQL. Files are written beneath `.local/sprint51` and secret values are not printed:

   ```powershell
   dotnet --version
   if ($LASTEXITCODE -ne 0 -or (dotnet --version) -ne '10.0.403') { throw 'Install/select .NET SDK 10.0.403; stop.' }
   dotnet run --project .local/sprint51/GenerateSyntheticAssets.csproj --configuration Release
   if ($LASTEXITCODE -ne 0) { throw 'Synthetic asset generation failed; stop.' }
   ```

   Expected: one message confirming synthetic files were created with values withheld. Stop if any generated path is outside `.local/sprint51`. The generator creates no admin account and does not connect to a database.

3. Create the isolated bridge and named volume. Docker must reject any overlapping subnet. Stop if it does:

   ```powershell
   docker network create --driver bridge --subnet 172.30.0.0/24 groudeal-sprint51-network
   if ($LASTEXITCODE -ne 0) { throw 'Bridge creation failed or subnet conflicts; stop and choose no alternative without reviewing Compose topology.' }
   docker volume create --label groudeal.sprint51.staging=true groudeal-sprint51-postgres-data
   if ($LASTEXITCODE -ne 0) { throw 'Volume creation failed; stop.' }
   ```

   Expected: the exact network and volume names. The API Compose already reserves `172.30.0.10` for its edge proxy; this network is specifically shared with that existing production-like Compose project.

4. Start only the isolated PostgreSQL container. It has a loopback-only host port for EF tooling; the API uses container DNS on the bridge. Its password and TLS private keys are read from ignored files, never placed in command arguments or output:

   ```powershell
   $repo = (Get-Location).Path.Replace('\','/')
   docker run --detach --name groudeal-sprint51-postgres `
     --label groudeal.sprint51.staging=true `
     --network groudeal-sprint51-network --network-alias groudeal-sprint51-postgres `
     --mount 'type=volume,source=groudeal-sprint51-postgres-data,target=/var/lib/postgresql/data' `
     --mount "type=bind,source=$repo/.local/sprint51/secrets,target=/run/sprint51-secrets,readonly" `
     --mount "type=bind,source=$repo/.local/sprint51/certificates,target=/run/sprint51-certs,readonly" `
     --publish 127.0.0.1:55432:5432 `
     --env POSTGRES_USER=groudeal_sprint51_bootstrap `
     --env POSTGRES_DB=postgres `
     --env POSTGRES_PASSWORD_FILE=/run/sprint51-secrets/bootstrap-password.txt `
     --health-cmd 'pg_isready -U groudeal_sprint51_bootstrap -d postgres' `
     --health-interval 5s --health-timeout 3s --health-retries 20 `
     postgres:17-alpine `
     sh -c 'cp /run/sprint51-certs/postgres-server.pem /tmp/postgres-server.pem && cp /run/sprint51-certs/postgres-server-key.pem /tmp/postgres-server-key.pem && chmod 600 /tmp/postgres-server-key.pem && exec /usr/local/bin/docker-entrypoint.sh postgres -c ssl=on -c ssl_cert_file=/tmp/postgres-server.pem -c ssl_key_file=/tmp/postgres-server-key.pem'
   if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL container failed to start; stop.' }
   ```

   Expected: a container ID. Wait for health (bounded to two minutes):

   ```powershell
   $until = (Get-Date).AddMinutes(2)
   do { $state = docker inspect --format '{{.State.Status}}|{{if .State.Health}}{{.State.Health.Status}}{{end}}' groudeal-sprint51-postgres; if ($LASTEXITCODE -ne 0) { throw 'Container inspect failed; stop.' }; if ($state -eq 'running|healthy') { break }; Start-Sleep -Seconds 3 } while ((Get-Date) -lt $until)
   if ($state -ne 'running|healthy') { throw 'PostgreSQL did not become healthy; stop and inspect privately.' }
   ```

   Expected: `running|healthy`. Do not print container environment or inspect secrets.

5. Create the two non-superuser login roles and dedicated database using the synthetic bootstrap SQL. The generated SQL contains random passwords and stays in the read-only secret mount:

   ```powershell
   docker exec groudeal-sprint51-postgres psql -X -U groudeal_sprint51_bootstrap -d postgres -v ON_ERROR_STOP=1 -f /run/sprint51-secrets/create-staging-roles.sql
   if ($LASTEXITCODE -ne 0) { throw 'Role/database bootstrap failed; stop.' }
   ```

   Expected: two `CREATE ROLE` and one `CREATE DATABASE`. The migration role owns only `groupbuy_sprint51`; the runtime role is a standalone `LOGIN` with `NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS` and owns no database/schema objects.

6. Apply the repository's actual EF migrations using the migration connection, not the runtime connection. The infrastructure project is the migrations/startup project per `docs/local-development.md` and `DesignTimeFactory`:

   ```powershell
   dotnet tool restore
   if ($LASTEXITCODE -ne 0) { throw 'Local tool restore failed; stop.' }
   $env:ConnectionStrings__Database = (Get-Content .local/sprint51/secrets/migration-database-connection.txt -Raw).Trim()
   try {
     dotnet ef database update --project src/GroupBuy.Infrastructure
     if ($LASTEXITCODE -ne 0) { throw 'EF migration failed; stop before starting the API.' }
   } finally { Remove-Item Env:ConnectionStrings__Database -ErrorAction SilentlyContinue }
   ```

   Expected: EF applies the checked-in migrations through `20261102012900_AdminMfaSecurity`. This creates no migration and manually changes no schema.

7. Apply the reviewed runtime grants as the database owner/migration identity. This uses a local container socket; the connection used by the API remains TLS `VerifyFull` with SCRAM credentials:

   ```powershell
   Get-Content scripts/grant-runtime.sql -Raw | docker exec -i groudeal-sprint51-postgres psql -X -U groudeal_sprint51_migrator -d groupbuy_sprint51 -v app_role=groudeal_sprint51_runtime -v ON_ERROR_STOP=1
   if ($LASTEXITCODE -ne 0) { throw 'Runtime grants failed; stop.' }
   ```

   Expected: `COMMIT`. Verify the role attributes and owners using safe catalog queries before proceeding. Check attempts to create a table/database or assign Admin fail with permission denied; check runtime registration/login still works later through the API. Do not grant runtime schema ownership or elevated attributes.

8. Verify the DB connection files without displaying their contents, and then start the API/edge verifier only when ready. The database CA is mounted as the Compose secret `/run/secrets/postgres-ca.crt`; the runtime connection uses the container DNS name and `Ssl Mode=VerifyFull`. The host migration connection uses `localhost:55432`, the same CA file, and a separate role. The generated edge cert SAN matches `sprint51.localhost`, and the script passes it to curl as a trust anchor.

   ```powershell
   foreach ($p in @('.local/sprint51/secrets/api-database-connection.txt','.local/sprint51/secrets/migration-database-connection.txt','.local/sprint51/secrets/jwt-signing-key.txt','.local/sprint51/secrets/data-protection.pfx','.local/sprint51/secrets/data-protection-password.txt','.local/sprint51/certificates/edge-chain.pem','.local/sprint51/certificates/edge-private-key.pem','.local/sprint51/certificates/postgres-ca.pem')) { if (-not (Test-Path $p -PathType Leaf)) { throw "Missing synthetic input: $p" } }
   .\scripts\verify-sprint51-staging.ps1
   $verifyExit = $LASTEXITCODE
   if ($verifyExit -eq 1) { throw 'Automated verification failed; inspect sanitized evidence and stop.' }
   if ($verifyExit -eq 2) { 'Automated API/edge checks passed; manual checks remain.' }
   ```

   The verifier starts only the API and edge Compose services, observes the separately labeled DB container, tests TLS liveness/readiness, restarts API/edge, and cleans only its newly generated Compose project (no volume deletion). Exit 2 is expected because manual checks remain. This script is intentionally NOT run by Codex for this task.

9. Admin MFA persistence. Register a synthetic account at `/api/v1/auth/register` using an address under `example.invalid` and a unique password (at least 12 chars, uppercase/lowercase/digit/symbol). Keep password, challenges, TOTP seed and recovery codes in the operator's private session; never paste them into evidence. The normal account is initially a Customer. Use `docs/admin-mfa-provisioning.md`: build the API, then run the one-shot CLI `--admin-operation provision --user-id <registered-uuid> --operator-id sprint51-local-operator --reference sprint51-admin-provision-001` under Production configuration with the migration connection, generated signing key, same DP certificate/password and same persistent `DP_KEYS_PATH`; set `AdminProvisioning__Enabled=true` only for that one CLI invocation and unset it immediately. The exact configuration is `ASPNETCORE_ENVIRONMENT=Production`, `ConnectionStrings__Database` from `migration-database-connection.txt`, `Jwt__SigningKey` from `jwt-signing-key.txt`, `Jwt__Issuer=Groudeal.Sprint51.Local`, `Jwt__Audience=Groudeal.Sprint51.SyntheticClient`, `Jwt__ExpiryMinutes=15`, `AllowedHosts=sprint51.localhost`, `DataProtection__KeyPath` equal to `DP_KEYS_PATH`, `DataProtection__CertificatePath` equal to `DP_CERTIFICATE_FILE`, and `DataProtection__CertificatePassword` from `DP_CERTIFICATE_PASSWORD_FILE`. Supply them as process environment variables, never command-line values; remove them in a `finally` block immediately after the command. This trusted CLI grants the fixed Admin role to the existing active identity and opens the 15-minute enrollment window; it does not create an account. Login at `POST /api/v1/auth/login`, request enrollment with the returned challenge at `POST /api/v1/auth/mfa/enrollment`, manually add the returned TOTP shared key to an authenticator, and verify a fresh six-digit code at `POST /api/v1/auth/mfa/verify`. Confirm the response indicates MFA success and Admin authorization works. Keep recovery codes private and store them through the approved local secret manager. Restart only the API using the exact Compose project name from `.local/sprint51/staging-verification-result.txt` (`docker compose -p <that-name> -f compose.production.yaml -f .local/sprint51/compose.image.yaml --env-file .local/sprint51/staging.env restart api`), then repeat login and verify with a fresh TOTP. Expected: MFA remains enabled and Admin authorization succeeds. The same synthetic DB, DP key directory, PFX and password must persist across the restart.

10. Worker recovery. There is no public API endpoint to inject a worker failure or force retry. Do not edit DB rows or install failure triggers in staging. The actual `ExpiryWorker` runs `ICampaignExpiryProcessor.Process` every 15 seconds in a fresh scope; due campaign work is selected from persisted state, retry count/backoff is persisted, and the batch is bounded. Runtime proof must use an ordinary synthetic campaign/reservation scenario that naturally reaches an expiry/confirmation deadline, record only its campaign ID/status and aggregate counts, restart the API while work is pending, and observe the normal worker process it once (check state/audit/notification idempotency without customer PII). A deliberately poisoned campaign/failure cannot safely be produced through public APIs, so failure/backoff/recovery proof remains the real PostgreSQL integration tests `OperationsTests.Worker_isolates_poison_campaign_persists_backoff_and_recovers_in_new_scope` and `OperationsTests.Worker_cancellation_does_not_change_campaign_or_generate_notifications`; they inject failure in a disposable test DB, not staging. Never claim live poison recovery from the API restart scenario.

11. PostgreSQL restart and backup/restore. Before the restart, record schema migration IDs and aggregate row counts without selecting identifying columns. Restart the exact named DB container and require healthy state plus unchanged counts. Dump to the container's temporary filesystem, copy into ignored local storage, create a distinct restore DB (never overwrite the original), restore as migration owner, reapply `grant-runtime.sql` for the restore DB, and compare migration IDs/aggregate counts:

   ```powershell
   docker exec groudeal-sprint51-postgres pg_dump -U groudeal_sprint51_bootstrap -d groupbuy_sprint51 -Fc -f /tmp/groudeal-sprint51.dump
   if ($LASTEXITCODE -ne 0) { throw 'pg_dump failed; stop.' }
   docker cp groudeal-sprint51-postgres:/tmp/groudeal-sprint51.dump .local/sprint51/groudeal-sprint51.dump
   docker exec groudeal-sprint51-postgres createdb -U groudeal_sprint51_bootstrap -O groudeal_sprint51_migrator groupbuy_sprint51_restore
   if ($LASTEXITCODE -ne 0) { throw 'Restore DB creation failed; do not overwrite original.' }
   docker cp .local/sprint51/groudeal-sprint51.dump groudeal-sprint51-postgres:/tmp/groudeal-sprint51.dump
   docker exec groudeal-sprint51-postgres pg_restore -U groudeal_sprint51_bootstrap --no-owner --role=groudeal_sprint51_migrator -d groupbuy_sprint51_restore /tmp/groudeal-sprint51.dump
   if ($LASTEXITCODE -ne 0) { throw 'pg_restore failed; keep original DB intact.' }
   Get-Content scripts/grant-runtime.sql -Raw | docker exec -i groudeal-sprint51-postgres psql -X -U groudeal_sprint51_migrator -d groupbuy_sprint51_restore -v app_role=groudeal_sprint51_runtime -v ON_ERROR_STOP=1
   if ($LASTEXITCODE -ne 0) { throw 'Restore grants failed.' }
   ```

   Expected: successful dump/restore and matching aggregate counts/schema history. The dump contains synthetic identity/business data; keep it only under ignored `.local/sprint51`, then remove it securely when done. Cleanup only after saving sanitized evidence: stop/remove the exact named DB container, remove the named volume only after checking its staging label and accepting permanent data loss, then remove the exact network only after it has no endpoints. Never use `docker system prune`, wildcard removal, or Compose `down -v`.

12. Final requested command (run only after all prior steps and explicit host-side readiness):

   ```powershell
   .\scripts\verify-sprint51-staging.ps1
   ```

   This report does not certify its output. Record the exit code and sanitized `.local/sprint51/staging-verification-result.txt`; leave the verdict **NOT VERIFIED** until all manual DB, MFA, worker and restore checks have actual evidence.

### Prepared input format and verification limits

`SPRINT51_STAGING_CONFIRMATION` must equal the exact sentinel `ISOLATED_SYNTHETIC_DATABASE`. `STAGING_DB_HOST` and `STAGING_DB_NAME` must match the connection-file Host/Database and visibly contain `sprint51` or `staging`; the prepared values are `groudeal-sprint51-postgres` and `groupbuy_sprint51`. `STAGING_DB_CONTAINER` and `STAGING_DOCKER_NETWORK` are fixed to `groudeal-sprint51-postgres` and `groudeal-sprint51-network`. `STAGING_HEALTH_HOST` is `sprint51.localhost`, included in explicit `API_ALLOWED_HOSTS`. JWT issuer/audience are nonsecret local identifiers. `DATABASE_CONNECTION_FILE` is the runtime Npgsql connection with `Ssl Mode=VerifyFull`, `Include Error Detail=false` and container CA path; `JWT_SIGNING_KEY_FILE` is 48 random bytes encoded as hex; DP certificate/password files form a private-key PFX; `DP_KEYS_PATH` is the persistent ignored key-ring directory; edge certificate/key are PEM files with the expected DNS SAN; `POSTGRES_CA_FILE` is the synthetic CA PEM; `EDGE_IMAGE` is an immutable nginx reference. Paths are fully qualified and dotenv-quoted to support spaces.

Confirmed: isolated PostgreSQL is feasible without altering production Compose, by attaching both the independently started DB and the existing API stack to the exact bridge subnet. The exact migration command is `dotnet ef database update --project src/GroupBuy.Infrastructure` after setting `ConnectionStrings__Database` to the generated migration connection. Runtime grants come from `scripts/grant-runtime.sql`, applied by the DB owner after migrations to a standalone least-privilege runtime login. The generator/commands, migration, MFA restart, worker restart, dump/restore, and Docker execution remain unverified until the user runs them in normal host PowerShell. Codex did not invoke Docker, start/stop containers, run this verifier, or alter application code.

## Recovery verification addendum — 2026-11-03

This addendum records the current Codex execution and supersedes any earlier expectation that the checked-in local staging configuration is safe to use against its configured database.

- The local staging configuration identifies `groupbuy_sprint51` as both the staging database and the runtime database. The API registers `ExpiryWorker` at startup; it periodically processes campaign expiry work and can write campaign state. The staging verifier starts the API. Therefore the verifier was **not run** against this configuration.
- `scripts/verify-sprint51-staging.ps1` now fails closed when `STAGING_DB_NAME` is `groupbuy_sprint51`, before any Compose/API startup. The verifier still targets the fixed `groudeal-sprint51-postgres` container and `groudeal-sprint51-network`; it is not ready to verify a separate PostgreSQL instance until that target can be configured safely.
- Docker CLI access from this Codex execution context was denied. No Docker inspect, start, stop, restart, Compose, dump, restore, migration, or cleanup action was performed. Container state, PostgreSQL version/readiness, restart policy, volume attachment, migrations, schema, roles/grants, and live API connectivity are **NOT VERIFIED**.
- `.NET SDK 10.0.403` full solution build passed with `--no-restore`, isolated intermediate/output directories, 0 warnings and 0 errors. `dotnet restore` and EF project metadata/model comparison were blocked by unreachable NuGet vulnerability-feed metadata (`NU1900`, treated as an error).
- Unit tests and the isolated `TestDatabaseSafetyTests` run both aborted before executing tests: VSTest could not connect to its testhost after 90 seconds (TRX total 0). The full PostgreSQL integration suite was also attempted with `GROUPBUY_TEST_POSTGRES` unset and a 20-second connection bound; it aborted before executing tests because VSTest could not connect to its testhost (TRX total 0). These suites are **BLOCKED**, not passed. The PowerShell synthetic staging diagnostics/sanitization test passed.
- Windows PowerShell parser reported zero errors for the staging verifier and its synthetic diagnostics test; both files were ASCII-only. `git diff --check` passed.
- No backup or restore was created or tested. No live application/security workflow, account/MFA persistence, worker restart, or recovered-database integrity check was performed.

**Current verdict: NO-GO / NOT VERIFIED.** Do not run the verifier against the recovered database. First provision a genuinely separate disposable PostgreSQL container, volume, database and network, then update the verifier’s fixed resource checks to target only those disposable resources. Run database migrations and runtime checks only against that disposable target. Re-run restore/tests when NuGet feed access and the local testhost issue are resolved. Preserve the recovered database and its volume.

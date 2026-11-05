# Groudeal comprehensive backend audit — 2026-11-03

## Executive summary

**Result: NO-GO for a real-customer pilot or production deployment.** The local backend builds, its 37 unit tests and 72 PostgreSQL integration tests pass, and the feed-backed NuGet audit reports no vulnerable packages. Two confirmed database ownership invariants are now enforced in PostgreSQL with additive migrations and regression coverage. Runtime-role test credentials, test asset paths, and a production-readiness assertion were also corrected.

The production/recovered database, staging verifier, production Compose services, persistent key/database recovery, and target-host controls were intentionally not accessed. Their schema, grants, TLS, backups, recovery, key ACLs, alerting, and actual deployment behavior remain unverified. Passing disposable tests is not production readiness.

## Scope and safety

Reviewed the repository projects and dependency graph, API startup and exception handling, Identity/JWT/MFA, service authorization, runtime grant template, EF model/snapshot/migrations, marketplace transactions and worker paths, configuration and Compose templates, tests, test fixture isolation, and existing security/readiness documentation. No external frontend repository was changed.

`GROUPBUY_TEST_POSTGRES` was unset for the completed test runs. The integration fixture started fresh Testcontainers PostgreSQL and applied the migrations to its disposable database. The test suite removed its containers at completion. The fixture's guard still rejects the protected Sprint 5.1 database host/name/loopback endpoint. No connection, migration, or write targeted `groupbuy_sprint51` or `groudeal-sprint51-postgres`; no staging verifier, production Compose run, deploy, commit, or push was performed.

## Findings and disposition

The 11 findings below use the required status labels. “Severity” describes the underlying defect or remaining release risk, not the amount of code changed.

| ID | Severity | Finding, impact, and correction | Disposition |
| --- | --- | --- | --- |
| F-01 | Medium | **Runtime-role test could not authenticate.** `RuntimePermissionsTests` and `DatabaseHardeningTests` created SCRAM login roles without a usable password, while the connection string set an empty password. Registration/login failed before the least-privilege assertions ran. `DatabaseFixture` now generates random test-only credentials, creates roles with PostgreSQL identifier/literal quoting through parameters, and builds connection strings with `NpgsqlConnectionStringBuilder`; pooling is disabled for runtime-role clients. No permissions were broadened. | **FIXED AND VERIFIED** — both runtime-role integration tests pass; the regression checks password handling and pooling. |
| F-02 | Medium | **Integration assets depended on a fragile output-directory depth.** Running tests from the isolated build output resolved the grant template and temporary production-guard directory incorrectly. The fixture now locates the repository root from `GroupBuy.sln`; temporary test state is under `.local` and removed in `finally`. | **FIXED AND VERIFIED** — full integration suite passes; paths resolve from the isolated output. |
| F-03 | High | **Database did not enforce seller ownership across related product data.** A direct SQL write could associate a campaign or inventory row with another seller's valid variant even though service code checks ownership. `ProductVariant.SellerId` is backfilled from its parent product; composite keys and FKs now bind product-to-variant, campaign-to-variant, and inventory-to-variant seller IDs. Migration and `docs/schema.sql` are additive and preserve existing rows; legacy mismatches fail migration rather than being silently reassigned. | **FIXED AND VERIFIED** — PostgreSQL regression attempts cross-seller campaign and inventory references and receives FK violations; fresh database migration and migration upgrade/rollback test pass. |
| F-04 | Medium | **Production configuration test did not prove the worker/readiness behavior against a database TLS failure.** The test now waits with a bounded timeout for the background worker to report the expected `NpgsqlException`, asserts readiness is 503 while liveness is 200, restores environment variables, and removes its exact synthetic key directory. The PostgreSQL fixture is deliberately not TLS-enabled, so this is a fail-closed test, not evidence of successful production TLS. | **FIXED AND VERIFIED** — test passes within the integration suite. |
| F-05 | Medium | **Request exception diagnostics previously exposed only the outer exception type, obscuring database failures.** `Errors` now records a bounded inner-exception chain with PostgreSQL SQLSTATE and schema/table/column/constraint/data-type/routine fields when present. Known credential, token, connection, URI, and private-key patterns are redacted; HTTP 500 responses remain generic. | **FIXED BUT NOT VERIFIED** — code path was statically inspected and the prior focused failure was diagnosed; this run did not include a dedicated automated assertion over diagnostic redaction for arbitrary exception messages. Add such a test before treating redaction as exhaustively verified. |
| F-06 | Low | `ExpiryWorker` logged `NpgsqlException` during `Production_requires_encrypted_key_storage_verified_database_TLS_and_redirects_API_HTTP`. The test configures `VerifyFull` against the disposable PostgreSQL instance, which does not serve TLS. The worker records database failure and readiness returns 503; liveness remains available. | **ACCEPTED / EXPECTED BEHAVIOR** — failure is intentionally induced and asserted; no database error is suppressed. |
| F-07 | Low | Data Protection logged `CryptographicException` during `Host_fails_closed_with_a_safe_error_when_factor_key_protection_is_unavailable`. The test injects an unavailable protector and verifies startup fails with a safe public exception and no inner exception. | **ACCEPTED / EXPECTED BEHAVIOR** — negative startup test validates fail-closed behavior; the synthetic sensitive message is not returned. |
| F-08 | Low | Campaign processing logged `DbUpdateException` in poison/retry/exhaustion tests. Test triggers reject the expected audit write so worker retry persistence, bounded backoff, isolation, exhaustion, and reviewed recovery can be verified. Logs contain campaign ID and exception type, not SQL/exception text. | **ACCEPTED / EXPECTED BEHAVIOR** — injected failure and recovery assertions pass. |
| F-09 | High | **Recovered database and target-host controls are not certified by this audit.** Its current migration history, live runtime grants, production TLS chain, backup/PITR, restore and key-restoration procedure, ACLs, operator IAM, monitoring, and real process/volume restart state were not inspected or exercised. | **BLOCKED — EXTERNAL ACTION REQUIRED** — release/security/data owners must authorize and complete a backup-backed review and restore drill on the intended environment. Do not apply these migrations to the recovered DB without that review and a separate approved change. |
| F-10 | High | **Production Compose and staging TLS/edge runtime remain unverified.** Testcontainers access to Docker was available for disposable PostgreSQL tests, but the staging verifier and production Compose stack were not run. Thus production image/edge health, ingress headers/TLS, persistent volumes, and actual service startup are not claimed. | **BLOCKED — EXTERNAL ACTION REQUIRED** — run the approved staging verification from the host against a separately reviewed staging environment, then record image, edge, health, TLS, and restart evidence. |
| F-11 | Medium | **Customer recovery/support and certain operational governance gates remain outside the backend implementation.** Self-service email verification/password recovery, approved retention/archive ownership, seller/legal review, and legacy stock/commitment cutover require product/provider and data-owner decisions. No fake recovery provider or automated historical backfill was introduced. | **OPEN — REMEDIATION REQUIRED** — define accountable workflows and owners before public pilot. |

### Finding counts

- Issues recorded: **11**
- Fixed and verified: **4**
- Fixed but not verified: **1**
- Accepted / expected behavior: **3**
- Blocked — external action required: **2**
- Open — remediation required: **1**

## Audit notes by area

- **Authentication and authorization:** Reviewed JWT issuer, audience, signing algorithm, expiry, role/version revocation, suspended-account handling, MFA enrollment/challenge/recovery/reset, and seller/admin ownership. Existing integration coverage rejects wrong issuer/audience/signature/algorithm, stale roles, suspended accounts, and Admin access without MFA. No authentication or role bypass was found in this pass.
- **Runtime database role:** `scripts/grant-runtime.sql` remains least privilege. Tests verify registration/login under the restricted role, no DDL/migration permission, no audit update/delete, no Admin assignment, and no MFA reset escalation. No grant was added by this audit.
- **Transactions and concurrency:** Reviewed capacity reservation/cancellation, campaign confirmation, seller approval locks, inventory allocation, worker retries, pickup collection, and idempotency paths. Integration tests cover concurrent final reservations, cancellation/confirmation races, inventory allocation, pickup collection, restart-scoped worker recovery, and bounded exhaustion.
- **Schema and migrations:** New migrations are `20261103144032_EnforceVariantSellerConstraint` and `20261103154949_EnforceInventoryVariantSellerOwnership`. The first adds `product_variants.SellerId`, backfills from `products`, makes it required, and adds composite ownership FKs. The second replaces the inventory-to-variant FK with a composite seller/variant FK. `docs/schema.sql` was regenerated from all five migrations. `has-pending-model-changes` reported no changes after building with the latest migrations. Fresh migration and the existing isolated upgrade/empty-security-rollback regression passed. These results do not certify a populated recovered schema.
- **Workers and shutdown:** `ExpiryWorker` uses scoped services, honors host cancellation, reports readiness failure after a batch error, and continues on the next interval. Marketplace processing isolates each campaign and persists bounded retry state. Three logged errors described above are expected test stimuli, not unexplained production exceptions.
- **Errors and logs:** HTTP errors remain generic and traceable; worker errors emit type/status metadata rather than raw SQL, tokens, or connection strings. The exception-handler diagnostic enhancement is redaction-reviewed but lacks an exhaustive automated arbitrary-message redaction test (F-05).
- **Configuration and dependencies:** Repository pins .NET SDK 10.0.403; projects target .NET 10 and treat warnings as errors. No package upgrades were needed. The earlier vulnerability-feed problem was isolated to restricted-sandbox DNS: normal NuGet restore with audit enabled succeeded from the host network, and a subsequent transitive vulnerability audit used `https://api.nuget.org/v3/index.json` and reported no vulnerable packages in all six projects. No audit-disable setting or warning suppression was added.
- **Tests and test isolation:** `DatabaseFixture` rejects the protected Sprint 5.1 endpoint and creates random databases on disposable Testcontainers PostgreSQL when the environment override is unset. It drops only its generated test database and container. Test-generated private material is synthetic and scoped to `.local`; no real credentials were added.
- **Compose and deployment:** No Compose runtime or staging verifier was executed. Production readiness, actual TLS, Docker image health, deployment secrets, volume persistence, and recovery must be established outside this local test run.

## Files changed in this audit

- `src/GroupBuy.Domain/Models.cs` — add variant seller ownership key.
- `src/GroupBuy.Infrastructure/Database.cs` — model composite ownership keys/FKs for product variants, campaigns, and inventory.
- `src/GroupBuy.Infrastructure/Marketplace.cs`, `DemoSeed.cs` — populate variant seller IDs in normal and development-seed creation paths.
- `src/GroupBuy.Infrastructure/Migrations/20261103144032_EnforceVariantSellerConstraint.cs` and `.Designer.cs` — backfill seller ID, add composite keys/FKs, and update snapshot.
- `src/GroupBuy.Infrastructure/Migrations/20261103154949_EnforceInventoryVariantSellerOwnership.cs` and `.Designer.cs` — enforce inventory variant seller ownership.
- `src/GroupBuy.Infrastructure/Migrations/MarketplaceDbModelSnapshot.cs`, `docs/schema.sql`, `docs/database-design.md` — current model, generated migration SQL, and relationship documentation.
- `tests/GroupBuy.IntegrationTests/BackendTests.cs` — cross-seller database constraint regression and safe fixture credential/path helpers.
- `tests/GroupBuy.IntegrationTests/DatabaseHardeningTests.cs`, `RuntimePermissionsTests.cs` — test-only runtime credentials and repository-root asset lookup.
- `tests/GroupBuy.IntegrationTests/MigrationSecurityTests.cs` — expect five applied migrations in upgrade/rollback verification.
- `tests/GroupBuy.IntegrationTests/ProductionSecurityTests.cs` — bounded worker/readiness assertion and exact synthetic artifact cleanup.
- `docs/comprehensive-backend-audit-2026-11-03.md` — this report.
- `docs/production-readiness-checklist.md` — appended current verification status; previous entries remain as dated history.

Pre-existing working-tree changes were preserved, including API diagnostics and package metadata, Sprint 5.1 docs/scripts, and test-safety files. No existing changes were reset or overwritten.

## Verification record

`GROUPBUY_TEST_POSTGRES` was confirmed unset before test execution.

| Check | Command / evidence | Result |
| --- | --- | --- |
| Restore | `dotnet restore GroupBuy.sln --verbosity minimal -p:BaseIntermediateOutputPath=obj/sprint5/` (normal vulnerability auditing enabled) | Pass, exit code 0; all six projects restored. |
| Build | `dotnet build GroupBuy.sln --no-restore --configuration Debug -m:1 -p:UseSharedCompilation=false -p:BaseIntermediateOutputPath=obj/sprint5/ -p:BaseOutputPath=bin/audit-20261103/` | Pass, exit code 0; 0 warnings, 0 errors. |
| Domain/application unit tests | `dotnet test tests/GroupBuy.UnitTests/GroupBuy.UnitTests.csproj --no-build --no-restore --configuration Debug -m:1 -p:BaseIntermediateOutputPath=obj/sprint5/ -p:BaseOutputPath=bin/audit-20261103/` | **37 passed, 0 failed, 0 skipped; exit code 0.** |
| PostgreSQL integrations | `dotnet test tests/GroupBuy.IntegrationTests/GroupBuy.IntegrationTests.csproj --no-build --no-restore --configuration Debug -m:1 -p:BaseIntermediateOutputPath=obj/sprint5/ -p:BaseOutputPath=bin/audit-20261103/ --blame-hang-timeout 12m` | **72 passed, 0 failed, 0 skipped; exit code 0.** Testcontainers disposable PostgreSQL; total ~8m. No separate Infrastructure test project exists; infrastructure behavior is exercised in these integration tests. |
| Package vulnerability audit | `dotnet list GroupBuy.sln package --vulnerable --include-transitive --no-restore` | Pass, exit code 0: no vulnerable packages reported for Domain, Application, Infrastructure, API, UnitTests, or IntegrationTests. |
| Migration script/model | `dotnet ef migrations script --project src/GroupBuy.Infrastructure --output docs/schema.sql --no-build --configuration Debug`; `dotnet ef migrations has-pending-model-changes --project src/GroupBuy.Infrastructure --no-build --configuration Debug` | Pass; latest script includes both ownership migrations; no pending model changes. |
| Formatting | `dotnet format whitespace GroupBuy.sln --verify-no-changes --no-restore --verbosity minimal --include` on changed files already following repository whitespace style | Pass for included files. The existing compact `Database.cs`, `DemoSeed.cs`, and `Marketplace.cs` produce whole-file formatter suggestions; they were excluded to avoid unrelated formatting churn. |
| Diff whitespace | `git diff --check` | Pass. |
| Secrets | Reviewed diffs and generated test configuration; no real credentials or production connection data added. Pattern matches were limited to a configuration lookup, the `NpgsqlConnectionStringBuilder.Password` assignment, and a generated random synthetic test password; matched values were not printed. | Pass by review; no credential values are included in this report. |
| Production/recovered resources | No staging verifier, production Compose stack, recovered database, or persistent volume was used. | Not run by design; see F-09/F-10. |

The suite emits `NpgsqlException`, `CryptographicException`, and `DbUpdateException` log records for the intentional tests summarized in F-06–F-08. These records contain type/status metadata and synthetic campaign identifiers; no secret values or raw connection strings were printed.

## Required remaining actions

1. Keep the deployment **NO-GO** until the intended release owner reviews the recovered/staging database schema, least-privilege grants, TLS chain, and forward migration plan.
2. Perform and document a backup-backed restore/PITR and Data Protection key restore/rotation drill on an approved non-production target.
3. Run the separate staging verifier against the explicitly approved staging endpoint; verify production Compose edge/API health, TLS, persistence, and restart behavior there.
4. Add a dedicated redaction regression for exception messages that deliberately contain synthetic credentials (F-05).
5. Assign owners and acceptance criteria for customer recovery/support, data retention, seller/legal review, and legacy inventory/commitment cutover (F-11).

No migration was applied to the recovered database, no staging verifier was run, and no deployment, commit, or push occurred.

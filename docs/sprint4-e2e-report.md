# Sprint 4 marketplace E2E and release verification

Date: 2026-11-01. Backend: D:/Manoj/Group-Buy/Group-Buying Backend. Frontend: D:/Manoj/Group-Buy/Group-Buying Frontend. No push, deployment, legacy backfill or production mutation was performed.

## Scope and inspection

Inspected both README files, API/security contracts, Sprint 1 audit and Sprint 2/2.1/Frontend Sprint 3 reports, both EF migrations/model mappings, domain states, authentication, ownership guards, reservation/stock/pickup transactions, retry worker, Compose/startup/seed configuration and customer/seller/admin/PWA test sources. Existing features and test scenarios were preserved. No application architecture, DTO, business rule, schema or package version changed in Sprint 4. Production startup was also exercised with an unreachable dummy database configuration: --seed-demo and a short JWT key were both rejected before any database work. Results are retained in .local/sprint4-startup-guards.json.

## Continuous journey

New frontend integration/zzzz-marketplace.spec.ts uses real loopback Kestrel HTTP, the actual API and a fresh PostgreSQL cluster. No route mocking is used. A newly registered Customer onboards as seller; a distinct newly registered Customer reserves. Only the explicitly Development-seeded fictional Admin is reused. Public role injection is never used.

| Step | Evidence |
| --- | --- |
| Customer registration/login | Two synthetic accounts registered/login through actual auth API |
| Seller onboarding | New Grocery profile/contact/location submitted |
| Seller approval | Unapproved publication denied, Admin approves, seller logs in again for newly granted role |
| Product/inventory | New Pack variant and 20 units declared via versioned inventory API |
| Campaign commitment | Target 2 draft submitted; inventory committed quantity verified as 2 |
| Campaign approval | Admin review opens campaign under server state/stock checks |
| Customer discovery/search | Actual browser catalog search finds the unique group; opens its detail |
| Reservation/progress | First quantity-1 reservation, same-key retry returns same ID, quantity 1 and progress 50% verified |
| Target completion | Second reservation moves campaign to AwaitingSellerConfirmation |
| Seller confirmation | Actual confirmation POST races cancellation; cancellation conflicts, confirmation succeeds |
| Pickup preparation | Both reservation pickups marked Ready through seller API |
| Pickup-code access | Customer explicitly requests high-entropy codes through owner API |
| Seller verification | Invalid code rejected; valid collection succeeds; code replay conflicts for both pickups |
| Final completion | Campaign Completed, both reservations Completed, on-hand 18, fulfilled 2, committed 0 |
| Admin audit | CampaignApproved, InventoryConfirmed and CampaignCompleted events visible in restricted real audit API; Customer audit access denied |

This primary journey combines API automation with browser discovery; it does not claim every one of its steps uses browser clicks. The preserved three real browser workflows separately exercise customer registration/reservation/cancellation/code access, seller product/inventory/campaign/preparation/collection and Admin seller/campaign decisions/audit UI.

## Negative, integrity and recovery coverage

| Requested scenario | Existing/new regression evidence |
| --- | --- |
| Unapproved seller publication | Setup helpers and continuous journey deny before approval |
| Cross-seller inventory | Seller_dashboard_inventory_and_reservations_are_owner_scoped |
| Cross-customer reservation | Same owner-scoping case and cancellation tests return 404 |
| Non-Admin / audit denial | AdministrationTests covers every new read route, protected health and application-port authorization |
| Concurrent distinct customers / capacity | New MarketplaceJourneyTests: 12 requests, two authenticated customers, capacity 6, exactly 6 accepts / 6 conflicts; persisted totals/pickups checked |
| Duplicate reservation | Idempotency_cancellation_and_ownership and primary journey same-key retry |
| Cancellation vs confirmation | New case races actual Decide endpoint after target closes cancellation; existing final-reservation race remains |
| Expiry / seller deadline | Expiry_is_idempotent; Confirmation_expiry_survives_new_processor_scopes_and_cannot_be_confirmed_late |
| Invalid/reused/expired codes | OperationsTests cover persisted failures/lockout, replay, parallel collect and expiry |
| Unrelated seller pickup | Pickup_codes_and_notifications_cannot_be_accessed_by_other_sellers_or_customers and direct detail tests |
| Invalid transitions | Domain tests plus confirmed/terminal transition HTTP regressions |
| Expired JWT / removed roles | Jwt_rejects_expiry_wrong_issuer_audience_signature_and_algorithm; Removed_roles_and_suspended_accounts_reject_existing_tokens |
| Persistent retry / restart | Trigger-injected audit failure rolls back campaign/notifications, records backoff, processes healthy candidate, new API host resumes exactly once |
| Worker cancellation | Worker_cancellation_does_not_change_campaign_or_generate_notifications |
| Notifications / transaction | Unique delivery key, repeated decisions, rollback and read-owner tests |
| Restricted DB runtime identity | New RuntimePermissionsTests register/login using a non-superuser login; DDL and audit DELETE denied. Test fixture only; not proof of production grants/all workflows |

New scripts/sprint4-integrity.sql checks campaign bounds, snapshot arithmetic, collected-code clearing, duplicate notification keys and selected table/migration counts. scripts/sprint4-query-plans.sql captures ANALYZE/EXPLAIN (ANALYZE, BUFFERS) for representative listing/detail/reservation queries on the disposable fixture. Plans are representative SQL, not a full EF query-count profile. Full fixture dump/restore compares the same invariant/count output after stopping API writes. It is not a full-table cryptographic comparison, PITR or production RPO/RTO certification.

## Executed results

| Check | Final result |
| --- | --- |
| Backend solution build | Passed, 0 warnings / 0 errors |
| Backend unit tests | 37 passed, 0 failed/skipped |
| Real PostgreSQL HTTP/integration tests | 49 passed, 0 failed/skipped, including 2 new scenarios |
| EF migration/model verification | Both existing migrations applied by fresh fixtures; no pending model changes |
| Frontend TypeScript / ESLint / production build | Passed |
| Frontend unit/component tests | 29 passed |
| Desktop/mobile browser suite | 60 passed, 0 failed/skipped in the final serial run |
| Real-backend customer/seller/Admin/continuous journey | 4 passed in one final run |
| Production PWA/offline check | 1 passed |
| Production startup negative probes | Demo seed and short signing key rejected before database work |
| Database integrity and logical dump/restore | All 4 violation counts zero, selected counts/migrations identical after restore |
| Test harness environment isolation | All 7 sentinel caller values restored; regression passed |
| Dependency audits | NuGet: no reported vulnerable packages in 6 projects; npm production: 0; full npm: 18 moderate + 2 low development-tooling package findings |
| Compose configuration | Syntax validation passed; engine runtime BLOCKED |

Commands: backend ./scripts/verify-sprint4-backend.ps1; dotnet list GroupBuy.sln package --vulnerable --include-transitive --format json; frontend npm run typecheck, npm run lint, npm run build, npm test, npx playwright test, npx playwright test --config playwright.pwa.config.ts, ./scripts/test-backend-isolation.ps1, npm audit --json and npm audit --omit=dev --json (NODE_OPTIONS=--use-system-ca). Final frontend type/lint were rerun after the auth fixture correction; application code/build assets did not change after the successful build/PWA check.

Build warnings remain the upstream Zod annotation comments and approximately 542 kB main chunk. No warning suppression, dependency upgrade, test skipping or removed scenario was used to obtain results. Backend logs: .local/sprint4-build.log, sprint4-test.log, sprint4-model.log and both projects' TestResults/sprint4-results.trx. Audit JSON is in each repository's ignored .local directory.

Environment: Windows, Node 22.18.0, SDK 9.0.302 / ASP.NET Core runtime 9.0.7, portable PostgreSQL 17.6. Backend fixture creates/drops a random database and applies both real EF migrations. Frontend harness initializes a separate loopback trust-authenticated cluster on 55434, applies existing docs/schema.sql and seeds Development fixtures. Backend cluster is separate on 55433. No shared/user database is migrated, cleared or load-tested. Servers stop in finally; ignored fixture files remain for diagnosis.

Docker CLI is present but engine connection fails at dockerDesktopLinuxEngine. docker compose config --quiet passes with a nonsecret configuration-only password placeholder; Compose runtime, Testcontainers, container restart/persistence and deployment host health remain BLOCKED/unverified. No container was started. Compose contains PostgreSQL only, not a full deployment.

## Bounded performance

| Measurement | Result |
| --- | --- |
| Campaign listing, 20 samples / concurrency 4 | p50 28.46 ms; p95 30.35 ms; max 32.31 ms |
| Campaign detail, 20 samples / concurrency 4 | p50 15.99 ms; p95 19.28 ms; max 19.99 ms |
| Reservation contention wave, concurrency 12 / capacity 6 | 6 accepted, 6 conflicts; 225.37 ms wall time; 26.62 accepted reservations/second for this wave only |
| Representative SQL execution: listing / detail / active reservations | 0.088 / 0.038 / 0.034 ms after ANALYZE |
| Fixture size at integrity/plan capture | 20 campaigns; 30 reservations; 9 pickups; 36 notifications; 76 audits |

Percentiles use nearest rank over 20 samples (the JSON medianMs field is the lower p50). PostgreSQL chose sequential scans on these tiny tables; this is not evidence that indexes are missing or useful at scale. Endpoint timings include the actual public projections/authentication where applicable; representative SQL plans omit parts of EF catalog projections and do not replace query tracing at realistic volume. No speculative index migration was added.

Final artifacts: frontend .local/sprint4-performance.json and .local/integration-db4389497b2646219c77b4955b62a0d0/{integrity-before.txt,integrity-after.txt,query-plans.txt,fixture.dump}. All four invariant violation counts were zero before and after restore; selected table and migration counts matched. Fixture used postgres superuser for setup/benchmark; the separate backend regression verifies register/login under a restricted login, not all benchmark workloads under production grants.

Method: warm loopback Kestrel/real PostgreSQL, synthetic small catalog, 20 response-body-complete samples each for campaign listing/detail in batches of 4; then 12 simultaneous reservation attempts from one authenticated Customer against capacity 6. Accept rate divides six accepted reservations by complete contention-wave wall time; it is not sustained throughput. Load campaign is failed through supported seller rejection afterward, releasing the hold while preserving history. No production/shared data targeted; no performance threshold or realistic pilot volume was supplied. These measurements are diagnostics, not a capacity SLA or a production scalability claim. Existing multi-customer backend race separately verifies competing principals.

## Failures and fixes during execution

- Initial backend invocation used postgres against an older local cluster initialized with a different role and failed fixture setup. Replaced with scripts/verify-sprint4-backend.ps1, creating a new uniquely named cluster with an explicit test identity. No existing cluster/roles were repaired or reset.
- Initial EF model command selected the API startup assembly, which lacks EF Design tooling; corrected to the Infrastructure design-time factory. No schema change was needed.
- Initial overlapping browser runs saw a desktop navigation assertion and two existing real-workflow loading assertions time out. No assertion was removed or timeout enlarged. Serial reruns are reported separately; these failed attempts do not count as passes.
- Serial real-workflow rerun exposed an existing Admin fixture dependence on test duration: registration can hit the shared 10/minute auth window. Added bounded 429-only fixture retry after 61 seconds without changing production limits or retrying successful mutations. The primary journey already follows the same rule.
- Dependency audits initially failed sandbox DNS / Node certificate trust. Approved network retry and NODE_OPTIONS=--use-system-ca succeeded; TLS checks were never disabled.
- Confirmed Medium harness finding: Development/URL/JWT issuer/audience values leaked into the caller environment. Fixed by restoring all seven changed settings, including pre-existing secrets, in finally. New scripts/test-backend-isolation.ps1 checks restoration through sentinel values after the complete real-browser/restore harness.

No confirmed new Critical/High application defect required a domain/authorization rewrite. High release blockers are documented in sprint4-security-audit.md; they are not waived by tests. Runtime/SDK installation, Admin MFA design and production infrastructure changes require separately reviewed implementation/evidence.

## Files changed

Backend new: tests/GroupBuy.IntegrationTests/MarketplaceJourneyTests.cs; tests/GroupBuy.IntegrationTests/RuntimePermissionsTests.cs; scripts/verify-sprint4-backend.ps1; scripts/sprint4-integrity.sql; scripts/sprint4-query-plans.sql; docs/sprint4-e2e-report.md; docs/sprint4-security-audit.md; docs/production-readiness-checklist.md; docs/deployment-plan.md.

Backend updated: README.md and docs/security-model.md (Sprint 4 links/evidence). Frontend new: integration/zzzz-marketplace.spec.ts; scripts/test-backend-isolation.ps1. Frontend updated: scripts/test-backend.ps1 (optional Sprint4Verification dump/restore/plans and environment restoration); README.md (verification commands/report pointers). Frontend integration/zz-admin-backend.spec.ts now retries only rate-limited authentication fixtures once after a new limiter window; all existing scenarios remain. Application sources are unchanged. Ignored .local logs/JSON/TRX/screenshots/clusters/dump are verification artifacts, not release files. No migration or dependency change.

## Decision and next steps

GO for continued isolated synthetic marketplace E2E/staging verification. NO-GO for a real-customer controlled pilot today. Close current runtime/.NET 10 verification, Admin MFA/provisioning, production TLS/proxy/permissions/secrets, seller legal review, audit retention, backups/PITR/restore objectives, hosting/Compose/restart/alerts and legacy reconciliation gates. Profile realistic pilot data against an agreed performance budget and remediate/accept development-tooling advisories. Obtain named release-owner approval using production-readiness-checklist.md and deployment-plan.md before deployment. No push/deploy is performed by this sprint.








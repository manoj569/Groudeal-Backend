# Sprint 2 implementation report

Implemented against the existing .NET 9 modular monolith on 1 November 2026. No new solution, service deployment, broker, frontend, real payment, wallet, escrow, delivery or category-specific workflow was introduced. No push or deployment was performed.

## Delivered behavior

- Seller dashboard summary, owned campaigns with status filtering, per-campaign performance, paginated customer reservations, variant inventory and pickup lists.
- Precise declared on-hand inventory with expected versions; draft availability checks; transactional whole-target commitment at submission; release on Admin rejection, expiry, confirmation rejection/timeout and missed pickups. Conservative holds prevent double allocation across all live campaigns, including nonoverlapping schedules. Stock consumption increments inventory version.
- Automatic AwaitingSellerConfirmation after target filling, configurable persisted deadlines, seller confirm/reject with exact retry keys and persisted decision actor/time/reason/outcome. Confirmation creates unique individual pickups. Late confirmation conflicts independently of worker timing.
- Secure Pending → Ready → Collected pickup flow, one-time high-entropy codes stored only as hashes, code expiry/reissue cooldown, durable failed-attempt lockouts, actor HTTP throttling and collection replay/concurrency protection. Successful group completion requires every pickup Collected; partial/missed pickup groups end Failed without erasing collected evidence.
- Customer reservation detail and pickup list/detail with owned location/instructions/deadlines/next actions; explicit NotTracked payment status/policy. Customers cannot self-mark collection or access another customer's code.
- Transactional in-app notification records for all eight requested types, paginated recipient-scoped list/read APIs, unique delivery keys, no external provider. These records are the durable delivery destination, so there is no external dispatch gap requiring a separate outbox yet.
- Per-campaign worker scope/transaction isolation, bounded PostgreSQL waits, persistent exponential retry timing, safe structured errors, cancellation handling, new-host recovery and Admin workload/heartbeat visibility.
- Required-null regex validation gaps fixed; existing revoked-role and stale-seller-approval fixes preserved. Publication/join/confirmation/pickup paths recheck applicable eligibility. Inventory units, precision, versions and pickup-code contracts are validated server-side.

## API / compatibility

The original routes remain. Inventory declaration is now required before new draft creation, and submission allocates the target. Final reservation now automatically awaits confirmation, rather than stopping indefinitely at TargetReached. The old Awaiting transition is a no-op if already awaiting; legacy Confirmed delegates to the secure decision flow. Legacy Completed cannot bypass verified pickups; the last collection completes the group automatically. The legacy dashboard replaces its mixed-unit reservedQuantity aggregate with activeReservations count; use the richer summary/per-variant metrics in new clients. Existing regression tests retain their scenarios, with setup declaring stock and lifecycle expectations updated to these intentional Sprint 2 changes. No test was removed to hide a failure.

See api-contracts.md for every route, DTO, status filter, error and retry behavior; fulfillment-workflow.md for accounting, code recovery and explicit payment limitations.

## Files changed

| Area | Files |
| --- | --- |
| Domain | src/GroupBuy.Domain/Models.cs; new Operations.cs |
| Application | src/GroupBuy.Application/Contracts.cs; new OperationsContracts.cs and WorkerHealth.cs |
| Infrastructure | src/GroupBuy.Infrastructure/Database.cs, Marketplace.cs, DemoSeed.cs; new Marketplace.Operations.cs |
| Migrations | new 20261101131443_Sprint2Operations.cs and .Designer.cs; updated MarketplaceDbModelSnapshot.cs; regenerated docs/schema.sql |
| API | src/GroupBuy.API/Program.cs, ExpiryWorker.cs, appsettings.json; new OperationsEndpoints.cs |
| Unit tests | new tests/GroupBuy.UnitTests/OperationsTests.cs |
| PostgreSQL tests | updated tests/GroupBuy.IntegrationTests/BackendTests.cs; new OperationsTests.cs |
| Documentation | README.md; docs/architecture.md, database-design.md, api-contracts.md, security-model.md, group-buying-state-machine.md; new docs/fulfillment-workflow.md and this report |

The Sprint 1 audit and verification reports remain historical records; this report describes the later implementation and supersedes their open-worker/deadline/required-null validation findings for the new flow. No Git repository is present at the supplied workspace root, so a Git diff/clean-tree claim cannot be made.

## Migration and existing-data safety

Sprint2Operations adds inventory/pickup/notification tables, campaign commitment/decision/deadline/retry columns, restrictive FKs, unique keys, indexes and checks. Default added quantities are zero; no physical stock, pickup collection evidence, financial status or operational notifications are fabricated from existing data. Startup does not migrate automatically.

Automatic approval review rejected a proposed broad legacy data backfill because it would rewrite commitments/create notifications and could affect rollout irreversibly. That proposal was not applied. The delivered alternative is schema-only. Existing live Sprint 1 rows with zero commitments are counted by Admin operations health and are blocked from new joins/approval/confirmation until an explicit reviewed cutover; deadline-less legacy full/confirmed campaigns are not automatically failed. Resolve existing commitments or approve a separate, data-specific conversion plan before upgrading a live pilot. New demo fixtures include synthetic declared stock; rerunning a pre-existing demo-marker account intentionally does not rewrite that old database.

## Verification

Test infrastructure uses an isolated PostgreSQL 17.6 server at 127.0.0.1:55433 inside ignored .local/audit-postgres. The fixture creates/drops a random test database, applies both actual migrations and uses the actual HTTP application. No user database is cleared or migrated. Process startup/tests/shutdown run in one permitted session because sandbox boundaries can terminate detached servers. PostgreSQL shutdown was confirmed after the final run.

| Final check | Result |
| --- | --- |
| dotnet build GroupBuy.sln --no-restore -m:1 -p:UseSharedCompilation=false | Passed, zero warnings/errors |
| dotnet test GroupBuy.sln --no-restore -m:1 -p:UseSharedCompilation=false | 37 unit tests and 29 real PostgreSQL integration tests passed; zero failures/skips |
| Regression baseline | All 21 existing unit tests and 13 existing integration scenarios remain; 16 unit and 16 integration cases added |
| InitialCreate + Sprint2Operations migrations | Applied successfully to fresh isolated PostgreSQL databases by the actual EF migration runner |
| dotnet ef migrations has-pending-model-changes | No model changes since the latest migration |
| Migration SQL | Regenerated docs/schema.sql; idempotent script generated at ignored .local/sprint2-idempotent.sql |
| NuGet audit with --vulnerable --include-transitive | No vulnerable packages reported across all six projects by configured sources; not a runtime or unknown-vulnerability certification |
| docker compose config --quiet | Passed using a temporary configuration-only password placeholder |
| Docker runtime / Compose startup / Testcontainers path | Not verified; Docker engine unavailable, alternate PostgreSQL path used |

Unit and integration TRX artifacts: tests/GroupBuy.UnitTests/TestResults/sprint2-results.trx and tests/GroupBuy.IntegrationTests/TestResults/sprint2-results.trx. Console capture: ignored .local/sprint2-test.log. Initial compilation caught ambiguous target-typed FluentValidation test constructors; explicit request types corrected them before successful builds. An earlier full run passed 37/25 before the final additional cases; the final totals above include all current changes. Sandbox DNS initially required an allowed network-access rerun for the dependency audit. No failed attempt is counted as successful verification.

Coverage includes the 13 Sprint 1 integration scenarios and new seller dashboard/resource isolation, stock commitments, double allocation, stale concurrent inventory versions, successful/duplicate/conflicting/rejected/late confirmation, cancellation races, hashed codes/invalid codes/expiry/lockout/replay/concurrent collection, cross-seller/customer isolation, notification transaction rollback/read/idempotency, poison-candidate failure isolation/backoff/new-host recovery, cancellation handling, partial fulfillment and group completion rules. Unit tests add precise bulk/discrete stock constraints, null validation and decision/code shapes.

| Requested test area | PostgreSQL/HTTP regression evidence |
| --- | --- |
| 1. Dashboard ownership | Seller_dashboard_inventory_and_reservations_are_owner_scoped |
| 2. Inventory commitment | Inventory_versions_and_concurrent_campaign_submissions_prevent_double_allocation |
| 3. Double allocation | Two competing draft submissions; only one remaining hold succeeds |
| 4. Concurrent stock updates | Same expected version used concurrently; one succeeds, one conflicts |
| 5. Confirmation success | Confirmation_is_transactional_idempotent_and_not_an_order_payment |
| 6. Rejection | Seller_rejection_releases_inventory_and_preserves_terminal_reservation_snapshots |
| 7. Confirmation deadline | Confirmation_expiry_survives_new_processor_scopes_and_cannot_be_confirmed_late |
| 8. Duplicate confirmation | Three parallel identical decisions, one pickup/decision audit/notification |
| 9. Cancellation races | Existing cancellation/confirmation test plus Cancellation_racing_final_reservation_preserves_active_quantity_and_confirmation_boundary |
| 10. Pickup verification | Pickup_code_is_hashed_single_use_and_concurrent_collection_completes_once |
| 11. Invalid code | Invalid_pickup_attempts_are_persisted_and_lock_even_the_correct_code; expired-code test |
| 12. Replay | Concurrent/repeated collection conflicts after the single successful handover |
| 13. Cross-seller pickup | Pickup_codes_and_notifications_cannot_be_accessed_by_other_sellers_or_customers |
| 14. Cross-customer reservation | Dashboard isolation test plus existing cancellation ownership tests |
| 15. Notifications | Notification_delivery_is_transactional_readable_and_idempotent; failure rollback checks |
| 16. Worker restart | Worker_isolates_poison_campaign_persists_backoff_and_recovers_in_new_scope creates a new application host and processes persisted work |
| 17. Completion | Partial_collection_does_not_complete_campaign_and_uncollected_stock_is_released; retained full-campaign terminal test |
| 18. Failure/retry idempotency | Trigger-injected transactional failure, persisted bounded backoff, healthy-candidate continuation and exactly one successful retry event |

These are correctness/regression tests, not a throughput benchmark, physical inventory audit or production process-kill experiment. New-host recovery verifies that no in-memory job state is needed; it does not claim every OS crash/disk/network failure mode was exercised.

## Remaining risks and frontend readiness

**GO for Frontend Sprint 1 development against a fresh/migrated development database using new inventory-backed campaigns. NO-GO for public production or unreviewed live-data cutover.** Frontend can build onboarding/catalog/dashboard, reservation tracking, in-app notifications, confirmation and pickup screens from the documented contracts. Show variant units, explicit deadlines, snapshot values, confirmation/cancellation boundaries and NotTracked payment semantics. Handle version conflicts by refetching, confirmation retries with the same key, collection retry via status lookup and pickup code secrets without logging.

Production gates remain: supported patched .NET runtime and eventual .NET 10 verification (only SDK 9.0.302/runtime 9.0.7 installed); real TLS/proxy/host/CORS configuration; secure Admin provisioning/MFA; seller legal/commercial verification and offline payment/invoicing policy; database least privilege/TLS/backups/restore; secret/key rotation; load/long-lock/process-kill and deployment monitoring tests. Docker runtime/Compose cannot be claimed verified while the engine is unavailable. HTTP rate limits are process-local, though pickup attempt lockouts and lifecycle retries persist in PostgreSQL. Seller-wide serialization may limit a high-volume merchant and needs measurement before scaling.

The stock ledger is intentionally minimal: no warehouse movements, replenishment forecasts, supplier reservations or predicted future stock. All live campaign holds compete conservatively. Notifications are durable in-app records, not external delivery guarantees. Aggregate conservation and cross-reservation pickup tuple consistency remain an application/transaction boundary for trusted database writers; restrict direct DML. Admin review reasons still need a protected operational review trail; new seller decisions do have reasons. No claim is made of full production compliance or a .NET 10 build.

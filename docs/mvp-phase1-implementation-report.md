# Groudeal MVP Phase 1 implementation report

Date: 5 November 2026. Phase 1 implementation and frozen-code regression validation are complete. Engineering status: **READY FOR CONTROLLED PILOT PREPARATION**. Real pilot activation is **NO-GO** until the listed activation inputs and provider acceptance are complete. Production remains **NO-GO**.

## Implemented flow

The existing modular monolith, campaigns, reservations, pickups, notifications, worker and audit services are extended rather than replaced. Free product/locality interest is idempotent and withdrawable, including when a product becomes unavailable. Seller/Admin demand contains aggregate counts/quantities only. Sellers use existing onboarding, approval, products, inventory and draft campaigns, then configure immutable ordered tiers before submission.

Paid checkout persists a bounded hold and server maximum-price/separate-fee snapshots. Only provider-verified captured money creates the existing reservation. Locks, payload-bound idempotency, durable provider receipts, webhook deduplication, reconciliation and source-refund operations protect capacity and money. Legacy free-reservation/pickup mutations reject paid campaigns. The final tier/quantity are frozen transactionally at deadline or full-capacity early finalization; below-minimum campaigns refund in full. Price reductions refund to the original payment method, including provider minimum-refund validation before checkout. No wallet or escrow is introduced.

Successful commitments confirm fulfillment choice. Seller pickup is free. Founder delivery uses explicitly configured locality/postal codes and fee, encrypted address storage, seller preparation, founder collection/batches, delivery states/exceptions and customer OTP. Physical stock is consumed once at seller handover; failed deliveries never silently restock goods. Opaque QR lookup reveals only an authorized record, then requires separate actor-bound physical handover. Backup codes are keyed, locked after repeated failures, expire, and cannot be replayed. In-app notifications and pilot metrics cover the workflow.

## Frontend

`/pilot`, `/seller/pilot` and `/admin/pilot` provide the corresponding customer, seller and founder experiences, linked from profiles/workspaces. Existing catalog/onboarding/inventory/approval/MFA screens are reused. Paid legacy detail links redirect into the paid flow. QR rendering is local; secrets remain memory-only, passes clear on backgrounding, and PWA API/private runtime caching remains disabled. Razorpay checkout uses HTTPS and server verification; sandbox actions are visibly test-only.

## Migrations and permissions

Added migrations:

1. `20261104194747_PhaseOnePilotTransactions`: demand, tiers, checkout/payment/webhook/refund/fulfillment records and durable sandbox ledger; campaign pricing/finalization metadata, constraints, foreign keys and unique indexes.
2. `20261104225226_RecordFounderStockHandover`: durable once-only physical-stock handover marker.
3. `20261104233113_TrackPaidPickupPolicy`: explicit captured-payment pickup policy; safe backfill for existing captured Phase 1 records.

Financial records, recorded handovers and paid pickup policies refuse destructive rollback. The runtime grant template adds only required new-table DML, with draft-tier deletion; no DDL, migration-history writes or audit tampering rights are granted. New schema is applied by disposable PostgreSQL 17 tests only. No staging database or production Compose configuration is modified. The additive SQL artifact is `docs/mvp-phase1-schema.sql`.

## Validation evidence

Final frozen-code backend results:

* Serial `dotnet build GroupBuy.sln --no-restore -m:1`: **PASS**, 0 warnings, 0 errors.
* `dotnet ef migrations has-pending-model-changes`: **PASS**, no pending model changes.
* Final idempotent migration SQL generated at `docs/mvp-phase1-schema.sql`; EF model contains all eight migrations (the five prior migrations and three Phase 1 migrations). Disposable PostgreSQL 17 migration/integration coverage applied and exercised the schema. No Sprint 5.1 or Sprint 5.2 staging database was migrated.
* `dotnet test GroupBuy.sln --no-build --no-restore -m:1 --blame-hang-timeout 5m --blame-hang-dump-type none`: **155 passed, 0 failed, 0 skipped**: 52 unit tests and 103 PostgreSQL Testcontainers integration tests. Integration suite duration was 11m14s. `GROUPBUY_TEST_POSTGRES` was unset; fixtures created disposable PostgreSQL resources.
* Frontend typecheck, lint and production build: **PASS**. Vitest: **57 passed, 0 failed, 0 skipped**. Full Playwright browser suite: **118 passed, 0 failed, 0 skipped**. PWA suite: **1 passed, 0 failed, 0 skipped**. Browser/API contract tests use mocked APIs; they do not establish live merchant processing.
* Dependency audit: .NET transitive package audit reported no vulnerable packages across the six solution projects; production-only npm audit reported 0 vulnerabilities. The npm audit deliberately omitted development dependencies, so it is not a claim about the dev dependency tree.
* Scoped read-only Docker inventory after test completion found no containers with `groudeal.mvp.testing=true` or `org.testcontainers=true`. Test resources ran on isolated ephemeral runner/fixture networks; protected Sprint 5.1 resources were not addressed.
* `git diff --check`: **PASS** in both repositories (Git emitted only existing LF-to-CRLF working-copy notices).

Frontend production build reported the existing oversized main entry chunk (545.91 kB against the 500 kB advisory threshold) and Zod pure-annotation notices. The build succeeded; these warnings remain visible and are not described as a warning-free frontend build. No failing assertion is skipped or suppressed.

Corrections found during regression:

* EF demand projection needed aggregation/pagination before DTO mapping.
* Migration assertions needed to compare all applied migrations with repository migrations, including after rollback/reapply.
* Test Admin enrollment/login moved immediately before Admin work so long seller setup does not consume the existing security window. Production expiry and MFA protections are unchanged.
* Exact pickup response allowlist adds only nonsecret paid-workflow metadata; cross-seller denial, code/hash/customer-ID exclusion remain asserted.
* New browser role fixtures retain the real base Customer role and navigate without losing the intentionally memory-only session.
* QR lookup supports direct opaque scanning, with six-digit fallback scoped to the displayed pickup reference. Verification never performs handover implicitly.

Coverage includes captured-only quantity, concurrent holds/callbacks/redemption, idempotency conflicts, cancellation/late funds, failed-campaign/source refunds, refund-success-before-local-crash recovery, signed/replayed/changed webhooks, delivery address privacy, OTP lockout, legacy bypass denial, rescue limits, immutable tiers, provider callback/capture/refund contracts and fail-closed activation. Existing security, authorization, runtime-permission, migration, worker and recovery suites are retained.

## Task file inventory

Backend root: `D:\Manoj\Group-Buy\Group-Buying Backend`.

* New: `src/GroupBuy.Domain/PhaseOne.cs`; `src/GroupBuy.Application/PhaseOneContracts.cs`; `src/GroupBuy.Infrastructure/PhaseOneDatabase.cs`, `PaymentGateways.cs`, `Marketplace.PhaseOne.cs`, `Marketplace.PhaseOne.Fulfillment.cs`; `src/GroupBuy.API/PhaseOneEndpoints.cs`.
* Extended: Domain `Models.cs`, `Operations.cs`, `CampaignRules.cs`; Application `Contracts.cs`, `OperationsContracts.cs`; Infrastructure `Database.cs`, `Marketplace.cs`, `Marketplace.Operations.cs`, `Marketplace.SellerManagement.cs`, `Marketplace.Administration.cs`; API `Program.cs`, `ExpiryWorker.cs`; `scripts/grant-runtime.sql`.
* Schema: the three migrations and their designer files, plus `MarketplaceDbModelSnapshot.cs`.
* Tests: new `tests/GroupBuy.UnitTests/PhaseOneTests.cs`, integration `PhaseOneTests.cs`, `PaymentGatewayTests.cs`; extended integration `BackendTests.cs`, `MigrationSecurityTests.cs`, `MigrationUpgradeTests.cs`, `SellerManagementTests.cs`.
* Documentation: this report, implementation plan, API contract, security notes and additive SQL schema artifact.

Frontend root: `D:\Manoj\Group-Buy\Group-Buying Frontend`.

* New: `src/phase-one/{types.ts,shared.tsx,checkout.ts,customer.tsx,seller.tsx,admin.tsx,fulfillment.tsx}`; `tests/phase-one.test.tsx`; `e2e/phase-one.spec.ts`; `docs/mvp-phase1.md`.
* Extended: `src/{main.tsx,types.ts,group.tsx,account.tsx,tracking.tsx}`, `src/seller/{routes.tsx,pickups.tsx}`, `src/admin/routes.tsx`, `package.json`, `package-lock.json`.

Pre-existing dirty files, Sprint 5.2 evidence and unrelated user changes are preserved. Git status contains prior work and is not a list of changes made solely by this task. No reset, stash, clean, commit, push or deployment was performed.

## Activation inputs and limits

1. Razorpay Test/Live credentials and webhook secret, merchant activation and real Test Mode capture/refund/webhook/reconciliation acceptance.
2. Explicitly approved real pilot locality/postal codes.
3. Explicitly approved flat home-delivery fee. Seller pickup remains free.

Until those inputs are supplied, real pilot activation is **NO-GO**. Automated sandbox/fake-HTTP coverage does not establish live Razorpay readiness. Merchant-console refunds outside the application require operator reconciliation; pending/exhausted provider operations require the documented monitored Admin procedure. Build warnings from dependency annotations and the existing large frontend entry chunk are recorded, not hidden. No protected Sprint 5.1 resource was used, modified, restarted, mounted or migrated; Sprint 5.2 verification was not rerun.

Next: provide the approved Razorpay Test Mode credentials/webhook secret through the authorized secret manager, approve the pilot area and delivery fee, then run a separately controlled merchant Test Mode acceptance covering capture, webhook, refund and reconciliation. No automatic deployment is authorized.

# Module 3 — Demand backend

## Audit and reuse

Baseline: `a8aa1e4` (Module 2). Existing `DiscoveryDemand`, `customer_interests`, public product interests, discovery/search/category queries, `DiscoveryAreas`, Customer/Admin policies including MFA, safe Problem Details, fixed-window limits, audit logs, transaction-scoped advisory locks and row locks are extended. Migration `20261107060508_HomeDiscoveryDemands` is unchanged. There is no second demand or membership table, payment stage or search engine.

## Canonical APIs and compatibility

All routes are `/api/v1` prefixed. Keep `/discovery/demands` canonical rather than adding duplicate `/demands` aliases.

- POST `/discovery/demands`: existing `{title,serviceArea,categoryId?}` remains valid and defaults to legacy Product intent, without inventing a unit. Rich requests add explicit `demandType` (Product/Service), active `categoryId`, optional physical `productId`, `quantity`, `unit`, `requirement`, `details`. Rich Product requires positive quantity and supported unit; Service may omit both. Returns existing `demandId,joined`, plus a backward-compatible `demand` detail when available.
- POST `/discovery/demands/similar`: same validation/normalization, no writes or audit persistence. Returns `normalized,similarDemandFound,demand,canSubmit,blockingCode`. A terminal canonical identity is excluded from active similarity but blocks submission with `demand_closed`, preserving Module 2's no-reopen/no-clone behavior.
- GET `/discovery/demands/{id}`: public aggregate detail; only an authenticated Customer's own participation is personalized.
- GET `/discovery/demands/mine`: Customer-only, includes created/joined/withdrawn history; filters `status,type,categoryId,serviceArea,page,pageSize`. Stable CreatedAt descending, Id ascending order.
- POST `/discovery/demands/{id}/join`, POST `/discovery/demands/{id}/withdraw`, GET `/discovery/demands/{id}/membership`: original routes remain. Join/withdraw are idempotent, free and share existing membership rows. Mutation detail includes aggregate progress and caller-only requirement data; withdrawal still succeeds if area configuration disappears, returning the core membership response without unavailable discovery metadata.
- Admin GET `/admin/discovery/demands`, GET `/{id}`, GET `/{id}/audit`, POST `/{id}/moderate`: existing Admin policy/MFA and admin rate policy. Moderation requires Closed/Cancelled and a public-safe reason of 10–500 characters. Existing POST `/{id}/close` is retained.

Existing discovery/search/suggestions/category endpoints remain the only search implementation. Existing product-interest cards retain ProductId/action contracts; linked demand definitions are not emitted a second time as free-text cards. Closing their aggregate suppresses product-interest discovery and rejects further participation through both action paths.

Unknown JSON members remain rejected by the existing global contract policy: clients cannot supply ownership, timestamps or counts. Existing frontend requests are unchanged. Older API binaries must drain before rich traffic; their old title+area assumptions are not safe against type/category-separated records. No automatic app/schema rollback is promised.

## Model and canonical matching

`DiscoveryDemand` gains explicit DemandType, optional ProductId/creator, compatibility identity flag, cancelled/closure metadata. Existing IsOpen remains the persisted lifecycle primitive; public status derives BuildingInterest/Closed/Cancelled. Canonical identity is immutable. Terminal identities cannot be reopened or recreated through this module.

Exact matching uses type + category + product identity (when present) or NFKC/collapsed/lower-invariant title + configured area. No fuzzy or AI merging. Product definitions use an empty normalized-title component so catalog display text cannot create another aggregate. Linked product identity+area is additionally unique. Rich type/category scopes may coexist; the legacy title+area uniqueness rule is retained for legacy identities. PostgreSQL NULLS NOT DISTINCT handles nullable canonical components.

Quantities, units, optional requirement (500) and details (1000) belong to each existing CustomerInterest, not the shared aggregate. They are returned only to that customer; another customer cannot overwrite or inspect them. Optional details are not public aggregate content. The historical DesiredQuantity remains for legacy compatibility; a Service request without a conventional quantity exposes null quantity/unit, not an invented household or project count.

Product units: kg, l, piece, pack; catalog-linked unit must match an active variant. Service units when explicitly applicable: household, project, hour, visit, square_metre. The current catalog supports physical products only. Service demands therefore use custom requirements; fake catalog products are never created. A future genuine service catalog can extend this explicit branch.

## Memberships and concurrency

Product-bound membership retains ProductId and may also carry DemandId. The existing customer+product+area unique index and customer+demand unique index prevent duplicate memberships. Legacy product-interest actions and discovery demand actions use the same rows, product aggregate, transaction/row lock and per-customer interest advisory key. Backfilled legacy rows retain their IDs, quantities, dates and active/withdrawn state.

Custom creation uses a canonical advisory transaction lock, plus the legacy title/area lock when appropriate; PostgreSQL unique indexes remain authoritative. All membership and moderation writes hold the aggregate row lock and reload after acquisition. Counts are SQL distinct active customers with active accounts, never client counters. Join/withdraw cannot produce a negative count, paid order, reservation, hold, refund or inventory/campaign mutation.

## Progress, privacy and areas

There is no approved interest threshold, so progress returns currentInterest with null targetInterest/progressPercentage. SellerOpportunityState is NotStarted and the message says outreach has not started. No automatic seller invitation, campaign or guaranteed price/fulfillment is implied.

Public DTOs contain demand/category/product IDs and public summaries, configured locality, counts, status and server timestamps. They exclude creator/customer IDs, contacts, addresses, other membership records, personal requirement fields and moderation reasons. Admin detail exposes the moderation reason and bounded recent audit; full audit history is paginated and contains action/time only. Current-customer personalization is owner-scoped. Details/listing use bounded scalar queries and batched metadata/participation queries, with no per-card query loop.

The existing approved-area abstraction is unchanged, including Production's 503 service_area_unconfigured gate. Unsupported areas fail closed; no real PIN or launch locality is configured by this module. Historical private membership lookup/withdrawal remains possible after area removal. No maps, GPS or external provider is introduced.

Titles/requirements normalize and reject controls, obvious contact/link/markup input with explicit errors; this is not comprehensive PII classification. All returned text must remain encoded as text by clients. Reasons/details are not written into logs/audit metadata. Existing strict JSON, CORS, JWT, MFA, ingress/TLS, encryption and logging rules are unchanged. Audit events use existing DiscoveryDemandCreated/Joined/Withdrawn/Closed conventions, DemandCreated/Cancelled and SimilarDemandMatched; previews do not persist events.

## Forward migration and grants

New migration: `20261107193743_DemandModule`. It extends the two existing tables, preserves free-text rows, derives known legacy creators from existing creation audits, backfills one product definition per product/area and attaches existing product-interest rows without changing their IDs/quantities/state. It adds null-aware canonical uniqueness, query indexes, bounded fields/checks, matching target/area/product FKs/guards and immutable identity guards. Down explicitly throws; use reviewed forward fixes.

The prior grant template already grants SELECT and INSERT/UPDATE on the existing tables and appends audit entries. No new table, sequence, owner privilege, DDL or DELETE is required, so `scripts/grant-runtime.sql` does not change. Runtime-role tests verify both new Product requests and My Demands with those exact grants. Apply/review grants only with the separate operator/migrator identity, never API startup.

Do not apply this migration to Neon, production or protected Sprint 5.1. Local tests use new PostgreSQL Testcontainers with generated database/role names. `scripts/verify-module3-readiness.ps1` wraps the existing complete restore/build/security/regression/migration gate and verifies the expanded complete suite counts.

## Validation and rollout

Final results are recorded after the frozen complete gate. Early diagnostic runs are retained under `.local/module3-*`. The first expanded tamper test expected ignored extra fields; it was corrected to the actual stronger strict-JSON rejection, preserving the policy. New tests use a separate fixture database so synthetic service taxonomy cannot affect the historical seven-category assertion. No regression assertion, timeout, retry or security policy was weakened.

Before rollout: review the new SQL, backfill/index lock impact on actual data volume, dedicated migrator/runtime identities and grants, approved area configuration, API instance draining, canonical route/type/unit frontend contract and controlled acceptance. Existing providers and payments remain disabled. Catalog reclassification that invalidates a linked demand requires an explicit operational review; this module never silently moves a canonical demand between categories. Full seller opportunity/offer/campaign conversion, configured interest targets, service catalog items, reopening, linguistic similarity and comprehensive moderation remain future work.

No frontend, protected Sprint 5.1, production environment, deployment, provider/payment activation, commit or push is part of this work.

## Changed file inventory

- API: `src/GroupBuy.API/DiscoveryEndpoints.cs`.
- Application: `src/GroupBuy.Application/DiscoveryContracts.cs`, `DemandContracts.cs`.
- Domain: `src/GroupBuy.Domain/DiscoveryDemand.cs`, `PhaseOne.cs`.
- Infrastructure: `DiscoveryDatabase.cs`, `DiscoveryDemandService.cs`, `DiscoveryQueries.cs`, `Marketplace.PhaseOne.cs`, new `DemandPersistence.cs` and `DemandReads.cs`.
- Migration: `20261107193743_DemandModule.cs`, its Designer, and `MarketplaceDbModelSnapshot.cs`.
- Tests: `BackendTests.cs` (shared helper visibility only), `DiscoveryTests.cs` (historical-schema upgrade projections/assertions), new `Module3DemandTests.cs` and `DemandInputTests.cs`.
- Local gate: `scripts/verify-module3-readiness.ps1`; this document.

Final review added Unicode-safe catalog-title normalization/truncation in both shared product persistence and discovery validation. Boundary assertions exercise a two-code-unit Unicode scalar at the 100-code-unit boundary through both HTTP creation paths, preserve full catalog product summaries, and verify NFKC/whitespace normalization. Identity remains product-ID based.

The expanded complete run under `.local/module3-final-66d9092505a24edd9fb84911b2a27a3a` passed all 140 unit cases and the focused ingress (67), Module 2 discovery (45), PostgreSQL/configuration (29), authentication (31), Admin/security (50), Seller/CORS (6) stages. Its complete PostgreSQL suite ended 312 passed / 1 failed: `Admin_provisioning_is_explicit_audited_idempotent_and_revokes_stale_tokens` took 16m51s and the enrollment grant correctly expired (`mfa_enrollment_closed`) following an extended execution pause. No expiry, timeout, retry or assertion was relaxed. The full final suite is rerun after the Unicode correction; authoritative results follow below.

## Authoritative final validation

Final evidence: `.local/module3-verified-ab038bc8e26a4c50bc1e5da62597ccda`. All ordered complete-suite and post-suite checks passed (`COMPLETE.txt`).

| Check | Passed/total | Failed |
|---|---:|---:|
| 04-all-unit | 140/140 | 0 |
| 08-all-postgres | 313/313 | 0 |
| 09-empty-migration | 1/1 | 0 |
| 10-upgrade-migration | 1/1 | 0 |
| 12-restart-protection | 3/3 | 0 |
| 13-render-production | 16/16 | 0 |

All listed runs have zero skipped/not-executed and aborted cases. Complete suites include 31 new DemandInput unit cases and 29 new Module3 PostgreSQL cases, plus existing Auth/Google/email OTP/Admin/MFA/Seller/Discovery/CORS/payment/security coverage. The previously interrupted Admin provisioning case passed in 7 seconds with unchanged policy and assertions.

EF reports no pending model changes. Offline migration SQL was reviewed. Final sequential build (`18-final-build.log`) passed with zero warnings/errors. Diff whitespace check is clean; secret scan found zero patterns. Cleanup: zero new Testcontainers resources and zero backend API processes remaining. No implementation source edits followed the suite.

Frontend source hashes match the pre-Module3 manifest. The historical Module 2 migration, runtime grant template, production configuration and authentication architecture remain unchanged.

No production migration, protected Sprint 5.1 access, provider activation, environment change, frontend edit, commit, push or deployment occurred. Before rollout, review schema/backfill/index timing, approved service areas, migrator/runtime separation, old-instance draining and frontend contracts.

Verdict: MODULE 3 BACKEND READY FOR FRONTEND INTEGRATION.

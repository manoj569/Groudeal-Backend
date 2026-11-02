# Groudeal Backend Sprint 2.1 — seller frontend API gaps

Scope: existing backend and frontend repositories inspected together; no solution/module rebuild, new domain statuses, new dependency, deployment or push. Backend root: D:/Manoj/Group-Buy/Group-Buying Backend. Frontend root: D:/Manoj/Group-Buy/Group-Buying Frontend. Neither checkout exposes a Git repository at the supplied root, so no Git-clean/diff claim is made.

## Implemented contracts

1. GET /api/v1/seller/profile returns the current authenticated owner's private business/contact/registration/verification/location fields. Onboarding accounts do not need Seller approval to read themselves. Missing profile returns 404; anonymous access returns 401. Public seller DTOs are unchanged and still omit the sensitive fields.
2. PUT /api/v1/seller/products/{id}/status accepts only Active/Inactive. It checks the current approved active seller and product ownership under the existing seller-first lock. Deactivation is rejected while any live campaign, outstanding commitment, active reservation or pending/ready pickup depends on the product; activation requires an active category/variant. No-op retries do not duplicate audit. Added GET /seller/products with pagination/status filtering so inactive products can still be found and reactivated without relying on public visibility.
3. GET /api/v1/seller/pickups/{id} directly returns the existing operational DTO for an owned pickup. Cross-owner/missing returns 404. Codes, hashes, lockouts, code timestamps, collector identity, customer account IDs and contact data never enter this response.

The frontend's actual Seller, Product, Pickup, ProfileRequest and dashboard types/call sites were inspected. The new private catalog is flat Product fields plus status; public shapes are untouched. Profile reads now prefill contact/FSSAI/coordinates for Pending/Approved/Rejected owners. Status toggles use review dialogs, disable automatic mutation retry and refetch owned state. Active-product and active-pickup-location filters remain in campaign selection. Pickup detail now calls the direct endpoint and preserves server-authoritative 404 errors. Customer routes and code issuance/collection behavior are retained.

## Compatibility and operational rules

- Existing POST/PUT profile replacement still resets verification and appends a location. Location history has no current/recency marker; private reads use stable ID order, and the form prefers an active pickup address with a visible instruction to verify it. This sprint does not change the schema or silently invent an address order.
- Both Active → Inactive and Inactive → Active are valid only under the described eligibility conditions; same-state retries are idempotent. Unknown/null/missing enum values are invalid inputs, not additional lifecycle states.
- Draft references alone do not allocate inventory; a draft cannot submit while its product is inactive. Submitted/live campaigns block deactivation even if inconsistent legacy data lacks stock holds. Resolved terminal history does not permanently prevent status changes. Existing campaign-referenced variant metadata editing restrictions remain.
- Read-only seller endpoints retain ownership and Seller-role gates, while the private onboarding profile deliberately permits Customer-only owners. Current-role revocation and stale-tracked-approval fixes remain intact.
- Pickup nextAction retains existing customer-oriented strings for DTO compatibility; seller controls continue to use operational status/deadline. No GET issues, verifies or reveals a pickup secret.

## Files changed

Backend:

- src/GroupBuy.Application/SellerManagementContracts.cs (new private profile/location, seller product/status DTOs and validator).
- src/GroupBuy.Application/Ports.cs and OperationsContracts.cs (existing service ports extended).
- src/GroupBuy.Infrastructure/Marketplace.SellerManagement.cs (new focused partial implementation using existing transactions/DTO projections).
- src/GroupBuy.API/Endpoints.cs and OperationsEndpoints.cs (additive routes).
- tests/GroupBuy.IntegrationTests/SellerManagementTests.cs (new real PostgreSQL regressions).
- README.md, docs/api-contracts.md, docs/security-model.md and this report.

Frontend:

- src/seller/types.ts, data.ts (private profile/catalog contracts and query integration).
- src/seller/profile.tsx (private profile display/prefill).
- src/seller/products.tsx (owned inactive visibility and status review actions).
- src/seller/pickups.tsx (direct owned detail lookup).
- src/seller/campaigns.tsx (active owned product/location choices).
- e2e/seller.spec.ts (updated actual contract mocks and new browser regressions).
- integration/seller-backend.spec.ts (real backend profile prefill and reversible status coverage).
- docs/sprint2-seller.md and sprint2-verification.md (resolved gaps/current contracts and historical-results annotation).

Generated dist, cache, test-result and .local fixture files are ignored artifacts. Local edit helper scripts are not production changes.

## Verification

Backend tests use real isolated PostgreSQL 17.6 and the actual HTTP application. Database fixtures create/drop randomly named databases, applying the two existing migrations. The frontend real-backend runner uses its own newly initialized loopback database, applies docs/schema.sql, seeds only synthetic Development users, starts the actual compiled API and stops it/database in finally. Both isolated servers and the frontend fixture API stopped successfully. No existing user database is migrated or cleared.

| Check | Result |
| --- | --- |
| Backend build | Succeeded, zero warnings/errors |
| Full backend unit suite | 37 passed, no failures/skips |
| Full real PostgreSQL integration suite | 42 passed, no failures/skips; 13 new cases including the seven-status theory |
| EF has-pending-model-changes | No model changes; no new migration required |
| Existing migrations | Applied successfully to new backend test databases; existing generated SQL also applied to the separate real-browser fixture |
| Frontend TypeScript / ESLint | Passed |
| Frontend production build | Passed; existing large-chunk/upstream Zod annotation warnings retained |
| Frontend unit tests | 24 passed |
| Full production desktop/mobile browser suite | 40 passed, including all customer regressions and 6 new seller browser cases across both viewports |
| Frontend real backend browser tests | 2 passed: existing customer flow and expanded seller flow against the actual API/PostgreSQL |
| Production PWA offline-cache test | 1 passed; private API views remain unavailable offline |

All final test suites passed without retries or skips. Backend TRX artifacts are tests/GroupBuy.UnitTests/TestResults/sprint21-results.trx and tests/GroupBuy.IntegrationTests/TestResults/sprint21-results.trx; console log is ignored .local/sprint21-test.log. Frontend browser/fixture artifacts are under its ignored .local tree. No dependencies were changed and this sprint does not claim a new dependency-vulnerability certification. An initial missing Domain namespace import was corrected before the passing builds; the first full backend run passed 41 integration cases before the final additional guard regression was added.

Coverage includes all requested profile/authentication/status/invalid-status/cross-seller/pickup/secret cases, all seven live campaign statuses, commitment and outstanding pickup guards, current seller approval and concurrent deactivation versus submission. Existing Sprint 1/2 tests are retained. Browser coverage includes private Pending profile prefill, reversible inactive visibility and direct pickup 404 protection on desktop/mobile.

## Migration and remaining risks

No domain/database model changes; no migration is required. Existing migrations remain the schema source of truth. Production/live-data cutover restrictions from Sprint 2 remain unchanged.

The earlier .NET runtime/hosting/Admin MFA/compliance/backup gates remain; this targeted sprint does not certify production readiness. The frontend build retains existing bundle-size and third-party annotation warnings; prior dependency/tooling audit follow-ups are not superseded by this sprint. Location replacement semantics and protected Admin review-reason storage remain follow-ups. Owner catalog/workspace joins still page through full lists and should gain server-side search before a large catalog rollout. API authorization and product-commitment checks remain server-authoritative even when frontend controls are disabled.

**Readiness:** the three seller frontend contract gaps are closed and verified against real PostgreSQL and the actual browser integration. Ready for continued frontend/pilot development within the existing production-readiness restrictions. No push or deployment was performed.

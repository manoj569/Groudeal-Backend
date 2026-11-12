# Module 6 — My Orders / Participation

## Architecture

This module is a read projection, with no database/model/migration changes. Existing payment orders are stable checkout identities; captured orders link to reservations. Historical reservations without a linked payment order remain visible under their reservation ID. A linked reservation never creates a second card. References reuse the UUID in canonical D format.

The projection joins existing campaign, public catalog/package, seller business label, reservation, payment, pickup, fulfillment, and refund state. It does not invoke providers, workers, recovery, service-area eligibility, or public catalog eligibility. Historical records remain readable when current catalog/seller/payment/area configuration is unavailable. Existing Module 5 recovery, cancellation, secure pickup/pass issuance, verification, and redemption remain authoritative mutation paths.

## API

Customer role and JWT subject are required:

- `GET /api/v1/me/orders?page=1&pageSize=20&status=all&search=wheat`
- `GET /api/v1/me/orders/{id}`

The subject is derived from authentication; no caller-supplied customer identity is accepted. Ownership is applied to both payment and legacy branches, with additional ownership conditions on reservation/pickup joins. Missing or foreign IDs return the standard problem response with `order_not_found` and HTTP 404. Existing authentication handles disabled accounts and revoked roles.

The list returns items, page, pageSize, total, hasMore, summary counts and serverTimeUtc. Page is 1–100000; pageSize is 1–100 (default 20). Sorting is joined time, otherwise checkout creation time, descending, then UUID descending. Pending checkouts have no fabricated joined timestamp. Search is trimmed, limited to 100 characters, case-insensitive literal title/product substring or exact UUID reference. Search does not inspect private seller data. Status is case-insensitive; integer and unknown status values reject. Summary counts include all statuses within the same customer/search scope, before the selected status filter.

Each card includes package/type/unit, seller public label, customer quantity, group and tier progress, payment/commercial/refund summary, fulfillment readiness, cancellation eligibility and next action. Detail adds safe refund records and a lightweight timeline. Neither endpoint returns addresses, payment/provider IDs, idempotency keys, pickup/pass secrets/hashes, raw audit logs, or private operational notes.

## Projection status precedence

`ParticipationStatus` is a read-only classification, never persisted or used to mutate business state. Its expression is shared by SQL filters/counts and unit tests:

1. Refunded: verified capture, full authoritative refunded amount, and payment Refunded.
2. Completed: this customer's completed reservation and verified collected pickup; paid records also require delivered fulfillment and redemption timestamp. Own completion takes precedence over a later campaign failure.
3. Cancelled: cancelled/expired reservation, closed/failed campaign, cancelled/refund-required fulfillment, zero final amount, or expired unpaid checkout.
4. PaymentFailed: authoritative failed payment.
5. PaymentPending: no qualified reservation/capture; authorization alone is insufficient.
6. Ready: qualified participation with a future pickup deadline, supported own fulfillment ready state, and no incomplete refunds.
7. Confirmed: actual successful/confirmed/preparing/fulfillment campaign state.
8. Active: qualified participation while group building.

Required tab filters are All, Active, Confirmed, Ready, Completed, Cancelled, Refunded. PaymentPending and PaymentFailed are additional explicit tabs to prevent unpaid checkouts appearing Active. Reaching a minimum tier or payment capture alone does not mean campaign confirmation.

## Progress and money

`GroupDealRules.Progress` is reused without alternative arithmetic. Quantity is `FinalQuantity ?? ReservedQuantity`, never seller allocation. Threshold 10 is current at quantity 18, threshold 20 is next, needed 2. Current price is final price when available, otherwise reached tier, otherwise first tier price, with legacy fixed-price fallback. Final tier is the reached tier at final quantity when finalized pricing exists.

Currency is INR. Snapshot unit/subtotal, original maximum, captured amount, authorization, final amount, delivery fee and refunded amount come from existing records. Legacy nonfinancial reservations show quoted snapshots but null paid/authorized/maximum amounts and `not_tracked` payment/refund status. Payment status uses Module 5 `CheckoutRules.Status`. Pending/failed next action `check_payment` maps to existing `GET /api/v1/me/checkouts/{id}` and its recovery flow; history creates no payment order and performs no reconciliation.

Refund summary distinguishes `none`, `pending`, `partially_refunded`, `refunded`, `requires_attention`, and legacy `not_tracked`. Incomplete refund rows are checked independently of payment state. Exhausted refunds require attention. A completed tier-price refund leaves valid participation in its appropriate Active/Confirmed/Ready/Completed section. Cancellation alone does not imply refunded. Refund reasons are deliberately generalized to `refund`, with no private notes/provider metadata.

## Fulfillment and actions

Existing SellerPickup physical orders are `seller_pickup`; FounderDelivery orders are `founder_delivery`. Service/membership orders use configured `seller_redemption`, suppressing grocery pickup status. There is no new logistics, entitlement activation, or production area configuration.

Paid readiness uses ReadyForCollection for seller pickup/redemption and OutForDelivery for founder delivery. Seller-ready stock is not customer-ready founder delivery. Legacy readiness requires campaign FulfillmentInProgress and pickup ReadyForPickup. All readiness requires future deadline and no incomplete refund. Pass-request availability additionally respects the existing one-minute issuance cooldown and verification lock. Issuance still occurs only through existing secure endpoints; no credential appears here. Detail fields do not authorize a mutation; the target endpoint always revalidates.

Existing paid cancellation is surfaced while financial campaign Open/MinimumReached before expiry and final amount is not zero. Existing legacy cancellation requires Active reservation and Open/TargetReached campaign before expiry. Actions use the existing checkout ID or legacy reservation ID; no cancellation route is added. Paid cancel: `POST /api/v1/mvp/customer/orders/{id}/cancel`; legacy cancel: `POST /api/v1/me/reservations/{id}/cancel`. Pass actions use fulfillment ID at the existing `/api/v1/mvp/customer/fulfillments/{id}/pass`; legacy code uses existing pickup APIs.

Next-action codes: check_payment, wait_for_group, wait_for_fulfillment, wait_for_delivery, view_pickup_pass, view_delivery_pass, view_redemption_pass, refund_processing, contact_support, completed, no_action. Server time accompanies deadline-dependent projections.

## Timeline and limitations

Timeline includes only trustworthy recorded timestamps: checkout/reservation creation, captured payment, joined reservation, campaign finalization, explicit cancellation, verified fulfillment completion, and refund initiation. Refund RecordUpdatedAtUtc is a record timestamp, not a promise of settlement/completion timing. Historical cancellation flows that did not record CancelledAt remain null. Exact ready/target/refund-completed event times are deferred; raw audits are not exposed or scanned. No new event persistence was introduced.

Catalog has no appropriate image field, so no image is invented. Fulfillment remains the existing seller redemption/founder delivery model; purely digital entitlements are not invented. Current catalog names/labels are returned because historical immutable catalog snapshots do not exist. Offset pagination is deterministic within a snapshot; concurrent changes between requests can move records between pages, consistent with repository conventions.

## Query and security review

Each nonempty list executes three bounded reader batches: grouped counts, filtered/ordered SQL page, and tiers for distinct campaign IDs in that page. Detail executes three: owned projection, tiers, refund records. Repeatable-read transactions keep each response internally consistent. There is no per-item database/provider lookup. Scalar selection excludes secrets and private columns. Both branches are owner filtered before UNION ALL; refund existence predicates use the existing payment-order index. Existing customer/payment/reservation, pickup, fulfillment and campaign-tier indexes are reused; no speculative index or migration was added.

Focused PostgreSQL tests cover query counts at different page sizes and EXPLAIN the actual parameterized page query. The local query plan is written under `.local/module6-query-plan.json`. Small fixture plans are not production-scale benchmarks; title substring search and summary counts scale with the customer's history and should be monitored before large-scale deployment.

## Validation

Tests are in ParticipationTests.cs, Module6MyOrdersTests.cs and Module6ProjectionTests.cs. They cover projection precedence, all status filters/counts, ownership/IDOR, types, pending/paid state, tier boundaries, privacy, search, invalid paging/filter input, deterministic tied timestamps, cancellation, partial refunds, pass cooldown and completed redemption, legacy history and constant query batches. Full repository regression, restore/build, model/diff/secret gates must pass before a readiness verdict. All PostgreSQL validation uses fresh disposable fixtures, never the protected Sprint 5.1 or production resources. No frontend, production settings, grants, deployment, commit, or push is part of this module.

## Changed files

- API registration and routes: `src/GroupBuy.API/Program.cs`, `src/GroupBuy.API/MyOrdersEndpoints.cs`.
- Read contracts/rules/query: `src/GroupBuy.Application/MyOrdersContracts.cs`, `src/GroupBuy.Domain/ParticipationRules.cs`, `src/GroupBuy.Infrastructure/Marketplace.MyOrders.cs`.
- Tests: `tests/GroupBuy.UnitTests/ParticipationTests.cs`, `tests/GroupBuy.IntegrationTests/Module6MyOrdersTests.cs`, `Module6ProjectionTests.cs`, `Module6StateTests.cs`, `Module6AuthorizationTests.cs`.
- Existing regression test: `tests/GroupBuy.IntegrationTests/PhaseOneTests.cs` scopes its delivery queue assertion by campaign, rather than assuming the shared first page contains every test record. Address privacy, pricing, pass verification and handover assertions remain intact.
- Validation: `scripts/verify-module6-readiness.ps1` and this document.

The readiness script restores previous process environment settings and disables NuGet vulnerability feed auditing only for the local run; it does not change repository audit policy. Dependency restore/build validation does not constitute an online vulnerability audit.

## Final local validation — 2026-11-11

The complete clean gate passed in `.local/module6-gate-20261111-c`:

- Restore and build: passed, zero warnings/errors.
- Full unit suite: 187/187 passed, including 9 Module 6 status cases.
- Focused Module 6 PostgreSQL suite: 15/15 passed.
- Full PostgreSQL suite: 359/359 passed, including all 344 existing cases.
- EF pending-model-change verification: no model changes since the last migration.
- Diff/whitespace and changed-file secret-pattern review: clean.
- Query-count and actual PostgreSQL EXPLAIN coverage: passed.
- New disposable containers and API processes remaining: zero.

Earlier failed runs are preserved. The first complete attempt had 357/359 passing: an existing delivery assertion assumed its record appeared on the shared first page, and a payment-recovery test's MFA challenge expired during a host pause. The delivery test now scopes its lookup to its own campaign. The clean full rerun passed both cases without changing authentication deadlines or security behavior. Temporary process-scoped system-sleep prevention was released after validation; saved power settings were not changed.

No commits, pushes, deployment, frontend edits, production access/configuration changes, or protected Sprint 5.1 resource operations were performed. Online NuGet vulnerability-feed auditing was not performed; this remains separate from restore/build and the secret/configuration review.

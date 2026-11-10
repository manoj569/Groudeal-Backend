# Module 5 â€” Join Group / Checkout backend

## Architecture audit and reuse

Baseline: `742c6f6 feat: implement Module 4 group deal marketplace`. Existing architecture includes seller-owned catalog products/variants, campaigns and price tiers, inventory/capacity allocations, reservations, payment orders, webhook deduplication, provider reconciliation, refunds, fulfillment/pass redemption, notifications, audit records, Admin MFA, customer authorization, discovery areas and demands/interests. All those models and workflows remain authoritative.

The new `ICheckout` interface is implemented by the same `Marketplace` partial service. `CheckoutSelectionFor` is shared by quotes, confirmations and the established paid-order endpoint. `JoinCore` retains the existing order/hold transaction and provider creation lease. There is no duplicate payment, reservation, quote database, address book, refund, notification or pickup subsystem.

## Checkout flow and APIs

All new routes require the Customer policy; identity comes from authenticated claims, never request IDs. Seller-only tokens are rejected. Users legitimately having both Seller and Customer roles can act as customers for their own checkouts.

1. POST `/api/v1/group-buys/{id}/checkout/quote` with `{quantity, fulfillmentMode, address?, productVariantId?}`.
2. Review the server summary, maximum initial payable amount and five-minute quote expiry.
3. POST `/api/v1/group-buys/{id}/checkout/confirm` with `{quoteId}` only.
4. The existing payment order/provider flow runs. A successful confirmation response allocates a checkout hold; it does not mean payment succeeded.
5. Use existing POST `/api/v1/mvp/customer/orders/{id}/confirm` for the provider-verified callback. Existing signed webhook and reconciliation flows remain the trusted authority. Browser success flags are never accepted.
6. GET `/api/v1/me/checkouts/{orderId}` for stored status. It is read-only and never contacts a provider or creates an order.
7. POST `/api/v1/me/checkouts/{orderId}/recover` to request bounded trusted reconciliation of that existing order.
8. If the browser lost the order ID but retained the quote, POST `/api/v1/group-buys/{id}/checkout/recover` with `{quoteId}`.

The existing customer order list `/api/v1/mvp/customer/orders` can recover order IDs when all browser checkout state is lost. Existing own order cancellation, finalization, refund, fulfillment and Pickup Pass routes are reused.

## Quote authority and security

A quote is an authenticated, encrypted ASP.NET Data Protection token with a versioned purpose bound to customer ID and campaign ID. Its protected payload contains a random operation ID, customer/deal identity, selections, material-configuration fingerprint and expiry. It is not a bearer credential: authentication and matching ownership are required. Tampering or use by another customer/deal fails without exposing token contents. Never put quote tokens in URLs or logs.

No quote row or capacity hold is created. Confirmation obtains the existing seller -> campaign PostgreSQL locks and validates current resources, quantity, capacity, fulfillment, address, provider availability, refundability, expiry and material fingerprint before inserting the existing PaymentOrder. The token's prices are never used to charge money; all amounts are recomputed from current database/configuration values.

Material fingerprint fields include immutable catalog/package data, seller/location, campaign limits/dates/terms, full ordered tiers, reached current tier, delivery fee, service-area postal configuration and public provider identity/refund minimum. A tier crossing or fee/terms/configuration change produces `checkout_quote_expired` and requires a new quote. Committed quantity changes within the same reached tier need not invalidate a quote; capacity is still rechecked. Progress and available capacity in a quote are issuance-time snapshots.

The quote expires at the earlier of five minutes or campaign deadline. An accepted order has the existing hold deadline (at most ten minutes and never later than campaign close). Quote expiry cannot create another order or prevent retrieval of an already-confirmed operation. Existing operations are returned before new-quote expiry/configuration checks, including verified payments when payments are subsequently disabled.

Production relies on the existing persisted, encrypted Data Protection key ring and stable application discriminator. Key loss/retirement may make an old token unreadable; owned order history/status remains the recovery path. No new encryption secrets are introduced.

## Quantity, package and tiers

Each campaign fixes one catalog variant/package. The optional ProductVariantId must match; choosing another package means choosing its corresponding deal. Physical products retain decimal bulk quantities or whole Piece/Pack quantities. Service/membership offers retain Module 4's single-entitlement quantity and seller redemption, with no fake inventory or recurring subscriptions.

The server computes the highest actually reached tier and first unreached next tier from accepted captured reservation quantity, excluding demand interest, quotes, pending holds and authorizations. At 18 with thresholds 10/20/30, current=10, next=20 and remaining=2. Below the first threshold currentTier is null, while CurrentUnitPrice shows the existing base payable/projected price. Near-target rescue remains the existing separately disclosed policy; demand-linked deals keep exact thresholds.

## Amounts and maximum authorization

Currency is INR; monetary totals are integer minor units (paise). `CurrentUnitPrice` and `ProjectedSubtotalMinor` show current group economics. `MaximumUnitPrice` is the existing first/highest tier price. `SubtotalMinor`, `TotalMinor` and `MaximumAuthorizedAmountMinor` describe the initial payable amount using that maximum, with Total = Subtotal + backend delivery fee. They intentionally do not promise an immediate lower-tier charge.

Existing model B is preserved: capture the maximum initial amount, finalize the group, then queue a deterministic refund of the lower final tier difference. Provider minimum-refund restrictions are checked before any order/provider call. PaymentOrder persists maximum unit price/amount, quantity, delivery fee, final amount, refund totals, provider references and timestamps. CheckoutConfirmed audit metadata records the internal quote operation ID, not the protected token or address.

## Fulfillment and addresses

Physical products support the existing free seller pickup and founder delivery when configured. Service/membership entitlements use existing seller redemption; delivery is rejected. Existing QR, backup code, verification, one-time redemption, audit and authorization are unchanged.

The existing encrypted PaymentOrder delivery-address snapshot is sufficient for this checkout. Inline details are validated against the campaign's configured area/postal codes and stored only with the authenticated order owner, using the existing protection purpose/digest. There is no saved-address ID input or cross-customer address lookup/update route. Unexpected address/customer/seller/amount fields are rejected by existing strict JSON binding. Quote tokens also encrypt address selections, avoiding a plaintext quote store.

Missing/unapproved service-area configuration produces `503 service_area_unconfigured` through DiscoveryAreas. Disabled payment/pilot configuration produces `503 payments_unavailable`. No locality, PIN, map, GPS or radius is invented. The current disabled/unconfigured production posture remains fail-closed. Existing startup validation also continues to reject invalid pilot/provider configurations.

## Idempotency and concurrency

The protected random quote operation ID maps deterministically to existing PaymentOrder.IdempotencyKey (`checkout-<guid>`). The customer cannot change it at confirmation. Same-token concurrent confirmations return the same durable order; there is no second key to vary for the same quote. A different quote cannot duplicate an active commitment/hold for the customer in the new checkout flow. Legacy unlinked physical multi-order behavior remains available through its existing API.

Seller-first then campaign FOR UPDATE locks serialize capacity decisions. Pending holds plus captured reservations cannot exceed allocated/target capacity. Quotes never reserve stock; competing quotes can therefore both be valid, but only capacity-consistent confirmations can succeed. PostgreSQL constraints, campaign xmin, unique order/provider/reservation keys and receipt-based provider recovery are preserved.

Order and CheckoutConfirmed audit commit before remote provider creation. Existing persisted provider creation leases, receipt identity, webhook IDs, capture handling, refund reasons and finalization markers prevent duplicate financial effects. A remote creation response loss returns processing state with the durable order identity when possible; retries/recovery reuse the same provider receipt. Client disconnect can lose the HTTP response, but cannot discard the committed operation.

## Payment lifecycle, recovery and errors

Only verified provider state creates a reservation and advances group progress. A pending or authorized order remains unqualified. Capture after hold expiry/cancellation/closure follows existing full-refund handling. Fulfillment is created by existing successful group finalization, not fabricated during quote/authorization.

Status exposes existing order state, machine status/code, ParticipationConfirmed, recovery readiness/time, operator escalation and owned refund summaries with provider refund references and timestamps. Codes include `payment_pending`, `payment_failed`, `payment_already_completed`, `reservation_expired`, `refund_pending` and `refund_completed`. Quote/selection failures include `checkout_quote_expired`, `checkout_quote_not_found`, `deal_unavailable`, `capacity_unavailable`, `invalid_quantity`, `invalid_package`, `fulfillment_unavailable`, `address_not_serviceable` and existing service-area/provider codes.

Recovery runs the existing fenced `payment_reconciliation` pilot job, preserving leases, backoff, finite retry budgets and audited Admin-only exhausted-job reset. It never resets budgets or allocates a different payment order. Reconciliation may complete a previously authorized payment only under the existing CanCapture eligibility rules; it does not create a new customer charge merely because the browser lost its response. Already verified captured/refund states are readable/recoverable without a new provider operation. Failed/expired operations remain distinct; retrying the old quote never silently starts another payment. A deliberate new checkout requires a fresh quote.

Refunds retain asynchronous worker processing and operator recovery. Partial refunds, full failure/cancellation refunds, provider idempotency and settlement-before-redemption rules remain unchanged. This API reports queued/pending/completed refunds, never an invented instant refund guarantee.

## Security, notifications and audit

Existing TLS/provider verification, production activation gates, authentication, customer ownership, seller ownership, Admin MFA, strict JSON and rate limits remain in place. Quote/confirm/recover mutations use the existing checkout limiter; status reads use the existing global limit. Provider secrets, JWTs, raw quotes and address text are never audit/log metadata. CheckoutKeyId is the provider's public checkout key only.

The new confirmation appends CheckoutConfirmed once in the same order transaction. Existing PaymentOrderCreated, PaymentCaptured/Failed, hold expiry, finalization, refund and fulfillment audit/notification events are reused. No duplicate notification subsystem or automatic conversion of free demand interest is added.

## Database, migrations and runtime grants

No schema change is needed. No migration, snapshot edit, new table, production grant or new runtime privilege is introduced. Existing table SELECT/INSERT/UPDATE, audit append, Data Protection key access and financial worker permissions suffice under the reviewed runtime template. Migrator/runtime separation is unchanged. EF pending-model detection and the existing migration regression suite remain quality gates.

Protected Sprint 5.1 container/volume/network/database/roles and endpoint 127.0.0.1:55432 are excluded from testing. No production/Neon connection, credential activation, migration, deployment, push or commit is part of this work.

## Tests and local verification

New unit tests cover quantity precision/bounds, discrete packages and trusted status precedence. New PostgreSQL tests cover all three deal types, authoritative amounts/tier progress, side-effect-free quotes, owner/deal binding, tampering, strict JSON, package/quantity rejection, expiry/requote, unchanged accepted-order recovery after quote expiry, two competing final-capacity confirmations, encrypted inline addresses, server fees/configuration changes, disabled/unconfigured behavior, unknown payment recovery, refunds, user cancellation, role isolation and provider response loss with a single receipt/order.

Run `scripts/verify-module5-readiness.ps1` for restore, build, full unit/integration suites, pending-model detection, diff/secret review and disposable-resource cleanup. All database fixtures are fresh Testcontainers resources; the runner removes shared database overrides. Validation results and any environment-specific limitations are recorded at completion below.

## Known limitations / deferred work

No cart/multiple packages within one campaign, saved address book, installation scheduling, subscription billing, new pickup system or delivery network is added. Existing two-day fulfillment/redemption policy remains. Quotes are intentionally non-reserving and expire; a customer must reconfirm an updated quote when material conditions change. A protected quote is not a permanent order record. Keep confirmed order IDs or recover through authenticated order history. Live credentials and real service areas still require separately authorized operational approval; passing local tests does not establish production readiness.

## Changed-file inventory

- `docs/module5-join-group-checkout-backend.md`
- `scripts/verify-module5-readiness.ps1`
- `src/GroupBuy.API/CheckoutEndpoints.cs`
- `src/GroupBuy.API/Program.cs`
- `src/GroupBuy.Application/CheckoutContracts.cs`
- `src/GroupBuy.Domain/CheckoutRules.cs`
- `src/GroupBuy.Infrastructure/Marketplace.Checkout.cs`
- `src/GroupBuy.Infrastructure/Marketplace.PhaseOne.cs`
- `tests/GroupBuy.IntegrationTests/Module5CheckoutTests.cs`
- `tests/GroupBuy.UnitTests/CheckoutTests.cs`

## Final validation — 11 November 2026

Evidence directory: `.local/module5-final-20261110/` (named for the run's start date).

- Restore and build succeeded with 0 errors and 0 warnings.
- Unit suite: 178 passed, 0 failed/skipped (19 new checkout unit cases).
- Complete integration run: 344 executed, 343 passed; one existing Module 3 fixture setup encountered an Npgsql read timeout after an unusually long, nearly 57-minute case duration. All 18 Module 5 cases passed in that full run.
- The affected Module 3 class was rerun without rebuilding or changing test limits: 29/29 passed, including the exact previously failed case. API/infrastructure/test binary SHA-256 manifests before and after the rerun match. `validation-summary.json` verifies that all 344 distinct integration cases have passing latest outcomes; it does not overwrite or hide the original failed TRX.
- The strict all-in-one script stopped at the original full-run timeout; its remaining model/diff/security/cleanup checks were completed explicitly. This is not represented as an uninterrupted all-green gate run. Original results are `04-integration.trx`; the regression rerun is `04b-demand-rerun.trx`.
- EF reports no pending model changes; no Module 5 migration is required. Existing migration regression cases were included in the full suite.
- `git diff --check` passed. Ten changed files were reviewed: zero secret-pattern or production-connection-pattern findings.
- Final inventory: zero labelled Testcontainers resources and zero checkout API processes remain.

NuGet vulnerability-feed retrieval was unavailable during earlier local work, so verification was invoked with process-local `NuGetAudit=false`; no repository audit/security policy was disabled and no dependency packages were added or upgraded. This is not a successful online dependency-vulnerability audit.

No production or protected Sprint 5.1 resource, production grant, service-area setting, live credential, TLS policy or frontend source was changed. HEAD remains `742c6f6`; changes remain uncommitted and unpushed. No deployment was performed.

Readiness: MODULE 5 BACKEND READY FOR FRONTEND INTEGRATION. This does not approve production activation.

# Phase 1 financial and fulfillment controls

For the current controlled Test Mode activation gates, exact environment variables,
startup restrictions and operational limitations, see
[controlled pilot activation](operations/controlled-pilot-activation.md).
That audit supersedes the older activation summary below: Sandbox is restricted
to Development/Testing, and an enabled pilot outside those environments requires
enabled Razorpay Test Mode payments. The [reliability follow-up](operations/backend-reliability-gate-closure.md)
adds persisted expiry retry budgets, operational queues and versioned MFA Admin
retry. Use its final validation results for the current code-level decision.

Production readiness remains **NO-GO**. No live merchant transaction or real geographic pilot is activated by these changes.

## Activation

Defaults are disabled: `Pilot:Enabled` and `Payments:Enabled` must be explicit. No payment credentials, real postal codes or delivery fee defaults are shipped. Non-development activation requires approved real six-digit postal codes (`Pilot:Approved`) and an explicit fee plus `Pilot:DeliveryFeeApproved`. Development tests use only `synthetic-area` / `TEST01` and an explicitly set 3000-paise test fee. Those values do not authorize a pilot.

Razorpay requires `Payments:Provider=Razorpay`, explicit `Payments:Razorpay:Mode` (`Test` or `Live`), matching key-ID prefix, secret-manager supplied `KeyId`, `KeySecret`, `WebhookSecret`, and optional merchant `AccountId` verification. Live mode additionally requires `Payments:Razorpay:LiveApproved`. Credentials/merchant activation and real Test Mode acceptance remain operator gates. Never put credentials in Git, frontend bundles, command-line arguments or reports.

Sandbox requires explicit `Payments:Provider=Sandbox`, `Payments:SandboxEnabled`, and a process/configuration supplied random `Payments:SandboxWebhookSecret`. It is forbidden in Production. Its durable simulated external ledger is independent of the business transaction, including refund operation IDs, so crash/retry behavior can be tested without real money.

## Financial invariants

* Seller-first, then campaign row locks serialize checkout holds, capacity changes, cancellations, provider results, finalization and handovers. Ten-minute holds count against availability, never against achieved/paid quantity.
* Only authoritative `captured` provider payments create reservations. Authorized, pending, failed, unsigned and browser-supplied states do not count. Released/expired holds cannot reclaim capacity; a late captured payment is refunded.
* Authorizations are actively captured only while the local checkout hold is valid. Already-captured late funds still reconcile. Cross-system capture/finalization races resolve through durable refunds rather than overselling.
* Idempotency keys are scoped to customer/campaign with immutable quantity, mode and address digest. Provider orders reuse a durable local receipt; remote calls happen outside business transactions under a persisted creation lease. Unknown outcomes retry the original operation.
* Raw-body HMAC-SHA256 verification and fixed-time comparison precede webhook acceptance. The inbox stores provider/event/order IDs and a body hash, not raw payment/customer payloads. Durable unique event IDs reject changed replay bodies. Out-of-order events fetch current provider state.
* Maximum price and separate delivery fee are server snapshots. Finalization freezes the highest achieved tier. Difference refunds and failed/cancelled/expired fulfillment refunds return to the original captured payment; there is no wallet, escrow or balance-credit substitution.
* Checkout validates all potential final totals and tier differences against the provider's refund floor before creating an order. Razorpay normal refunds have a documented 100-paise floor; unsupported tiny quantities/differences are rejected before money is taken. The isolated sandbox supports single-paise synthetic cases. Post-finalization cancellation reduces outstanding paid quantity but does not retroactively reprice other customers; the original final quantity remains an immutable pricing snapshot.
* Refunds use stable local IDs, receipt lookup and Razorpay `X-Refund-Idempotency`. Provider success before a local crash is recovered with the same operation key. Unknown or pending refunds never masquerade as completed. Bounded failures surface to Admin for audited retry/reconciliation.
* Financial records and recorded seller handovers block destructive migration rollback. Review/archive/restore requires an explicit operator procedure. The additive migration is applied only to disposable PostgreSQL by tests; runtime receives no DDL, migration-history writes or audit-update/delete privileges.

## Fulfillment

Passes contain 256 random bits, no PII/payment information or predictable identifier. Their SHA-256 lookup is indexed and ownership-filtered; the record binds state, expiry and regeneration. The six-digit backup/delivery code uses a purpose-derived HMAC key and fulfillment/version binding, protecting against database-only offline enumeration. Only hashes are stored. Codes expire within ten minutes and the fulfillment deadline; regeneration has a one-minute cooldown and preserves lockouts. Five failures lock verification for fifteen minutes. Named pickup and checkout rate limits also apply.

Verification creates a two-minute grant bound to the same authenticated seller/founder. Handover is a separate locked transaction. Redemption clears pass hashes/grants and checks reservation/stock; concurrent duplicates cannot consume stock twice. Seller pickup is free. Only MFA-authenticated Admin/founder can collect from seller, move delivery states, verify a delivery OTP or manage delivery exceptions.

Physical stock is consumed once when the founder collects from the seller, or when the customer collects a seller pickup. A failed delivery refund does not pretend those goods returned to the shelf. Restocking requires an actual verified return and an explicit inventory declaration.

Delivery addresses use the existing Data Protection key ring with a separate purpose. Customer and authorized Admin receive necessary address fields; sellers receive none. Passes, OTPs and checkout credentials remain in process/browser memory only. No API responses are added to the PWA cache. Never log raw provider errors, webhook bodies, checkout signatures, addresses, Authorization headers, secrets or passes. Background errors record bounded stage/entity/type classifications only.

## Operations and limits

The existing worker also persists checkout-hold expiry, opens scheduled paid campaigns, reconciles payments/webhooks, finalizes due campaigns, expires uncollected fulfillment and retries refunds in bounded batches with cancellation propagation. Hold expiry/finalization/refund queuing continue if the provider is disabled; outbound calls await provider activation. Durable state survives worker/API restarts; the durable sandbox regression simulates remote refund success followed by local process loss. Exhausted payments/refunds require an audited Admin retry; do not manually mark money paid/refunded. Merchant-console operations outside this flow require explicit operator reconciliation and are not a substitute for the supported refund procedure.

In-app notifications are durable; this phase does not claim SMS/WhatsApp delivery. Sharing opens an explicit user-controlled WhatsApp link containing only public campaign title/URL. Metrics report historical funding participation, interest-to-paid conversion for the same product/locality, final price-tier distribution and fulfillment completion. No PII is exposed in aggregate demand.

Operator activation must also confirm Razorpay merchant-specific capture/refund rules (including minimum refund amounts and pending-refund handling), webhook delivery and real Test Mode reconciliation. Synthetic/fake-HTTP tests cannot establish live merchant readiness. Preserve strict TLS; never trust Avast interception roots or use certificate-ignore switches. No Sprint 5.1 resource is used, restarted, mounted or migrated, and completed Sprint 5.2 evidence is retained.

Provider references: [Razorpay order filters](https://github.com/razorpay/razorpay-java/blob/master/documents/order.md), [normal refund idempotency](https://github.com/razorpay/markdown-docs/blob/master/api/refunds/normal-refunds-idempotent.md), and [webhook signature/retry guidance](https://razorpay.com/blog/payment-gateway-webhooks-india).

# Backend controlled pilot activation — 2026-11-05

## Decision and scope

The subsequent [reliability gate-closure report](backend-reliability-gate-closure.md)
supersedes the worker retry/visibility limitations recorded in this original audit.
Its final validation determines code-level readiness; the external activation
inputs and Test Mode acceptance procedure below still apply.

Activation remains **NO-GO pending approved configuration and controlled Razorpay Test Mode acceptance**. The reliability follow-up records code-level acceptance readiness and closure of the worker gates. This is not production approval. Backend only. No real locality, fee, credentials or external acceptance is approved by this document. No Sprint 5.1 operations or Sprint 5.2 staging migrations are authorized. Preserve existing worktree changes; do not commit, push or deploy.

## Exact configuration contract

Supply values through the process environment or the authorized secret store. Outside Development, the host also loads `/run/secrets` with .NET key-per-file configuration; filenames use the same double-underscore names. Never paste secrets into chat, source, shell history, logs, screenshots or evidence. Key ID is a public checkout identifier; Key Secret and Webhook Secret are secrets. There are no committed credentials or approved postal codes/fee in this change.

| Environment variable | Required value / meaning |
| --- | --- |
| `ASPNETCORE_ENVIRONMENT` | Approved isolated hosting environment; Development/Testing permit synthetic fixtures. Other environments enforce real-area/fee approvals and existing host security. |
| `Pilot__Enabled` | `true` only when all gates are approved; otherwise absent/false. |
| `Pilot__Approved` | `true` after explicit service-area approval outside Development/Testing. |
| `Pilot__ServiceAreas__0__Id` | Approved stable lowercase slug, 1–80 characters. |
| `Pilot__ServiceAreas__0__Name` | Approved display name, 1–100 characters. |
| `Pilot__ServiceAreas__0__PostalCodes__0` | Approved postal code; six digits beginning 1–9 outside Development/Testing. |
| `Pilot__DeliveryFeeMinor` | Explicit approved integer paise, 0–100000 inclusive; no fallback. |
| `Pilot__DeliveryFeeApproved` | `true` after explicit fee approval outside Development/Testing. |
| `Payments__Enabled` | `true` for acceptance; otherwise absent/false. |
| `Payments__Provider` | `Razorpay`. |
| `Payments__Razorpay__Mode` | `Test`. Live is rejected for enabled pilot outside Development/Testing. Never supply Live credentials for this procedure. |
| `Payments__Razorpay__KeyId` | Test key ID beginning `rzp_test_` with an alphanumeric suffix. |
| `Payments__Razorpay__KeySecret` | Secret paired with that Test key; at least 16 characters. |
| `Payments__Razorpay__WebhookSecret` | Separate webhook signing secret; at least 16 characters. |
| `Payments__Razorpay__AccountId` | Optional expected provider account ID; recommended to bind the webhook to the intended account. Omit if not configured; do not set an empty string. |
| `Payments__Razorpay__LiveApproved` | Leave absent/false. |

Repeat zero-based area/postal indices for additional entries: 1–10 areas, 1–30 codes per area. Syntax validation does not establish business approval. Synthetic `TEST01` fixtures belong only to Development/Testing. Sandbox provider is restricted to those environments and requires `Payments__SandboxEnabled=true` and `Payments__SandboxWebhookSecret` (32+ characters); do not configure these for acceptance.

The webhook URL is **not an application environment variable**: configure the Razorpay Test dashboard with `https://<approved-public-api-host>/api/v1/mvp/payments/webhook`. The route is fixed. The API base URL is fixed to `https://api.razorpay.com/v1/`; mode is selected by the credentials, not by another endpoint. There is no configurable capture flag: the backend captures authorized payments only while its business hold is valid.

Existing hosting requirements remain: `ConnectionStrings__Database` (isolated runtime role), `Jwt__SigningKey`, `Jwt__Issuer`, `Jwt__Audience`, `AllowedHosts`, explicit `Cors__Origins__0` if needed, and outside Development persistent `DataProtection__KeyPath`, `DataProtection__CertificatePath`, `DataProtection__CertificatePassword`, approved TLS/proxy configuration (`Proxy__KnownProxies__0`). Do not repurpose staging credentials. Existing production Compose does not wire the pilot variables/secrets: an operator must prepare and review target-specific environment/secret injection before activation. An arbitrary `.env` file alone is not loaded by the API or automatically passed through Compose.

## Webhook setup

Use a publicly reachable HTTPS endpoint with a valid certificate and reviewed proxy/host settings. In the **Test** dashboard subscribe to `payment.authorized`, `payment.captured`, `payment.failed`, and `order.paid`. Preserve raw request bytes and `X-Razorpay-Signature` / `X-Razorpay-Event-Id`; no JSON rewriting. The endpoint has no bearer requirement; raw-body HMAC is its authentication. A 200 means durable event receipt, not paid commitment. The worker fetches authoritative provider state before applying money. Refund events are not consumed for settlement; refunds are polled.

Invalid signatures must return 401 and persist nothing. Unknown signed event kinds are recorded as ignored. Identical event ID/body redelivery is safe; same ID/different signed body conflicts (409). Records store hashes/order references rather than raw webhook PII. Keep the signing secret stable during this acceptance; secret rotation needs an explicit old-delivery/retry plan because only one secret is currently accepted.

## Architecture and security audit

| Area | Observed implementation / acceptance implication |
| --- | --- |
| Order creation | Server computes maximum tier product amount plus separately stored delivery fee. Durable local order precedes remote I/O; 45-second lease, stable receipt recovery and ambiguous-order rejection. Provider amount/currency must match. Receipt lookup is recovery, not an atomic provider order idempotency guarantee; explicitly test response-loss recovery. |
| Callback | Customer ownership, HMAC over stored provider order ID and supplied payment ID, provider fetch/capture, then order/amount/INR checks. Duplicate captured confirmation cannot add a second reservation. |
| Webhook/replay | Fixed-time HMAC, durable unique provider/event ID, payload hash conflict detection and transaction advisory lock. Replayed signed content cannot directly award quantity; reconciliation controls state. No timestamp freshness rejection; durable identity and authoritative fetch are the replay controls. |
| States/retry | Authorized is not paid. Only captured funds create a reservation. Failed attempt does not count toward quantity. A new checkout uses a new idempotency key; reusing an existing key returns the existing operation. Pending/failed/expired orders continue provider reconciliation while failure budget remains. |
| Capacity/expiry | Campaign/seller locks protect holds and captured commitments. Ten-minute checkout hold capped at campaign deadline. Late capture after expiry/cancellation/finalization/resource failure queues a full source refund and contributes no quantity. |
| Final tier | Uses captured active reservation quantity, immutable tiers and transactional finalization. Failed minimum refunds entire payment including delivery; successful tier refunds product difference while retaining snapshotted fee. Refundability rejects sub-INR-1 differences before charging. |
| Refunds | Unique order/reason, stable refund UUID receipt and `X-Refund-Idempotency`, persisted lease and attempts. Provider status `processed` alone credits refunded amount; pending/failed remain unresolved. Recovery now scans up to 1000 refund records, then fails closed. No manual new refund key on timeout. |
| Authorization | Customer order ownership, seller campaign/fulfillment ownership and eligibility; Admin role plus MFA for operational/refund/retry routes. Founder delivery handover/OTP verification requires Admin. |
| Pickup Pass / OTP | Random opaque 256-bit QR token; keyed backup-code hash bound to record/version. Short expiry, regeneration delay, five-failure lockout, bounded verification window and atomic one-use redemption. Regeneration preserves lockout. Signing-key rotation invalidates backup codes and requires operational coordination. |
| Privacy/logs | Delivery address protected by ASP.NET Data Protection; seller DTO omits address, customer/admin ownership controls access. Protect and back up the key ring/certificate. Provider response bodies and credentials are not surfaced in provider errors. Worker logs use stage/entity/type, not secrets or PII. Audit events record financial/fulfillment transitions. Existing identity/privacy controls retained. |
| Fail closed | Missing areas reject new checkouts; missing delivery fee rejects home delivery. Seller Pickup always snapshots fee zero and rejects an address. New startup validation checks pilot/provider configuration before hosting requests. Disabled ordinary development remains permitted. |

Provider references reviewed: [idempotent refunds](https://github.com/razorpay/markdown-docs/blob/master/api/refunds/normal-refunds-idempotent.md), [refund fetch/pagination](https://github.com/razorpay/razorpay-python/blob/master/documents/refund.md), [order contract](https://github.com/razorpay/razorpay-java/blob/master/documents/order.md). These confirm the API contract, not account enablement or successful provider acceptance.

## Service-area and delivery-fee operations

No locality assumption is embedded in the pilot selection: campaigns select a configured area ID and delivery validates its postal-code list. No approved real area or fee has been supplied. Add/remove codes in reviewed configuration, replace the complete indexed list (avoid stale indices or merging old JSON entries), restart all instances, inspect `/api/v1/mvp/options`, then verify a checkout with a removed code fails. Keep area IDs stable for existing campaigns. Removal blocks new checkout, not an already paid delivery obligation; review active orders before removing an area. Disabling payments also pauses refund processing, so it is not a complete operational shutdown plan.

Approve fee in paise explicitly, including zero if free home delivery is intended. Inspect server response `DeliveryFeeMinor` and product/total amounts for both modes. Configuration changes affect new orders; existing orders keep the captured fee snapshot. Do not accept a client-supplied fee or mutate an existing financial order to reprice it.

## Controlled Test Mode acceptance procedure

Prerequisites: Manoj approves isolated target, service codes/fee, operator and limited participants, HTTPS host, secret injection and time window. Verify Test keys and dashboard mode without recording values. Confirm runtime migrations already present on the disposable acceptance DB; any staging migration needs separate explicit authorization. Verify host security, worker health, protected key persistence, refund permissions/balance and rollback procedure. Do not run this procedure until authorized credentials are actually configured. This audit has made no provider calls.

Use synthetic identities/addresses; provider-hosted test instruments only. Record local order/campaign/refund IDs, provider IDs, timestamps, state transitions and redacted assertions. Do not record tokens, OTPs, signatures, addresses, payment instrument data or secret configuration. Start with one small supported INR transaction; only increase after evidence review.

1. Create/approve a limited tier campaign via existing seller/admin APIs. Confirm configured area, capacity, ordered tiers and permitted quantities. Obtain `/api/v1/mvp/options`; verify Razorpay, sandbox false and expected public Test key ID privately.
2. As Customer POST `/api/v1/mvp/customer/orders` with campaign, quantity, fulfillment mode, unique idempotency key and valid address only for delivery. Verify server price/fee, provider order, INR and expiry; quantity must not increase yet. Repeat identical request; verify same local/provider operation. Change payload with same key; expect conflict.
3. Use provider Test checkout against that order. POST `/orders/{id}/confirm` with payment ID/signature under the customer route. Verify provider fetch/capture, `CapturedAtUtc`, one reservation and exactly one quantity increment. Replay confirmation and verify no extra increment. An unrelated customer must not read/confirm/cancel it.
4. Observe signed webhook durable receipt, worker reconciliation and processed timestamp. Redeliver the same event from the Test dashboard: one durable event, one commitment. Send an invalid signature on an isolated request: 401, no event/quantity change. Test same ID with changed signed payload only in an isolated synthetic test harness; do not expose the signing key.
5. Reach full capacity (or let a short controlled deadline expire), finalize campaign, verify final quantity/achieved tier and immutable final price. Repeat finalization: no additional fulfillment/refund. Verify delivery fee is separate and pickup fee zero.
6. If final price decreases, observe one `tier_difference` refund for `(maximum product price - final product price) × quantity` in minor units. Poll Admin refunds and provider dashboard to `processed`; local `RefundedMinor` increases once. Reconciliation must converge after callback loss/worker restart.
7. Complete a separate failed-minimum campaign: verify full refund including delivery, no fulfillment and released commitments. Test customer cancellation before deadline as another full-refund path.

| Negative / recovery case | Required evidence |
| --- | --- |
| Failed test payment | Failure recorded after provider reconciliation; no paid quantity. Retry with a fresh checkout/idempotency key; successful attempt counts once. |
| Invalid callback signature | 401 before any provider call; unchanged commitment. |
| Authorized only | No commitment until captured; expired hold must never trigger backend capture. |
| Expired checkout | Wait beyond local expiry; capacity releases. Confirmation rejects; worker still checks provider for late funds. |
| Late captured payment | Reproduce using supported Test facilities; if timing cannot be produced safely, mark provider scenario unverified and retain automated evidence. Full refund, zero commitment, no oversell. |
| Duplicate/out-of-order webhook | Replay authorized/captured/failed deliveries around capture; authoritative final state wins, no duplicate reservation. |
| Full / partial refund retry | Simulate transport response loss with a controlled harness; same refund UUID/header/payload, one provider refund. Pending remains unresolved; never mark completed merely because request succeeded. |
| Recovery and reconciliation | Temporarily interrupt callback/webhook or worker in isolated target, restore it, observe provider/local state convergence and bounded failure handling. |
| Unapproved area / missing fee | Startup fails outside Development/Testing; unsupported postal code checkout rejected. Pickup has zero fee. |

Stop on wrong mode, unexpected capture/quantity, duplicate financial operation, unresolved refund, PII leak or exhausted job. Do not work around failures by changing operation keys, editing financial rows or resetting attempt counts without MFA operator review. Preserve refund/reconciliation capability for existing payments while blocking new participation through controlled campaign closure. Review all outstanding holds, provider captures and refunds before ending acceptance.

## Worker and operational findings / gates

The worker runs every 15 seconds, batches at most 50 records per stage, uses isolated scopes and redacted failure logs. Payment/webhook failures stop after ten recorded attempts; refund attempts and campaign opening/finalization failures also stop at ten. Successful pending-payment polling remains ongoing intentionally, to detect late capture. Refunds left pending after ten polls require operator review even if provider processing is merely slow. In-app notifications are transactional DB records, not an SMS/email transport; no external delivery guarantee is claimed.

**Remaining operational limitations:** payment-hold expiry and fulfillment-expiry failure handlers log and retry next batch without a persisted per-item failure budget. Repeated bad records can monopolize the first 50 due items. Webhook exhaustion has no dedicated retry endpoint; recovery must use reviewed reconciliation/operator procedure. Batch health can report completion while individual jobs failed, so health alone is insufficient. Pilot metrics expose refund backlog/exhaustion; operators must additionally inspect payment/webhook attempt exhaustion and campaign worker errors using approved read-only DB access and logs. Establish alert ownership and escalation before enabling paid participation. These are explicit gates, not a claim of fully bounded operations or production readiness.

## Information/actions required from Manoj

- Approve isolated backend acceptance target and public HTTPS API host; authorize any necessary target-specific deployment separately.
- Supply Razorpay **Test** Key ID/Key Secret and separate webhook secret through the authorized secret mechanism; optionally expected account ID. Confirm capture/refund capability and configure Test webhook subscriptions above. Never send Live credentials.
- Approve actual service-area IDs/names/postal codes and the exact home-delivery fee in paise, including approval flags. No assumed city/locality or default fee is acceptable.
- Name an MFA-enabled acceptance operator, retry/alert owner, participant/transaction limit and acceptance window; resolve expiry retry/monitoring gates or explicitly review a constrained supervised test plan before paid participation.
- Review and sign off the evidence matrix and unresolved provider scenarios. No production activation follows automatically from passing Test Mode.

## Validation evidence for this change

- Focused `PaymentGatewayTests`: 23 passed, zero failures/skips; includes incomplete activation rejection and later-page refund recovery.
- Final frozen-code regression: unit **52 passed / 0 failed / 0 skipped**; PostgreSQL integration **115 passed / 1 failed / 0 skipped** (116 total, 32m34s). Combined **167 passed / 1 failed**. The one isolated rerun passed; it does not erase the full-run failure.
- Frozen solution build: zero warnings, zero errors.
- EF `migrations has-pending-model-changes --no-build`: no pending model changes; no migration applied.
- `git diff --check`: passed (existing LF/CRLF normalization notices only).
- The full frozen-code backend regression uses Testcontainers with `GROUPBUY_TEST_POSTGRES` removed from the child process. Its authentication rate-limit test failed after 1m53s (expected 429, got 200). The same unchanged test passed alone against a separate disposable PostgreSQL container in 30s. The test counts registration/login toward a one-minute fixed-window budget, making crossing a window boundary a plausible cause. Do not describe the full regression as clean on the strength of this isolated rerun.
- No provider acceptance, frontend changes, staging migrations, commit, push or deployment occurred. Existing unrelated worktree edits were preserved.

Files touched by this task: `src/GroupBuy.API/Program.cs` (explicit startup validation); `src/GroupBuy.Infrastructure/PaymentGateways.cs` (validation and bounded refund-history recovery); `tests/GroupBuy.IntegrationTests/PaymentGatewayTests.cs` (13 additional cases); this runbook; `docs/mvp-phase1-security.md` (current activation-gate cross-reference). These paths include files that were already modified/untracked before this task; they are not a claim that all working-tree changes originated here.

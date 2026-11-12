# Module 7 — Fulfillment backend

Baseline: d8813f8. Implementation extends the existing Phase One infrastructure, not a parallel fulfillment system.

## Preimplementation audit

- Module 4/TierPricing and financial finalization determine successful campaigns and final prices. Finalize creates one existing PickupRecord and OrderFulfillment for each captured active reservation only after success; failure queues existing refunds.
- Module 5 owns checkout selection, encrypted DeliveryAddress, backend fee snapshots, service-area allowlists and authoritative payment reconciliation. Nonphysical products already require SellerPickup transport mode as seller redemption. Production service area remains unconfigured/fail closed.
- Module 6 projects participation from reservations, payment orders, pickups and fulfillments. It already distinguishes seller_pickup, founder_delivery and seller_redemption. Its broad endpoints deliberately omit pass credentials and addresses.
- OrderFulfillment already stores state, handover/redemption times, hashed token/code, code version, issue/expiry, failed attempts/cooldown and verified actor/window. PickupRecord stores collection actor/time, deadline and instruction snapshot. No demonstrated schema gap requires a new table or migration.
- Existing customer/seller/admin fulfillment list/detail, confirm-choice, status, pass, opaque verify, scoped verify and redeem APIs are under /api/v1/mvp. Legacy pickups reject bypass of paid tier fulfillment. Existing seller service redemption consumes entitlement capacity without physical inventory.
- QR: random 32-byte opaque token, SHA-256 lookup. Backup code: random six digits, domain-separated HMAC bound to fulfillment ID and version. Ten-minute expiry bounded by deadline, one-minute issue cooldown, five failed attempts and fifteen-minute verification cooldown; regeneration does not reset failures. Existing split verify/redeem uses seller/campaign locks and a two-minute verified-actor window.
- Legacy nonfinancial physical pickup retains its separate preexisting 128-bit/32-hex-character one-time code contract, scoped SHA-256 hash and thirty-minute expiry bounded by pickup deadline. Paid tier fulfillment cannot bypass the modern pass workflow through these legacy actions.
- Founder delivery already includes preparing, ready-for-collection, collection, out-for-delivery, failure/reschedule and OTP-based handover through the shared pass infrastructure. Admin-only delivery operations use existing role + MFA policy. Seller projections omit encrypted delivery addresses.
- Existing internal notifications are durable rows; audit events record actor/entity/correlation without credentials. Workers own bounded retryable finalization, refund reconciliation and fulfillment expiry. Expiry/cancellation/refund operations use the same financial locks.
- Existing indexes cover fulfillment payment/pickup identity, token hash and state/updated time; commercial and pickup foreign keys already exist. Runtime grants already include these tables.

Gaps identified: write/pass paths did not consistently revalidate commercial state; retry transitions retained previously verified credentials; arbitrary failure notes could reach customer responses; queue hydration performed per-item lookups; service presentation and legacy pickup readers reused physical wording for service/delivery records; no single-request atomic verification/completion API existed. Existing concurrency behavior returned one completion and a deterministic conflict for replay.

## Fulfillment model and transitions

No persistence or enum state values are duplicated. Existing OrderFulfillmentMode remains SellerPickup/FounderDelivery. ProductType distinguishes physical pickup from service/membership seller redemption. New `kind` and `status` projections are authoritative presentation codes; clients must not label services from the shared legacy storage enum alone.

| Kind | Allowed lifecycle after finalization |
| --- | --- |
| seller_pickup | confirmed → preparing → ready_for_pickup → securely verified handover → completed |
| founder_delivery | confirmed → preparing → ready_for_collection → collected_from_seller → out_for_delivery → securely verified handover → completed |
| seller_redemption | confirmed → service_pending → service_ready → securely verified redemption → completed |

ReadyForCollection remains the existing shared storage state; its public `status` depends on kind. Delivered remains the existing terminal storage state; public status is completed. Services never support founder collection/out-for-delivery transitions. Service pass means confirmation of completed service/entitlement redemption, not subscription activation or installation scheduling. Customer choice confirmation remains part of the existing flow before preparation.

FulfillmentRules centralizes campaign eligibility, commercial eligibility, pass readiness, type/status mapping, delivery-only operations and customer-safe reason codes. TierPricing.CanFulfill remains the single transition matrix and now restricts all delivery exception/retry transitions to FounderDelivery. Finalization, not a seller status update, establishes CampaignConfirmed.

Every confirm-choice, status mutation, pass issuance, verification and redemption rechecks server state under existing seller → campaign financial locks: campaign Successful/Preparing/ReadyForFulfillment and finalized, captured payment in Captured state, positive final amount, less than a full refund, active reservation, no outstanding refund, approved active seller/account, unexpired fulfillment deadline, not cancelled/refund-required/redeemed. Preparation additionally requires choice confirmation and the legal source state. A completed tier-difference refund preserves captured payment and fulfillment eligibility. Payment success alone does not authorize fulfillment. Cancellation, failure, full refund and outstanding refund block it.

## Pass security and completion

The dedicated owned customer pass POST is reused for pickup QR/code, delivery OTP and service redemption. The response remains opaqueToken, backupCode, expiresAtUtc and version. Existing random 256-bit QR, SHA-256 lookup, random six-digit code, domain-separated keyed HMAC, ten-minute expiry, one-minute regeneration cooldown, five-attempt/fifteen-minute lock and two-minute verified-actor window remain. Credentials are neither persisted plaintext nor added to broad reads, audit metadata or notifications. Verification accepts only a six-digit ASCII code or 64-character hexadecimal token; malformed bounded input counts as a failed attempt. Unbounded input rejects before hashing.

New `handover` performs verification, stock/entitlement accounting, reservation and pickup completion, fulfillment completion, credential clearing, audit and customer notification within one transaction. The existing split verify/redeem contract is retained. Only the verified actor can complete that split flow, and it rechecks current commercial state. Both paths serialize using existing locks. Exactly one concurrent completion succeeds; duplicates/replays return the existing stable HTTP 409 rule_violation response (opaque lookup after credentials are cleared returns 404). No duplicate completion event or inventory/entitlement consumption is performed. Direct status transitions to Delivered are forbidden.

Collection consumes seller inventory once for founder delivery; customer OTP handover completes participation without consuming stock again. Physical pickup consumes inventory at verified handover. Service redemption consumes entitlement capacity without inventory. The pickup record preserves completion actor/time; fulfillment preserves seller handover time and redemption time. No second customer status is stored.

New physical pickup instruction snapshots capture the public seller name/location at finalization in the existing PickupRecord.Instructions field. Later location edits do not rewrite those instructions. Historical paid records without a snapshot receive generic safe pass guidance; no historical location is invented. Seller private phone/contact/notes are excluded.

Legacy customer/seller/admin pickup queues and customer/seller pickup details are restricted to physical seller pickup. Their reservation pickup fields and pickup summary counts use the same filter. Service redemption and founder-delivery accounting still reuse internal PickupRecord rows, but those rows cannot appear as customer pickup in legacy screens or be mutated via legacy pickup actions. Dedicated fulfillment APIs remain the route for those experiences.

## Delivery address, fees and retries

Checkout remains the sole source of address validation, configured-area serviceability and delivery fee. Existing encrypted per-order address and paid DeliveryFeeMinor snapshot are reused. Module 7 never accepts a new fee, reprices fulfillment, configures a service area, or creates an address book. Customer-owned dedicated fulfillment reads and authorized MFA-admin operations may access the address; seller queues/details do not select its ciphertext. Module 6 lists/details still omit addresses entirely. Production service area remains unconfigured and Module 5 service_area_unconfigured behavior remains fail closed.

Only the admin/MFA fulfillment actor can collect, dispatch, fail, reschedule, or verify founder delivery. Seller readiness is ready_for_collection, not customer pickup readiness. Customer My Orders becomes Ready for the delivery pass only at OutForDelivery. A failed attempt accepts only customer_unavailable, address_inaccessible, delivery_attempt_failed or other. Arbitrary notes reject; historical/internal exception notes are suppressed by SafeReason in every broad fulfillment response. RescheduleRequired is the existing MVP reschedule state, without an invented date or scheduling engine.

Every actual status change clears prior QR/code hashes, expiry and verified-actor window and increments the existing version. Failure → RescheduleRequired → OutForDelivery therefore cannot reuse a prior OTP or prior verification. Customer requests a new pass explicitly after issuance cooldown. Failed attempts/lock are deliberately preserved across transitions/regeneration; an authorized actor cannot bypass brute-force controls by retrying delivery. Idempotent same-state status requests do not rotate credentials or create duplicate audit/notification rows. Already-terminal actions remain conflicts.

## API contract

Existing `/api/v1/mvp` architecture is retained:

| Actor | Endpoint | Behavior |
| --- | --- | --- |
| Customer | GET /customer/fulfillments | Owned bounded history |
| Customer | GET /customer/fulfillments/{id} | Owned safe fulfillment details; type/status, instructions, fee, pass eligibility, recorded timestamps and protected delivery address |
| Customer | POST /customer/fulfillments/{id}/confirm-choice | Existing choice confirmation |
| Customer | POST /customer/fulfillments/{id}/pass | Existing state/ownership-restricted pickup/delivery/redemption credential issuance |
| Seller/admin | GET /{seller or admin}/fulfillments | Bounded queue, state/area/seller/campaign/kind filters |
| Seller/admin | GET /{seller or admin}/fulfillments/{id} | Authorized fulfillment detail |
| Seller/admin | POST /{seller or admin}/fulfillments/{id}/status | Existing `{state, reason}` transition; preparing/ready and admin-only delivery operations |
| Seller/admin | POST /{seller or admin}/fulfillments/verify | Existing opaque QR lookup/verification |
| Seller/admin | POST /{seller or admin}/fulfillments/{id}/verify | Existing scoped `{secret}` verification |
| Seller/admin | POST /{seller or admin}/fulfillments/{id}/redeem | Existing verified-actor handover completion |
| Seller/admin | POST /{seller or admin}/fulfillments/{id}/handover | New atomic `{secret}` verification and completion |
| Admin | POST /admin/fulfillments/{id}/refund | Existing controlled failed-fulfillment refund |

The optional queue `kind` accepts seller_pickup, founder_delivery or seller_redemption. Admin founder delivery queue uses kind=founder_delivery plus existing state filter. Preparing/ready service queues use kind=seller_redemption and shared Preparing/ReadyForCollection storage states. Page defaults 1/20, bounds 1–100000 / 1–100, deterministic CreatedAt/UUID order. Unknown kind or undefined enum state rejects. Two reader batches (count and joined page) replace per-item hydration; seller ownership lookup is constant additional work, never N+1. Existing indexes are sufficient for these joins/filter/order patterns; no speculative new index is added. No production-scale benchmark is claimed.

FulfillmentResponse retains previous fields and adds kind, status, canRequestPass, passAvailableAtUtc, instructions, collectedFromSellerAtUtc, completedAtUtc and updatedAtUtc. These timestamps are recorded values, not inferred transition dates. collectedFromSellerAtUtc is exclusive to physical founder delivery; service/pickup completion uses completedAtUtc. MyOrderFulfillment adds optional status and tightens readiness to match commercial/choice eligibility; pickup ID/status are returned only for physical seller pickup, never services or founder delivery. Existing enums, status precedence, next actions and broad secret omission are preserved. No duplicate GET /me/orders/{id}/fulfillment is needed.

## Authorization, notifications and audit

Existing Customer/Seller/Admin policies, strict JSON handling, disabled-account/token-revocation checks and admin MFA are unchanged. Customer ownership and seller store ownership are derived from the authenticated actor; request IDs never authorize access. Foreign fulfillment/pass IDs return standard 404. Customer and seller cannot self-declare founder delivery complete. Existing pickup rate limiter (30/user/minute) covers issuance, verification, redeem and new handover, in addition to per-fulfillment attempts/cooldown. Only authorized owners/operators can consume attempts, preventing public-ID permanent lockout attacks. Server TimeProvider UTC controls deadlines, expiry, cooldown and completion.

Existing audit events cover preparation/readiness, collection/dispatch, failure/reschedule, pass issue/rejection/verification and completion, including actor and timestamp. Existing durable internal notifications cover readiness, out-for-delivery and completion; appended DeliveryFailed/DeliveryRescheduled types cover those transitions. Per-attempt version in the new transition notification deduplication key allows a genuine second dispatch notification while suppressing repeated same-state submissions. No external messaging provider is introduced. Service notifications use the existing fulfillment-ready/completed events with the distinct fulfillment kind/status.

No schema migration, runtime grant change or backfill is needed. Existing tables/columns and integer notification type storage support the added behavior. Fresh migration, runtime grants, upgrade/backup tests and EF pending-model validation are included in the local gate.

## Tests and environment

Focused tests cover commercial gates, distinct service/pickup/delivery states, legal/illegal transitions, duplicate readiness, QR and backup-code concurrency/replay, expiry, attempts/cooldown, failed/refunded participation, service entitlement consumption, delivery failure/reschedule/old-OTP/old-verification invalidation, admin authority, customer/seller IDOR, strict JSON, queue filtering/paging, bounded SQL without selected hashes, My Orders integration and completion/cancellation concurrency. Existing Modules 1–6 remain in the full regression run, including service-area fail closed, payment/refunds, admin MFA, rate limits, migration/runtime security and legacy paid pickup bypass prevention.

Run `scripts/verify-module7-readiness.ps1` for the complete Windows gate or add -FocusedOnly during development. Windows PowerShell 5.1 and PowerShell 7 are supported. It uses complete existing `.local/audit-postgres/pgsql/bin` PostgreSQL 17.6 binaries (override -PostgresBin if needed), a fresh GUID directory/data cluster, random SCRAM password, free loopback port excluding 55432, D:-local temporary files and scoped process environment. It never starts/stops an existing database or accesses protected Sprint 5.1 resources. Existing backup/restore regression now supports both its original Testcontainers execution and an explicitly configured native test binary path, with the same dump/restore/content assertions and no skipped test. Native subprocess arguments are structured, credentials are process environment only, and the dump is deleted after use. Fixture data/logs remain ignored evidence after the owned cluster is stopped.

RenderTlsFixture also honors the explicit test-only native binary path. It creates another fresh isolated cluster with a generated synthetic CA/server key, real SSL, SCRAM authentication, a free loopback port excluding 55432 and client SslMode=VerifyFull using that CA. Its original Docker path is preserved when native binaries are not requested. All seven encrypted-key persistence, certificate rotation, runtime grants, migration and actual local production-Kestrel assertions remain unchanged. It stops its own server before deleting its validated backend-local directory. The gate separately checks for remaining TLS fixture PID files and new API processes. No Docker daemon/protected container restart is needed.

Early development hit C: disk exhaustion; temporary files were moved to backend-local D:. The incomplete system PostgreSQL installation failed initdb before any server started; complete existing backend-local binaries are used instead. Early build/test failures are retained in `.local` evidence and are not represented as passing gates. No saved machine settings were changed.

## Deferred and safety boundaries

Service scheduling/installation calendar is deferred. Existing campaign fulfillment deadlines (currently two days from paid campaign finalization) are retained for all kinds; retry/reschedule does not silently extend a deadline, and expiry/refund workers remain authoritative. Long-running installation scheduling requires a separately reviewed policy change. No subscription/digital activation, delivery ETA, GPS, rider app/marketplace, route optimization, automated dispatch, advanced scheduling, external logistics, saved address book or new SMS/WhatsApp/email provider is added. Production area/live payment activation, production migrations/connections, production grant changes, deployment, frontend edits, commit and push are outside this change. Dependency vulnerability feed auditing is disabled only for the local restore process; restore/build is not a vulnerability audit.

## Changed files

- API/contracts: src/GroupBuy.API/PhaseOneEndpoints.cs; src/GroupBuy.Application/PhaseOneContracts.cs; src/GroupBuy.Application/MyOrdersContracts.cs.
- Rules/events: src/GroupBuy.Domain/FulfillmentRules.cs; src/GroupBuy.Domain/PhaseOne.cs; src/GroupBuy.Domain/Operations.cs.
- Existing fulfillment and history: src/GroupBuy.Infrastructure/Marketplace.PhaseOne.Fulfillment.cs; src/GroupBuy.Infrastructure/Marketplace.FulfillmentQueries.cs; src/GroupBuy.Infrastructure/Marketplace.MyOrders.cs. Legacy pickup readers/counts are guarded in Marketplace.Operations.cs, Marketplace.SellerManagement.cs and Marketplace.Administration.cs in the same directory.
- Focused tests: tests/GroupBuy.UnitTests/FulfillmentTests.cs; tests/GroupBuy.IntegrationTests/Module7FulfillmentTests.cs.
- Existing test fixtures: tests/GroupBuy.IntegrationTests/Module6ProjectionTests.cs and Module6StateTests.cs now seed confirmed/finalized, priced and choice-confirmed readiness rather than impossible open-group readiness; existing assertions remain. DisposableBackupRestoreTests.cs and RenderTlsFixture.cs add native execution of the same backup/restore and TLS/key-persistence assertions.
- Validation/documentation: scripts/verify-module7-readiness.ps1; this document.

## Recovery audit — 2026-11-13

The recovered working tree remains based on `d8813f8` (Module 6). Initial `git status --short`, `git diff --stat` and `git diff --check` were run before continuing. All 14 tracked modifications and six untracked Module 7 files were inspected, including the fulfillment implementation, query projections, contracts, rules, legacy reader guards, focused tests, native PostgreSQL/TLS fixture changes, verification script and this document. The existing implementation already covers the requested pickup, home-delivery/retry and service-redemption scope; the outstanding work was a fresh complete verification gate and final evidence report. Existing source changes were preserved without reset, checkout, restore, clean, stash, commit, push or deployment.

The fresh verification evidence directory is `.local/module7-4547beec604a4705806bd3f0a6a8d61e`. An initial sandbox attempt could not query the process inventory required for cleanup verification; it exited before restore or database initialization. The gate was then rerun with the required execution permission.

| Required check | Fresh result |
| --- | --- |
| dotnet restore | Passed |
| dotnet build | Passed; zero warnings/errors |
| Focused Module 7 unit tests | 38/38 passed |
| Focused Module 7 PostgreSQL tests | 22/22 passed |
| Full unit suite | 225/225 passed |
| Full PostgreSQL integration suite | 381/381 passed; no failures or skipped cases; 21.0655 minutes |
| EF pending-model-change check | No changes since the last migration |
| Diff whitespace check | Passed |
| Changed-file secret/production-connection scan | Zero findings across 20 files |
| Cleanup | Owned PostgreSQL PID absent; zero new TLS PostgreSQL PID files; zero new backend API processes; temporary sleep guard released |

The gate completed restore/build/tests/model validation, then Windows PowerShell treated Git's advisory LF/CRLF stderr warning as a terminating error despite Git's successful whitespace result. Its `finally` cleanup still completed and was verified. The script now suppresses that advisory with invocation-local `core.safecrlf=false`; no saved Git setting is changed and whitespace/exit validation remains enabled. The final diff/scan/status checks were rerun separately against the final documentation and script, with evidence in the same directory. Test sources and application code were unchanged after the successful suites, so the suites were not needlessly repeated.

The recovered backend is ready for frontend integration within the documented MVP scope. No implementation blockers remain. Frontend consumers should use `kind`/`status` to distinguish pickup, delivery and service redemption; obtain credentials only from the owned customer pass endpoint; and use atomic `handover` or the retained verified-actor split flow for authorized completion. The production service area remains intentionally unconfigured and fail closed. Service scheduling and deadline-policy expansion remain deferred product work, not part of this fulfillment/redemption API.

## Earlier validation — 2026-11-12

The focused gate passed in `.local/module7-0dd91febf71e47729b8cf0a0e9c1f4ef`: restore/build passed with zero warnings/errors; focused unit **38/38** and PostgreSQL **22/22** passed with no skips; EF reported no pending model changes; diff check and 16-file secret/production-connection scans passed; the owned PostgreSQL cluster stopped and its PID file was absent.

The subsequent full gate uses -SkipFocused because the dedicated focused run already passed. The full unit/integration suites still include every Module 7 case. Full results are recorded below after completion.

The initial full attempt in `.local/module7-58ef3f5e312f464e9d45324a474843cb` passed all 225 unit tests, then the host paused for over an hour during an existing Module 3 role test. That test resumed with an expired token (401 instead of expected 403), followed by a transient database timeout. The interrupted run is preserved, not claimed green. Its process tree stopped; native pg_ctl confirmed no server remained before its stale owned PID file was removed. The retry uses a temporary thread-scoped SetThreadExecutionState guard, released in finally without changing saved power settings. Authentication deadlines/assertions and SQL/test timeouts are unchanged. Review also added explicit legacy pickup isolation assertions for service/membership/delivery and reran focused checks before restarting the full suite.

The final-source focused gate passed in `.local/module7-50766622c7a941a0b40db50ebe78ef23`: **38/38 unit**, **22/22 PostgreSQL**, zero skipped cases, clean build/model/diff/scans across **19 changed files**, and zero remaining owned PostgreSQL/API processes. Its sleep guard was released. A subsequent full attempt correctly failed the existing production-security test because the native harness had explicitly disabled pooling, unlike the normal test fixture. The disposable connection now uses standard pooled connections; production configuration and its assertions remain unchanged. That interrupted attempt and verified cleanup are preserved in `.local/module7-a95282072a6f40d3b2fd2aad7fd4dee8`. Final complete results follow from the corrected run.

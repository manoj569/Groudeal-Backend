# Grocery pilot fulfillment workflow

This workflow uses generic seller, variant, inventory, campaign, reservation and pickup records. There is no vehicle/property workflow, warehouse system, delivery assignment or financial settlement.

## Inventory and campaigns

1. An approved seller creates a product variant and declares physical stock through PUT /api/v1/seller/inventory/{variantId}. Use expectedVersion=0 initially, then the latest returned version for every update.
2. All quantities count that variant's units. A 5 kg variant reserved at quantity 2 means two 5 kg units. Bulk variants can have up to three decimal places; Piece/Pack counts are whole. Price is INR per variant unit.
3. Draft capacity must fit currently uncommitted inventory but drafts do not hold stock. Submission locks the seller, rechecks free stock, and commits the entire target. Multiple draft campaigns can compete, but only submissions backed by stock succeed. Holds cover all live campaigns regardless of date overlap; no future replenishment is assumed.
4. Admin approves or rejects. Rejection returns Draft and releases stock. Pending/Scheduled/Open campaigns release holds when acquisition expiry is processed. Cancellation of an individual Open reservation frees reservation capacity, not the target stock hold.

Let A = declared physical stock still on hand, C = all outstanding campaign holds, R = active customer reservations and F = historically collected quantities. Free inventory is A − C. R is a separately reported quantity within those holds, not an additional subtraction from A. On collection of q: A decreases by q, campaign C decreases by q and campaign F increases by q. On releasing an uncollected hold: C decreases, A is unchanged. Restocking is an explicit version-checked declaration of new on-hand A. Historical fulfilled units are never returned to stock automatically. No sums mix variant quantities from different products; the dashboard aggregates counts and INR snapshot values instead.

Every allocation, confirmation, release and collection uses a transaction with seller-first locking. Inventory declarations and collections invalidate optimistic inventory versions, preventing a concurrent stock declaration from restoring consumed stock. Campaign and pickup locks also serialize final reservation, cancellation, decision, deadline processing and replay.

## Target and seller confirmation

The final reservation atomically fills capacity, records TargetReached, moves to AwaitingSellerConfirmation, sets confirmationDeadlineUtc and saves CampaignTargetReached/SellerConfirmationRequired notifications for the seller. Customer cancellation is now closed. The acquisition deadline no longer controls this full campaign; its own confirmation deadline does.

The seller POSTs confirm or reject with a unique idempotencyKey and optional reason (required for reject). A key is scoped to that campaign; exact retries are safe and return current state. Changed reason/outcome or a different key after a decision returns 409. The actor, timestamp, reason and outcome are persisted. Confirmation after deadline is rejected without waiting for the worker. A rejected or timed-out group becomes Failed; active reservations become Expired, outstanding holds are released, and customers receive the matching rejection/expiry notification. Snapshot prices remain unchanged; these are non-financial outcomes, not refunds.

Confirmation rechecks account/seller/product/category/variant/pickup availability and declared inventory backing. It creates one Pending pickup per Active reservation with a unique reservation index, snapshots the approved seller address/instructions, sets pickupDeadlineUtc and notifies customers. Defaults: confirmation 60 minutes, pickup 48 hours; validated startup configuration can change future windows, not deadlines already stored.

## Pickup eligibility and payment policy

Every pickup explicitly has PaymentPolicy=NotTrackedOfflineResponsibility. Reservation details explicitly report PaymentStatus=NotTracked. Code verification records physical handover and never marks a customer paid. No platform payment or offline-payment acknowledgment is required by this backend; any payment arrangement, invoicing, tax, consumer protection or seller eligibility obligations remain separate operational/compliance prerequisites for the live pilot. Snapshot total is an agreed campaign value, not a payment receipt. The UI must not label these records paid orders.

The seller marks a Pending pickup ReadyForPickup, which also starts FulfillmentInProgress if needed and saves PickupReady. A customer sees approved instructions/status/deadline and requests a code only when ready to collect. Collection requires the owning approved active seller, a Ready record, an Active reservation, an unexpired window, unlocked attempts and a matching unexpired one-time secret. Customers cannot self-collect through the API.

## One-time pickup codes

POST /me/pickups/{id}/code returns 128 random bits encoded as 32 uppercase hexadecimal characters, expiring in at most 30 minutes and never after the pickup deadline. Only the pickup-ID-bound SHA-256 hash is saved. GETs, notifications, seller DTOs and audit logs never contain the plaintext code. Responses use no-store. Frontend may show it to the owning customer or render a local QR representation, but must not send it to analytics, URLs, logs or unrelated users.

If a response is lost or a code expires, the owning customer may reissue after one minute. Reissue invalidates the previous code and retains failed-attempt lockout. The seller enters/scans the code at physical handover and POSTs /seller/pickups/{id}/collect. Constant-time hash comparison plus a single transaction consume the code, record collector/time, consume stock, complete that reservation, save PickupCompleted/audit and evaluate group completion. Concurrent verification or replay cannot collect twice. If the success response is lost, refetch status rather than assume a 409 means failure.

An incorrect/expired well-formed code returns 400 and persists a failed attempt. Each five failures causes a 15-minute per-pickup lockout (429, even for the correct code). Malformed codes return validation 400. Actor-based HTTP code-operation throttling is 30/minute; persistent per-pickup lockout remains effective across application restarts. Reissue does not reset attempts. Current high-entropy codes avoid offline brute-force weaknesses of short numeric PIN hashes.

## Completion, missed pickups and failure

Collected is terminal. The campaign completes automatically only when every pickup is Collected. A seller cannot manually complete a group with Pending, Ready, Cancelled or NotCollected records. Completed reservations represent physically collected commitments, not paid transactions.

At pickup deadline, outstanding Pending/Ready records become NotCollected, their Active reservations become Expired, their stock is released, and the campaign ends Failed after all pickup outcomes are terminal. Previously collected units retain evidence and remain fulfilled; partial fulfillment never counts as successful group completion. A seller reporting fulfillment failure through the retained lifecycle route cancels outstanding pickups and releases only uncollected stock. Completed, Failed and Expired campaigns cannot reopen.

## Reliability and cutover

Business state, inventory, pickup outcomes, audit and in-app notifications commit together. A unique event/recipient/entity key prevents duplicate notification delivery. No external notification provider or broker exists. External delivery later requires a transactional outbox dispatcher with its own idempotency and retries.

The worker polls every 15 seconds, isolates each candidate in a new scope/transaction, uses a 3-second lock timeout and 10-second command timeout, and persists 15–900 second bounded exponential retry timing. Retry persistence itself has a 5-second command timeout. Healthy candidates continue after a poison candidate. Shutdown cancellation propagates without recording a campaign failure. Admin health combines persisted backlog/retry/legacy counts with process-local batch heartbeat. It is visibility, not a replacement for deployment monitoring.

Sprint 2's migration adds schema only. Existing active Sprint 1 campaigns have no automatically invented stock or pickup evidence. New joins/approval/confirmation fail closed without backing; existing deadline-less full/confirmed legacy campaigns appear in health and require operator review. Resolve old operational commitments or obtain a separately reviewed explicit data-conversion plan before live cutover. Do not reset production data, replay demo seed into it, or infer physical inventory from historical target quantities.

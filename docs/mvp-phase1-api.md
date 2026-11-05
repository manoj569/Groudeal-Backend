# Phase 1 pilot API contract

All new routes are additive under `/api/v1/mvp`. Existing JWT account, role, token-version and Admin MFA checks apply. JSON enums use names. Lists use `page` (default 1), `pageSize` (default 20, maximum 100), and `{items,page,pageSize,total}`. Monetary provider amounts and delivery fees are integer INR paise; quantities use existing variant units and at most three decimal places.

## Public configuration and campaigns

* `GET /options`: activation/payment availability, provider, public checkout key ID, configured service areas and explicitly configured delivery fee. No server credentials.
* `GET /campaigns`, `GET /campaigns/{id}`: approved/public campaigns with ordered tiers, paid quantity, projected/current and next threshold, final price/quantity, extension count and near-target acceptance.
* `POST /payments/webhook`: original raw body plus `X-Razorpay-Signature` and `X-Razorpay-Event-Id`. Signature verification precedes durable event-ID/body-hash deduplication. Acknowledgment records an inbox entry, not a paid commitment. The worker fetches authoritative provider state. Reused event IDs with changed bodies return 409; invalid signatures return 401.

## Customer

* `GET /customer/interests`; `POST /customer/interests`, `/customer/interests/withdraw`: `{productId,serviceArea,desiredQuantity}`. Unique per authenticated customer/product/area. Withdrawal remains possible after a product is unavailable. Interest is free and consumes no campaign capacity.
* `POST /customer/orders`: `{campaignId,quantity,idempotencyKey,fulfillmentMode,address}`. Mode is `SellerPickup` or `FounderDelivery`. Pickup requires `address:null` and costs zero delivery fee. Delivery address is `{recipient,phone,address,landmark,serviceArea,postalCode}`. Postal code must belong to that campaign's configured area. Do not send prices, fees or financial states.
* `GET /customer/orders`, `GET /customer/orders/{id}`: own orders only; maximum charge, separate delivery fee, final charge, source refunds, hold expiry, provider/order identifiers and bounded safe reconciliation status. An order without a reservation is not a paid commitment.
* `POST /customer/orders/{id}/confirm`: `{paymentId,signature}`. The server verifies the checkout HMAC and provider payment, never the browser's claimed status. Captured funds alone create a reservation. An expired checkout does not actively capture an authorization; late captured funds reconcile to a full refund.
* `POST /customer/orders/{id}/sandbox?success=true|false`: explicitly enabled non-production sandbox only, owner-authorized. Real provider selection cannot invoke this endpoint.
* `POST /customer/orders/{id}/cancel`: before finalization/deadline, idempotently releases the hold/commitment and queues a captured-payment refund. Does not cancel another customer's order.
* `GET /customer/fulfillments`, `GET /customer/fulfillments/{id}`.
* `POST /customer/fulfillments/{id}/confirm-choice`: confirms the previously disclosed and priced mode/address; changing paid delivery terms is not silently permitted.
* `POST /customer/fulfillments/{id}/pass`: ready pickup or out-for-delivery only. Returns a fresh opaque token and six-digit backup/delivery code once, with expiry/version. Render locally; never persist in storage, analytics, logs or URLs. Regeneration invalidates the previous pass and verification grant.

## Seller

* `GET /seller/demand`: aggregate product/locality customer counts, desired units and recent interest. No customer identifiers/contact information.
* Create inventory-backed drafts through the existing seller campaign API. `PUT /seller/campaigns/{id}/tiers`: `{serviceArea,tiers:[{quantity,unitPrice}],nearTargetQuantity}`. One to ten increasing thresholds with non-increasing prices, within capacity/reference price. Draft ownership required; tiers freeze on submission. An enabled pilot requires configured tiers before submission.
* Existing submission and Admin campaign approval remain required.
* `POST /seller/campaigns/{id}/finalize`: deadline or full capacity only. Idempotent, seller-owned, transactionally freezes the highest achieved tier. Does not require maximum capacity at deadline. Below-minimum commitments receive full source refunds.
* `POST /seller/campaigns/{id}/rescue`: `{action,newDeadlineUtc,reason}`. Actions `remind`, `extend`, `accept-near-target`. One reminder; one extension up to 24 hours before expiry; only a previously disclosed near-target quantity (at least 90% of minimum) can be accepted. No price rewrite.
* `GET /seller/fulfillments[/{id}]`; seller-owned records only, no delivery address or pass secrets.
* `POST /seller/fulfillments/{id}/status`: `{state,reason}`. Confirmed choice and settled tier refunds required before preparation/readiness. Seller can prepare/mark ready, not perform founder collection/delivery transitions.
* `POST /seller/fulfillments/{id}/verify`: `{secret}`; `POST .../redeem`: separate physical-handover confirmation using a short-lived verification grant bound to the same operator. Pickup mode only for sellers. Replay returns 409 and never consumes stock twice.
* `POST /seller/fulfillments/verify`: `{secret}` containing the 64-character opaque QR token. Indexed token lookup is ownership-filtered before the same locked verification. No order ID or PII is encoded in the QR. Six-digit backup codes use the matching displayed pickup reference and the per-record endpoint.

## Admin/founder (MFA required)

* `GET /admin/demand`, `/admin/metrics`, `/admin/orders`, `/admin/refunds`.
* `GET /admin/jobs?state=Failed|Exhausted|Running`: paginated, safe per-item worker status, attempt history, last/next timestamps and approval version.
* `POST /admin/jobs/{id}/retry?expectedRetryCount={observed}`, `/admin/orders/{id}/retry?expectedRetryCount={observed}`, `/admin/refunds/{id}/retry?expectedRetryCount={observed}`: MFA Admin-only, versioned and audited retries of exhausted operations, preserving original provider receipt/refund ID and checkout deadline. The query parameter is required, including on the existing payment/refund routes. Replayed old approvals do not reset a later cycle. Never create an unrelated refund key to bypass an uncertain result. See [worker operations](operations/backend-reliability-gate-closure.md).
* `GET /admin/fulfillments[/{id}]`: filter `area`, `seller`, `campaign`, `state`; minimal delivery address available to the authorized founder. The frontend batches the filtered page by locality/seller/campaign. Pagination does not imply a bulk handover.
* `POST /admin/fulfillments/{id}/status`: seller preparation plus founder-only `CollectedFromSeller`, `OutForDelivery`, `DeliveryFailed`, `RescheduleRequired`, `RefundRequired` transitions.
* `POST .../verify`, `POST .../redeem`: delivery OTP and actor-bound handover; no direct arbitrary `Delivered` transition.
* `POST /admin/fulfillments/verify`: the same opaque-token lookup for an MFA-authenticated founder.
* `POST /admin/fulfillments/{id}/refund`: `{reason}` for a failed/rescheduled delivery. Refunds outstanding source funds, including the fee. Goods already collected from the seller are never automatically restocked.

## Compatibility

Existing campaign/reservation/pickup responses add `usesTierPricing` (default false for legacy rows); pickup responses also add a nonsecret, ownership-scoped `fulfillmentId`. Paid pickups use `VerifiedCapturedPayment`, while legacy offline records retain their policy. Existing enum numeric values remain unchanged. Legacy free-reservation and pickup mutation routes reject paid campaigns. The frontend redirects paid campaign/reservation/pickup detail to the pilot workspaces; existing legacy workflows remain available for legacy campaigns. New `Pilot*` notifications open the pilot workspace. Protected staging configuration and production Compose are not changed by this implementation.

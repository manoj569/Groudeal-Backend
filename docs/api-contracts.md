# API contracts

Base URL `/api/v1`. JSON is camelCase, enum values are strings. Unknown JSON fields are rejected (including roles, reservedQuantity, status or prices outside the designated DTO). Missing required/invalid fields produce 400. All list endpoints accept `page` (1–100000, default 1), `pageSize` (1–100, default 20) and return `{items,page,pageSize,total}`. ID route values are UUIDs. GETs have no body.

| Method and route | Access | Request → response |
| --- | --- | --- |
| POST /auth/register | Public, rate limited | RegisterRequest → 201 UserResponse (Customer only) |
| POST /auth/login | Public, rate limited | LoginRequest → 200 TokenResponse |
| GET /auth/me | Authenticated | → UserResponse |
| POST /auth/logout | Authenticated | No body → 204 (revokes all this user's tokens) |
| GET /categories | Public | → page of CategoryResponse (active only) |
| GET /categories/{slug} | Public | → CategoryResponse |
| GET /sellers | Public | → page of SellerResponse |
| GET /sellers/{id} | Public | → SellerResponse (approved active only) |
| POST /seller/profile | Authenticated | SellerRequest → 201 SellerResponse (Pending) |
| PUT /seller/profile | Owner | SellerRequest → SellerResponse (resets review) |
| GET /seller/dashboard | Owner | → DashboardResponse |
| GET /products | Public | → page of ProductResponse |
| GET /products/{id} | Public | → ProductResponse |
| POST /seller/products | Approved Seller | ProductRequest → 201 ProductResponse |
| PUT /seller/products/{id} | Approved owner | ProductRequest → ProductResponse |
| GET /group-buys | Public | → page of CampaignResponse |
| GET /group-buys/{id} | Public | → CampaignResponse |
| POST /seller/group-buys | Approved Seller | CampaignRequest → 201 CampaignResponse (Draft) |
| PUT /seller/group-buys/{id} | Approved owner | CampaignRequest → CampaignResponse (Draft only) |
| POST /seller/group-buys/{id}/submit | Approved owner | No body → CampaignResponse |
| POST /seller/group-buys/{id}/transition | Approved owner | TransitionRequest → CampaignResponse |
| POST /group-buys/{id}/reservations | Customer | ReservationRequest → 201 ReservationResponse |
| GET /me/reservations | Customer | → page of own ReservationResponse |
| POST /me/reservations/{id}/cancel | Customer owner | No body → ReservationResponse |
| GET /admin/sellers/pending | Admin | → page of SellerReviewResponse |
| POST /admin/sellers/{id}/approve | Admin | ReviewRequest → SellerReviewResponse |
| POST /admin/sellers/{id}/reject | Admin | ReviewRequest → SellerReviewResponse |
| GET /admin/group-buys/pending | Admin | → page of CampaignResponse |
| POST /admin/group-buys/{id}/approve | Admin | ReviewRequest → CampaignResponse |
| POST /admin/group-buys/{id}/reject | Admin | ReviewRequest → CampaignResponse |

Complete typed contracts live in `src/GroupBuy.Application/Contracts.cs` and generated OpenAPI `/openapi/v1.json` (Admin-only outside Development). 200 is the default successful status except where noted. Request types include explicit validators; no domain entities are serialized. Auth header: `Authorization: Bearer <accessToken>`.

## Examples

Registration: `{"email":"customer@example.com","password":"a-long-unique-secret-with-A1!","displayName":"Customer"}`. No role input. Login accepts email/password and returns accessToken/expiresAtUtc. Seller approval grants Seller role; log in again to obtain updated claims.

Seller profile:

```json
{"businessName":"Example Shop","sellerType":"Grocery","description":"Local pickup","contactEmail":"shop@example.com","contactPhone":"+919876543210","fssaiRegistrationNumber":"12345678901234","location":{"addressLine1":"Pickup counter","addressLine2":null,"area":"Area","city":"City","state":"State","postalCode":"110001","latitude":null,"longitude":null}}
```

This sample licence value is illustrative, not evidence of eligibility. Admin must review actual seller information before approval. Public SellerResponse excludes owner identity, contact details, FSSAI and review metadata. Public pickup responses include only active pickup location addresses, not coordinates or private user data.

Product creation includes one variant; future multiple-variant management can be added without changing the schema:

```json
{"categoryId":"10000000-0000-0000-0000-000000000002","name":"Wheat","slug":"wheat","description":"Wheat flour","productType":"Physical","sku":"WHEAT-1KG","variantName":"1 kg","unitOfMeasure":"Kilogram","unitQuantity":1,"attributesJson":"{\"brand\":\"Example\"}"}
```

AttributesJson is a JSON object serialized as a string: at most 2048 characters, 8 distinct properties. Allowed keys: brand, size, color, material, model, configuration, grade, origin. Values must be strings of at most 100 characters. No arbitrary attributes, SQL or schema changes are accepted.

Campaign creation:

```json
{"productVariantId":"<variant UUID>","sellerLocationId":"<pickup UUID>","title":"Wheat group","description":"Pickup after confirmation","referenceUnitPrice":50,"groupUnitPrice":40,"targetQuantity":100,"minimumReservationQuantity":1,"maximumReservationQuantity":20,"startsAtUtc":"2026-11-02T00:00:00Z","expiresAtUtc":"2026-11-05T00:00:00Z"}
```

Use future dates relative to your actual test time. Price precision 2, quantity precision 3; all prices in INR. Client cannot set currency, capacity or status. Reservation: `{"quantity":2,"idempotencyKey":"client-generated-unique-key"}`; key 8–100 ASCII letters/digits/underscore/hyphen. Same key/payload returns the same reservation (201); changed quantity returns 409. The returned campaignProgress is current at transaction time, not a fixed original retry value.

Legacy seller transition accepts AwaitingSellerConfirmation (a no-op when already awaiting), Confirmed (delegates to stock-backed confirmation), FulfillmentInProgress, Completed (only with verified pickup evidence) and Failed (reports failure and cancels outstanding pickups). The new confirm/reject and pickup endpoints below are preferred. Admin review body is `{"reason":null}` for optional approval notes, or a nonblank reason for rejection (maximum 500 characters). Review reasons are now persisted in restricted audit metadata; seller confirmation/rejection reasons remain persisted on the campaign. Never include credentials, pickup codes or unnecessary personal data in notes or public text.

## Sprint 2 operations

All seller routes require Seller plus resource ownership; all customer routes require Customer plus ownership. Lists use the same page/pageSize envelope. Status filters accept the respective documented enum values; invalid values return 400. Decimal stock fields are variant-unit counts, not an aggregate across different products. Monetary metrics are INR snapshot values, not payment receipts or revenue.

The retained GET /seller/dashboard replaces the invalid mixed-unit reservedQuantity aggregate with activeReservations (an integer count). Frontend consumers should prefer /seller/dashboard/summary and use per-variant inventory/per-campaign performance for quantities.

| Method and route | Request / filter | Response |
| --- | --- | --- |
| GET /seller/dashboard/summary | None | Product/campaign/reservation/pickup counts, reservationValueInr and fulfilledValueInr |
| GET /seller/campaigns | page, pageSize, optional status | Page of OperationCampaignResponse (public campaign fields plus commitment/fulfillment and decision/deadline fields), including own drafts |
| GET /seller/campaigns/{id}/performance | None | Campaign quantities/progress/deadlines, reservation counts, collected count and fulfilled snapshot value |
| GET /seller/campaigns/{id}/reservations | page, pageSize, optional status | Page of SellerReservationResponse, pickup ID/status; no customer contact or code |
| GET /seller/inventory | page, pageSize | All owned variants including undeclared zero stock, precise unit definition, available/committed/uncommitted/active-reserved/historic-fulfilled quantities and version |
| PUT /seller/inventory/{variantId} | {availableQuantity,expectedVersion} | InventoryResponse; 409 stale version or below existing holds; 400 precision/unit validation |
| POST /seller/campaigns/{id}/confirm | {idempotencyKey,reason} | OperationCampaignResponse; creates Pending pickups and customer notifications |
| POST /seller/campaigns/{id}/reject | {idempotencyKey,reason} (nonblank reason required) | OperationCampaignResponse; Failed, active reservations Expired, remaining stock released |
| GET /seller/pickups | page, pageSize, optional status | Page of PickupDetailResponse |
| POST /seller/pickups/{id}/ready | No body | PickupDetailResponse; repeated Ready is safe before deadline |
| POST /seller/pickups/{id}/collect | {code} | PickupDetailResponse; 400 incorrect/expired code, 429 lockout, 409 closed/replayed pickup |
| GET /me/reservations | Existing list contract | Page of snapshot summaries; follow detail route for next actions |
| GET /me/reservations/{id} | None | ReservationDetailResponse with status, approved location, deadlines, pickup ID, nextAction and paymentStatus=NotTracked |
| GET /me/pickups | page, pageSize, optional status | Page of owned PickupDetailResponse |
| GET /me/pickups/{id} | None | Owned quantity/snapshot value, instructions, status, deadline, nextAction and NotTrackedOfflineResponsibility policy; never a code/hash |
| POST /me/pickups/{id}/code | No body | {code,expiresAtUtc}; plaintext code only in this response; 429 if reissued within one minute |
| GET /me/notifications | page, pageSize, unreadOnly (default false); any authenticated role | Page of own type/entity/timestamps |
| POST /me/notifications/{id}/read | No body; any authenticated role | 204, idempotent; 404 for another recipient |
| GET /admin/operations/health | Admin only | Retry/overdue/max-failure/legacy-cutover counts, checked timestamp and worker heartbeat |

Example inventory declaration: `{"availableQuantity":100.125,"expectedVersion":0}` for a fractional bulk variant. The response version must accompany the next update. Piece/Pack quantities must be whole. availableQuantity means stock still on hand, including stock already committed; do not send free stock as availableQuantity. A stale version must be refetched, not blindly retried. Initial declaration is version 0, accepted mutation increments the version, and each collection increments it again.

Example confirmation: `{"idempotencyKey":"seller-confirm-20261101-001","reason":"Inventory physically checked"}`. Reuse the exact key/outcome/reason on transport retry. Same-key retry returns current campaign state with no duplicated pickups/notifications; another key or changed outcome/reason returns 409. Confirmation/rejection accepts only AwaitingSellerConfirmation before the persisted deadline. Configured window is 1–1440 minutes. Ready and code issuance require the persisted pickup window (configured 1–168 hours).

Pickup verification expects a 32-character uppercase hexadecimal code. It is not a six-digit PIN. Customer code requests return a new secret after the one-minute reissue cooldown and invalidate any previous code; secrets expire after 30 minutes or at pickup deadline. A ready customer nextAction is RequestCodeAndCollect; do not request a code while rendering a passive GET page. Codes must never enter URLs or analytics. Customers cannot call the seller collection route. Five failed well-formed verifications lock a pickup for 15 minutes; role/IP endpoint throttling applies independently. Collection is single-use, so a network retry after success returns 409; refetch pickup status to distinguish successful prior collection.

All successful mutation responses are 200 except notification read (204); existing resource-creation routes retain 201. Reservation Location now resolves to an owned detail endpoint. Notification GET accepts an optional trailing slash. Notifications are durable in-app records, with no SMS/email/WhatsApp delivery claim. Current event types are ReservationCreated, CampaignTargetReached, SellerConfirmationRequired, CampaignConfirmed, CampaignRejected, CampaignExpired, PickupReady and PickupCompleted.

## Sprint 2.1 seller frontend additions

Existing public seller/product DTOs and existing mutation contracts are unchanged. These routes are additive:

| Method and route | Authorization | Contract |
| --- | --- | --- |
| GET /seller/profile | Authenticated current owner, including Customer-only onboarding accounts | SellerPrivateProfileResponse; 404 if this account has no seller profile |
| GET /seller/products | Seller role, current owner | Page of SellerProductResponse; optional status=Active/Inactive plus standard page/pageSize; includes inactive products |
| PUT /seller/products/{id}/status | Approved active Seller and product owner | `{ "status": "Active" }` or `{ "status": "Inactive" }` → SellerProductResponse; 200 including unchanged-status retries |
| GET /seller/pickups/{id} | Seller role and pickup seller ownership | Existing PickupDetailResponse allowlist; 404 for missing/another seller's pickup |

SellerPrivateProfileResponse has id, businessName, sellerType, description, verificationStatus, status, contactEmail, contactPhone, fssaiRegistrationNumber, reviewedAt and pickupLocations. Its private location records add latitude, longitude, isActive and isPickupEnabled to the existing address fields. All retained locations are returned in stable ID order; no recency/current-location claim is made because the existing schema has no such marker. No owner ID may be supplied to select another seller. Pending/rejected owners may read their own profile without a Seller claim; existing revoked-role token rules still apply, so a rejected owner whose token previously claimed Seller must log in again. Public /sellers APIs still omit private contact, registration, coordinates and review metadata. Admin review reasons are available only through the restricted audit API, not this owner profile endpoint.

SellerProductResponse has the same flat fields as ProductResponse plus status (Active/Inactive); public ProductResponse remains unchanged. Product status body must contain a defined non-null status; missing/null/unknown/extra fields return 400. Unknown status filters and invalid pagination return 400. Cross-owner edits return 404; Customer-only callers return 403; unauthenticated callers return 401. Same-status writes are idempotent without duplicate audit records. No product/variant metadata, stock quantities or campaign states are changed by status writes.

Deactivation returns 409 if any variant supports a PendingApproval, Scheduled, Open, TargetReached, AwaitingSellerConfirmation, Confirmed or FulfillmentInProgress campaign, even if legacy data has zero committed quantity. Outstanding stock holds, Active reservations or Pending/Ready pickups also block deactivation irrespective of campaign status. Draft-only references without holds/outstanding commitments and fully resolved terminal history permit deactivation. Draft submission rechecks active product eligibility. Activation requires an active category and at least one active variant. The existing seller-first lock serializes status changes with campaign submission, joins, stock declarations, collection and expiry; a race cannot leave a new campaign hold on an inactive product.

Seller pickup detail returns only id, reservationId, campaignId, status, quantity, totalPriceSnapshot, instructions, deadlineUtc, paymentPolicy and nextAction. It never serializes code, codeHash, code timestamps, attempt/lockout fields, customerUserId/contact fields or collector identity. The existing customer-oriented nextAction strings remain for DTO compatibility; seller actions use status/deadline and the existing ready/collect routes. A detail GET neither issues nor consumes a code.

Frontend integration now uses private profile reads for prefill, the owned catalog for inactive visibility/reactivation and direct pickup-detail reads. Profile replacement behavior (append location and reset review), metadata-edit restrictions and all pickup verification semantics remain unchanged. No database migration is required.

## Frontend Sprint 3 administration additions

Every route below requires the existing Admin JWT policy and a second service-level Admin check. Anonymous requests return 401; Customer/Seller-only requests return 403. No Admin signup, role-grant, arbitrary editor, code issuance or force-retry route was added. These server-paginated read APIs supplement, rather than replace, the existing pending lists and review mutations.

| Method and route | Query | Response |
| --- | --- | --- |
| GET /admin/dashboard/summary | None | AdminSummaryResponse with total/pending/approved seller counts, pending/active/awaiting-confirmation/expired campaign counts, total reservations and awaiting-collection pickup count |
| GET /admin/sellers | page, pageSize, optional verification status, search | Page of AdminSellerResponse; search is a case-insensitive literal substring of businessName |
| GET /admin/sellers/{id} | None | AdminSellerResponse with owner reference, business/type/status, contact/FSSAI, review time and retained private pickup locations |
| GET /admin/campaigns | page, pageSize, optional campaign status, search, failedOnly | Page of AdminCampaignResponse; search matches title or seller business name; failedOnly means persisted workerFailureCount > 0 |
| GET /admin/campaigns/{id} | None | AdminCampaignResponse: operation/campaign snapshots, seller business/verification, product ID/name, variant/SKU/unit, committed/fulfilled quantities/deadlines and safe retry metadata |
| GET /admin/categories | page, pageSize, optional active, search | Page of AdminCategoryResponse including inactive categories; search matches name |
| GET /admin/products | page, pageSize, optional resource status, search | Page of existing SellerProductResponse for all sellers, including inactive products; search matches name |
| GET /admin/reservations | page, pageSize, optional reservation status, campaignId | Page of AdminReservationResponse: IDs for reservation/campaign/seller, quantity, unit/total price snapshots, status, creation time; no customer identity/contact |
| GET /admin/pickups | page, pageSize, optional pickup status, campaignId | Page of AdminPickupResponse: pickup/reservation/campaign/seller IDs, quantity, status, deadline and collection time; no code/hash/attempt/customer fields |
| GET /admin/audit-logs | page, pageSize, optional action, entityType, actorId, entityId, fromUtc, toUtc | Page of AdminAuditResponse: timestamp, actor reference/display name, action, entity type/ID, correlation ID, allowlisted safeMetadata |

All lists use `{items,page,pageSize,total}`, default pageSize 20, maximum 100, page 1–100000, and stable ID tie-breakers. search/action/entityType filters are bounded to 100 characters; oversized filters, unknown statuses and invalid pagination return 400. Search is performed in PostgreSQL, not by downloading all records. Audit action/entityType are exact-case matches; IDs are exact UUIDs. Audit time endpoints are inclusive UTC timestamps with fromUtc <= toUtc. Counts and pages are READ COMMITTED snapshots and may change between concurrent reads; the dashboard is not a transaction-wide reporting snapshot.

Active campaigns means Scheduled/Open/TargetReached/AwaitingSellerConfirmation/Confirmed/FulfillmentInProgress; awaiting confirmation is a subset. Pending/ReadyForPickup both count as awaiting collection. Approved seller count refers to verification status, including inactive approved records. The UI explicitly labels counts as operational, never financial metrics.

AdminAuditResponse does not expose SafeMetadataJson verbatim. Only a bounded string `reason` is returned for known review/failure actions; other keys/actions or malformed/non-object data produce no metadata. Historical reasons were not previously recorded and are not backfilled. New seller/campaign approve/reject actions save the supplied bounded reason with the existing transactional audit event; rejection now requires a nonblank reason (400 otherwise). Existing approval payloads with reason=null remain valid. State/ownership/eligibility/locking checks and 409 on already-reviewed resources remain unchanged; there is no retry-key contract for these review routes.

GET /admin/operations/health is reused unchanged. Persisted retry metadata is shown as campaign work items; workerErrorCode is normalized to processing_failed rather than returning arbitrary internal error text. No job reset, stack trace, credential or raw application-log API exists. Governance is read-only outside the existing controlled review endpoints. No migration is required: review reasons use the existing audit JSON column, and the new APIs use existing tables/indexes.

## Errors

RFC 7807 `application/problem+json`, standard status/title plus traceId; validation adds `errors` keyed by property. 400 validation/unknown fields, 401 invalid/revoked/missing token, 403 insufficient role or seller eligibility, 404 missing or another seller/customer's resource, 409 lifecycle/capacity/idempotency/constraint conflict, 429 rate limit, 500 generic unexpected failure. Malformed route UUIDs return 404. Framework JSON parsing errors do not expose request bodies.

## Sprint 5 Admin MFA and health contracts

Admin password-only authentication is intentionally no longer supported. Customer/Seller login remains compatible: `accessToken` is populated and new optional challenge fields may be ignored. Admin clients must implement this flow; never interpret a challenge as an access token.

| Route | Purpose and request | Response / access |
| --- | --- | --- |
| POST /auth/login | Existing email/password | Customer/Seller TokenResponse; Admin `{accessToken:null,expiresAtUtc,mfaChallenge,enrollmentRequired}` |
| POST /auth/mfa/enrollment | `{challenge}` from a password-proven first-enrollment challenge | `{sharedKey,issuer,accountName}`; no-store; only within operator-authorized enrollment window |
| POST /auth/mfa/verify | `{challenge,code,useRecoveryCode:false}` for authenticator, or true for an enrolled account's recovery code | `{accessToken,expiresAtUtc,recoveryCodes}`; ten codes once on enrollment, otherwise empty array |
| GET /health/live | Status-only public process probe | 200 `{status:"live"}` |
| GET /health/ready | Bounded DB/migration/worker-progress readiness probe | 200 `{status:"ready"}` or safe 503 `{status:"not_ready"}` |

MFA challenges are opaque 43-character base64url values, expire after five minutes, are account/security-context/purpose bound, and become invalid after consumption, expiry, a fresh password challenge or relevant role/security changes. Enrollment requires a 15-minute operator authorization, fixed Admin role, active account and no existing MFA. Tokens/codes go only in POST bodies, never URLs. Wrong factor/challenge generally returns 401; malformed code shape returns 400, closed enrollment 403, limiter exhaustion 429. Do not blindly retry consumed enrollment responses; follow the MFA recovery runbook.

TOTP is six digits/30 seconds through Identity's authenticator provider. Accepted code digests reject replay for ten minutes across concurrent/fresh challenges. Five failures lock account authentication for fifteen minutes; password-only success does not reset Admin MFA failures. Recovery codes are case-preserving, single-use and encrypted at rest. Redemption/security changes revoke earlier sessions. Admin JWTs require `amr:mfa`, current enabled MFA and current role/version/status, with a ten-minute maximum access lifetime. No HTTP Admin grant/reset/disable endpoint exists. The controlled CLI is documented in admin-mfa-provisioning.md.

Authentication IP limits remain 10/minute, MFA endpoints 30/minute plus durable account/challenge attempt bounds, Admin operations 60/minute/actor and global IP budget 300/minute. All existing ownership/secret/price contracts remain authoritative.

Protected `/admin/operations/health` adds `exhaustedCampaigns`, `workerMaxAttempts` and optional worker `lastProgressAtUtc`. Automatic attempts stop at the configured maximum (default ten); no business state is fabricated. Exhausted items require reviewed operator recovery. Existing retry/backlog/legacy fields remain. Health does not certify external TLS, privilege grants, backups or actual hosting.

Sprint 5 rate-limit clarification: only POST `/api/v1/auth/register` and `/api/v1/auth/login` consume the 10/IP/minute credential quota. GET `/api/v1/auth/me` and logout retain authentication and global throttling. The additive `exhaustedCampaigns` field identifies attempts at/above `workerMaxAttempts`; historical `retryingCampaigns` includes exhausted failures.

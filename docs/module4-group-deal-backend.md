# Module 4 — Group Deal backend

Local implementation only. No production migration, grants, deployment, commit, or push is authorized. The recovered Sprint 5.1 container, volume, network, database and port 55432 are excluded from verification.

## 1. Architecture audit and reuse

The baseline already has `GroupBuyCampaign`, `CampaignPriceTier`, `GroupBuyReservation`, seller-owned `Product`/`ProductVariant`, inventory, pickup records, payment orders/webhooks/refunds, internal notifications, audit logs, Admin MFA, a bounded durable pilot job runner, discovery, `DiscoveryDemand` and `CustomerInterest`. Application interfaces are IMarketplace, IOperations, IAdministration, IPhaseOne and IDiscovery; Marketplace implements the transactional commercial workflows. EF/Npgsql maps these to established tables and uses xmin on campaigns. No replacement reservation, order, payment, seller, demand or notification system was added. Private seller/Admin offer reads preserve draft visibility without publishing drafts. Public detail includes structured unit quantity and the existing public-safe seller pickup address.

Unchanged commercial components include provider adapters, webhook signatures, encrypted delivery addresses, settlement refund queue, payment reconciliation, pickup pass hashing/verification, retry leases, and runtime/migrator separation. Changes extend catalog types, campaign metadata, capacity handling and safe read projections. Duplicating any of those financial or identity components would create conflicting consistency boundaries.

## 2. Extensions

ProductType now includes Physical=0, Service=1 and Membership=2. Nonphysical catalog variants represent one service/membership entitlement (Piece, UnitQuantity=1), rather than manufactured stock. Campaigns add nullable OriginatingDemandId, OfferTerms (4,000 characters), and DemandOfferNotifiedAtUtc. IGroupDeals adds projections, opportunity browsing, idempotent draft linkage, and restricted Admin cancellation. No new domain entity/table is introduced.

## 3. Demand to seller offer

A verified seller creates a catalog entry and campaign draft through existing routes, configures price tiers, and PUTs the originating demand and customer terms onto that draft. Submission and Admin approval reuse existing endpoints. The demand must be open, in the same category and configured area, with Product demand for physical items or Service demand for service/membership items. A competitor can answer a category-compatible demand using its own catalog item; it cannot edit the original seller's catalog item. Once selected, the demand identity cannot be replaced. Multiple campaigns/sellers can reference the same demand; no uniqueness constraint merges their economics. Originating demand and area have a composite restrictive FK.

## 4. Deal types

Physical products retain stock-backed quantity reservations and bulk-unit pricing. Services and memberships use a finite campaign entitlement capacity with one entitlement per customer and integral tier thresholds. They do not create or decrement inventory rows. Membership is a single group-priced entitlement, with no recurring billing. Existing catalog ownership and category validation remain authoritative. Changing catalog type requires clearing physical stock first, and campaign-linked catalog entries remain immutable. Service/membership fulfillment uses seller pass redemption; founder delivery is rejected for these types. This release does not implement appointment scheduling or installation tracking.

## 5. Tier pricing

The existing TierPricing validator requires 1–10 ordered positive thresholds, increasing quantities, no duplicates, positive two-decimal prices, and nonincreasing prices bounded by the reference price. Threshold precision is three decimals for physical bulk units, integral for entitlements. The new projection returns the last tier whose threshold is <= commercially committed quantity, and the first strictly greater tier. Below the first threshold, currentTier is null. At 18 with thresholds 10/20/30, current=10, next=20, remaining=2. Existing `CurrentProjectedUnitPrice` remains a maximum/projected quote, not proof of an unlocked tier.

Demand-linked offers cannot configure the historical near-target exception. Their actual threshold must be reached. Unlinked campaigns retain the existing disclosed rescue policy.

## 6. Count semantics

InterestedCount uses Module 3 semantics: distinct active interests belonging to active accounts, including legacy product/area interests without a DemandId. ReconfirmedCount is null because reconfirmation is not implemented. CommittedMemberCount counts distinct customers with Active/Completed reservations. PaidMemberCount counts distinct captured-payment customers attached to such reservations. Pending/authorized holds are excluded from paid progress. Prices use committed **quantity**, not member count for physical bulk products. Each service/membership entitlement equals one member. Campaign.CommittedQuantity is the seller's capacity/stock allocation; Campaign.ReservedQuantity is accepted captured reservation quantity. Neither is the free demand-interest count.

## 7. Progress and read consistency

GroupDealProgress embeds current/next tier, committed quantity, remaining quantity and highest-tier status. FinalQuantity preserves the close-time pricing basis. Detail reads run under a PostgreSQL repeatable-read snapshot, so separate counts/holds/prices agree. Lists batch tiers and distinct customer aggregates for the selected page instead of fetching full detail per row. Sorts are only newest and ending-soon, with GUID tie breakers. No invented ranking, ratings or discounts.

## 8. Seller authorization

Routes require Seller authorization. Mutations resolve ownership from Actor.UserId, lock the seller then campaign, enforce approved active seller/account status, and return 404 for a different seller's campaign. Opportunities return only ID/title/type/category/area/aggregate interest. They never return participant records, requirements, identities or contacts. Existing seller campaign/performance endpoints remain the owner-only operational reads.

## 9. Lifecycle

Draft -> PendingApproval -> Scheduled/Open -> MinimumReached -> Finalizing -> Successful/Failed -> existing fulfillment/completion states. Rejection returns an editable Draft under the established lifecycle; it is audited. Submission retries in PendingApproval return the existing campaign. Price tiers are immutable after submission. Approval, scheduled opening, cancellation and finalization remain server-controlled UTC actions. MinimumReached now permits the restricted pre-finalization Admin cancellation.

## 10. Join Group

Use POST /api/v1/mvp/customer/orders with campaignId, quantity, idempotencyKey, fulfillmentMode and optional delivery address. Authentication uses the existing Customer policy. The server computes maximum price and delivery fee, validates deadline, capacity and configured area, and creates a hold. Payment capture creates the existing reservation and advances paid quantity. A click or free interest never does so. Demand-linked and nonphysical offers allow one active/pending commitment per customer, checked while holding the seller/campaign locks. Same-key retries return the same order and changed payload conflicts. Legacy unrelated physical campaigns retain their established multiple-order behavior.

## 11. Existing payment integration

No financial provider call was added. Existing gateway availability, pilot enablement, idempotent provider orders, webhook deduplication, reconciliation, reservation creation and refund records are reused. Authorizations and pending checkouts reserve capacity temporarily but do not unlock tiers. Late captures after close/cancellation follow the existing refund path.

## 12. Settlement strategy

The existing model is initial capture at the highest/first tier followed by deterministic partial refund after finalization (model B). The finalizer computes the achieved price from accepted captured reservation quantity, snapshots it, updates reservation prices, and queues one tier-difference refund per eligible order. Failure queues a full captured refund. Delivery fees remain part of the established amount calculation. Provider minimum refund validation happens before checkout. Refunds are asynchronous and may require operational retry; the API promises a queued refund rather than instant settlement. Existing sandbox/real-provider configuration remains unchanged.

## 13. Fulfillment

Physical offers retain free SellerPickup and founder delivery when configured. QR plus backup code, one-time redemption and stock handover are reused. Service/membership entitlements use SellerPickup as the established technical mode for seller redemption. Entitlement redemption changes campaign committed/fulfilled capacity and audits SellerEntitlementRedeemed without touching inventory. The default existing pass fulfillment deadline is two days after finalization. Offers must explain the entitlement/redemption expectations in terms; extended installation schedules need a later structured fulfillment policy.

## 14. Cancellation and refunds

Detail exposes the server cancellation deadline, whether the pre-finalization cancellation window is open, and group-failure/better-tier refund behavior. Existing customer order cancellation remains available before campaign deadline/finalization. New Admin cancellation requires a bounded reason and is limited to pre-finalization states. It atomically expires unpaid holds, cancels active reservations, queues captured refunds, releases capacity and audits the action. Repeating cancellation cannot queue a second refund. Terminal deals cannot be reopened.

## 15. Admin governance

Existing /api/v1/admin/group-buys/pending and approve/reject routes, /api/v1/admin/campaigns reads, /api/v1/admin/audit-logs and Admin MFA policy are reused. New POST /api/v1/admin/group-buys/{id}/cancel uses the same Admin policy/rate limiter. No generic data mutation endpoint was added. Financial finalization uses the existing finalizer, not an Admin status override.

## 16. PostgreSQL concurrency

Seller-first then campaign FOR UPDATE serializes all commercial capacity accounting and coordinates inventory across seller campaigns. Pending holds plus accepted reservations cannot exceed TargetQuantity. Capture, cancellation and finalization acquire the same locks and re-read tracked values. Campaign xmin and existing indexes/constraints remain in place. Finalization snapshots its result in the same transaction as refunds/fulfillment/audit; FinalizedAtUtc makes retries no-ops. No financial action relies on an unlocked application-only count.

## 17. Idempotency and notifications

Demand detail now reports active public offers, an OffersAvailable state and a demand-filtered Group Deals route; its free-interest semantics remain unchanged.

Draft linkage is PUT: identical retries do not create extra audit rows. Submission retries while PendingApproval return the existing campaign. Existing payment keys/webhook IDs/refund reasons and finalization marker are reused. Approval or scheduled activation appends a single durable DemandOfferAvailable notification per active interested customer, using existing unique deduplication keys. DemandOfferNotifiedAtUtc prevents repeated fan-out, including the zero-recipient case. Interest is never automatically converted into an order.

## 18. Security and privacy

Strict JSON binding, authorization, Admin MFA, CORS and rate limiting are unchanged. New opportunity responses are aggregate-only. Deal seller information is an allowlist of display name and verified status. Detail participation is scoped to the authenticated caller; no other customer identity, seller contacts, payment identifiers, encrypted addresses, notes, secrets or bank data is returned. Area resolution reuses DiscoveryAreas and retains 503 service_area_unconfigured. No real areas or PIN codes were invented.

## 19. Schema changes

Only campaign metadata columns, two demand indexes, the restrictive composite demand/area FK and ProductType value check are added. Existing rows backfill OfferTerms to empty string and leave origin/notification timestamp null. Existing reservations, prices, demands, interests and financial records are preserved. There are no table renames.

## 20. Migration

20261108135842_GroupDealDemandOffers is the single forward migration. Its Down throws NotSupportedException because dropping offer linkage/terms would lose commercial data. Historical migrations, including 20261107193743_DemandModule, are untouched. EF migration-history locking/transactions provide restart protection. Validation must use fresh Testcontainers PostgreSQL only; never the recovered Sprint 5.1 endpoint.

## 21. Runtime grants

No additional grant is required for this extension under scripts/grant-runtime.sql: it already permits table-level INSERT/UPDATE on products/group_buy_campaigns and commercial tables, SELECT on all tables, audit append, and tier replacement DELETE. New reads: discovery_demands, customer_interests, sellers, users, products, product_variants, locations, campaign_price_tiers, group_buy_campaigns, group_buy_reservations, payment_orders. New linkage updates: group_buy_campaigns. Publication inserts: internal_notifications and audit_logs. Cancellation updates campaigns/orders/reservations and inserts refunds/audit. Entitlement fulfillment updates existing fulfillment/campaign/reservation rows. No additional deletes or production grant changes were made. Migrator/runtime separation remains mandatory.

## 22. APIs

New endpoints:

- GET /api/v1/seller/group-buys/{id}/offer � owner-only draft/submitted/active metadata including terms and originating demand.
- GET /api/v1/admin/group-buys/{id}/offer � Admin MFA review of the same metadata.

- GET /api/v1/group-buys/deals — page/pageSize, categoryId, demandId, type, status, serviceArea, search, sort=newest|ending-soon.
- GET /api/v1/group-buys/{id}/deal — safe commercial detail and caller participation.
- GET /api/v1/seller/demand-opportunities?serviceArea=... — verified seller aggregate opportunities, paginated.
- PUT /api/v1/seller/group-buys/{id}/demand-offer — {demandId, terms}, tier-priced draft only.
- POST /api/v1/admin/group-buys/{id}/cancel — {reason}, Admin MFA required.

Reuse existing POST/PUT seller products/group-buys, PUT /api/v1/mvp/seller/campaigns/{id}/tiers, group-buys/{id}/submit, Admin approve/reject, seller campaign reads, /api/v1/mvp/customer/orders, own orders/cancel, finalization and fulfillment APIs. GroupDealType strings are Product, Service, Membership. Catalog ProductType strings are Physical, Service, Membership. Quantity labels remain explicit for bulk products.

## 23. Tests and evidence

New domain tests cover exact 9/10, 18/20, 19/20 and 29/30 boundaries, below-minimum and highest-tier cases, deterministic ordering, tier economics, demand type/category/area compatibility and the separation of interest from tier input. New PostgreSQL tests cover all three offer types, safe detail/list/opportunity responses, duplicate same-key requests, prevention of different-key duplicate commitment, free interest isolation, captured progress, no nonphysical inventory, audited publication/linkage, Admin cancellation/refund deduplication, 20 customers competing for 10 slots, and simultaneous finalization. See the local evidence directory and final report for executed checks and any failures; a test's presence alone is not proof it passed.

## 24. Known limitations

No appointment calendar, installation milestones, subscription renewal, maps, media storage, third-party delivery or seller ranking was added. Existing catalog has no general image collection in its ProductResponse; this extension does not invent one. Service/membership delivery is intentionally unavailable. Existing two-day fulfillment/redemption policy may be unsuitable for long-running installations; such offers need an explicit policy extension before rollout. Customer location UX remains separate. Existing financial provider activation is an operational prerequisite, not automatically enabled by this module.

## 25. Reconfirmation

Deferred. ReconfirmedCount=null explicitly distinguishes unsupported tracking from an actual zero. A future separate offer-specific signal must reference the campaign/customer and must not change reserved quantity, paid members, inventory or tier thresholds. Module 3 interests continue unchanged.

## 26. Production rollout considerations

Review service entitlement wording and fulfillment deadlines with the product owner. Review the generated SQL and backup/forward-fix procedure; apply only through approved migrator credentials during a separately authorized rollout. Preserve service-area approval, gateway enablement, runtime grants and Admin MFA. Verify async refund monitoring and retry operations with real provider configuration. No production operation, credential change, protected-resource operation, frontend edit, commit, push or deployment is part of this implementation.

## Local verification environment

The pinned local .NET 10 SDK is used. NuGet vulnerability-feed retrieval was unavailable in this environment (NU1900); validation was invoked with the process-local NuGetAudit=false setting. No repository security/audit policy was disabled. This is not a successful online dependency-vulnerability audit. Run scripts/verify-module4-readiness.ps1 for the complete isolated local gate.

## File inventory

- `docs/module4-group-deal-backend.md`
- `scripts/verify-module4-readiness.ps1`
- `src/GroupBuy.API/GroupDealEndpoints.cs`
- `src/GroupBuy.API/Program.cs`
- `src/GroupBuy.Application/DemandContracts.cs`
- `src/GroupBuy.Application/GroupDealContracts.cs`
- `src/GroupBuy.Domain/CampaignRules.cs`
- `src/GroupBuy.Domain/GroupDeals.cs`
- `src/GroupBuy.Domain/Models.cs`
- `src/GroupBuy.Domain/Operations.cs`
- `src/GroupBuy.Infrastructure/Database.cs`
- `src/GroupBuy.Infrastructure/DemandReads.cs`
- `src/GroupBuy.Infrastructure/DiscoveryQueries.cs`
- `src/GroupBuy.Infrastructure/GroupDealDatabase.cs`
- `src/GroupBuy.Infrastructure/Marketplace.Administration.cs`
- `src/GroupBuy.Infrastructure/Marketplace.GroupDeals.cs`
- `src/GroupBuy.Infrastructure/Marketplace.Operations.cs`
- `src/GroupBuy.Infrastructure/Marketplace.PhaseOne.Fulfillment.cs`
- `src/GroupBuy.Infrastructure/Marketplace.PhaseOne.cs`
- `src/GroupBuy.Infrastructure/Marketplace.cs`
- `src/GroupBuy.Infrastructure/Migrations/20261108135842_GroupDealDemandOffers.Designer.cs`
- `src/GroupBuy.Infrastructure/Migrations/20261108135842_GroupDealDemandOffers.cs`
- `src/GroupBuy.Infrastructure/Migrations/MarketplaceDbModelSnapshot.cs`
- `tests/GroupBuy.IntegrationTests/BackendTests.cs`
- `tests/GroupBuy.IntegrationTests/Module4GroupDealTests.cs`
- `tests/GroupBuy.UnitTests/GroupDealTests.cs`

## Final validation results � 10 November 2026

Final evidence: `.local/module4-final-20261110b/` (the earlier attempt logs are retained separately).

- Build: 0 warnings, 0 errors using the pinned .NET 10 SDK.
- Unit suite: 159 passed, 0 failed, 0 skipped; 19 new Module 4 domain cases.
- Complete integration suite: 326 passed, 0 failed, 0 skipped; all 13 Module 4 cases passed.
- Dedicated migration checks: clean migration 1/1, upgrade migration 1/1, restart protection 3/3.
- EF pending-model check: no changes since the last migration. No empty follow-up migration is needed.
- PostgreSQL concurrency: 20 customers competing for 10 entitlements, duplicate checkout, simultaneous finalization, cancellation/join and deadline races, and quantity boundaries 9/10, 19/20 and 29/30 passed.
- `git diff --check`: passed. Secret-pattern scan across all 26 changed files: zero findings.
- Gate cleanup: zero new Testcontainers resources and zero new API processes remaining.
- Completion marker: `.local/module4-final-20261110b/MODULE4-COMPLETE.txt`.

Earlier verification found a test fixture attempting to mutate Module 3's immutable demand identity. The fixture now creates the correct canonical identity initially; the database guard was preserved. Earlier demand-race and local startup timing failures passed with their original limits unchanged in the final serial run. Inventory type validation now follows seller ownership validation, with a regression assertion preventing cross-seller type disclosure.

No frontend source, production configuration, production database, production grants, or protected recovered Sprint 5.1 resource was changed. HEAD remains `727f2d9`; all Module 4 changes remain uncommitted and unpushed. No deployment was performed.

Verdict: MODULE 4 BACKEND READY FOR FRONTEND INTEGRATION.

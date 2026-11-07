# Module 2 — Home, discovery and free demand

## Existing capabilities and scope

Reuses the existing catalog, category hierarchy, verified sellers, seller pickup locations, tier campaigns, payment orders/reservations, customer interests, service-area configuration, authentication, audit logs and rate policies. No inventory, seller, campaign, category, price, rating, image, GPS distance, fee or launch date is seeded by this module. The Render ingress contract, HTTPS/HSTS, host filtering, CORS, JWT/MFA, Data Protection and database TLS are unchanged.

The existing `/api/v1/categories` and `/api/v1/categories/{slug}` are active configured taxonomy metadata, not promises of local inventory or purchasability. Existing products, group-buys and `/api/v1/mvp/*` contracts remain available. Legacy group-buy endpoints include campaign history; their results must not be treated as current paid deals. Use the new discovery contracts for that purpose. Home only includes categories backed by actual discoverable product metadata or active demand. Inactive/future categories are not activated or fabricated.

## Configuration and location

Reuses `Pilot:ServiceAreas`: at most ten entries, each with `Id` (lowercase letters/digits/hyphen, 1–80), `Name` (1–100), and 1–30 six-digit Indian PIN codes starting with 1–9. Duplicate IDs or PIN codes are rejected as ambiguous. No real PIN code is provided here. Operators must supply approved actual values. Production also requires either existing `Pilot:Approved=true` or the narrower `Discovery:ServiceAreasApproved=true`; the latter approves discovery areas only and does not enable Pilot, payments or providers. Neither approval is set by this change.

Missing, invalid or unapproved area configuration returns 503 `service_area_unconfigured` on discovery requests. It does not change API startup, existing endpoints or health readiness. Optional `postalCode` is trimmed and validated; optional `serviceArea` is an exact configured ID. If both are supplied they must identify the same area. With one configured area, omission selects it; with multiple areas, omission returns `selection_required` and empty discovery results. A valid but unknown PIN/area returns `unsupported` and empty results. Responses never invent locality names, GPS distances or launch dates. Location is a filter, not an authorization credential.

Example deployment keys (values must come from the operator, not these placeholders):

```text
Pilot__ServiceAreas__0__Id=<approved-id>
Pilot__ServiceAreas__0__Name=<approved-display-name>
Pilot__ServiceAreas__0__PostalCodes__0=<approved-pin>
Discovery__ServiceAreasApproved=true
```

There is no new expansion-notification subsystem. Unsupported areas cannot create an out-of-area demand.

## Routes

All GET routes below are anonymous/public and remain subject to global rate limits and explicit CORS. All are under `/api/v1`.

| Method and route | Parameters | Response |
|---|---|---|
| GET `/discovery/service-area` | `postalCode?`, `serviceArea?` | `{state,supported,postalCode,serviceArea,displayName}` |
| GET `/discovery/home` | location, `categoryId?` | `{location,categories,nearYou,trendingDeals,popularDemands,newDeals}` |
| GET `/discovery/search` | `q`, location, `categoryId?`, pagination | `{location,query,deals,demands,products,categories,noResults,createDemand}` |
| GET `/discovery/search/suggestions` | `q`, location, `categoryId?`, `limit?` (default 8, 1–10) | `{location,query,items:[{type,id,text}]}` |
| GET `/discovery/deals/trending` | location, `categoryId?`, pagination | page of deal cards |
| GET `/discovery/deals/near-you` | location, `categoryId?`, pagination | page of deal cards |
| GET `/discovery/deals/new` | location, `categoryId?`, pagination | page of deal cards |
| GET `/discovery/demands/popular` | location, `categoryId?`, pagination | page of demand cards |
| GET `/categories/{slug-or-id}/discovery` | location, pagination | `{location,category,subcategories,deals,demands}` |
| POST `/discovery/demands` | `{title,serviceArea,categoryId?}` | `{demandId,joined:true}`; Customer required |
| POST `/discovery/demands/{id}/join` | no body | `{demandId,joined:true}`; Customer required |
| POST `/discovery/demands/{id}/withdraw` | no body | `{demandId,joined:false}`; Customer required |
| GET `/discovery/demands/{id}/membership` | no body | `{demandId,joined}` for the caller only; Customer required |
| POST `/admin/discovery/demands/{id}/close` | no body | 204; existing Admin policy including MFA required |

Pagination uses `{items,page,pageSize,total}`, default page 1/size 20, page 1–100000 and size 1–100. Search paginates each explicitly typed section independently with the same page parameters. Home has at most six cards per section and twenty categories; nearYou reuses the newest area-eligible preview, avoiding another query batch. Category detail returns at most 100 discoverable immediate subcategories and paginated deals/demands. Category filtering includes the selected category and actual immediate children (the existing Phase 1 hierarchy); no speculative taxonomy is added. Unknown/inactive category detail returns 404; an active category without local data has `isDiscoverable:false` and empty sections.

Errors follow the repository Problem Details format: 400 invalid query, postal code, page/size, limit, category or public demand title; 401 unauthenticated protected action; 403 role/MFA failure; 404 missing category/demand; 409 closed demand or conflicting canonical demand category; 429 existing rate limits; 503 unconfigured service area. Customer mutations use the existing customer-auth rate policy; Admin close uses the Admin policy. No client-supplied counts, prices, identity or progress fields are consumed.

## Eligibility and cards

One shared discovery query serves Home, Search, Suggestions, category detail and all deal rankings. A paid deal requires enabled Pilot and an available existing payment gateway, tier pricing, Open/MinimumReached state, current start/end window, approved active seller and active owner, active product/category/parent/variant, active matching seller pickup location and configured area, declared inventory/committed capacity and space for the minimum additional commitment including unexpired pending payment holds. Disabled payment/Pilot settings remain disabled; they result in no currently purchasable deal cards, not fabricated offers. Discoverable products are metadata with `type:product`, `hasEligibleDeal` and `purchasable`; these booleans are false without an eligible paid campaign.

Deal cards (`type:deal`) expose campaign/product/category IDs, campaign/product titles, public seller business name, price/tier/progress information, public locality, status, publication/end timestamps and known fulfillment capabilities. No customer/seller contact fields, addresses, provider IDs, notes, tokens or private attributes are projected. No image field exists in the current product model; no image/rating is invented.

Paid quantity is a SQL sum of Captured orders with captured timestamp, no finalized/cancelled amount, no refunded amount or queued refund, active customer, and a matching active reservation of the same customer/campaign/quantity. Free interest, legacy unpaid reservations, pending/authorized/failed payments, cancelled/expired reservations and refunded/refund-pending orders cannot inflate it. A captured order's original ten-minute checkout-hold expiry does not erase a valid paid commitment.

Price reuses `TierPricing.Achieved`: highest attained threshold, falling back to the first tier's maximum checkout price before minimum is reached. This is a current projected price, not guaranteed finalization. Minimum quantity is the first tier. Next tier is the first threshold above paid quantity. Progress is `min(100, round(paid/minimum*100,2))`; target capacity is separately returned. Reference price is the stored positive value only when not below effective price. Discount is `round((reference-effective)/reference*100,2)`; otherwise reference/discount are null. Pickup is derived from eligible active locations; delivery is advertised only with an existing nonnegative configured fee and delivery-fee approval. No fees are invented.

Demand cards (`type:demand`) expose only title, optional category/product/demand IDs, source (`product_interest` or `free_text`), configured public locality, active distinct-customer count and update time. `isFree:true` and `guaranteesFulfillment:false` distinguish interest from paid commitment, inventory or fulfillment promises. Product-bound aggregates use ProductId and the existing MVP customer-interest action; free-text aggregates use DemandId and the new demand actions. No participant identities or account data are returned. Personal joined-state is returned only by authenticated mutation responses and the caller's protected membership lookup; public GETs are not personalized.

## Ranking and search

* Trending: valid paid quantity descending, publication time descending, campaign ID ascending. No views or free interest are counted.
* New and Near You: publication time descending, campaign ID ascending. Publication is the latest existing CampaignApproved audit timestamp, falling back to actual StartsAtUtc for historical records without that audit event. Draft creation time is not a publication signal.
* Near You means eligible for the selected configured area; it is not physical-distance ordering.
* Popular demands: active distinct-customer count descending, latest active-interest update descending, source then ID ascending. Duplicate joins do not update timestamps or increase counts. Closed demands and inactive/suspended-member interests are excluded.

Search normalizes the input with Unicode NFKC and collapsed whitespace, preserving normalized display case in `query`. It requires 2–100 normalized characters (raw maximum 256), rejects invalid control text, and uses parameterized case-insensitive PostgreSQL literal matching. `%`, `_`, quotes and backslashes are data, not SQL or pattern syntax. No stemming, fuzzy or accent-insensitive search is claimed. Suggestions use the same visibility rules, bounded SQL selections from four sources, then deterministic type/text/ID ordering capped at ten; product suggestions use prefix matching.

Suggestions are search-text handoffs. Resolve demand action targets from the subsequent search's typed demand cards: product-bound cards use ProductId, while free-text cards use DemandId. A suggestion ID by itself is not a promise of a free-text demand action route.

A zero-result search is still 200 with empty typed pages, `noResults:true`, and, in a supported area, `createDemand:{query,createMethod:"POST",createRoute:"/api/v1/discovery/demands",requiresAuthentication:true,createsAutomatically:false}`. Search never writes demand. The frontend must render query/title as text, not HTML, and require the customer to explicitly submit a demand. How Groudeal Works needs no backend configuration.

The same handoff is also supplied when product/category metadata matches but there is no matching eligible deal or active demand. In that case `noResults:false` accurately reflects the metadata results while still offering an actionable demand path.

## Free-text demand lifecycle, privacy and deployment

Free-text creation is explicitly authorized for this module. It extends the existing `customer_interests` membership table, instead of creating a second membership system. Each interest has exactly one target: ProductId or DemandId. Canonical title plus area is unique; repeat creation joins the same demand. Customer plus demand is unique. Transaction-scoped advisory creation locks and per-demand row locks serialize races. Joining/withdrawing is idempotent. Withdrawal remains possible after closure or area removal. Admin closure is audited, removes the demand from public results and prevents joins/recreation of that canonical closed demand. There is no automatic seller offer or reopening workflow.

Public title is deliberately public user-submitted content, not a private note. It is bounded, normalized and rejects obvious email/contact-number/link/markup inputs; this is not a comprehensive PII/moderation classifier. The frontend must explain its public nature, escape it as text, and avoid requesting personal information. Admin close is available for moderation. No account email, phone, name, precise address or member list is returned or logged by discovery. Product-bound and free-text requests are not merged by ambiguous product-name guesses.

Migration `20261107060508_HomeDiscoveryDemands` adds the free-text aggregate and extends existing interests without deleting data. ProductId becomes nullable, DemandId is optional, an XOR check enforces exactly one target, and FK/unique indexes enforce referential integrity and membership uniqueness. New indexes support new FKs/uniqueness; no speculative search indexes are added. The migration is forward-only and rejects destructive Down. Apply it with the separate authorized migrator before deploying this API, then review/apply the updated `scripts/grant-runtime.sql` to permit runtime INSERT/UPDATE on discovery_demands (SELECT is already granted on application tables). Runtime has no new DDL, role-administration or DELETE capability. Never apply this to the protected Sprint 5.1 database or test against Neon.

Keep free-text demand clients/mutations out of traffic until old API instances have drained. Once free-text interests exist, an older binary that assumes every ProductId is non-null is not a safe application rollback target; use a reviewed forward fix. Legacy product-interest clients remain compatible with this new API because those legacy endpoints explicitly return product-bound interests only.

Queries are no-tracking scalar projections with SQL counts/sums, bounded page selections and one tier query per selected deal page. No reservation/member collections are loaded and no per-card database queries are issued. No Redis, search server or caching is added. Counts/pages/sections are current reads, not a transactional availability guarantee; paid checkout remains authoritative under existing locks. The small Phase 1 dataset does not justify speculative full-text indexes; query plans and test query bounds are reviewed during validation. Broader taxonomy depth, expansion notifications, demand-to-offer conversion, comprehensive UGC moderation and linguistic/fuzzy search remain follow-ups.

## Validation

The complete frozen-code gate passed on 2026-11-07 using `scripts/verify-render-readiness.ps1`; ignored local evidence is under `.local/module2-final3/`, including `COMPLETE.txt`, per-stage logs and TRX counters. Restore/build passed with zero warnings/errors. Complete suites: **109 unit tests + 284 PostgreSQL integration tests = 393 distinct passes**, zero failed/skipped/aborted. New coverage is 25 discovery unit cases and 45 discovery integration cases. Overlapping focused stages passed: 67 ingress, 29 existing PostgreSQL/configuration, 31 authentication, 48 Admin/security, six seller/CORS, three restart-protection and 16 production/explicit-proxy cases. Empty-database/upgrade checks passed; EF reported no pending-model changes.

The home performance regression proves constant query batches as one deal grows to a six-card preview (at most ten SQL reader commands), with no per-card queries. PostgreSQL `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)` confirms a Limit node; the synthetic plan is retained in `.local/discovery-query-plan.json`. This is not a production latency guarantee.

Development failures were investigated, not hidden: a constructor projection could not be sorted in SQL and was replaced with translatable member projections; new test fixtures were corrected to read the existing string-enum/201 contracts and supply CORS configuration before host startup. Historical MFA and Pilot rollback tests had assumed all future migrations were reversible; they now exercise their own migrations' original exact guards and retained audit/failure evidence, then verify upgrade to latest. The new discovery test separately proves prohibited downgrade. Earlier failing runs remain under `.local/discovery-*`, `.local/module2-final/` and `.local/module2-final2/`; the final full gate is clean.

Final checks: `git diff --check` passed, secret-pattern findings zero across 25 changed files, zero new Testcontainers resources remaining, zero new backend API processes remaining. The Render adapter and production security configuration are unchanged. Only this documentation result was updated after the gate; application/test/script sources were unchanged. No frontend or protected Sprint 5.1 resources were changed. No Neon migration, production grant, commit, push or deployment was performed. Frontend integration can begin against these contracts; live rollout still requires the reviewed migration/grants, approved real service-area configuration and controlled deployment acceptance. Existing disabled providers/payments remain disabled.

## Changed-file inventory

* API: `src/GroupBuy.API/DiscoveryEndpoints.cs`, `src/GroupBuy.API/Program.cs`.
* Application: `src/GroupBuy.Application/DiscoveryContracts.cs`.
* Domain: `src/GroupBuy.Domain/DiscoveryDemand.cs`, `src/GroupBuy.Domain/PhaseOne.cs`.
* Infrastructure: `src/GroupBuy.Infrastructure/DiscoveryAreas.cs`, `DiscoveryDatabase.cs`, `DiscoveryDemandService.cs`, `DiscoveryQueries.cs`, `DiscoveryService.cs`, `PublicCatalog.cs`, `Database.cs`, `Marketplace.cs`, `Marketplace.PhaseOne.cs`.
* Migration/model: `src/GroupBuy.Infrastructure/Migrations/20261107060508_HomeDiscoveryDemands.cs`, its `.Designer.cs`, and `MarketplaceDbModelSnapshot.cs`.
* Tests: `tests/GroupBuy.UnitTests/DiscoveryInputTests.cs`, `tests/GroupBuy.IntegrationTests/DiscoveryTests.cs`, `tests/GroupBuy.IntegrationTests/MigrationSecurityTests.cs`, `tests/GroupBuy.IntegrationTests/PilotReliabilityTests.cs` (scope historical MFA/Pilot rollback assertions to their migrations; retain exact guard assertions, latest upgrades and preserved evidence).
* Operations: `scripts/grant-runtime.sql`, `scripts/verify-render-readiness.ps1`.
* Documentation: `docs/module2-discovery.md`, `docs/render-free-deployment.md` (migration-order cross-reference only; ingress behavior unchanged).

# Deployment and legacy cutover plan

Status: plan for separate review; NOT deployment approval. No production infrastructure or data was changed. See production-readiness-checklist.md and sprint4-security-audit.md. Current verdict: NO-GO for real-customer pilot until gates close.

## 1. Establish the release environment

Release owner records intended domains, pilot shops/users, access limits, support owner, maintenance window, RPO/RTO and stop criteria. Use a separate staging database with synthetic fixtures first. Inventory OS, .NET runtime, PostgreSQL minor, container digests and Node/build chain; upgrade to current serviced .NET 10 LTS and supported PostgreSQL patches. Build/test this exact artifact set before release. Do not install unreviewed SDKs globally or merely edit target frameworks and claim migration verified.

Compose currently runs only PostgreSQL, bound to loopback, with persistent volume and pg_isready health check. docker compose config validates syntax, not engine availability, image health, persistence, credentials or API startup. Docker runtime testing is blocked on this machine. Define reviewed API/static frontend/service-manager/ingress packaging separately; a production deployment is not present in the current Compose file.

Create separate migration-owner and API runtime identities. Runtime gets CONNECT, schema USAGE and only required table DML; audit_logs permits SELECT/INSERT only. Do not grant CREATE, superuser, CREATEDB/CREATEROLE or migration history writes to the API. Review default privileges for new tables. Production authentication uses managed secrets, SCRAM/TLS/network restrictions; loopback trust used by tests must never be copied. Confirm permissions using the actual runtime login and smoke workflows, not only role membership inspection.

## 2. Configure without seeding

Use Production explicitly. Set signing key from secret manager, issuer/audience, 1–60 minute token expiry, TLS database connection, AllowedHosts and exact trusted frontend origins. Configure certificates and a known proxy chain; preserve HTTPS scheme so redirects, HSTS and rate-limit client identity work correctly. Never blindly trust arbitrary forwarded headers. Production demo seed must fail; never pass --seed-demo or deploy fictional passwords.

Provision named verified Admins through the documented controlled operator procedure, record grant/removal, and enforce MFA before live access. Verify role removal and disabled accounts reject outstanding JWTs. Set retention/redaction for logs, reviews and pickup-related operations. Frontend VITE variables are public: use only nonsecret API origin/branding values.

Serve frontend over HTTPS with reviewed CSP, frame/referrer/content-type headers and SPA route rewrites. Exclude /api from fallback and any runtime cache; forward API failures as API failures. Verify login/logout/expiry, customer/seller/admin role boundaries, offline private-view blocking and pickup code disappearance. Never put codes/tokens in logs, analytics, URLs, screenshots or shared storage. Test service-worker updates against previous deployed assets before rollout.

## 3. Back up and assess cutover

Before schema/data changes take an encrypted access-controlled backup and verify a restore into a separate database. Define and measure recovery time/data loss; configure offsite backup and WAL/PITR as appropriate. Local Sprint 4 fixture dump/restore demonstrates tooling and selected invariants only. It does not establish production RPO/RTO or point-in-time recovery.

Freeze seller/customer writes and pause workers during a reviewed legacy cutover. Inventory all PendingApproval/Scheduled/Open/TargetReached/AwaitingSellerConfirmation/Confirmed/FulfillmentInProgress campaigns, reservations, pickups, stock holds and variant units. Use Admin operations-health legacy count plus read-only SQL under migration-owner access; a zero commitment flag alone is a review candidate, not proof of the correct physical allocation.

Reconcile each variant with physical stock and existing customer commitments. Classify every live row:

1. Fresh inventory-backed campaigns: retain persisted commitments/deadlines/pickups and validate aggregates.
2. Legacy draft/unpublished rows: owner/operator reviews stock and units before new submission.
3. Legacy live rows without stock evidence: operator chooses an approved failure/cancellation/customer communication path or a separately specified migration, with named sign-off for every row.
4. Legacy confirmed/fulfillment rows without pickup evidence: never fabricate collection, codes, inventory or Completed state. Establish a reviewed resolution plan.

Sprint2Operations is schema-only and defaults commitments to zero; applying it is not a stock migration. Do not bulk assign target quantity as committed stock. Do not auto-create pickups for historical reservations or change original price snapshots. Do not run any speculative backfill from this document. A new cutover script requires dry-run output, transactional/restart behavior, rollback plan, row IDs/counts, reconciled totals and separate approval. No real legacy dataset was supplied here.

## 4. Apply and validate a release

Run reviewed idempotent migration SQL through migration identity once, with backups and access freeze. Startup does not automatically migrate production. Check migration history and model compatibility; do not rebuild schema by dropping existing data. Deploy immutable API/frontend artifacts and start one worker instance. Observe authenticated operations health, DB connectivity, retry/backlog age and external liveness/readiness. The protected Admin heartbeat alone is insufficient for a load balancer; readiness must fail when dependencies prevent service.

Run the complete real marketplace journey with designated synthetic accounts, plus negative authorization checks, two-customer capacity race, expired/removed-role JWT and pickup replay tests in staging. Run bounded load on an isolated environment with realistic data and agreed latency/capacity budgets. Test graceful service-manager stop/restart, DB outage/recovery, persisted retries and notification deduplication. Do not send load to production without a separate bounded plan.

## 5. Pilot approval and rollback

Require release-owner/security/data-owner acceptance of the checklist. Limit the live pilot to approved shops/operators and agreed user volume; monitor errors, slow queries, lock waits, worker deadlines and support tickets without logging secrets. Stop new joins/publication if inventory correctness, data isolation, pickup verification or deadline processing is uncertain. Pause workers only under the incident procedure because delays affect deadlines.

Rollback frontend/API only when schema compatibility is demonstrated. Preserve audit/notification/pickup evidence and physical-collection records; never run Down migrations that drop fulfillment columns/tables on live data. Prefer forward fixes. Restore a database only under incident/data-owner approval after reconciling post-backup physical pickups and commitments; a raw restore can otherwise re-enable consumed codes or stock. Document recovery decisions and revalidate all affected campaigns before reopening.

## Evidence to attach before GO

Current supported artifact versions and scans; passing CI/staging reports; deployed TLS/proxy/headers/CORS/rate-limit checks; Admin MFA/provisioning evidence; runtime DB grants; legal seller-review policy; accepted performance budget; backup/restore/PITR results; legacy per-row decision sign-offs; process shutdown/restart/outage drills; external alerts and named incident/rollback owners.

## Sprint 5 deployment delta

Use the .NET 10 build/package baseline and the additive AdminMfaSecurity migration. Reviewed fresh SQL is docs/schema.sql; generate/review an idempotent operator artifact before any nonempty database deployment. No startup migration or legacy backfill is performed. Keep old fulfillment evidence; do not run Down on enrolled/live systems. Isolated upgrade/empty-security rollback is regression-tested, not approved as a live rollback.

Dockerfile, compose.production.yaml, .env.production.example and deployment/nginx.conf are templates for a non-root API and explicit trusted TLS edge with external verified-TLS PostgreSQL. No production database port is published. Supply protected secret files, persistent key directory ownership and reviewed immutable images; the templates do not generate credentials/certificates. Docker engine/runtime, Linux containers, real ingress and target host remain unverified until actually executed. The pipeline configuration is present but no GitHub workflow has been executed by this task.

Apply/review scripts/grant-runtime.sql using migration privileges on a dedicated database. Runtime has necessary DML but cannot assign/revoke Admin, reopen/reset MFA, mutate audit history or write migration history. Use a separate privileged one-shot job for the audited Admin CLI, with independent identity verification and MFA enrollment; never enable provisioning on the API host. Secret-file precedence must be considered when selecting operator vs runtime credentials. See admin-mfa-provisioning.md and production-operations-runbook.md for complete prerequisites, certificate rotation, incident and restore procedures.

Customer/Seller auth contracts stay compatible; Admin login now intentionally requires the updated MFA UI. Deploy compatible API/frontend artifacts together in staging and run all five real workflows before pilot approval. Retain the existing per-campaign legacy assessment and physical-stock/customer-commitment reconciliation above; Sprint 5 does not approve or execute a cutover.

# Backend reliability and operational gates — 2026-11-05

Backend only. This report supersedes the unbounded expiry-retry and missing webhook-recovery findings in [controlled pilot activation](controlled-pilot-activation.md). It does not authorize paid activation, provider acceptance, Live Mode, staging migrations, deployment or production use.

## Rate-limit failure root cause and fix

`Profile_reads_do_not_exhaust_credential_budget_but_login_limit_remains_enforced` previously relied on real elapsed time. Registration and initial login consumed two permits; fifteen profile reads followed; eight logins then consumed the remaining eight permits. The last login asserted 429. In the failed 1m53s full-run execution, automatic one-minute replenishment could restore permits before the assertion. The unchanged 30s isolated execution passed. The limiter is scoped to each application host, so the shared database fixture does not share quota state between hosts. Test ordering, parallel class execution and stale DB rows do not account for this host-local timer dependency.

Production still uses the .NET fixed-window limiter: ten permits, one minute, no queue, automatic replenishment and the same IP partition key. A DI seam lets this single quota test wrap the **real .NET limiter** with a non-replenishing window. The wrapper intentionally hides `ReplenishingRateLimiter`, because the partition manager otherwise invokes replenishment even when the limiter's auto-replenishment flag is false. All request counts and HTTP assertions are retained; no retries, assertion weakening, sleeps, disabled tests or arbitrary timeout increases were added. A separate test guards the unchanged production settings. The override is test-host DI, not an environment setting.

The first complete regression also exposed two pre-existing manual-fixture races under the new explicit leases. Phase One scenarios manually called `Process` while the hosted worker could claim the same job; a test then observed RefundPending instead of waiting for that other owner. Read-only inspection of the disposable DB showed the Running lease, followed by successful recovery and completion of the same suspended-seller refund. The readiness/outage test similarly drove synthetic worker-health states while a live worker could overwrite them and compete with its DB probes. These manually controlled fixtures now opt out of the hosted loop. All completion, health, security and refund assertions remain unchanged; production scheduling, health deadlines and retry limits are unchanged. Existing host-shutdown and worker-restart tests continue to exercise the real hosted worker.

## Durable worker ledger

The additive `20261105101152_PersistPilotWorkerRetries` migration adds `pilot_jobs`, uniquely keyed by stage/item ID. It covers hold expiry, fulfillment expiry, payment reconciliation, webhook reconciliation, refund processing, campaign opening and campaign finalization.

Each job stores:

- `State`: Ready, Running, Failed, Exhausted or Succeeded.
- `Attempts`: lifetime recorded job attempts, retained across success, manual retry and restart.
- `CycleAttempts`: attempts in the current failure/recovery cycle, capped at ten; successful processing resets this cycle counter.
- `RetryCount`: monotonically increasing operator-approval version.
- `LastAttemptAtUtc`, `NextAttemptAtUtc`, a fenced lease ID and a bounded error classification.

Acquisition uses a PostgreSQL transaction advisory lock and commits its attempt plus a five-minute lease **before** the business action. Competing workers cannot acquire an active lease. Failed work backs off from 30 seconds exponentially to a 15-minute maximum. Ten unsuccessful or abandoned attempts exhaust the job. Restarting the host neither resets history nor removes the lease. A crash on the last attempt is marked exhausted when its lease becomes due. Legacy financial exhaustion is checked before calling the provider, including when a lease was abandoned before its legacy counter reached its limit.

Eligibility filtering occurs before the 50-item stage batch limit, preventing delayed/exhausted jobs from monopolizing a batch. Completion is fenced by lease identity. Existing transaction locks and business-state checks still make expiry, capture, refund and fulfillment effects idempotent if a process loses its lease after committing the business action. A stale completion cannot overwrite a newer attempt or an operator decision. Completed one-shot work is rechecked before accepting a stale worker snapshot.

Each batch also scans up to 50 expired Running leases independently of business due-lists. If the action committed before process loss, authoritative persisted business state resolves the ledger to Succeeded without another attempt or side effect. Otherwise the remaining budget is resumed, or the item becomes Exhausted. This also handles legacy counters exhausted by another path while the original lease was abandoned. Last-attempt history and attempt totals are retained throughout recovery.

Successful pending-payment polling is monitoring for late capture, not failure retry. Pending refunds retain their existing ten-poll budget and become explicitly exhausted if unresolved. Provider-order creation failures from customer checkout also enter the ledger; a customer cannot bypass exhaustion by repeatedly submitting the same checkout. Successful provider-order recovery clears its failure counter before ordinary payment reconciliation. No operation changes its provider order/payment/refund identity to retry.

The migration imports pre-existing payment/webhook/refund/campaign retry evidence and exhausted states without modifying financial rows or inventing unknown historical attempt timestamps. Rollback refuses to delete a nonempty ledger. The runtime grant script adds SELECT/INSERT/UPDATE access as appropriate and no delete permission for retry evidence. Review/reapply grants only on an authorized target after its migration; this task does not apply either to staging.

## Operator signals and versioned manual retry

All these routes require the existing Admin policy, including completed MFA, current account/role validation and Admin rate limiting:

| Operation | Route |
| --- | --- |
| Failed attempts awaiting backoff | `GET /api/v1/mvp/admin/jobs?state=Failed` |
| Items requiring intervention | `GET /api/v1/mvp/admin/jobs?state=Exhausted` |
| In-flight / potentially abandoned leases | `GET /api/v1/mvp/admin/jobs?state=Running` |
| Rearm a reviewed exhausted job | `POST /api/v1/mvp/admin/jobs/{jobId}/retry?expectedRetryCount={observedRetryCount}` |
| Existing payment retry | `POST /api/v1/mvp/admin/orders/{orderId}/retry?expectedRetryCount={observedRetryCount}` |
| Existing refund retry | `POST /api/v1/mvp/admin/refunds/{refundId}/retry?expectedRetryCount={observedRetryCount}` |

Use existing pagination (`page`, `pageSize`). Responses identify the stage, item ID, state, lifetime/cycle attempts, last/next timestamps, approval version and error code. **The existing payment/refund retry routes now require `expectedRetryCount` too.** Read it from the job queue; a legacy exhausted item with no ledger entry starts at zero.

An approval rearms at most one ten-attempt cycle. Retrying the same observed version is a harmless no-op, even after a later cycle has exhausted. A future/mismatched version conflicts. To approve another cycle, review the new failure evidence and submit the newly observed count. Concurrent approvals are serialized. Only scheduling/counter metadata resets: lifetime attempt history and original financial/provider identifiers remain. The approval is audited. A Running job cannot be reset. If authoritative business state already resolved an exhausted item, the generic job endpoint audits that resolution instead of replaying its effects.

Payment/refund retry requires the original configured, available provider. It cannot capture an expired hold, reinstate a cancelled reservation, change campaign quantity, create a new refund operation or fulfill an order. The worker executes all original business checks again. Pickup Pass/OTP controls, ownership, server pricing and delivery-fee rules remain unchanged.

Worker logs retain only stage/item/error type. Job errors are classifications, not exception text, provider bodies, webhook signatures, credentials, tokens or PII. Audit events cover attempt start, failure, exhaustion, successful completion, approved retry and resolution from business state. Existing refund metrics and Admin order/refund views complement the queue. Batch health indicates batch execution, not that every item succeeded: operators must review the Failed/Exhausted queues and old Running leases.

Durable lease recovery and job execution also refresh the existing worker-progress signal, so a batch containing many items does not appear stalled merely because its Phase One stage takes longer than a single health interval. The existing readiness deadlines and DB-probe timeout remain unchanged. Progress is recorded around actual job work/recovery; item failures remain visible in the durable queue.

Webhook inbox records are never automatically discarded. To correlate an item, approved read-only SQL can join `pilot_jobs."ItemId"` to `payment_webhooks."Id"` for `webhook_reconciliation`, selecting provider, event ID, provider order ID and payload hash. Do not export raw bodies, secrets or customer data. Payment and refund stages correlate to their existing Admin records. Fix the underlying configuration/provider/data issue before approving a new cycle; never manually mark money paid/refunded or alter operation identifiers.

## Regression coverage

New PostgreSQL cases cover both expiry types failing ten times, persisted history across a real host disposal/recreation, skipped delayed work, exhaustion, authorized/unauthorized retry, delayed approval replay across retry cycles, successful recovery and duplicate worker execution. Other cases cover retained webhook/reconciliation evidence, abandoned leases, legacy-counter exhaustion, provider refund success followed by local completion failure, reuse of the original refund, original provider-order receipt recovery, MFA requirements, migration upgrade/backfill and guarded rollback. Test workers are disabled only in these explicit manual-scheduler fixtures. Test helpers make recorded jobs due explicitly; they do not add production retries or wait for wall-clock backoff.

## Activation remains external

Startup validation remains fail closed. No real configuration values were added. Razorpay Test keys and webhook secret must come from the authorized secret mechanism. Approved area IDs/names/postal codes, an approved delivery fee in paise, an isolated HTTPS target and a named MFA operator/acceptance window remain required. Real provider acceptance has not run. Follow the existing acceptance matrix before paid participation. No Live credentials are needed or requested.

Final frozen-code validation results are recorded below when complete. Earlier focused passes are development evidence and do not substitute for that final run. A transient Docker internal-server error during one focused fixture startup is infrastructure failure, not a skipped or passing PostgreSQL test; no Docker service or protected container was restarted.

The first complete validation returned 52 unit passes and 124 integration passes / 2 failures in 17m59s. Those two failures exposed the manual-scheduler and synthetic-health fixture races described above. The test isolation and worker-progress corrections are followed by focused validation and a fresh final frozen-code regression; the earlier failed run is retained as diagnostic evidence, not counted as passing acceptance.

## Files changed in this reliability pass

- API: new `AuthenticationRateLimitSettings.cs`; updated `Program.cs`, `PhaseOneEndpoints.cs`.
- Application/domain: `PhaseOneContracts.cs`, `PhaseOne.cs`.
- Infrastructure: new `Marketplace.PilotJobs.cs`; updated `PhaseOneDatabase.cs`, `Marketplace.PhaseOne.cs`, `Marketplace.PhaseOne.Fulfillment.cs`.
- Schema: `20261105101152_PersistPilotWorkerRetries.cs`, its generated designer and `MarketplaceDbModelSnapshot.cs`; `scripts/grant-runtime.sql`.
- Tests: `AuthenticationRateTests.cs`, `BackendTests.cs`, `PaymentGatewayTests.cs`, `PhaseOneTests.cs`, `OperationalSecurityTests.cs`, new `PilotReliabilityTests.cs`.
- Documentation: this report, `controlled-pilot-activation.md`, `mvp-phase1-api.md`, `mvp-phase1-security.md`.

Several listed files already contained Phase 1 edits or were untracked before this pass. Existing unrelated worktree changes were preserved. No frontend, protected staging resource, commit, push or deployment was touched.

## Final frozen-code validation and decision

- Focused verification after the fixture-race corrections: **37 passed, 0 failed, 0 skipped**, including the real hosted-worker restart case.
- Final build: **0 warnings / 0 errors**.
- One complete regression of the corrected frozen code: **52 unit passed / 0 failed / 0 skipped** and **126 PostgreSQL integration passed / 0 failed / 0 skipped**. Combined: **178 passed**. Integration duration: **11m9s**.
- Command: `dotnet test GroupBuy.sln --no-build --no-restore -m:1 --blame-hang-timeout 5m --blame-hang-dump-type none --logger trx --results-directory .local/reliability-final-validation`. The child process explicitly removed `GROUPBUY_TEST_POSTGRES`; fixtures used disposable PostgreSQL Testcontainers.
- TRX evidence: `.local/reliability-final-validation/Ashish_LAPTOP-SH2NN93A_2026-11-05_17_05_40_net10.0.trx` (unit) and `Ashish_LAPTOP-SH2NN93A_2026-11-05_17_05_49_net10.0.trx` (integration, same directory).
- EF `migrations has-pending-model-changes --no-build`: **no pending model changes**. The new migration was exercised only in disposable tests; staging was not migrated.
- `dotnet list GroupBuy.sln package --vulnerable --include-transitive --no-restore`: **no known vulnerable packages** across all six projects, using the configured sources. No dependencies changed during this pass.
- Security regression includes MFA/authorization, privacy/redaction, runtime permissions, payment idempotency and fail-closed activation checks. No production controls, health deadlines, payment validation or assertions were weakened.
- Final `git diff --check`: **passed**; existing LF/CRLF normalization notices only. Both saved TRX files were read back and confirm 52/52 and 126/126 passes, with zero failures.

**BACKEND CODE-LEVEL READY FOR CONTROLLED RAZORPAY TEST MODE ACCEPTANCE.**

Actual paid activation remains **NO-GO** until Manoj supplies the authorized external configuration and the controlled Test Mode acceptance matrix passes. Required inputs/actions: Test Key ID/Key Secret and separate webhook secret through the authorized secret mechanism; approved isolated HTTPS host and Test webhook subscriptions; approved service-area configuration and explicit delivery fee for the target environment; a named MFA operator, transaction/participant limits and acceptance window; and separately authorized application of the migration/runtime grants to the intended isolated target. Do not apply these to Sprint 5.1 or migrate Sprint 5.2 staging under this task. Provider acceptance was not executed. **Production remains NO-GO.**

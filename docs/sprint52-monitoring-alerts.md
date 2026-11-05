# Sprint 5.2 monitoring, alerts, and operational response

## Application signals

`/health/live` reports process liveness. `/health/ready` has a two-second bounded dependency check and requires PostgreSQL connectivity, no pending migrations, and a recent successful worker batch/progress (45-second freshness window). Public health responses are status-only. The expiry worker runs every 15 seconds, records batch error type, retries campaign work with bounded persisted backoff, caps attempts, and exposes poison/exhausted work to operations health.

Sprint 5.2 adds `System.Diagnostics.Metrics` instruments:

- `groudeal.api.requests` and `groudeal.api.request.duration` tagged only with route template, method, and status code.
- `groudeal.security.authentication.failures` tagged only with exception type.
- `groudeal.worker.batch.failures`, `groudeal.worker.campaign.retries`, and `groudeal.worker.campaign.retry.exhaustions`.

No raw URL, route parameter, account ID, IP address, token, request body, exception message, or connection data is emitted as a metric tag. Configure an approved OpenTelemetry/metrics agent or collector to subscribe to the `Groudeal.Operations` meter; no exporter, endpoint, alert destination, or provider credential is configured in this repository.

## Initial alert thresholds (owner approval required)

| Signal | Trigger | First response |
| --- | --- | --- |
| API availability | readiness fails for 2 consecutive probes or 5xx >2% for 5 minutes | check release, PostgreSQL reachability, worker health and recent structured error type; preserve logs by trace ID |
| API latency | p95 >750 ms for 10 minutes or p99 >2 seconds for 5 minutes | inspect route-template latency, DB pool saturation and query duration; scale only after resource diagnosis |
| Authentication abuse | 10+ failed authentication attempts per account/IP/minute or 429 increase >3x baseline | inspect aggregate counts, verify rate limiter and upstream WAF; do not log raw tokens or add IP tags |
| DB pool exhaustion | pool wait >1 second p95 for 5 minutes or connection failures >1% | inspect pool usage, leaked/long transactions and PostgreSQL max connections; avoid granting elevated DB rights |
| Worker recovery | any retry exhaustion, or worker readiness stale >45 seconds | use operations health, inspect safe error classifications, retry only after cause is corrected |
| Storage / restart | filesystem <20% free, PostgreSQL volume <25% free, or >3 container restarts/10 minutes | verify backup status and storage growth; never prune volumes as an emergency response |
| Backup / migration | scheduled backup missing >1 hour, integrity check failure, or migration job nonzero | freeze release, preserve artifacts, restore to disposable target and escalate to data owner |

Thresholds are proposed defaults; calibrate from synthetic/load baseline and assign paging ownership before deployment.

## Synthetic failure verification

The previous Sprint 5.1 disposable integration run covered database unavailability, worker persistence failures, poison/retry exhaustion, and cancellation, with readiness failing closed while liveness remained available. Sprint 5.2 added metrics regressions, but VSTest could not connect to its testhost, so those new tests and the previous synthetic failures were not rerun in this task. No failure was generated against recovered or production infrastructure.

## Blocked

No metrics collector, dashboard, alert rules, paging route, host disk/container exporter, database pool telemetry pipeline, backup job scheduler, or migration job is configured here. Alert delivery and on-call response must be tested in staging before pilot approval.

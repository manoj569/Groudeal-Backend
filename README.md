# Groudeal backend — Sprint 5 security hardening

Multi-category modular monolith; the initial operational pilot is grocery pickup from local sellers. Reservations are non-financial commitments: they are neither paid orders nor guarantees of purchase. Sprint 2 adds declared inventory, exclusive campaign commitments, seller confirmation deadlines, verified individual pickups, internal notifications and persistent background retries. No payment, mobile, frontend, vehicle or property workflows are included.

## Stack and layout

C# / ASP.NET Core 10 LTS, EF Core 10, PostgreSQL 17, Identity, JWT, FluentValidation, Serilog, OpenAPI, xUnit and Testcontainers.

Sprint 5 targets net10.0 with SDK 10.0.403 (global.json), Microsoft/EF 10.0.14, Npgsql EF 10.0.3 and Serilog ASP.NET 10.0.0. A checksum-verified workspace SDK is available at .local/dotnet10; the global installation may still be .NET 9. Install the pinned supported SDK or use scripts/verify-sprint5-backend.ps1. No system-wide SDK upgrade or production deployment is claimed.

```text
GroupBuy.sln
src/GroupBuy.Domain           Entities and state machine; no framework dependencies
src/GroupBuy.Application      DTOs, validation, application ports
src/GroupBuy.Infrastructure   PostgreSQL, migrations, Identity, application services
src/GroupBuy.API              Thin HTTP adapters, middleware, expiry worker
tests/GroupBuy.UnitTests
tests/GroupBuy.IntegrationTests
docs/                         Architecture, schema, contracts, security, setup
scripts/start-local.ps1
```

## Start locally (PowerShell, repository root)

Install the pinned .NET 10 SDK (or use the verified workspace SDK) and start Docker Desktop with Linux containers. Use development-only generated secrets; save them outside Git if you want persistent login tokens between restarts.

```powershell
$env:POSTGRES_PASSWORD = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$env:Jwt__SigningKey = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
$env:Demo__Password = 'Demo!' + [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(16)) + 'a1'
./scripts/start-local.ps1 -SeedDemo
```

Existing PostgreSQL volumes retain their original password. Reuse the original password on subsequent starts. The script never deletes volumes. The API listens at `http://localhost:5080`, development OpenAPI JSON at `/openapi/v1.json`. Production OpenAPI requires Admin.

Development demo accounts: `admin@fictional-demo.invalid`, `customer@fictional-demo.invalid`, `seller1@fictional-demo.invalid` through `seller3@fictional-demo.invalid`; all use the supplied `Demo__Password`. All sellers and registration numbers are synthetic fixtures, enabled only through an explicit Development seed command.

```powershell
dotnet restore GroupBuy.sln
dotnet build GroupBuy.sln --no-restore
dotnet test tests/GroupBuy.UnitTests --no-restore
dotnet test tests/GroupBuy.IntegrationTests --no-restore
```

Integration tests start a real PostgreSQL Testcontainer. Alternatively set `GROUPBUY_TEST_POSTGRES` to a PostgreSQL administrative connection that can CREATE DATABASE; tests create and drop a uniquely named test database. They never migrate or clear the supplied database. Missing PostgreSQL/Docker is a test failure, not a silent pass.

## Documentation

- [Architecture](docs/architecture.md)
- [Database and relationships](docs/database-design.md), [generated migration SQL](docs/schema.sql)
- [API contracts and examples](docs/api-contracts.md)
- [Security model and limitations](docs/security-model.md)
- [Campaign state machine](docs/group-buying-state-machine.md)
- [Local setup, migrations and configuration](docs/local-development.md)
- [Execution results and Sprint 2](docs/verification.md)
- [Sprint 1 security audit](docs/sprint1-security-audit.md)
- [Sprint 2 implementation and verification](docs/sprint2-implementation.md)
- [Inventory, confirmation and pickup workflow](docs/fulfillment-workflow.md)
- [Sprint 2.1 seller frontend API gaps](docs/sprint21-implementation.md)

Before creating campaigns, declare variant inventory with `PUT /api/v1/seller/inventory/{variantId}` using `availableQuantity` and `expectedVersion` (0 for the first declaration). Draft capacity must fit uncommitted stock; submission exclusively commits it. When the target fills, cancellation closes and the seller has 60 minutes by default to confirm or reject. Confirmation generates individual pending pickups; mark each ready, let the customer request their one-time code, and verify physical collection through the seller API. Every collected pickup is required for successful campaign completion.

`Operations__ConfirmationMinutes` accepts 1–1440 (default 60); `Operations__PickupHours` accepts 1–168 (default 48). Deadlines are persisted when established, so changing configuration does not change existing deadlines. Run migrations before the API. The Sprint 2 migration is schema-only: pre-existing live Sprint 1 campaigns need reviewed cutover; stock or pickup evidence is never fabricated automatically. Use a fresh development database for the new demo fixture rather than deleting or resetting existing data. See the implementation report for cutover restrictions.

No changes have been pushed or deployed. Production administrator provisioning uses the explicit audited CLI and supervised MFA enrollment; public registration always grants only Customer. See security documentation for the controlled bootstrap procedure.

Frontend Sprint 3 adds Admin-only operational summary, paginated seller/campaign review queues, read-only governance and filtered audit APIs. Existing review mutations record bounded reasons; private pickup secrets and customer fields are excluded from governance responses. See [API contracts](docs/api-contracts.md) and [security model](docs/security-model.md). No schema migration is required.


Sprint 4 release evidence: [E2E/results](docs/sprint4-e2e-report.md), [security audit](docs/sprint4-security-audit.md), [production checklist](docs/production-readiness-checklist.md), [deployment and legacy cutover plan](docs/deployment-plan.md). Synthetic verification is supported; a real-customer pilot remains NO-GO until documented gates close. Run ./scripts/verify-sprint4-backend.ps1 for the isolated portable-PostgreSQL regression runner.


## Sprint 5 security and operations

Every Admin login now requires TOTP or a single-use recovery code after password verification. First enrollment/reset is authorized only by a restricted one-shot operator command; no public role/reset route or production account/credential is auto-created. Customer/Seller contracts and inventory/pickup workflows remain. See [Admin MFA/provisioning](docs/admin-mfa-provisioning.md), [hardening and findings](docs/sprint5-security-hardening.md), [verification](docs/sprint5-verification.md), [operations/restore runbook](docs/production-operations-runbook.md), [readiness statuses](docs/production-readiness-checklist.md) and [deployment/cutover plan](docs/deployment-plan.md).

Run `./scripts/verify-sprint5-backend.ps1` for isolated portable-PostgreSQL build/tests/migrations. It uses the workspace SDK when present and separates intermediate assets from older IDE restores; it refuses an empty test run. Ordinary CLI commands require .NET 10. For EF commands using those isolated artifacts, set `$env:BaseIntermediateOutputPath='obj/sprint5/'` in the command's process. The development startup script restores its changed environment after exit and never implicitly loads /run/secrets. The Development demo Admin must also complete MFA; default manual enrollment is available for 15 minutes after fresh seeding, then requires the controlled reset procedure.

Production templates: Dockerfile, compose.production.yaml, deployment/nginx.conf and .env.production.example. Supply real secret files, persistent key ownership and reviewed image digests; do not reuse Development PostgreSQL trust/owner settings. Production enforces certificate-protected Data Protection keys and verified database TLS. Run reviewed migrations/privilege SQL separately, never from API startup. The Docker engine/target host and external operational gates must be verified before a real-customer pilot. Nothing has been pushed or deployed.

# Sprint 1 implementation and verification report

Implemented November 1, 2026. The repository initially contained only `.git`, with no existing code, dependencies or tests. Git working tree was clean. SDK 9.0.302 was available; .NET 10 was not. No unrelated work was overwritten. Generated empty template classes/tests were removed.

## Architecture and delivered files

The solution has four production projects and two xUnit test projects. Domain contains generic marketplace entities and campaign rules; Application defines validated contracts and infrastructure-independent service ports; Infrastructure supplies PostgreSQL transactions, migrations, Identity and use-case implementations; API provides versioned endpoints, middleware and a background expiry adapter.

Created root files: GroupBuy.sln, global.json, Directory.Build.props, .gitignore, .config/dotnet-tools.json, compose.yaml, README.md and scripts/start-local.ps1.

Created source files:

- Domain: Models.cs, CampaignRules.cs, GroupBuy.Domain.csproj.
- Application: Contracts.cs, Ports.cs, GroupBuy.Application.csproj.
- Infrastructure: Database.cs, IdentityService.cs, Marketplace.cs, DemoSeed.cs, GroupBuy.Infrastructure.csproj, InitialCreate migration/designer and MarketplaceDbModelSnapshot.
- API: Program.cs, Endpoints.cs, Errors.cs, ExpiryWorker.cs, GroupBuy.API.csproj, appsettings.json, appsettings.Development.json and local launch settings.
- Tests: CampaignTests.cs, BackendTests.cs and both test project files.
- Docs: architecture.md, database-design.md, api-contracts.md, security-model.md, group-buying-state-machine.md, local-development.md, schema.sql and this report.

## Delivered behavior

Identity account/profile, roles, generic sellers and multiple pickup locations, hierarchical categories, seller products/variants, campaigns, reservations and audit tables are linked by UUID FKs. Identity claim tables retain standard integer keys. Explicit precision, capacity/price/date checks, unique keys and query indexes are included. See database-design.md for every table and relationship.

All requested `/api/v1` authentication, public catalog, seller onboarding/product/campaign, customer reservation and Admin review routes are implemented. Additional logout and seller lifecycle-transition routes complete the requested behavior. Typed DTOs exclude internal entities and sensitive verification data. See api-contracts.md for the route matrix, inputs, outputs and error statuses.

Authentication uses Identity hashing/lockout, externally signed JWTs, current account/token-version validation and logout revocation. Public signup always grants Customer. Seller approval grants Seller; role and ownership checks both apply. Admin reviews require the Admin policy and a second service-level Admin check.

Reservations lock seller eligibility and campaign capacity within one PostgreSQL transaction, calculate snapshots from stored prices, enforce scoped idempotency and move full groups to TargetReached. Cancellation, confirmation, completion/failure, expiry and scheduled opening obey domain state rules. The periodic worker persists no in-memory job state and rechecks rows under locks, so retries/restarts and competing workers are safe. No payment promises are made.

Security includes CORS allowlisting, HTTPS/HSTS outside Development, input/body/pagination bounds, IP rate limiting, generic Problem Details, restricted DTOs, safe audit fields and no committed production secrets. See security-model.md for safeguards and gaps.

## Executed checks

- Package restore succeeded.
- `dotnet build GroupBuy.sln --no-restore`: succeeded, zero warnings and zero errors.
- `dotnet ef migrations add InitialCreate`: succeeded.
- Generated schema.sql from the migration successfully.
- `dotnet ef migrations has-pending-model-changes`: no model changes since migration.
- Initial migration applied successfully to fresh real PostgreSQL databases through integration fixtures.
- Unit suite: 21 passed, zero failures/skips; no empty template tests counted.
- Integration suite: 8 passed, zero failures/skips, using real PostgreSQL 17.6 and the actual HTTP application.
- OpenAPI JSON fetched and checked in the authentication/catalog integration scenario.
- Development fixture seed executed twice, producing exactly three fictional sellers, fifteen sample products/campaign reservations, without falsely marking email/phone verified.
- NuGet vulnerability audit initially found SSH.NET 2024.2.0 in Testcontainers' transitive dependencies. IntegrationTests explicitly pins patched 2026.0.0. See [advisory one](https://github.com/advisories/GHSA-q939-rpr3-3284) and [advisory two](https://github.com/advisories/GHSA-mggc-4xg6-vcxf).
- Final `dotnet list GroupBuy.sln package --vulnerable --include-transitive`: no vulnerable packages reported across all six projects by the configured NuGet sources.

Integration scenarios cover registration, invalid login, missing authentication, public role-injection rejection, Admin-only approval, unapproved seller denial, inactive categories, page bounds, logout revocation, seller onboarding/approval/product/campaign creation, cross-seller access, cross-customer cancellation, client price-injection rejection, snapshot integrity, repeated concurrent idempotent retries, 20 competing joins against capacity 10, TargetReached, cancellation, expiry, repeated expiry processing, confirmation/fulfillment/completion, terminal-state protection, direct database constraint violations, demo seeding and seller reapproval.

## Test infrastructure limitations

Docker CLI is installed but its engine was initially stopped. Starting Docker Desktop failed with an existing local socket-access error. The installed PostgreSQL directory was missing its library directory and could not initialize a database. No repair/reset was performed on either installation.

To execute real database checks, official EDB PostgreSQL 17.6 portable binaries were downloaded into the separate task workspace. An isolated loopback-only server on port 55432 was used. Tests created and dropped randomly named databases; no user database was modified. The test server is stopped after verification. The Docker/Testcontainers path and Docker Compose quick-start path remain unverified on this machine; the alternate PostgreSQL connection path is verified.

The application runs in WebApplicationFactory/TestServer for HTTP integration tests. Production TLS, hosting/reverse-proxy configuration, the Docker Compose orchestration script and real seller legal eligibility were not externally validated. A full production launch is not claimed.

## Remaining limitations and Sprint 2

1. Upgrade to .NET 10 LTS and current supported runtime/package patches. The installed .NET 9 runtime is old and .NET 9 reaches end of support November 10, 2026 ([Microsoft policy](https://dotnet.microsoft.com/en-us/platform/support/policy)).
2. Add audited operator provisioning and Admin MFA, email verification/password recovery, anti-abuse protections and key rotation.
3. Formalize seller verification/review notes and least-privilege operational access. A syntactically valid registration number is not proof of eligibility.
4. Add confirmation deadlines, seller cancellation policy, fulfillment pickup evidence and notifications; keep payment architecture separate until an explicitly scoped payment sprint.
5. Add richer seller draft/history querying, product variant management and search/filtering based on pilot needs. Existing campaign variants intentionally cannot be edited.
6. Add CI with PostgreSQL containers, production backups/restore tests, structured operational monitoring and load testing. Current concurrency checks are correctness tests, not a throughput benchmark.
7. Extract growing use-case orchestration from the Infrastructure adapter behind database-independent transaction/query ports if module complexity warrants it.

Exact startup commands and secret generation are in README.md and docs/local-development.md. No push or deployment was performed.

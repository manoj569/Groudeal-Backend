# Local development

Prerequisites: .NET SDK 9 (global.json selects 9.0.302 with latest feature-band roll-forward), Docker Linux containers or a real PostgreSQL 17 server, PowerShell. Upgrade to .NET 10 before production; the current target is the installed-SDK fallback.

Quick start commands are in README and `scripts/start-local.ps1`. For manual setup:

```powershell
$env:POSTGRES_PASSWORD = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$env:Jwt__SigningKey = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
$env:ConnectionStrings__Database = "Host=localhost;Database=groupbuy;Username=groupbuy;Password=$env:POSTGRES_PASSWORD"
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS = 'http://localhost:5080'
docker compose up -d --wait
dotnet restore GroupBuy.sln
dotnet tool restore
dotnet ef database update --project src/GroupBuy.Infrastructure
dotnet run --project src/GroupBuy.API --no-launch-profile
```

Keep the original POSTGRES_PASSWORD when restarting a persistent volume. To seed once after migration, set `Demo__Password` to a strong development password and run `dotnet run --project src/GroupBuy.API --no-launch-profile -- --seed-demo`. Seed does not run at server startup and refuses non-Development environments. Reruns detect the marker admin account. One transaction ensures seed rollback on failure. Do not change production to Development to seed.

## Migrations

```powershell
dotnet ef migrations add MeaningfulChange --project src/GroupBuy.Infrastructure
dotnet ef migrations script --idempotent --project src/GroupBuy.Infrastructure --output migration-review.sql
dotnet ef database update --project src/GroupBuy.Infrastructure
dotnet ef migrations has-pending-model-changes --project src/GroupBuy.Infrastructure
```

DesignTimeFactory reads ConnectionStrings__Database without requiring API JWT secrets. It has a local username/database fallback but no embedded password. Review and deploy migrations separately with database-owner credentials; runtime credentials should only have application DML permissions. Never run EnsureCreated or migrate on every request/startup. The generated schema.sql is for initial schema review; use an idempotent release script for existing deployments.

## Environment settings

| Variable | Purpose |
| --- | --- |
| ConnectionStrings__Database | PostgreSQL connection, required at runtime |
| Jwt__SigningKey | External 32+ byte secret, required |
| Jwt__Issuer / Jwt__Audience | Defaults GroupBuy / GroupBuy.Client |
| Jwt__ExpiryMinutes | Default 15, range 1–60 |
| Cors__Origins__0 | Explicit trusted browser origin, optional |
| AllowedHosts | Default localhost; set production host names |
| ASPNETCORE_ENVIRONMENT | Development only locally |
| ASPNETCORE_URLS | Local HTTP URL, HTTPS binding outside Development |
| Demo__Password | Only for explicit development fixtures |
| GROUPBUY_TEST_POSTGRES | Optional existing server connection with CREATE DATABASE privileges for tests |

Secrets are not printed by startup scripts and `.env` is ignored. Signing key regeneration invalidates prior tokens. HTTPS requires appropriate certificate configuration outside Development. Production does not create demo users or migrate automatically.

## Testing

```powershell
dotnet build GroupBuy.sln
dotnet test tests/GroupBuy.UnitTests
dotnet test tests/GroupBuy.IntegrationTests
```

Without GROUPBUY_TEST_POSTGRES, Testcontainers starts postgres:17-alpine and cleans it up. With it, the fixture creates a random `groupbuy_test_<UUID>` database, applies actual migrations, runs the actual HTTP API and drops only that database with FORCE at completion. Never set the test connection to credentials without permission to create temporary databases. Tests do not use EF InMemory. Build/test output describes infrastructure failures honestly.

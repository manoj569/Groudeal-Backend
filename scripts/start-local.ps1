param([switch]$SeedDemo)
$ErrorActionPreference = 'Stop'
if (-not $env:POSTGRES_PASSWORD) { throw 'Set POSTGRES_PASSWORD first (development only).' }
$root = Split-Path -Parent $PSScriptRoot
$dotnet = if (Test-Path (Join-Path $root '.local\dotnet10\dotnet.exe')) { Join-Path $root '.local\dotnet10\dotnet.exe' } else { 'dotnet' }
$keys = @('ConnectionStrings__Database','ASPNETCORE_ENVIRONMENT','ASPNETCORE_URLS','Jwt__SigningKey','DOTNET_ROOT','BaseIntermediateOutputPath')
$previous = @{}
foreach ($key in $keys) { $previous[$key] = [Environment]::GetEnvironmentVariable($key, 'Process') }
Push-Location $root
try {
 if (-not $env:Jwt__SigningKey) { $env:Jwt__SigningKey = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(48)) }
 $env:ConnectionStrings__Database = "Host=localhost;Database=groupbuy;Username=groupbuy;Password=$env:POSTGRES_PASSWORD"
 $env:ASPNETCORE_ENVIRONMENT = 'Development'
 $env:ASPNETCORE_URLS = 'http://localhost:5080'
 $env:BaseIntermediateOutputPath = 'obj/sprint5/'
 if ($dotnet -ne 'dotnet') { $env:DOTNET_ROOT = Split-Path -Parent $dotnet }
 docker compose up -d --wait
 if ($LASTEXITCODE -ne 0) { throw 'Development PostgreSQL did not start.' }
 & $dotnet restore GroupBuy.sln
 if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
 & $dotnet build GroupBuy.sln --no-restore
 if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
 & $dotnet tool restore
 if ($LASTEXITCODE -ne 0) { throw 'Tool restore failed.' }
 & $dotnet ef database update --project src/GroupBuy.Infrastructure --no-build
 if ($LASTEXITCODE -ne 0) { throw 'Development migration failed.' }
 if ($SeedDemo) {
  if (-not $env:Demo__Password) { throw 'Set Demo__Password before explicit Development seeding.' }
  & $dotnet run --project src/GroupBuy.API --no-build --no-launch-profile -- --seed-demo
  if ($LASTEXITCODE -ne 0) { throw 'Development seed failed.' }
 }
 & $dotnet run --project src/GroupBuy.API --no-build --no-launch-profile
} finally {
 foreach ($key in $keys) { [Environment]::SetEnvironmentVariable($key, $previous[$key], 'Process') }
 Pop-Location
}

# Isolated local PostgreSQL verification. Requires existing portable binaries; never targets a shared DB.
param([string]$PostgresBin = (Join-Path $PSScriptRoot '..\.local\audit-postgres\pgsql\bin'))
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$run = Join-Path $root ('.local\sprint4-backend-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run | Out-Null
$data = Join-Path $run 'data'
$started = $false
$previousConnection = $env:GROUPBUY_TEST_POSTGRES
Push-Location $root
try {
 $env:DOTNET_CLI_HOME = Join-Path $root '.local'
 & "$PostgresBin\initdb.exe" -D $data -U postgres -A trust --no-locale -E UTF8 *> (Join-Path $run 'init.log')
 if ($LASTEXITCODE -ne 0) { throw 'Fixture initialization failed.' }
 & "$PostgresBin\pg_ctl.exe" -D $data -l (Join-Path $run 'postgres.log') -o '-h 127.0.0.1 -p 55433' -w start
 if ($LASTEXITCODE -ne 0) { throw 'Fixture startup failed.' }
 $started = $true
 $env:GROUPBUY_TEST_POSTGRES = 'Host=127.0.0.1;Port=55433;Username=postgres;Database=postgres'
 dotnet build GroupBuy.sln --no-restore -m:1 -p:UseSharedCompilation=false *> (Join-Path $root '.local\sprint4-build.log')
 if ($LASTEXITCODE -ne 0) { throw 'Build failed; see .local/sprint4-build.log.' }
 dotnet test GroupBuy.sln --no-build --no-restore -m:1 --logger 'trx;LogFileName=sprint4-results.trx' *> (Join-Path $root '.local\sprint4-test.log')
 if ($LASTEXITCODE -ne 0) { throw 'Tests failed; see .local/sprint4-test.log.' }
 dotnet ef migrations has-pending-model-changes --project src/GroupBuy.Infrastructure --no-build *> (Join-Path $root '.local\sprint4-model.log')
 if ($LASTEXITCODE -ne 0) { throw 'Model check failed.' }
 Get-Content .local/sprint4-build.log -Tail 5
 Get-Content .local/sprint4-test.log -Tail 16
 Get-Content .local/sprint4-model.log
} finally {
 if ($started) { & "$PostgresBin\pg_ctl.exe" -D $data -m fast -w stop }
 $env:GROUPBUY_TEST_POSTGRES = $previousConnection
 Pop-Location
}


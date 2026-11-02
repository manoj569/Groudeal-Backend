# Isolated local PostgreSQL verification. Requires existing portable binaries; never targets a shared DB.
param([string]$PostgresBin = (Join-Path $PSScriptRoot '..\.local\audit-postgres\pgsql\bin'), [string]$TestFilter = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dotnet = if (Test-Path (Join-Path $root '.local\dotnet10\dotnet.exe')) { Join-Path $root '.local\dotnet10\dotnet.exe' } else { 'dotnet' }
$run = Join-Path $root ('.local\sprint5-backend-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run | Out-Null
$data = Join-Path $run 'data'
$started = $false
$previousConnection = $env:GROUPBUY_TEST_POSTGRES
$previousDotnetRoot = $env:DOTNET_ROOT
$previousCliHome = $env:DOTNET_CLI_HOME
$previousIntermediate = $env:BaseIntermediateOutputPath
Push-Location $root
try {
 $env:DOTNET_CLI_HOME = Join-Path $root '.local'
 if ($dotnet -ne 'dotnet') { $env:DOTNET_ROOT = Split-Path -Parent $dotnet }
 & "$PostgresBin\initdb.exe" -D $data -U postgres -A trust --no-locale -E UTF8 *> (Join-Path $run 'init.log')
 if ($LASTEXITCODE -ne 0) { throw 'Fixture initialization failed.' }
 & "$PostgresBin\pg_ctl.exe" -D $data -l (Join-Path $run 'postgres.log') -o '-h 127.0.0.1 -p 55433' -w start
 if ($LASTEXITCODE -ne 0) { throw 'Fixture startup failed.' }
 $started = $true
 $env:GROUPBUY_TEST_POSTGRES = 'Host=127.0.0.1;Port=55433;Username=postgres;Database=postgres'
 & $dotnet restore GroupBuy.sln --force -p:BaseIntermediateOutputPath=obj/sprint5/ *> (Join-Path $root '.local\sprint5-restore.log')
 if ($LASTEXITCODE -ne 0) { throw 'Restore failed; see .local/sprint5-restore.log.' }
 & $dotnet build GroupBuy.sln --no-restore -p:BaseIntermediateOutputPath=obj/sprint5/ -m:1 -p:UseSharedCompilation=false *> (Join-Path $root '.local\sprint5-build.log')
 if ($LASTEXITCODE -ne 0) { throw 'Build failed; see .local/sprint5-build.log.' }
 foreach ($project in @('GroupBuy.UnitTests','GroupBuy.IntegrationTests')) {
  $result = Join-Path $root "tests/$project/TestResults/sprint5-results.trx"
  if (Test-Path -LiteralPath $result) { Remove-Item -LiteralPath $result -Force }
 }
 $testArgs = @('test','GroupBuy.sln','-p:BaseIntermediateOutputPath=obj/sprint5/','--no-build','--no-restore','-m:1','--logger','trx;LogFileName=sprint5-results.trx')
 if ($TestFilter) { $testArgs += @('--filter', $TestFilter) }
 & $dotnet @testArgs *> (Join-Path $root '.local\sprint5-test.log')
 if ($LASTEXITCODE -ne 0) { throw 'Tests failed; see .local/sprint5-test.log.' }
 $integrationResult = Join-Path $root 'tests/GroupBuy.IntegrationTests/TestResults/sprint5-results.trx'
 if (-not (Test-Path -LiteralPath $integrationResult)) { throw 'Integration tests were not discovered.' }
 [xml]$integration = Get-Content -LiteralPath $integrationResult -Raw
 if ([int]$integration.TestRun.ResultSummary.Counters.executed -lt 1) { throw 'No integration tests executed.' }
 if (-not $TestFilter) {
  [xml]$unit = Get-Content -LiteralPath (Join-Path $root 'tests/GroupBuy.UnitTests/TestResults/sprint5-results.trx') -Raw
  if ([int]$unit.TestRun.ResultSummary.Counters.executed -lt 37 -or [int]$integration.TestRun.ResultSummary.Counters.executed -lt 49) { throw 'Existing baseline tests were not all discovered.' }
 }

 $env:BaseIntermediateOutputPath = 'obj/sprint5/'
 & $dotnet ef migrations has-pending-model-changes --project src/GroupBuy.Infrastructure --no-build *> (Join-Path $root '.local\sprint5-model.log')
 if ($LASTEXITCODE -ne 0) { throw 'Model check failed.' }
 Get-Content .local/sprint5-build.log -Tail 5
 Get-Content .local/sprint5-test.log -Tail 16
 Get-Content .local/sprint5-model.log
} finally {
 if ($started) { & "$PostgresBin\pg_ctl.exe" -D $data -m fast -w stop }
 $env:GROUPBUY_TEST_POSTGRES = $previousConnection
 $env:DOTNET_ROOT = $previousDotnetRoot
 $env:DOTNET_CLI_HOME = $previousCliHome
 $env:BaseIntermediateOutputPath = $previousIntermediate
 Pop-Location
}




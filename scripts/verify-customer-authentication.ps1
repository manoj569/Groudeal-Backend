# Runs against a freshly initialized, disposable cluster only. Never accepts a shared connection.
param([string]$PostgresBin = (Join-Path $PSScriptRoot '..\.local\audit-postgres\pgsql\bin'))
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $root '.local\dotnet10\dotnet.exe'
$run = Join-Path $root ('.local\customer-auth-validation-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run | Out-Null
$data = Join-Path $run 'data'
$previous = @{}
foreach ($key in @('GROUPBUY_TEST_POSTGRES','DOTNET_ROOT','DOTNET_CLI_HOME','BaseIntermediateOutputPath')) { $previous[$key] = [Environment]::GetEnvironmentVariable($key) }
$started = $false
Push-Location $root
function Invoke-Check([string]$Name, [string[]]$Arguments) {
 & $dotnet @Arguments *> (Join-Path $run "$Name.log")
 $result = $LASTEXITCODE
 Get-Content (Join-Path $run "$Name.log") -Tail 12
 if ($result -ne 0) { throw "$Name failed; inspect $run/$Name.log" }
}
try {
 $env:DOTNET_ROOT = Split-Path -Parent $dotnet
 $env:DOTNET_CLI_HOME = Join-Path $root '.local'
 $env:BaseIntermediateOutputPath = 'obj/customerauth/'
 Write-Output "Validation evidence: $run"
 Invoke-Check 'build' @('build','GroupBuy.sln','--no-restore','-p:BaseIntermediateOutputPath=obj/customerauth/','-m:1','-p:UseSharedCompilation=false')
 $unit = 'tests/GroupBuy.UnitTests/GroupBuy.UnitTests.csproj'
 $integration = 'tests/GroupBuy.IntegrationTests/GroupBuy.IntegrationTests.csproj'
 $common = @('--no-build','--no-restore','-p:BaseIntermediateOutputPath=obj/customerauth/','-m:1','--settings','scripts/customer-authentication.runsettings','--blame-hang-timeout','5m','--blame-hang-dump-type','none','--results-directory',$run,'--logger','trx')
 Invoke-Check 'new-unit' (@('test',$unit,'--filter','FullyQualifiedName~CustomerAuthenticationTests') + $common)
 $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
 $listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop()
 if ($port -eq 55432) { throw 'Refusing protected port.' }
 & "$PostgresBin\initdb.exe" -D $data -U postgres -A trust --no-locale -E UTF8 *> (Join-Path $run 'init.log')
 if ($LASTEXITCODE -ne 0) { throw 'Disposable cluster initialization failed.' }
 & "$PostgresBin\pg_ctl.exe" -D $data -l (Join-Path $run 'postgres.log') -o "-h 127.0.0.1 -p $port" -w start
 if ($LASTEXITCODE -ne 0) { throw 'Disposable cluster startup failed.' }
 $started = $true
 $env:GROUPBUY_TEST_POSTGRES = "Host=127.0.0.1;Port=$port;Username=postgres;Database=postgres"
 Invoke-Check 'new-integration' (@('test',$integration,'--filter','FullyQualifiedName~CustomerAuthenticationTests') + $common)
 Invoke-Check 'existing-security' (@('test',$integration,'--filter','FullyQualifiedName~Authentication|FullyQualifiedName~Admin_|FullyQualifiedName~MFA|FullyQualifiedName~Mfa|FullyQualifiedName~Recovery_codes|FullyQualifiedName~Jwt_|FullyQualifiedName~Production_|FullyQualifiedName~Distinct_customers_race_capacity') + $common)
 Invoke-Check 'all-unit' (@('test',$unit) + $common)
 # The existing logical backup regression requires its disposable Testcontainers fixture.
 # Remove only this runner's connection override; never point it at another cluster.
 Remove-Item Env:GROUPBUY_TEST_POSTGRES
 Invoke-Check 'all-integration' (@('test',$integration) + $common)
 Invoke-Check 'model' @('ef','migrations','has-pending-model-changes','--project','src/GroupBuy.Infrastructure','--no-build')
 git diff --check
 if ($LASTEXITCODE -ne 0) { throw 'git diff --check failed.' }
 Write-Output 'All ordered checks passed.'
} finally {
 if ($started) { & "$PostgresBin\pg_ctl.exe" -D $data -m fast -w stop }
 foreach ($key in $previous.Keys) { [Environment]::SetEnvironmentVariable($key,$previous[$key]) }
 Pop-Location
}

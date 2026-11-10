# Complete local Module 4 validation. Never accepts a shared database connection.
param([string]$RunDirectory = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dotnet = if (Test-Path (Join-Path $root '.local/dotnet10/dotnet.exe')) { Join-Path $root '.local/dotnet10/dotnet.exe' } else { 'dotnet' }
if (-not $RunDirectory) { $RunDirectory = Join-Path $root ('.local/module4-final-' + [Guid]::NewGuid().ToString('N')) }
$run = [IO.Path]::GetFullPath($RunDirectory)
$allowed = [IO.Path]::GetFullPath((Join-Path $root '.local')) + [IO.Path]::DirectorySeparatorChar
if (-not $run.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Evidence must stay inside the backend .local directory.' }
New-Item -ItemType Directory -Path $run | Out-Null
$previous = @{}
foreach ($key in @('DOTNET_ROOT','DOTNET_CLI_HOME','GROUPBUY_TEST_POSTGRES')) { $previous[$key] = [Environment]::GetEnvironmentVariable($key) }
Push-Location $root
function Invoke-Check([string]$Name, [string[]]$Arguments) {
    & $dotnet @Arguments *> (Join-Path $run "$Name.log")
    $code = $LASTEXITCODE
    Get-Content (Join-Path $run "$Name.log") -Tail 9
    if ($code -ne 0) { throw "$Name failed; inspect $run/$Name.log" }
}
function Invoke-Tests([string]$Name, [string]$Project, [string]$Filter = '', [int]$Minimum = 1) {
    $arguments = @('test',$Project,'--no-build','--no-restore','-m:1','--settings','scripts/customer-authentication.runsettings',
        '--blame-hang-timeout','5m','--blame-hang-dump-type','none','--results-directory',$run,'--logger',"trx;LogFileName=$Name.trx",'--logger','console;verbosity=normal')
    if ($Filter) { $arguments += @('--filter',$Filter) }
    Invoke-Check $Name $arguments
    [xml]$trx = Get-Content (Join-Path $run "$Name.trx")
    $counts = $trx.SelectSingleNode('//*[local-name()="Counters"]')
    if ([int]$counts.GetAttribute('passed') -lt $Minimum -or [int]$counts.GetAttribute('failed') -ne 0 -or [int]$counts.GetAttribute('notExecuted') -ne 0 -or [int]$counts.GetAttribute('aborted') -ne 0) { throw "$Name had missing, failed, skipped or aborted tests." }
}
try {
    $env:DOTNET_CLI_HOME = Join-Path $root '.local'
    if ($dotnet -ne 'dotnet') { $env:DOTNET_ROOT = Split-Path -Parent $dotnet }
    Remove-Item Env:GROUPBUY_TEST_POSTGRES -ErrorAction SilentlyContinue
    $baselineContainers = @(docker ps -aq --filter label=org.testcontainers=true)
    if ($LASTEXITCODE -ne 0) { throw 'Docker must be available.' }
    $baselineApiProcesses = @(Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like "*$root*GroupBuy.API.dll*" } | Select-Object -ExpandProperty ProcessId)
    Invoke-Check '01-restore' @('restore','GroupBuy.sln','--disable-parallel','-m:1')
    Invoke-Check '02-build' @('build','GroupBuy.sln','--no-restore','-m:1','-p:UseSharedCompilation=false')
    Invoke-Tests '03-unit' 'tests/GroupBuy.UnitTests' '' 159
    Invoke-Tests '04-postgres' 'tests/GroupBuy.IntegrationTests' '' 326
    [xml]$trx = Get-Content (Join-Path $run '04-postgres.trx')
    $module4 = @($trx.SelectNodes('//*[local-name()="UnitTestResult"]') | Where-Object { $_.GetAttribute('testName') -like '*Module4_*' })
    if ($module4.Count -lt 13 -or @($module4 | Where-Object { $_.GetAttribute('outcome') -ne 'Passed' }).Count) { throw 'Module 4 coverage did not pass completely.' }
    Invoke-Tests '05-clean-migration' 'tests/GroupBuy.IntegrationTests' 'FullyQualifiedName~Migration_from_empty_isolated_Postgres'
    Invoke-Tests '06-upgrade-migration' 'tests/GroupBuy.IntegrationTests' 'FullyQualifiedName~Migration_from_previous_schema'
    Invoke-Check '07-model' @('ef','migrations','has-pending-model-changes','--project','src/GroupBuy.Infrastructure','--no-build')
    Invoke-Tests '08-restart-protection' 'tests/GroupBuy.IntegrationTests' 'FullyQualifiedName~Postgres_encrypted_ring|FullyQualifiedName~Production_postgres_startup|FullyQualifiedName~Production_rejects_an_existing_unencrypted' 3
    git diff --check *> (Join-Path $run '09-diff.log')
    if ($LASTEXITCODE -ne 0) { throw 'git diff --check failed.' }
    git status --short | Set-Content (Join-Path $run '10-worktree.log')
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    do {
        $remaining = @(docker ps -aq --filter label=org.testcontainers=true | Where-Object { $_ -and $baselineContainers -notcontains $_ })
        if (-not $remaining.Count) { break }
        Start-Sleep -Milliseconds 500
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    "New Testcontainers resources remaining: $($remaining.Count)" | Set-Content (Join-Path $run '11-cleanup.log')
    if ($remaining.Count) { throw 'Disposable resource cleanup remains outstanding; no resources were pruned.' }
    $remainingApi = @(Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like "*$root*GroupBuy.API.dll*" -and $baselineApiProcesses -notcontains $_.ProcessId })
    "New API processes remaining: $($remainingApi.Count)" | Add-Content (Join-Path $run '11-cleanup.log')
    if ($remainingApi.Count) { throw 'An isolated API process remains; no unrelated process was stopped.' }
    'Module 4 complete local gate passed; no production rollout performed.' | Set-Content (Join-Path $run 'MODULE4-COMPLETE.txt')
    Get-Content (Join-Path $run 'MODULE4-COMPLETE.txt')
} finally {
    foreach ($key in $previous.Keys) { [Environment]::SetEnvironmentVariable($key,$previous[$key]) }
    Pop-Location
}

# Complete local Module 6 gate. Uses only fresh disposable PostgreSQL fixtures.
param([string]$RunDirectory = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dotnet = if (Test-Path (Join-Path $root '.local/dotnet10/dotnet.exe')) { Join-Path $root '.local/dotnet10/dotnet.exe' } else { 'dotnet' }
if (-not $RunDirectory) { $RunDirectory = Join-Path $root ('.local/module6-final-' + [Guid]::NewGuid().ToString('N')) }
$run = [IO.Path]::GetFullPath($RunDirectory)
$allowed = [IO.Path]::GetFullPath((Join-Path $root '.local')) + [IO.Path]::DirectorySeparatorChar
if (-not $run.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)) { throw 'Evidence must stay in backend .local.' }
New-Item -ItemType Directory -Path $run | Out-Null
$previous = @{}
foreach ($key in @('DOTNET_ROOT','DOTNET_CLI_HOME','GROUPBUY_TEST_POSTGRES','NuGetAudit')) { $previous[$key] = [Environment]::GetEnvironmentVariable($key) }
Push-Location $root
function Invoke-Check([string]$Name,[string[]]$Arguments) {
    & $dotnet @Arguments *> (Join-Path $run "$Name.log")
    $code = $LASTEXITCODE
    Get-Content (Join-Path $run "$Name.log") -Tail 8
    if ($code -ne 0) { throw "$Name failed; inspect $run/$Name.log" }
}
function Invoke-Tests([string]$Name,[string]$Project,[int]$Minimum,[string]$Filter = '') {
    $arguments = @('test',$Project,'--no-build','--no-restore','-m:1','--settings','scripts/customer-authentication.runsettings',
        '--blame-hang-timeout','5m','--blame-hang-dump-type','none','--results-directory',$run,'--logger',"trx;LogFileName=$Name.trx",'--logger','console;verbosity=normal')
    if ($Filter) { $arguments += @('--filter',$Filter) }
    Invoke-Check $Name $arguments
    [xml]$trx = Get-Content (Join-Path $run "$Name.trx")
    $counts = $trx.SelectSingleNode('//*[local-name()="Counters"]')
    if ([int]$counts.GetAttribute('passed') -lt $Minimum -or [int]$counts.GetAttribute('failed') -ne 0 -or [int]$counts.GetAttribute('notExecuted') -ne 0 -or [int]$counts.GetAttribute('aborted') -ne 0) { throw "$Name has missing/failed/skipped tests." }
}
try {
    $env:DOTNET_CLI_HOME = Join-Path $root '.local'
    if ($dotnet -ne 'dotnet') { $env:DOTNET_ROOT = Split-Path -Parent $dotnet }
    $env:NuGetAudit = 'false'
    Remove-Item Env:GROUPBUY_TEST_POSTGRES -ErrorAction SilentlyContinue
    $baselineContainers = @(docker ps -aq --filter label=org.testcontainers=true)
    if ($LASTEXITCODE -ne 0) { throw 'Docker must be available.' }
    $baselineApi = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'dotnet.exe' -and $_.CommandLine -like "*$root*GroupBuy.API.dll*" } | Select-Object -ExpandProperty ProcessId)
    Invoke-Check '01-restore' @('restore','GroupBuy.sln','--disable-parallel','-m:1')
    Invoke-Check '02-build' @('build','GroupBuy.sln','--no-restore','-m:1','-p:UseSharedCompilation=false')
    Invoke-Tests '03-unit' 'tests/GroupBuy.UnitTests' 187
    Invoke-Tests '03-focused' 'tests/GroupBuy.IntegrationTests' 15 'FullyQualifiedName~Module6_'
    Invoke-Tests '04-integration' 'tests/GroupBuy.IntegrationTests' 359
    [xml]$trx = Get-Content (Join-Path $run '04-integration.trx')
    $checkout = @($trx.SelectNodes('//*[local-name()="UnitTestResult"]') | Where-Object { $_.GetAttribute('testName') -like '*Module6_*' })
    if ($checkout.Count -lt 15 -or @($checkout | Where-Object { $_.GetAttribute('outcome') -ne 'Passed' }).Count) { throw 'Module 6 PostgreSQL coverage did not pass.' }
    Invoke-Check '05-model' @('ef','migrations','has-pending-model-changes','--project','src/GroupBuy.Infrastructure','--no-build')
    git diff --check *> (Join-Path $run '06-diff.log')
    if ($LASTEXITCODE -ne 0) { throw 'git diff --check failed.' }
    $paths = @(@(git diff --name-only) + @(git ls-files --others --exclude-standard) | Sort-Object -Unique)
    $findings = @()
    foreach ($path in $paths) {
        $line=0
        foreach ($content in (Get-Content -LiteralPath $path)) {
            $line++
            if ($content -match '(-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----|AKIA[0-9A-Z]{16}|sk_live_[0-9A-Za-z]{16,}|AIza[0-9A-Za-z_-]{30,}|ghp_[0-9A-Za-z]{30,})') { $findings += "$path`:$line" }
        }
    }
    "Reviewed files: $($paths.Count); secret-pattern findings: $($findings.Count)" | Set-Content (Join-Path $run '07-secrets.log')
    if ($findings.Count) { throw 'Secret-pattern review requires investigation.' }
    git status --short | Set-Content (Join-Path $run '08-worktree.log')
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    do {
        $remaining = @(docker ps -aq --filter label=org.testcontainers=true | Where-Object { $_ -and $baselineContainers -notcontains $_ })
        if (-not $remaining.Count) { break }
        Start-Sleep -Milliseconds 500
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    $remainingApi = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'dotnet.exe' -and $_.CommandLine -like "*$root*GroupBuy.API.dll*" -and $baselineApi -notcontains $_.ProcessId })
    "New Testcontainers resources remaining: $($remaining.Count); new API processes remaining: $($remainingApi.Count)" | Set-Content (Join-Path $run '09-cleanup.log')
    if ($remaining.Count -or $remainingApi.Count) { throw 'Test cleanup remains outstanding; no unrelated resources were removed.' }
    'Module 6 local gate passed; no production operations performed.' | Set-Content (Join-Path $run 'MODULE6-COMPLETE.txt')
    Get-Content (Join-Path $run 'MODULE6-COMPLETE.txt')
} finally {
    foreach ($key in $previous.Keys) { [Environment]::SetEnvironmentVariable($key,$previous[$key]) }
    Pop-Location
}



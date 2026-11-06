# Ordered local gate. Uses only freshly created Testcontainers fixtures; never accepts a shared DB.
param([string]$RunDirectory = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dotnet = if (Test-Path (Join-Path $root '.local/dotnet10/dotnet.exe')) { Join-Path $root '.local/dotnet10/dotnet.exe' } else { 'dotnet' }
if (-not $RunDirectory) { $RunDirectory = Join-Path $root ('.local/render-final-' + [Guid]::NewGuid().ToString('N')) }
$run = [IO.Path]::GetFullPath($RunDirectory)
$allowed = [IO.Path]::GetFullPath((Join-Path $root '.local')) + [IO.Path]::DirectorySeparatorChar
if (-not $run.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Evidence must stay inside the backend .local directory.' }
New-Item -ItemType Directory -Path $run | Out-Null
$previous = @{}
foreach ($key in @('DOTNET_ROOT','DOTNET_CLI_HOME','BaseIntermediateOutputPath','GROUPBUY_TEST_POSTGRES')) { $previous[$key] = [Environment]::GetEnvironmentVariable($key) }
Push-Location $root
function Invoke-Check([string]$Name, [string[]]$Arguments) {
 & $dotnet @Arguments *> (Join-Path $run "$Name.log")
 $code = $LASTEXITCODE
 Get-Content (Join-Path $run "$Name.log") -Tail 9
 if ($code -ne 0) { throw "$Name failed; inspect $run/$Name.log" }
}
function Invoke-Tests([string]$Name, [string]$Project, [string]$Filter = '', [int]$Minimum = 1) {
 $arguments = @('test',$Project,'--no-build','--no-restore','-p:BaseIntermediateOutputPath=obj/render/','-m:1','--settings','scripts/customer-authentication.runsettings',
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
 $env:BaseIntermediateOutputPath = 'obj/render/'
 Remove-Item Env:GROUPBUY_TEST_POSTGRES -ErrorAction SilentlyContinue
 $baselineContainers = @(docker ps -aq --filter label=org.testcontainers=true)
 if ($LASTEXITCODE -ne 0) { throw 'Docker must be available before the frozen-code gate starts.' }
 $baselineApiProcesses = @(Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like "*$root*GroupBuy.API.dll*" } | Select-Object -ExpandProperty ProcessId)
 Write-Output "Final evidence: $run"
 Invoke-Check '01-restore' @('restore','GroupBuy.sln','-p:BaseIntermediateOutputPath=obj/render/','--disable-parallel','-m:1')
 Invoke-Check '02-build' @('build','GroupBuy.sln','--no-restore','-p:BaseIntermediateOutputPath=obj/render/','-m:1','-p:UseSharedCompilation=false')
 $unit = 'tests/GroupBuy.UnitTests/GroupBuy.UnitTests.csproj'
 $integration = 'tests/GroupBuy.IntegrationTests/GroupBuy.IntegrationTests.csproj'
 Invoke-Tests '03-new-unit' $unit 'FullyQualifiedName~DataProtectionConfigurationTests' 13
 Invoke-Tests '04-all-unit' $unit '' 84
 $new = 'FullyQualifiedName~DataProtectionPersistenceTests|FullyQualifiedName~RenderProductionConfigurationTests|FullyQualifiedName~Me_contract_returns|FullyQualifiedName~Me_immediately|FullyQualifiedName~Google_only_me|FullyQualifiedName~Gmail_address|FullyQualifiedName~Otp_error_codes'
 Invoke-Tests '05-new-postgres' $integration $new 29
 Invoke-Tests '06-authentication' $integration 'FullyQualifiedName~CustomerAuthenticationTests|FullyQualifiedName~Authentication|FullyQualifiedName~Jwt_|FullyQualifiedName~Removed_roles|FullyQualifiedName~credential_budget' 24
 Invoke-Tests '07-admin-security' $integration 'FullyQualifiedName~MFA|FullyQualifiedName~Mfa|FullyQualifiedName~Admin_|FullyQualifiedName~Recovery_codes|FullyQualifiedName~Runtime_|FullyQualifiedName~Production_requires|FullyQualifiedName~Payment|FullyQualifiedName~ExceptionRedaction' 10
 Invoke-Tests '08-all-postgres' $integration '' 172
 Invoke-Tests '09-empty-migration' $integration 'FullyQualifiedName~Migration_from_empty_isolated_Postgres' 1
 Invoke-Tests '10-upgrade-migration' $integration 'FullyQualifiedName~Migration_from_previous_schema' 1
 Invoke-Check '11-model' @('ef','migrations','has-pending-model-changes','--project','src/GroupBuy.Infrastructure','--no-build')
 Invoke-Tests '12-restart-protection' $integration 'FullyQualifiedName~Postgres_encrypted_ring|FullyQualifiedName~Production_postgres_startup|FullyQualifiedName~Production_rejects_an_existing_unencrypted' 3
 Invoke-Tests '13-render-production' $integration 'FullyQualifiedName~RenderProductionConfigurationTests|FullyQualifiedName~Real_production_Kestrel' 16
 git diff --check *> (Join-Path $run '14-diff.log')
 if ($LASTEXITCODE -ne 0) { throw 'git diff --check failed.' }
 $paths = @(@(git diff --name-only) + @(git ls-files --others --exclude-standard) | Sort-Object -Unique)
 $findings = @()
 foreach ($path in $paths) {
  $lines = Get-Content -LiteralPath $path
  for ($line=0;$line -lt $lines.Count;$line++) {
   if ($lines[$line] -match '(-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----|AKIA[0-9A-Z]{16}|sk_live_[0-9A-Za-z]{16,}|AIza[0-9A-Za-z_-]{30,}|ghp_[0-9A-Za-z]{30,})') { $findings += "$path`:$($line+1)" }
  }
 }
 "Reviewed files: $($paths.Count); secret-pattern findings: $($findings.Count)" | Set-Content (Join-Path $run '15-secrets.log')
 if ($findings.Count) { $findings | Add-Content (Join-Path $run '15-secrets.log'); throw 'Secret-pattern review requires investigation.' }
 git status --short | Set-Content (Join-Path $run '16-worktree.log')
 git diff --stat | Add-Content (Join-Path $run '16-worktree.log')
 # Wait only for the test framework's own Ryuk cleanup; never prune or remove arbitrary resources.
 $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
 do {
  $remaining = @(docker ps -aq --filter label=org.testcontainers=true | Where-Object { $_ -and $baselineContainers -notcontains $_ })
  if (-not $remaining.Count) { break }
  Start-Sleep -Milliseconds 500
 } while ([DateTimeOffset]::UtcNow -lt $deadline)
 "New Testcontainers resources remaining: $($remaining.Count)" | Set-Content (Join-Path $run '17-cleanup.log')
 if ($remaining.Count) { throw 'Disposable test resource cleanup remains outstanding; no resources were pruned.' }
 $remainingApi = @(Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like "*$root*GroupBuy.API.dll*" -and $baselineApiProcesses -notcontains $_.ProcessId })
 "New backend API processes remaining: $($remainingApi.Count)" | Add-Content (Join-Path $run '17-cleanup.log')
 if ($remainingApi.Count) { throw 'An isolated API test process remains; no unrelated process was stopped.' }
 'All ordered frozen-code checks passed.' | Set-Content (Join-Path $run 'COMPLETE.txt')
 Get-Content (Join-Path $run 'COMPLETE.txt')
} finally {
 foreach ($key in $previous.Keys) { [Environment]::SetEnvironmentVariable($key,$previous[$key]) }
 Pop-Location
}

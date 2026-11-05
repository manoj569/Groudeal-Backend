$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
. (Join-Path $repositoryRoot 'scripts\sprint52-provisioning-safety.ps1')

function Assert-Sprint52([string]$Name, [bool]$Condition) {
    if (-not $Condition) { throw "FAIL $Name" }
    Write-Output "PASS $Name"
}

$runId = 'a1b2c3d4e5f6'
$names = New-Sprint52ResourceNames $runId
$failure = Get-Sprint52ResourceSafetyFailure $names 18443 '172.31.22.0/24'
Assert-Sprint52 'generated names satisfy verifier naming patterns' ($null -eq $failure -and
    $names.Container -match '^groudeal-sprint52-postgres-[a-f0-9]{12}$' -and
    $names.Database -match '^groupbuy_sprint52_[a-f0-9]{12}$' -and
    $names.Volume -match '^groudeal-sprint52-postgres-data-[a-f0-9]{12}$' -and
    $names.Network -match '^groudeal-sprint52-network-[a-f0-9]{12}$' -and
    $names.RuntimeRole -match '^groudeal_sprint52_runtime_[a-f0-9]{12}$')

foreach ($entry in @(
    @{ Name = 'protected database rejected'; Property = 'Database'; Value = 'groupbuy_sprint51' },
    @{ Name = 'protected container rejected'; Property = 'Container'; Value = 'groudeal-sprint51-postgres' },
    @{ Name = 'protected volume rejected'; Property = 'Volume'; Value = 'groudeal-sprint51-postgres-data' },
    @{ Name = 'protected network rejected'; Property = 'Network'; Value = 'groudeal-sprint51-network' }
)) {
    $invalid = $names | Select-Object *
    $invalid | Add-Member -NotePropertyName $entry.Property -NotePropertyValue $entry.Value -Force
    Assert-Sprint52 $entry.Name ($null -ne (Get-Sprint52ResourceSafetyFailure $invalid 18443 '172.31.22.0/24'))
}
Assert-Sprint52 'protected recovered port rejected' ($null -ne (Get-Sprint52ResourceSafetyFailure $names 55432 '172.31.22.0/24'))
Assert-Sprint52 'production subnet rejected' ($null -ne (Get-Sprint52ResourceSafetyFailure $names 18443 '172.30.0.0/24'))
$malformedRejected = $false
try { $null = New-Sprint52ResourceNames 'not-valid' } catch { $malformedRejected = $true }
Assert-Sprint52 'malformed run identifier rejected' $malformedRejected

$existingPorts = @(18443, 19000, 25432)
Assert-Sprint52 'edge port collision detected without deleting resource' (Test-Sprint52Collision '18443' @($existingPorts | ForEach-Object { [string]$_ }))
Assert-Sprint52 'unused edge port does not collide' (-not (Test-Sprint52Collision '18444' @($existingPorts | ForEach-Object { [string]$_ })))
Assert-Sprint52 'port selection advances past occupied candidate' ((Select-Sprint52CollisionFreeValue @('18443','18444','18445') @('18443','18444')) -ceq '18445')
$existingSubnets = @('172.31.22.0/24','172.31.23.0/24','172.30.0.0/24')
Assert-Sprint52 'Docker subnet collision detected without deleting network' (Test-Sprint52Collision '172.31.22.0/24' $existingSubnets)
Assert-Sprint52 'unused subnet does not collide' (-not (Test-Sprint52Collision '172.31.24.0/24' $existingSubnets))
Assert-Sprint52 'subnet selection advances past occupied candidate' ((Select-Sprint52CollisionFreeValue @('172.31.22.0/24','172.31.24.0/24') $existingSubnets) -ceq '172.31.24.0/24')

$started = [datetime]::UtcNow
$identity = [pscustomobject]@{ Name = $names.Container; Id = ('a' * 64); Label = 'true'; CreatedUtc = $started.AddSeconds(1).ToString('o') }
$expected = [pscustomobject]@{ Name = $names.Container; Id = ('a' * 64) }
Assert-Sprint52 'cleanup accepts exact labeled new resource identity' (Test-Sprint52CleanupIdentity $expected $identity $started)
$unlabeled = [pscustomobject]@{ Name = $identity.Name; Id = $identity.Id; Label = 'false'; CreatedUtc = $identity.CreatedUtc }
Assert-Sprint52 'cleanup refuses unlabeled resource' (-not (Test-Sprint52CleanupIdentity $expected $unlabeled $started))
$mismatch = [pscustomobject]@{ Name = $identity.Name; Id = ('b' * 64); Label = 'true'; CreatedUtc = $identity.CreatedUtc }
Assert-Sprint52 'cleanup refuses mismatched resource ID' (-not (Test-Sprint52CleanupIdentity $expected $mismatch $started))
$old = [pscustomobject]@{ Name = $identity.Name; Id = $identity.Id; Label = 'true'; CreatedUtc = $started.AddSeconds(-1).ToString('o') }
Assert-Sprint52 'cleanup refuses pre-existing resource' (-not (Test-Sprint52CleanupIdentity $expected $old $started))
$protectedIdentity = [pscustomobject]@{ Name = 'groudeal-sprint51-postgres'; Id = ('c' * 64); Label = 'true'; CreatedUtc = $identity.CreatedUtc }
Assert-Sprint52 'cleanup refuses protected resource' (-not (Test-Sprint52CleanupIdentity $protectedIdentity $protectedIdentity $started))

$secretCanary = 'SYNTHETIC_SECRET_CANARY_57c0'
$evidence = Get-Sprint52SafeEvidence @{ RunId = $runId; ContainerName = $names.Container; Password = $secretCanary; ConnectionString = "Password=$secretCanary" }
Assert-Sprint52 'safe evidence excludes arbitrary secret fields' (-not $evidence.Contains($secretCanary) -and -not $evidence.Contains('Password') -and -not $evidence.Contains('ConnectionString'))
$null = & git -C $repositoryRoot check-ignore -q -- '.local/sprint52/staging.env'
$ignoredExit = $LASTEXITCODE
Assert-Sprint52 '.local/sprint52 staging environment path remains Git-ignored' ($ignoredExit -eq 0)

$tokens = $null
$parseErrors = $null
[void][System.Management.Automation.Language.Parser]::ParseFile((Join-Path $repositoryRoot 'scripts\sprint52-provisioning-safety.ps1'), [ref]$tokens, [ref]$parseErrors)
Assert-Sprint52 'provisioning safety module parses' ($parseErrors.Count -eq 0)
Write-Output 'Sprint 5.2 provisioning safety tests complete'

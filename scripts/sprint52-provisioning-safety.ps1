# Pure Sprint 5.2 provisioning and cleanup guards. This file performs no I/O.

function New-Sprint52RunId {
    $bytes = New-Object byte[] 6
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) }
    finally { $rng.Dispose() }
    return ([System.BitConverter]::ToString($bytes).Replace('-', '').ToLowerInvariant())
}

function New-Sprint52ResourceNames([string]$RunId) {
    if ($RunId -notmatch '^[a-f0-9]{12}$') { throw 'Run identifier must be 12 lowercase hexadecimal characters.' }
    return [pscustomobject]@{
        Container = "groudeal-sprint52-postgres-$RunId"
        Database = "groupbuy_sprint52_$RunId"
        Volume = "groudeal-sprint52-postgres-data-$RunId"
        Network = "groudeal-sprint52-network-$RunId"
        BootstrapRole = "groudeal_sprint52_bootstrap_$RunId"
        MigratorRole = "groudeal_sprint52_migrator_$RunId"
        RuntimeRole = "groudeal_sprint52_runtime_$RunId"
        NetworkAlias = "groudeal-sprint52-postgres-$RunId"
    }
}

function Get-Sprint52ResourceSafetyFailure([object]$Names, [int]$Port, [string]$Subnet) {
    $protected = @('groupbuy_sprint51','groudeal-sprint51-postgres','groudeal-sprint51-postgres-data','groudeal-sprint51-network')
    foreach ($property in @('Container','Database','Volume','Network','BootstrapRole','MigratorRole','RuntimeRole','NetworkAlias')) {
        $value = [string]$Names.$property
        foreach ($reserved in $protected) {
            if ($value.IndexOf($reserved, [StringComparison]::OrdinalIgnoreCase) -ge 0) { return 'protected resource identifier rejected' }
        }
    }
    if ([string]$Names.Container -notmatch '^groudeal-sprint52-postgres-[a-f0-9]{12}$' -or
        [string]$Names.Database -notmatch '^groupbuy_sprint52_[a-f0-9]{12}$' -or
        [string]$Names.Volume -notmatch '^groudeal-sprint52-postgres-data-[a-f0-9]{12}$' -or
        [string]$Names.Network -notmatch '^groudeal-sprint52-network-[a-f0-9]{12}$' -or
        [string]$Names.RuntimeRole -notmatch '^groudeal_sprint52_runtime_[a-f0-9]{12}$' -or
        [string]$Names.MigratorRole -notmatch '^groudeal_sprint52_migrator_[a-f0-9]{12}$' -or
        [string]$Names.BootstrapRole -notmatch '^groudeal_sprint52_bootstrap_[a-f0-9]{12}$' -or
        [string]$Names.NetworkAlias -cne [string]$Names.Container) { return 'resource names do not match unique Sprint 5.2 naming rules' }
    if ($Port -eq 55432) { return 'protected recovered PostgreSQL port rejected' }
    if ($Port -lt 18000 -or $Port -gt 29999) { return 'staging edge port is outside the permitted range' }
    if ($Subnet -eq '172.30.0.0/24') { return 'production subnet rejected' }
    if ($Subnet -notmatch '^172\.(1[6-9]|2[0-9]|3[01])\.([0-9]{1,3})\.0/24$') { return 'subnet is not a valid private /24 in the allowed range' }
    $octet = 0
    if (-not [int]::TryParse($Matches[2], [ref]$octet) -or $octet -gt 255) { return 'subnet octet is invalid' }
    return $null
}

function Test-Sprint52Collision([string]$Candidate, [string[]]$Existing) {
    return @($Existing | Where-Object { $_ -ieq $Candidate }).Count -gt 0
}

function Select-Sprint52CollisionFreeValue([string[]]$Candidates, [string[]]$Existing) {
    foreach ($candidate in $Candidates) {
        if (-not (Test-Sprint52Collision $candidate $Existing)) { return $candidate }
    }
    return $null
}

function Test-Sprint52CleanupIdentity([object]$Expected, [object]$Actual, [datetime]$InvocationStartedUtc) {
    $protected = @('groupbuy_sprint51','groudeal-sprint51-postgres','groudeal-sprint51-postgres-data','groudeal-sprint51-network')
    if ($null -eq $Expected -or $null -eq $Actual) { return $false }
    if ([string]$Actual.Name -in $protected -or [string]$Actual.Name -match 'sprint51') { return $false }
    if ([string]$Expected.Name -cne [string]$Actual.Name -or [string]$Expected.Id -cne [string]$Actual.Id) { return $false }
    if ([string]$Expected.Id -notmatch '^[a-f0-9]{64}$' -or [string]$Actual.Id -notmatch '^[a-f0-9]{64}$') { return $false }
    if ([string]$Actual.Label -cne 'true') { return $false }
    $created = [datetime]::MinValue
    if (-not [datetime]::TryParse([string]$Actual.CreatedUtc, [ref]$created)) { return $false }
    if ($created.ToUniversalTime() -lt $InvocationStartedUtc.ToUniversalTime()) { return $false }
    return $true
}

function Get-Sprint52SafeEvidence([hashtable]$Evidence) {
    # Deliberately allow only non-secret identity metadata; never serialize arbitrary input.
    $allowed = @('RunId','ContainerName','DatabaseName','VolumeName','NetworkName','EdgePort','Subnet','CreatedUtc')
    $safe = [ordered]@{}
    foreach ($key in $allowed) {
        if ($Evidence.ContainsKey($key)) { $safe[$key] = [string]$Evidence[$key] }
    }
    return ($safe | ConvertTo-Json -Compress)
}

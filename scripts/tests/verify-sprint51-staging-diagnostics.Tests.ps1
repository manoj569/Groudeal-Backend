$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$verifierPath = Join-Path $repositoryRoot 'scripts\verify-sprint51-staging.ps1'
$tokens = $null
$parseErrors = $null
$verifierAst = [System.Management.Automation.Language.Parser]::ParseFile($verifierPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) {
    throw "Verifier parser errors: $($parseErrors.Count)"
}

$testFunctions = @('Set-SafeFailure','Get-CurlFailureCategory','Get-OpenSslExecutable','Get-SafeEdgeState','Format-SafeDockerPortMappings','Get-SafeEdgeLogSummaries','Get-SafeLogScanSummary','Write-ContainerLogSecretScan','Write-PreTlsEdgeDiagnostics','Test-HostTlsDiagnostic','Test-HealthEndpoint','Get-ComposeServiceContainer','Wait-Healthy','Get-Sprint52StagingSafetyFailure','Get-UnexpectedComposeOverrides','Get-Sprint52NetworkMembershipFailure','New-ComposeInvocationArguments','Test-FullyQualifiedWindowsPath','Get-Sprint52EdgeProbeCaPath')
$functionAsts = $verifierAst.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $testFunctions -contains $node.Name
}, $true)
foreach ($functionAst in $functionAsts) {
    Invoke-Expression $functionAst.Extent.Text
}

function Assert-Equal([string]$Name, [object]$Expected, [object]$Actual) {
    if ($Expected -cne $Actual) {
        throw "$Name failed: expected '$Expected', received '$Actual'"
    }
}

function Assert-NotContains([string]$Name, [string]$Text, [string]$Forbidden) {
    if ($Text.Contains($Forbidden)) {
        throw "$Name leaked a synthetic sensitive marker"
    }
}

$canary = 'SYNTHETIC_SECRET_CANARY_83DF2'
$safeSprint52Settings = @{
    STAGING_DB_CONTAINER = 'groudeal-sprint52-postgres-a1b2c3d4e5f6'
    STAGING_DB_NAME = 'groupbuy_sprint52_a1b2c3d4e5f6'
    STAGING_DB_VOLUME = 'groudeal-sprint52-postgres-data-a1b2c3d4e5f6'
    STAGING_DB_RUNTIME_USER = 'groudeal_sprint52_runtime_a1b2c3d4e5f6'
    STAGING_DOCKER_NETWORK = 'groudeal-sprint52-network-a1b2c3d4e5f6'
    STAGING_DB_HOST = 'groudeal-sprint52-postgres-a1b2c3d4e5f6'
    EDGE_HOST_PORT = '18443'
    PRIVATE_SUBNET = '172.31.22.0/24'
    EDGE_PRIVATE_IP = '172.31.22.10'
}
$safeSprint52Connection = 'Host=groudeal-sprint52-postgres-a1b2c3d4e5f6;Database=groupbuy_sprint52_a1b2c3d4e5f6;Username=groudeal_sprint52_runtime_a1b2c3d4e5f6;Password=SYNTHETIC_ONLY;Ssl Mode=VerifyFull'
Assert-Equal 'Unique Sprint 5.2 resource safety' $null (Get-Sprint52StagingSafetyFailure $safeSprint52Settings $safeSprint52Connection)
Assert-Equal 'Only unique database on staging network' $null (Get-Sprint52NetworkMembershipFailure (ConvertFrom-Json '{"id1":{"Name":"groudeal-sprint52-postgres-a1b2c3d4e5f6"}}') 'groudeal-sprint52-postgres-a1b2c3d4e5f6')
if (-not (Get-Sprint52NetworkMembershipFailure (ConvertFrom-Json '{"id1":{"Name":"groudeal-sprint51-postgres"}}') 'groudeal-sprint52-postgres-a1b2c3d4e5f6')) { throw 'Protected container network attachment was not refused' }
if (-not (Get-Sprint52NetworkMembershipFailure (ConvertFrom-Json '{"id1":{"Name":"groudeal-sprint52-postgres-a1b2c3d4e5f6"},"id2":{"Name":"unexpected"}}') 'groudeal-sprint52-postgres-a1b2c3d4e5f6')) { throw 'Unexpected staging network member was not refused' }
Assert-Equal 'No process overrides' '' (Get-UnexpectedComposeOverrides @('SPRINT52_STAGING_CONFIRMATION','EDGE_IMAGE') @{})
Assert-Equal 'Unexpected process override' 'EDGE_IMAGE' (Get-UnexpectedComposeOverrides @('SPRINT52_STAGING_CONFIRMATION','EDGE_IMAGE') @{ EDGE_IMAGE = 'synthetic' })
Assert-Equal 'Legacy edge probe CA fallback' 'legacy-edge-chain.pem' (Get-Sprint52EdgeProbeCaPath @{ EDGE_CERTIFICATE_FILE = 'legacy-edge-chain.pem' })
$edgeCaTestPath = Join-Path $repositoryRoot ('.local\sprint52\edge-ca-test-' + [Guid]::NewGuid().ToString('N') + '.pem')
try {
    [IO.File]::WriteAllText($edgeCaTestPath,"-----BEGIN CERTIFICATE-----`nSYNTHETIC_PUBLIC_CA`n-----END CERTIFICATE-----")
    Assert-Equal 'Explicit edge CA-only path accepted' ([IO.Path]::GetFullPath($edgeCaTestPath)) (Get-Sprint52EdgeProbeCaPath @{ EDGE_CERTIFICATE_FILE = 'edge-chain.pem'; EDGE_CA_FILE = [IO.Path]::GetFullPath($edgeCaTestPath) })
    [IO.File]::WriteAllText($edgeCaTestPath,"-----BEGIN CERTIFICATE-----`nSYNTHETIC_PUBLIC_CA`n-----END CERTIFICATE-----`n-----BEGIN PRIVATE KEY-----`nSYNTHETIC_PRIVATE_KEY`n-----END PRIVATE KEY-----")
    $privateEdgeCaRejected = $false
    try { $null = Get-Sprint52EdgeProbeCaPath @{ EDGE_CERTIFICATE_FILE = 'edge-chain.pem'; EDGE_CA_FILE = [IO.Path]::GetFullPath($edgeCaTestPath) } } catch { $privateEdgeCaRejected = $true }
    Assert-Equal 'Private key rejected from edge CA file' $true $privateEdgeCaRejected
}
finally { if (Test-Path -LiteralPath $edgeCaTestPath) { Remove-Item -LiteralPath $edgeCaTestPath -Force } }
$composeArgs = @(New-ComposeInvocationArguments 'groudeal-sprint52-staging-a1b2c3d4e5f6' @('--env-file','synthetic.env','-f','compose.production.yaml','-f','override.yaml') @('config'))
Assert-Equal 'Compose env-file flag precedes file flags' '--env-file' $composeArgs[3]
Assert-Equal 'Compose env-file path follows flag' 'synthetic.env' $composeArgs[4]
foreach ($property in @(
    @{ Name = 'database'; Key = 'STAGING_DB_NAME'; Value = 'groupbuy_sprint51' },
    @{ Name = 'container'; Key = 'STAGING_DB_CONTAINER'; Value = 'groudeal-sprint51-postgres' },
    @{ Name = 'network'; Key = 'STAGING_DOCKER_NETWORK'; Value = 'groudeal-sprint51-network' },
    @{ Name = 'volume'; Key = 'STAGING_DB_VOLUME'; Value = 'groudeal-sprint51-postgres-data' }
)) {
    $unsafe = @{} + $safeSprint52Settings
    $unsafe[$property.Key] = $property.Value
    if (-not (Get-Sprint52StagingSafetyFailure $unsafe $safeSprint52Connection)) { throw "Protected $($property.Name) was not refused" }
}
if (-not (Get-Sprint52StagingSafetyFailure $safeSprint52Settings ($safeSprint52Connection + ';Host=groudeal-sprint51-postgres'))) {
    throw 'Protected endpoint embedded in connection input was not refused'
}
if (-not (Get-Sprint52StagingSafetyFailure $safeSprint52Settings ($safeSprint52Connection + ';Port=55432'))) {
    throw 'Protected recovered database port was not refused'
}
if (-not (Get-Sprint52StagingSafetyFailure $safeSprint52Settings 'Host=localhost;Database=groupbuy_sprint52_a1b2c3d4e5f6;Username=groudeal_sprint52_runtime_a1b2c3d4e5f6;Password=SYNTHETIC_ONLY;Port=5432;Ssl Mode=VerifyFull')) {
    throw 'Loopback database endpoint was not refused'
}
Assert-Equal 'Safe edge state' 'running' (Get-SafeEdgeState 'running')
Assert-Equal 'Unsafe edge state' 'unknown (details withheld)' (Get-SafeEdgeState "running password=$canary")

$hostBindings = '{"443/tcp":[{"HostIp":"127.0.0.1","HostPort":"8443"}],"bad password=' + $canary + '":[{"HostIp":"' + $canary + '","HostPort":"secret"}]}'
$safeBindings = Format-SafeDockerPortMappings $hostBindings
if ($safeBindings -notlike '*443/tcp=>127.0.0.1:8443*' -or $safeBindings -notlike '*unknown-port=>unknown-host:unknown-port*') {
    throw 'HostConfig.PortBindings safe projection failed'
}
Assert-NotContains 'HostConfig.PortBindings projection' $safeBindings $canary

$networkPorts = '{"443/tcp":[{"HostIp":"127.0.0.1","HostPort":"8443"}],"8443/tcp":null}'
$safeNetworkPorts = Format-SafeDockerPortMappings $networkPorts
if ($safeNetworkPorts -notlike '*443/tcp=>127.0.0.1:8443*' -or $safeNetworkPorts -notlike '*8443/tcp=unbound*') {
    throw 'NetworkSettings.Ports safe projection failed'
}

$rawLogs = @(
    "[error] connect() failed (111: Connection refused); Authorization: Bearer $canary",
    "[error] host not found in upstream; JWT=$canary",
    "[error] upstream timed out; password=$canary",
    "[emerg] SSL certificate/private key error; secret=$canary",
    "unclassified log line; connection string=$canary"
)
$safeLogs = @(Get-SafeEdgeLogSummaries -Lines $rawLogs)
Assert-Equal 'Sanitized log line count' 5 $safeLogs.Count
Assert-Equal 'Connection log category' 'upstream connection refused' $safeLogs[0]
Assert-Equal 'DNS log category' 'upstream DNS resolution failure' $safeLogs[1]
Assert-Equal 'Timeout log category' 'upstream timeout' $safeLogs[2]
Assert-Equal 'TLS log category' 'TLS or certificate configuration error' $safeLogs[3]
Assert-Equal 'Unknown log category' 'edge log output (details withheld)' $safeLogs[4]
Assert-NotContains 'Edge log summaries' ($safeLogs -join '|') $canary

$secretLogCanary = @(
    "password=$canary",
    "Host=synthetic-db;Database=synthetic;Username=test;Password=$canary",
    "Authorization: Bearer eyJabcdefgh.abcdefgh.abcdefgh",
    'JWT=eyJabcdefgh.abcdefgh.abcdefgh',
    '-----BEGIN PRIVATE KEY-----',
    "RecoveryCode=$canary",
    "AccessToken=$canary",
    'customer@example.invalid',
    'Customer phone=+919876543210'
)
$secretScan = Get-SafeLogScanSummary -Lines $secretLogCanary
if ($secretScan.TotalMatches -lt 9 -or $secretScan.Summary -notlike '*password_or_credential=*' -or
    $secretScan.Summary -notlike '*credentialed_connection_string=*' -or
    $secretScan.Summary -notlike '*authorization_or_bearer=*' -or
    $secretScan.Summary -notlike '*jwt=*' -or
    $secretScan.Summary -notlike '*private_key_material=*' -or
    $secretScan.Summary -notlike '*mfa_or_recovery_secret=*' -or
    $secretScan.Summary -notlike '*security_token=*' -or
    $secretScan.Summary -notlike '*email_pii=*' -or
    $secretScan.Summary -notlike '*phone_pii=*') {
    throw 'Secret log scanner did not classify synthetic sensitive patterns'
}
Assert-NotContains 'Secret log scan summary' $secretScan.Summary $canary
Assert-NotContains 'Secret log scan summary' $secretScan.Summary 'customer@example.invalid'
Assert-NotContains 'Secret log scan summary' $secretScan.Summary '+919876543210'
$timestampScan = Get-SafeLogScanSummary -Lines @('2026-11-04 15:12:25 +05:30', '2026-11-04T15:12:25Z')
Assert-Equal 'ISO timestamps are not phone PII' 0 $timestampScan.TotalMatches
$testNameScan = Get-SafeLogScanSummary -Lines @('BackendTests.Admin_Mfa_secret_and_recovery_codes_are_never_logged')
Assert-Equal 'MFA/recovery test names are not secret disclosures' 0 $testNameScan.TotalMatches

$script:CapturedResults = New-Object 'System.Collections.Generic.List[string]'
$script:StagingPort = 8443
function Set-Stage([string]$Name, [string]$Message = '') { }
function Invoke-Compose([string[]]$Arguments, [switch]$Quiet, [string]$Stage, [int]$TimeoutSeconds = 60) {
    $script:SyntheticLookupCount++
    if ($script:SyntheticLookupCount -eq 1 -and $script:SyntheticLookupMode -eq 'timeout-once') {
        Set-SafeFailure $Stage 'timeout after 1 seconds'
        throw 'Native command timed out.'
    }
    if ($script:SyntheticLookupCount -eq 1 -and $script:SyntheticLookupMode -eq 'empty-once') { return @() }
    return @('synthetic-edge-id')
}
function Invoke-Docker([string[]]$Arguments, [switch]$Quiet, [string]$Stage, [int]$TimeoutSeconds = 30) {
    if ($script:SyntheticHealthState) { return $script:SyntheticHealthState }
    return 'running|healthy'
}
function Start-Sleep([int]$Seconds) { }
function Add-Result([string]$Name, [string]$Status, [string]$Detail = '') {
    $script:CapturedResults.Add("$Name|$Status|$Detail")
}
function Get-HostPortReachability([int]$Port = 8443, [int]$TimeoutMilliseconds = 1500) {
    return 'listening/reachable'
}
function Get-OpenSslExecutable { return 'synthetic-openssl.exe' }
function Invoke-NativeProcess([string]$FileName, [string[]]$Arguments, [string]$Stage, [int]$TimeoutSeconds = 30, [switch]$Quiet, [string]$WorkingDirectory, [switch]$AllowNonZeroExit, [switch]$CaptureStandardError, [string]$StandardInputText) {
    $script:SyntheticProbeArguments = $Arguments
    $script:SyntheticProbeFileName = $FileName
    $script:SyntheticProbeInput = $StandardInputText
    if ($FileName -eq 'synthetic-openssl.exe') {
        return [pscustomobject]@{ ExitCode = 1; Output = @(); StandardError = "verify error:num=20:unable to get local issuer certificate; password=$canary" }
    }
    if ($script:SyntheticCurlMode -eq 'success') {
        return [pscustomobject]@{ ExitCode = 0; Output = @('{"status":"live"}','200'); StandardError = '' }
    }
    if ($script:SyntheticCurlMode -eq 'timeout') {
        Set-SafeFailure $Stage 'timeout after 12 seconds'
        throw 'Native command timed out.'
    }
    return [pscustomobject]@{ ExitCode = 7; Output = @(); StandardError = "curl: (7) Connection refused; password=$canary" }
}
function Invoke-EdgeDiagnosticCommand([string[]]$Arguments, [string]$Stage, [switch]$IncludeStandardError) {
    if ($Arguments[0] -eq 'inspect' -and $Arguments[2] -eq '{{.State.Status}}') {
        return [pscustomobject]@{ Succeeded = $true; Output = @('running') }
    }
    if ($Arguments[0] -eq 'inspect' -and $Arguments[2] -eq '{{json .HostConfig.PortBindings}}') {
        return [pscustomobject]@{ Succeeded = $true; Output = @($hostBindings) }
    }
    if ($Arguments[0] -eq 'inspect' -and $Arguments[2] -eq '{{json .NetworkSettings.Ports}}') {
        return [pscustomobject]@{ Succeeded = $true; Output = @($networkPorts) }
    }
    if ($Arguments[0] -eq 'logs' -and $Arguments[1] -eq '--tail' -and $Arguments[2] -eq '40') {
        return [pscustomobject]@{ Succeeded = $true; Output = @($rawLogs) }
    }
    throw 'Unexpected synthetic diagnostic request'
}
$script:SyntheticLookupCount = 0
$script:SyntheticLookupMode = 'timeout-once'
$script:FailureStage = $null
$script:FailureReason = $null
$retriedContainer = Wait-Healthy 'synthetic-api' 5
Assert-Equal 'Transient Compose lookup timeout retried within health deadline' 'synthetic-edge-id' $retriedContainer
Assert-Equal 'Compose lookup retry count' 2 $script:SyntheticLookupCount
Assert-Equal 'Transient timeout failure state cleared' $null $script:FailureReason
$script:SyntheticLookupCount = 0
$script:SyntheticLookupMode = 'empty-once'
$retriedContainer = Wait-Healthy 'synthetic-api' 5
Assert-Equal 'Empty Compose lookup retried within health deadline' 'synthetic-edge-id' $retriedContainer
Assert-Equal 'Empty lookup retry count' 2 $script:SyntheticLookupCount
$script:SyntheticLookupMode = $null
$script:SyntheticLookupCount = 0
$script:SyntheticHealthState = 'running|none'
$edgeWithoutHealthcheck = Wait-Healthy 'synthetic-edge' 5 -AllowNoHealthcheck
Assert-Equal 'Edge running state accepted when no Docker HEALTHCHECK exists' 'synthetic-edge-id' $edgeWithoutHealthcheck
$script:SyntheticHealthState = $null
Write-PreTlsEdgeDiagnostics 'synthetic-edge-id'
Test-HostTlsDiagnostic 'synthetic.host' 'synthetic-ca.pem' '/health/live' 'Synthetic host TLS'
$capturedDiagnosticText = $script:CapturedResults -join '|'
if ($capturedDiagnosticText -notlike '*Pre-TLS edge container state|DIAGNOSTIC|running*' -or
    $capturedDiagnosticText -notlike '*Pre-TLS Docker HostConfig.PortBindings|DIAGNOSTIC*443/tcp=>127.0.0.1:8443*' -or
    $capturedDiagnosticText -notlike '*Pre-TLS Docker NetworkSettings.Ports|DIAGNOSTIC*443/tcp=>127.0.0.1:8443*' -or
    $capturedDiagnosticText -notlike '*Pre-TLS edge log 1/5|DIAGNOSTIC|upstream connection refused*' -or
    $capturedDiagnosticText -notlike '*Pre-TLS host TCP 8443|DIAGNOSTIC|listening/reachable*' -or
    $capturedDiagnosticText -notlike '*Synthetic host TLS host TLS diagnostic|DIAGNOSTIC|untrusted issuer presented on host loopback path*') {
    throw 'Synthetic pre-TLS diagnostic assembly failed'
}
Assert-NotContains 'Pre-TLS diagnostic evidence' $capturedDiagnosticText $canary

$script:CapturedResults.Clear()
$script:StagingPort = 18443
$script:FailureStage = $null
$script:FailureReason = $null
$curlFailed = $false
try {
    Test-HealthEndpoint 'synthetic-edge-id' 'synthetic.host' '/health/live' 200 'live' 'Synthetic liveness'
}
catch {
    $curlFailed = $true
}
if (-not $curlFailed -or $script:FailureReason -notlike '*edge curl exit code 7; stderr category: connection refused*' -or
    $script:CapturedResults[0] -notlike '*edge curl exit code 7; stderr category: connection refused*' -or
    $script:SyntheticProbeFileName -cne 'docker.exe' -or
    $script:SyntheticProbeArguments -notcontains 'exec' -or
    $script:SyntheticProbeArguments -notcontains 'synthetic-edge-id' -or
    $script:SyntheticProbeArguments -notcontains '/run/secrets/edge_certificate' -or
    $script:SyntheticProbeArguments -notcontains 'synthetic.host:443:127.0.0.1' -or
    $script:SyntheticProbeArguments -notcontains 'https://synthetic.host/health/live') {
    throw 'Synthetic strict edge-container curl failure was not recorded'
}
Assert-NotContains 'TLS probe failure record' ($script:CapturedResults -join '|') $canary
Assert-NotContains 'TLS probe failure reason' $script:FailureReason $canary

$script:SyntheticCurlMode = 'success'
$script:CapturedResults.Clear()
$script:FailureStage = $null
$script:FailureReason = $null
Test-HealthEndpoint 'synthetic-edge-id' 'synthetic.host' '/health/live' 200 'live' 'Synthetic liveness success'
if ($script:CapturedResults[0] -notlike '*Synthetic liveness success|PASS|HTTPS HTTP 200 (live)*') {
    throw 'Synthetic OpenSSL verified health response was not accepted'
}

$script:SyntheticCurlMode = 'timeout'
$script:CapturedResults.Clear()
$script:FailureStage = $null
$script:FailureReason = $null
$curlTimedOut = $false
try {
    Test-HealthEndpoint 'synthetic-edge-id' 'synthetic.host' '/health/live' 200 'live' 'Synthetic liveness timeout'
}
catch {
    $curlTimedOut = $true
}
if (-not $curlTimedOut -or $script:CapturedResults[0] -notlike '*edge curl exit code unavailable; stderr category: timeout; raw stderr unavailable*') {
    throw 'Synthetic curl process timeout was not recorded safely'
}
$script:SyntheticCurlMode = $null

$fortyOneLines = @('connect() failed') + @(1..40 | ForEach-Object { "safe synthetic log $_" })
$lastForty = @(Get-SafeEdgeLogSummaries -Lines $fortyOneLines)
Assert-Equal 'Recent log limit' 40 $lastForty.Count
if ($lastForty -contains 'upstream connection refused') {
    throw 'Log summarizer did not retain only the most recent 40 lines'
}

$curlCases = @(
    @{ ExitCode = 7; Error = "curl: (7) Connection refused; password=$canary"; Expected = 'connection refused' },
    @{ ExitCode = 28; Error = "curl: (28) Operation timed out; token=$canary"; Expected = 'timeout' },
    @{ ExitCode = 6; Error = "curl: (6) Could not resolve host; token=$canary"; Expected = 'DNS resolution failure' },
    @{ ExitCode = 60; Error = "curl: (60) SSL certificate problem; password=$canary"; Expected = 'TLS or certificate failure' },
    @{ ExitCode = 1; Error = 'verify error:num=20:unable to get local issuer certificate'; Expected = 'TLS CA chain validation failed (issuer unavailable)' },
    @{ ExitCode = 1; Error = 'verify error:num=62:hostname mismatch'; Expected = 'TLS hostname validation failed' },
    @{ ExitCode = 99; Error = "synthetic error; connection string=$canary"; Expected = 'other curl transport failure' }
)
foreach ($testCase in $curlCases) {
    $category = Get-CurlFailureCategory -ExitCode $testCase.ExitCode -StandardError $testCase.Error
    Assert-Equal "curl exit $($testCase.ExitCode) category" $testCase.Expected $category
    Assert-NotContains "curl exit $($testCase.ExitCode) category" $category $canary
}

Write-Output 'PASS synthetic Sprint 5.2 isolation, Compose argument, diagnostic parsing and secret-sanitization tests'

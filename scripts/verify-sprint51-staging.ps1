# Host-side verification of the production Compose topology against isolated Sprint 5.2 assets.
# Prerequisites: create the unique labeled PostgreSQL container, volume, and external bridge
# network described in docs/sprint52-staging-isolation.md;
# apply EF migrations with the separate migration identity and reviewed runtime grants.
[CmdletBinding()]
param(
    [string]$EnvFile = '.local/sprint52/staging.env',
    [switch]$KeepResources
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$script:RepositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$script:Failures = [System.Collections.Generic.List[string]]::new()
$script:Results = [System.Collections.Generic.List[string]]::new()
$script:ComposeFiles = @('--env-file', $EnvFile, '-f', 'compose.production.yaml')
$script:RunId = [Guid]::NewGuid().ToString('N').Substring(0, 12)
$script:Project = "groudeal-sprint52-staging-$script:RunId"
$script:ImageTag = 'groudeal-api:sprint52-staging-' + [Guid]::NewGuid().ToString('N')
$script:ImageOverride = Join-Path $PSScriptRoot '..\.local\sprint52\compose.image.yaml'
$script:ApiService = 'api'
$script:EdgeService = 'edge'
$script:ComposeStarted = $false
$script:CleanupDone = $false
$script:EvidencePath = Join-Path $PSScriptRoot '..\.local\sprint52\staging-verification-result.txt'
$script:CurrentStage = 'Initialization'
$script:FailureStage = $null
$script:FailureReason = $null
$script:StagingPort = 8443

function Add-Result([string]$Name, [string]$Status, [string]$Detail = '') {
    $line = if ($Detail) { "$Status - $Name - $Detail" } else { "$Status - $Name" }
    $script:Results.Add($line)
    Write-Host $line
}

function Set-Stage([string]$Name, [string]$Message = '') {
    $script:CurrentStage = $Name
    if ($Message) { Write-Host $Message }
}

function Set-SafeFailure([string]$Stage, [string]$Reason) {
    $script:FailureStage = $Stage
    $script:FailureReason = $Reason
}

function Get-Sprint52StagingSafetyFailure([hashtable]$Settings, [string]$ConnectionString) {
    $container = [string]$Settings['STAGING_DB_CONTAINER']
    $database = [string]$Settings['STAGING_DB_NAME']
    $network = [string]$Settings['STAGING_DOCKER_NETWORK']
    $volume = [string]$Settings['STAGING_DB_VOLUME']
    $hostName = [string]$Settings['STAGING_DB_HOST']
    $runtimeUser = [string]$Settings['STAGING_DB_RUNTIME_USER']
    $hostPort = [string]$Settings['EDGE_HOST_PORT']
    $subnet = [string]$Settings['PRIVATE_SUBNET']
    $edgeIp = [string]$Settings['EDGE_PRIVATE_IP']
    $protected = @('groupbuy_sprint51','groudeal-sprint51-postgres','groudeal-sprint51-postgres-data','groudeal-sprint51-network')
    foreach ($value in @($container,$database,$network,$volume,$hostName,$runtimeUser,$ConnectionString)) {
        foreach ($reserved in $protected) {
            if ($value.IndexOf($reserved, [StringComparison]::OrdinalIgnoreCase) -ge 0) { return 'protected recovered resource reference detected' }
        }
    }
    if ($ConnectionString -match '(?i)\b(?:Port\s*=\s*55432|localhost|127\.0\.0\.1|::1)\b') {
        return 'protected recovered database endpoint detected'
    }
    if ($container -notmatch '^groudeal-sprint52-postgres-[a-f0-9]{12}$' -or
        $database -notmatch '^groupbuy_sprint52_[a-f0-9]{12}$' -or
        $network -notmatch '^groudeal-sprint52-network-[a-f0-9]{12}$' -or
        $volume -notmatch '^groudeal-sprint52-postgres-data-[a-f0-9]{12}$' -or
        $hostName -cne $container -or
        $runtimeUser -notmatch '^groudeal_sprint52_runtime_[a-f0-9]{12}$' -or
        $ConnectionString -notmatch ('(?i)(?:^|;)\s*Username\s*=\s*' + [Regex]::Escape($runtimeUser) + '\s*(?:;|$)')) {
        return 'staging resource names and runtime identity must be unique Sprint 5.2 values'
    }
    $port = 0
    if (-not [int]::TryParse($hostPort, [ref]$port) -or $port -lt 18000 -or $port -gt 29999) { return 'staging edge port must be unique and between 18000 and 29999' }
    if ($subnet -notmatch '^172\.(1[6-9]|2[0-9]|3[01])\.([0-9]{1,3})\.0/24$' -or $subnet -eq '172.30.0.0/24') { return 'staging subnet must be a unique private /24 outside the production subnet' }
    $expectedEdge = $subnet -replace '\.0/24$','.10'
    if ($edgeIp -cne $expectedEdge) { return 'staging edge address must be the .10 address of its isolated subnet' }
    return $null
}

function Get-UnexpectedComposeOverrides([string[]]$Names, [hashtable]$EnvironmentSettings) {
    $overrides = @($Names | Where-Object { $EnvironmentSettings.ContainsKey($_) -and -not [string]::IsNullOrWhiteSpace([string]$EnvironmentSettings[$_]) })
    return $overrides -join ', '
}

function Get-Sprint52NetworkMembershipFailure([object]$ContainerMap, [string]$ExpectedContainer) {
    $members = @()
    if ($null -ne $ContainerMap) { $members = @($ContainerMap.PSObject.Properties | ForEach-Object { [string]$_.Value.Name }) }
    if ($members.Count -ne 1 -or $members[0] -cne $ExpectedContainer) { return 'staging database network must contain only the configured isolated database container' }
    if ($members[0] -ieq 'groudeal-sprint51-postgres') { return 'protected recovered container is attached to staging network' }
    return $null
}

function Get-Sprint52EdgeProbeCaPath([hashtable]$Settings) {
    $edgeCaPath = [string]$Settings['EDGE_CA_FILE']
    if ([string]::IsNullOrWhiteSpace($edgeCaPath)) { return [string]$Settings['EDGE_CERTIFICATE_FILE'] }
    if (-not (Test-FullyQualifiedWindowsPath $edgeCaPath) -or -not (Test-Path -LiteralPath $edgeCaPath -PathType Leaf)) { throw 'EDGE_CA_FILE must be an existing absolute public CA certificate file.' }
    $edgeCaPem = Get-Content -LiteralPath $edgeCaPath -Raw
    if ($edgeCaPem -notmatch '-----BEGIN CERTIFICATE-----' -or $edgeCaPem -match '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----') { throw 'EDGE_CA_FILE must contain public certificate material only.' }
    return $edgeCaPath
}

function Test-FullyQualifiedWindowsPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    # Match Windows drive-rooted paths (for example D:\folder or D:/folder)
    # and UNC server/share paths. Reject drive-relative (D:folder), root-relative
    # (\folder), and ordinary relative paths just as IsPathFullyQualified does.
    if ($Path -match '^[A-Za-z]:[\\/]') { return $true }
    if ($Path -match '^(?:\\\\|//)[^\\/]+[\\/][^\\/]+(?:[\\/].*)?$') { return $true }
    return $false
}

function ConvertTo-WindowsCommandLineArgument([string]$Argument) {
    if ($null -eq $Argument) { $Argument = '' }
    if ($Argument.Length -gt 0 -and $Argument -notmatch '[\s"]') { return $Argument }
    $builder = New-Object System.Text.StringBuilder
    [void]$builder.Append([char]34)
    $slashes = 0
    for ($i = 0; $i -lt $Argument.Length; $i++) {
        $character = $Argument[$i]
        if ($character -eq [char]92) { $slashes++; continue }
        if ($character -eq [char]34) {
            for ($n = 0; $n -lt (2 * $slashes + 1); $n++) { [void]$builder.Append([char]92) }
            [void]$builder.Append([char]34)
        }
        else {
            for ($n = 0; $n -lt $slashes; $n++) { [void]$builder.Append([char]92) }
            [void]$builder.Append($character)
        }
        $slashes = 0
    }
    for ($n = 0; $n -lt (2 * $slashes); $n++) { [void]$builder.Append([char]92) }
    [void]$builder.Append([char]34)
    return $builder.ToString()
}

function Invoke-NativeProcess([string]$FileName, [string[]]$Arguments, [string]$Stage, [int]$TimeoutSeconds = 30, [switch]$Quiet, [string]$WorkingDirectory = $script:RepositoryRoot, [switch]$AllowNonZeroExit, [switch]$CaptureStandardError, [string]$StandardInputText) {
    $quotedArguments = @($Arguments | ForEach-Object { ConvertTo-WindowsCommandLineArgument $_ })
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $FileName
    $startInfo.Arguments = $quotedArguments -join ' '
    $startInfo.WorkingDirectory = [System.IO.Path]::GetFullPath($WorkingDirectory)
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $redirectInput = $PSBoundParameters.ContainsKey('StandardInputText')
    $startInfo.RedirectStandardInput = $redirectInput
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    try {
        try { $started = $process.Start() }
        catch { Set-SafeFailure $Stage 'native process could not be started'; throw 'Native command failed.' }
        if (-not $started) { Set-SafeFailure $Stage 'native process did not start'; throw 'Native command failed.' }
        if ($redirectInput) {
            try {
                $process.StandardInput.Write($StandardInputText)
                $process.StandardInput.Close()
            }
            catch { Set-SafeFailure $Stage 'native process input could not be written'; throw 'Native command failed.' }
        }
        # Drain both pipes asynchronously. stderr is intentionally discarded: Docker
        # progress is commonly written there and must never become a PowerShell error.
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $timer = [System.Diagnostics.Stopwatch]::StartNew()
        $nextProgress = 30
        if (-not $Quiet) { Write-Host "Starting $Stage..." }
        while (-not $process.WaitForExit(1000)) {
            if ($timer.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
                $terminated = $false
                try { $process.Kill() } catch { }
                try { $terminated = $process.WaitForExit(5000) } catch { }
                $timeoutReason = if ($terminated) { "timeout after $TimeoutSeconds seconds" } else { "timeout after $TimeoutSeconds seconds; process termination wait expired" }
                Set-SafeFailure $Stage $timeoutReason
                throw 'Native command timed out.'
            }
            if ($timer.Elapsed.TotalSeconds -ge $nextProgress) {
                Write-Host "$Stage is still running (elapsed $([int]$timer.Elapsed.TotalSeconds) seconds)."
                $nextProgress += 30
            }
        }
        if (-not [System.Threading.Tasks.Task]::WaitAll([System.Threading.Tasks.Task[]]@($stdoutTask, $stderrTask), 5000)) {
            Set-SafeFailure $Stage 'native output stream did not close'
            throw 'Native command output did not close.'
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        $exitCode = $process.ExitCode
        if ($exitCode -ne 0 -and -not $AllowNonZeroExit) {
            Set-SafeFailure $Stage "native command exited with code $exitCode"
            throw 'Native command failed.'
        }
        $lines = @($stdout -split "`r?`n" | Where-Object { $_ -ne '' })
        $capturedError = if ($CaptureStandardError) { $stderr } else { '' }
        return [pscustomobject]@{ ExitCode = $exitCode; Output = $lines; StandardError = $capturedError }
    }
    finally { $process.Dispose() }
}

function Invoke-Docker([string[]]$Arguments, [switch]$Quiet, [string]$Stage = 'Docker command', [int]$TimeoutSeconds = 30) {
    $result = Invoke-NativeProcess -FileName 'docker.exe' -Arguments $Arguments -Stage $Stage -TimeoutSeconds $TimeoutSeconds -Quiet:$Quiet
    if ($result.ExitCode -ne 0) {
        Set-SafeFailure $Stage "Docker command exited with code $($result.ExitCode)"
        throw 'Docker command failed.'
    }
    return $result.Output
}

function Redact-Line([string]$Line) {
    # Errors may contain expanded Compose settings or connection strings. Do not print them.
    if ($script:FailureReason) { return $script:FailureReason }
    return 'verification failed; details withheld'
}

function Invoke-Compose([string[]]$Arguments, [switch]$Quiet, [string]$Stage = '', [int]$TimeoutSeconds = 60) {
    $all = New-ComposeInvocationArguments -Project $script:Project -ComposeFiles $script:ComposeFiles -Arguments $Arguments
    if (-not $Stage) { $Stage = "Compose $($Arguments[0])" }
    return Invoke-Docker -Arguments $all -Quiet:$Quiet -Stage $Stage -TimeoutSeconds $TimeoutSeconds
}

function New-ComposeInvocationArguments([string]$Project, [string[]]$ComposeFiles, [string[]]$Arguments) {
    return @('compose', '--project-name', $Project) + $ComposeFiles + $Arguments
}

function Get-ComposeServiceContainer([string]$Service, [int]$TimeoutSeconds = 15, [string]$FailureStage = '') {
    $previousStage = $script:FailureStage
    $previousReason = $script:FailureReason
    try {
        $lookup = @(Invoke-Compose @('ps', '-q', $Service) -Quiet -Stage "$Service container lookup" -TimeoutSeconds $TimeoutSeconds)
    }
    catch {
        if ($script:FailureReason -match '(?i)timeout after') {
            $script:FailureStage = $previousStage
            $script:FailureReason = $previousReason
            return ''
        }
        throw
    }
    $id = if ($lookup.Count -gt 0) { ([string]$lookup[0]).Trim() } else { '' }
    return $id
}

function Wait-Healthy([string]$Service, [int]$TimeoutSeconds = 180, [switch]$AllowNoHealthcheck) {
    $stage = if ($AllowNoHealthcheck) { "$Service startup" } else { "$Service health" }
    $waitMessage = if ($AllowNoHealthcheck) { "Waiting for $Service container to run..." } else { "Waiting for $Service health..." }
    Set-Stage $stage $waitMessage
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $nextProgress = [DateTimeOffset]::UtcNow.AddSeconds(30)
    do {
        $remaining = [Math]::Max(1, [Math]::Ceiling(($deadline - [DateTimeOffset]::UtcNow).TotalSeconds))
        $commandTimeout = [Math]::Min(15, $remaining)
        $id = Get-ComposeServiceContainer $Service $commandTimeout $stage
        if (-not $id) {
            if ([DateTimeOffset]::UtcNow -ge $deadline) { break }
            if ([DateTimeOffset]::UtcNow -ge $nextProgress) {
                $progressMessage = if ($AllowNoHealthcheck) { "Still waiting for $Service startup." } else { "Still waiting for $Service health." }
                Write-Host $progressMessage
                $nextProgress = [DateTimeOffset]::UtcNow.AddSeconds(30)
            }
            Start-Sleep -Seconds 3
            continue
        }
        $remaining = [Math]::Max(1, [Math]::Ceiling(($deadline - [DateTimeOffset]::UtcNow).TotalSeconds))
        $commandTimeout = [Math]::Min(15, $remaining)
        $state = (Invoke-Docker @('inspect', '--format', '{{.State.Status}}|{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}', $id) -Quiet -Stage "$Service health inspection" -TimeoutSeconds $commandTimeout | Select-Object -First 1).ToString().Trim()
        if ($state -eq 'running|healthy' -or ($AllowNoHealthcheck -and $state -eq 'running|none')) { return $id }
        if ($state -like 'exited|*' -or $state -like 'dead|*') {
            Set-SafeFailure $stage 'container stopped before becoming healthy'
            throw 'Health wait failed.'
        }
        if ([DateTimeOffset]::UtcNow -ge $nextProgress) {
            $progressMessage = if ($AllowNoHealthcheck) { "Still waiting for $Service startup." } else { "Still waiting for $Service health." }
            Write-Host $progressMessage
            $nextProgress = [DateTimeOffset]::UtcNow.AddSeconds(30)
        }
        Start-Sleep -Seconds 3
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    $timeoutReason = if ($AllowNoHealthcheck) { 'timeout waiting for container to run' } else { 'timeout waiting for container health' }
    Set-SafeFailure $stage $timeoutReason
    throw 'Service wait timed out.'
}

function Get-CurlFailureCategory([int]$ExitCode, [string]$StandardError) {
    if ($StandardError -match '(?i)connection refused|actively refused') { return 'connection refused' }
    if ($ExitCode -eq 28 -or $StandardError -match '(?i)timed out|timeout') { return 'timeout' }
    if ($ExitCode -eq 6 -or $StandardError -match '(?i)could not resolve|resolve host|name or service not known') { return 'DNS resolution failure' }
    if ($ExitCode -eq 7 -or $StandardError -match '(?i)failed to connect|could not connect|couldn.t connect') { return 'connection refused or unreachable' }
    if ($StandardError -match '(?i)hostname mismatch|host name mismatch|does not match.*host') { return 'TLS hostname validation failed' }
    if ($StandardError -match '(?i)verify error:num=20:') { return 'TLS CA chain validation failed (issuer unavailable)' }
    if ($StandardError -match '(?i)verify error:num=19:|verify error:num=18:') { return 'TLS CA chain validation failed (untrusted self-signed certificate)' }
    if ($StandardError -match '(?i)verify error:num=21:') { return 'TLS CA chain validation failed (unable to verify peer certificate)' }
    if ($StandardError -match '(?i)unable to get local issuer|unable to verify the first certificate|self.signed certificate|certificate verify failed|verify error:num=') { return 'TLS CA chain validation failed' }
    if (@(1,35,51,58,60,77,80,82,83,90,91,98) -contains $ExitCode -or $StandardError -match '(?i)SSL|TLS|certificate|cert verify|PEM|verify error|hostname mismatch') { return 'TLS or certificate failure' }
    return 'other curl transport failure'
}

function Get-OpenSslExecutable {
    $candidates = New-Object 'System.Collections.Generic.List[string]'
    $git = Get-Command git.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($git -and $git.Source) {
        $gitRoot = Split-Path -Parent (Split-Path -Parent $git.Source)
        $candidates.Add((Join-Path $gitRoot 'mingw64\bin\openssl.exe'))
        $candidates.Add((Join-Path $gitRoot 'usr\bin\openssl.exe'))
    }
    if ($env:ProgramFiles) { $candidates.Add((Join-Path $env:ProgramFiles 'Git\mingw64\bin\openssl.exe')) }
    if (${env:ProgramFiles(x86)}) { $candidates.Add((Join-Path ${env:ProgramFiles(x86)} 'Git\mingw64\bin\openssl.exe')) }
    $pathCommand = Get-Command openssl.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($pathCommand -and $pathCommand.Source) { $candidates.Add($pathCommand.Source) }
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return [IO.Path]::GetFullPath($candidate) }
    }
    return $null
}

function Get-SafeEdgeState([string]$State) {
    $normalized = if ($State) { $State.Trim().ToLowerInvariant() } else { '' }
    if ($normalized -match '^(created|running|paused|restarting|removing|exited|dead)$') { return $normalized }
    return 'unknown (details withheld)'
}

function Format-SafeDockerPortMappings([string]$Json) {
    if ([string]::IsNullOrWhiteSpace($Json) -or $Json.Trim() -eq 'null') { return 'none' }
    try { $ports = ConvertFrom-Json -InputObject $Json -ErrorAction Stop }
    catch { return 'unavailable (invalid diagnostic data)' }
    if ($null -eq $ports) { return 'none' }
    $summaries = New-Object 'System.Collections.Generic.List[string]'
    foreach ($property in $ports.PSObject.Properties) {
        $portName = if ($property.Name -match '^[0-9]{1,5}/(tcp|udp|sctp)$') { $property.Name } else { 'unknown-port' }
        $bindings = @($property.Value)
        if ($null -eq $property.Value -or $bindings.Count -eq 0) {
            $summaries.Add("$portName=unbound")
            continue
        }
        foreach ($binding in $bindings) {
            if ($null -eq $binding) {
                $summaries.Add("$portName=unbound")
                continue
            }
            $hostIp = [string]$binding.HostIp
            if ($hostIp -notmatch '^[0-9A-Fa-f:.]+$') { $hostIp = 'unknown-host' }
            $hostPort = [string]$binding.HostPort
            if ($hostPort -notmatch '^[0-9]{1,5}$') { $hostPort = 'unknown-port' }
            $summaries.Add(('{0}=>{1}:{2}' -f $portName,$hostIp,$hostPort))
        }
    }
    if ($summaries.Count -eq 0) { return 'none' }
    return ($summaries -join '; ')
}

function Get-SafeEdgeLogSummaries([string[]]$Lines) {
    $summaries = New-Object 'System.Collections.Generic.List[string]'
    foreach ($line in @($Lines | Select-Object -Last 40)) {
        if ($line -match '(?i)connection refused|connect[(][)] failed') { $summaries.Add('upstream connection refused') }
        elseif ($line -match '(?i)host not found|could not be resolved|name resolution') { $summaries.Add('upstream DNS resolution failure') }
        elseif ($line -match '(?i)no live upstreams') { $summaries.Add('no live upstreams') }
        elseif ($line -match '(?i)upstream timed out|operation timed out') { $summaries.Add('upstream timeout') }
        elseif ($line -match '(?i)SSL|TLS|certificate|private key|PEM') { $summaries.Add('TLS or certificate configuration error') }
        elseif ($line -match '(?i)[[](emerg|alert|crit)[]]') { $summaries.Add('nginx critical/startup error (details withheld)') }
        elseif ($line -match '(?i)[[]error[]]') { $summaries.Add('nginx error (details withheld)') }
        elseif ($line -match '(?i)[[]warn[]]') { $summaries.Add('nginx warning (details withheld)') }
        else { $summaries.Add('edge log output (details withheld)') }
    }
    return $summaries.ToArray()
}

function Get-SafeLogScanSummary([string[]]$Lines) {
    $text = @($Lines) -join "`n"
    $patterns = [ordered]@{
        'password_or_credential' = '(?i)\b(?:password|pwd)\s*[:=]\s*[^\s;,]+'
        'credentialed_connection_string' = '(?i)\b(?:Host|Server)\s*=\s*[^;\s]+;[^\r\n]*\b(?:Password|Pwd)\s*=\s*[^\s;,]+'
        'authorization_or_bearer' = '(?i)\bAuthorization\s*:\s*Bearer\s+\S+|\bBearer\s+eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}'
        'jwt' = '\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b'
        'private_key_material' = '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----|\bssh-(?:rsa|ed25519)\s+[A-Za-z0-9+/]{40,}'
        'mfa_or_recovery_secret' = '(?i)\b(?:authenticator.?key|shared.?key|totp.?secret|mfa.?secret|recovery.?codes?)\b\s*[:=]\s*\S+|\botpauth://\S+'
        'security_token' = '(?i)\b(?:access.?token|refresh.?token|mfa.?challenge)\s*[:=]\s*\S+'
        'email_pii' = '(?i)\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b'
        'phone_pii' = '(?i)\b(?:phone|mobile|contactphone)\s*[:=]\s*\+?[0-9][0-9 ()-]{7,}[0-9]'
    }
    $counts = [ordered]@{}
    $total = 0
    foreach ($entry in $patterns.GetEnumerator()) {
        $count = [Regex]::Matches($text, $entry.Value).Count
        $counts[$entry.Key] = $count
        $total += $count
    }
    $matched = @($counts.GetEnumerator() | Where-Object { $_.Value -gt 0 } | ForEach-Object { '{0}={1}' -f $_.Key,$_.Value })
    $summary = if ($matched.Count) { 'matches=' + ($matched -join ',') } else { 'matches=0' }
    return [pscustomobject]@{ LineCount = @($Lines).Count; TotalMatches = $total; Summary = $summary }
}

function Write-ContainerLogSecretScan([string]$ContainerId, [string]$ServiceName) {
    $logResult = Invoke-EdgeDiagnosticCommand @('logs','--since','2h','--tail','500',$ContainerId) "$ServiceName restricted log scan"
    if (-not $logResult.Succeeded) {
        Add-Result "$ServiceName restricted log scan" 'NOT VERIFIED' 'container logs unavailable; raw output withheld'
        return
    }
    $scan = Get-SafeLogScanSummary -Lines $logResult.Output
    $status = if ($scan.TotalMatches -eq 0) { 'PASS' } else { 'FAIL' }
    Add-Result "$ServiceName restricted log scan" $status "lines=$($scan.LineCount); $($scan.Summary); raw output withheld"
    if ($scan.TotalMatches -gt 0) { $script:Failures.Add("$ServiceName restricted log scan found sensitive-pattern matches") }
}

function Get-HostPortReachability([int]$Port = 0, [int]$TimeoutMilliseconds = 1500) {
    if ($Port -le 0) { $Port = $script:StagingPort }
    $client = New-Object System.Net.Sockets.TcpClient
    $connect = $null
    try {
        $connect = $client.BeginConnect([System.Net.IPAddress]::Loopback, $Port, $null, $null)
        if (-not $connect.AsyncWaitHandle.WaitOne($TimeoutMilliseconds)) { return 'connection attempt timed out' }
        $client.EndConnect($connect)
        if ($client.Connected) { return 'listening/reachable' }
        return 'not listening/reachable'
    }
    catch { return 'not listening/reachable' }
    finally {
        if ($connect -and $connect.AsyncWaitHandle) { $connect.AsyncWaitHandle.Close() }
        $client.Close()
    }
}

function Invoke-EdgeDiagnosticCommand([string[]]$Arguments, [string]$Stage, [switch]$IncludeStandardError) {
    $previousStage = $script:FailureStage
    $previousReason = $script:FailureReason
    try {
        $result = Invoke-NativeProcess -FileName 'docker.exe' -Arguments $Arguments -Stage $Stage -TimeoutSeconds 15 -Quiet -AllowNonZeroExit -CaptureStandardError:$IncludeStandardError
        if ($result.ExitCode -eq 0) {
            $output = @($result.Output)
            if ($IncludeStandardError -and $result.StandardError) { $output += @($result.StandardError -split '\r?\n' | Where-Object { $_ -ne '' }) }
            return [pscustomobject]@{ Succeeded = $true; Output = $output }
        }
        return [pscustomobject]@{ Succeeded = $false; Output = @() }
    }
    catch { return [pscustomobject]@{ Succeeded = $false; Output = @() } }
    finally {
        $script:FailureStage = $previousStage
        $script:FailureReason = $previousReason
    }
}

function Write-PreTlsEdgeDiagnostics([string]$ContainerId) {
    Set-Stage 'Pre-TLS edge diagnostics' 'Capturing sanitized edge port, TCP and recent-log diagnostics...'
    $stateResult = Invoke-EdgeDiagnosticCommand @('inspect','--format','{{.State.Status}}',$ContainerId) 'Edge state diagnostics'
    $state = if ($stateResult.Succeeded -and @($stateResult.Output).Count -gt 0) { Get-SafeEdgeState ([string]$stateResult.Output[0]) } else { 'unavailable' }
    Add-Result 'Pre-TLS edge container state' 'DIAGNOSTIC' $state

    $bindingResult = Invoke-EdgeDiagnosticCommand @('inspect','--format','{{json .HostConfig.PortBindings}}',$ContainerId) 'Edge host-port-binding diagnostics'
    $bindings = if ($bindingResult.Succeeded -and @($bindingResult.Output).Count -gt 0) { Format-SafeDockerPortMappings ([string]$bindingResult.Output[0]) } else { 'unavailable' }
    Add-Result 'Pre-TLS Docker HostConfig.PortBindings' 'DIAGNOSTIC' $bindings

    $networkPortResult = Invoke-EdgeDiagnosticCommand @('inspect','--format','{{json .NetworkSettings.Ports}}',$ContainerId) 'Edge network-port diagnostics'
    $networkPorts = if ($networkPortResult.Succeeded -and @($networkPortResult.Output).Count -gt 0) { Format-SafeDockerPortMappings ([string]$networkPortResult.Output[0]) } else { 'unavailable' }
    Add-Result 'Pre-TLS Docker NetworkSettings.Ports' 'DIAGNOSTIC' $networkPorts

    Add-Result "Pre-TLS host TCP $script:StagingPort" 'DIAGNOSTIC' (Get-HostPortReachability -Port $script:StagingPort -TimeoutMilliseconds 1500)

    $logResult = Invoke-EdgeDiagnosticCommand @('logs','--tail','40',$ContainerId) 'Recent edge log diagnostics' -IncludeStandardError
    if (-not $logResult.Succeeded) {
        Add-Result 'Pre-TLS recent edge logs' 'DIAGNOSTIC' 'unavailable (details withheld)'
    }
    else {
        Add-Result 'Pre-TLS recent edge logs' 'DIAGNOSTIC' 'sanitized classifications of docker logs --tail 40; raw lines withheld'
        $safeLogs = @(Get-SafeEdgeLogSummaries -Lines $logResult.Output)
        if ($safeLogs.Count -eq 0) { Add-Result 'Pre-TLS recent edge logs' 'DIAGNOSTIC' 'no recent log lines' }
        else {
            for ($i = 0; $i -lt $safeLogs.Count; $i++) {
                Add-Result ("Pre-TLS edge log {0}/{1}" -f ($i + 1), $safeLogs.Count) 'DIAGNOSTIC' $safeLogs[$i]
            }
        }
    }
}

function Test-HostTlsDiagnostic([string]$HostName, [string]$CaFile, [string]$Path, [string]$Name) {
    $openSsl = Get-OpenSslExecutable
    if (-not $openSsl) {
        Add-Result "$Name host TLS diagnostic" 'DIAGNOSTIC' 'OpenSSL unavailable; strict in-container TLS probe remains authoritative'
        return
    }
    $stage = "Host TLS diagnostic: $Name"
    $previousFailureStage = $script:FailureStage
    $previousFailureReason = $script:FailureReason
    try {
        $httpRequest = "GET $Path HTTP/1.1`r`nHost: $HostName`r`nConnection: close`r`n`r`n"
        $result = Invoke-NativeProcess -FileName $openSsl -Arguments @('s_client','-quiet','-connect',('127.0.0.1:' + $script:StagingPort),'-servername',$HostName,'-verify','5','-verify_hostname',$HostName,'-verify_return_error','-verify_quiet','-CAfile',$CaFile) -Stage $stage -TimeoutSeconds 12 -Quiet -AllowNonZeroExit -CaptureStandardError -StandardInputText $httpRequest
        if ($result.ExitCode -ne 0) {
            $category = Get-CurlFailureCategory -ExitCode $result.ExitCode -StandardError $result.StandardError
            $detail = if ($category -eq 'TLS CA chain validation failed (issuer unavailable)') { 'untrusted issuer presented on host loopback path; possible local TLS inspection; strict in-container probe will verify the edge certificate' } else { "$category; details withheld" }
            Add-Result "$Name host TLS diagnostic" 'DIAGNOSTIC' $detail
            return
        }
        $statusMatch = [Regex]::Match(($result.Output -join "`n"), '(?m)^HTTP/1\.[01]\s+(\d{3})\b')
        if ($statusMatch.Success) { Add-Result "$Name host TLS diagnostic" 'DIAGNOSTIC' 'strict host TLS verification succeeded' }
        else { Add-Result "$Name host TLS diagnostic" 'DIAGNOSTIC' 'TLS verified but HTTP response was unavailable' }
    }
    catch {
        $category = if ($script:FailureReason -match '(?i)timeout') { 'timeout' } else { 'process failure' }
        Add-Result "$Name host TLS diagnostic" 'DIAGNOSTIC' "$category; details withheld; strict in-container probe remains authoritative"
    }
    finally {
        $script:FailureStage = $previousFailureStage
        $script:FailureReason = $previousFailureReason
    }
}

function Test-HealthEndpoint([string]$ContainerId, [string]$HostName, [string]$Path, [int]$ExpectedStatus, [string]$ExpectedBodyStatus, [string]$Name) {
    $stage = "TLS health probe: $Name"
    Set-Stage $stage "Running strict TLS health probe inside the isolated edge container: $Name..."
    try {
        $result = Invoke-NativeProcess -FileName 'docker.exe' -Arguments @('exec',$ContainerId,'curl','--silent','--show-error','--max-time','8','--cacert','/run/secrets/edge_certificate','--resolve',($HostName + ':443:127.0.0.1'),'--output','-','--write-out',([Environment]::NewLine + '%{http_code}'),('https://' + $HostName + $Path)) -Stage $stage -TimeoutSeconds 12 -Quiet -AllowNonZeroExit -CaptureStandardError
    }
    catch {
        $processReason = if ($script:FailureReason) { $script:FailureReason } else { 'process failure; details withheld' }
        $processCategory = if ($processReason -match '(?i)timeout') { 'timeout' } else { 'process failure' }
        $failure = "edge curl exit code unavailable; stderr category: $processCategory; raw stderr unavailable"
        Add-Result "$Name curl failure" 'DIAGNOSTIC' $failure
        Set-SafeFailure $stage $failure
        throw 'TLS health probe failed.'
    }
    if ($result.ExitCode -ne 0) {
        $category = Get-CurlFailureCategory -ExitCode $result.ExitCode -StandardError $result.StandardError
        $failure = "edge curl exit code $($result.ExitCode); stderr category: $category (raw stderr withheld)"
        Add-Result "$Name curl failure" 'DIAGNOSTIC' $failure
        Set-SafeFailure $stage $failure
        throw 'TLS health probe failed.'
    }
    $lines = @($result.Output -split "`r?`n")
    $status = 0
    if (-not [int]::TryParse($lines[-1], [ref]$status)) { Set-SafeFailure $stage 'unexpected HTTP status'; throw 'TLS health probe failed.' }
    $body = ($lines | Select-Object -First ([Math]::Max(0, $lines.Count - 1))) -join "`n"
    if ($status -ne $ExpectedStatus) { Set-SafeFailure $stage 'unexpected HTTP status'; throw 'TLS health probe failed.' }
    if ($ExpectedBodyStatus -and $body -notmatch ('"status"\s*:\s*"' + [Regex]::Escape($ExpectedBodyStatus) + '"')) { Set-SafeFailure $stage 'unexpected safe health response'; throw 'TLS health probe failed.' }
    Add-Result $Name 'PASS' "HTTPS HTTP $status ($ExpectedBodyStatus)"
}

try {
    Set-Location $script:RepositoryRoot
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $script:EvidencePath) | Out-Null

    # Engine connectivity is tested before configuration, build, or Compose access.
    Set-Stage 'Docker preflight' 'Checking Docker CLI, Engine, and Compose connectivity...'
    foreach ($command in @(@('version'), @('info'), @('compose','version'))) {
        $null = Invoke-Docker -Arguments $command -Quiet -Stage "Docker $($command[0]) preflight" -TimeoutSeconds 30
    }
    Add-Result 'Docker CLI, Engine and Compose connectivity' 'PASS'

    $dockerVersion = (Invoke-Docker @('version','--format','{{.Client.Version}}|{{.Server.Version}}') -Quiet -Stage 'Docker version check' -TimeoutSeconds 30 | Select-Object -First 1).ToString().Trim()
    Add-Result 'Docker versions' 'PASS' $dockerVersion

    if (-not (Test-Path -LiteralPath $EnvFile -PathType Leaf)) {
        $requiredNames = @('SPRINT52_STAGING_CONFIRMATION','STAGING_DB_HOST','STAGING_DB_NAME','STAGING_DB_CONTAINER','STAGING_DB_VOLUME','STAGING_DB_RUNTIME_USER','STAGING_DOCKER_NETWORK','EDGE_HOST_PORT','PRIVATE_SUBNET','EDGE_PRIVATE_IP','API_IMAGE','API_ALLOWED_HOSTS','STAGING_HEALTH_HOST','JWT_ISSUER','JWT_AUDIENCE','DATABASE_CONNECTION_FILE','JWT_SIGNING_KEY_FILE','DP_CERTIFICATE_FILE','DP_CERTIFICATE_PASSWORD_FILE','DP_KEYS_PATH','EDGE_CERTIFICATE_FILE','EDGE_PRIVATE_KEY_FILE','POSTGRES_CA_FILE','EDGE_IMAGE')
        Add-Result 'Required staging configuration' 'FAIL' ('Missing file ' + $EnvFile + '; required variable names: ' + ($requiredNames -join ', '))
        throw 'Create the untracked staging env file from .env.production.example using synthetic isolated values only. Do not use production paths, credentials, database or key material.'
    }
    Set-Stage 'Staging configuration' 'Validating isolated staging configuration...'
    $resolvedEnvFile = (Resolve-Path -LiteralPath $EnvFile).Path
    if ($resolvedEnvFile.ToLowerInvariant().Contains('production') -or $resolvedEnvFile.ToLowerInvariant().Contains('prod_') -or $resolvedEnvFile.ToLowerInvariant().Contains('prod-')) { throw 'Production configuration paths are forbidden.' }
    $trackedResult = Invoke-NativeProcess -FileName 'git.exe' -Arguments @('ls-files','--',$EnvFile) -Stage 'Git staging-file tracking check' -TimeoutSeconds 15 -Quiet
    if ($trackedResult.ExitCode -ne 0) { Set-SafeFailure 'Staging configuration' 'unable to verify Git tracking status'; throw 'Staging configuration check failed.' }
    if ($trackedResult.Output.Count -gt 0) { Set-SafeFailure 'Staging configuration' 'staging env file is Git-tracked'; throw 'Staging configuration check failed.' }
    $ignoredResult = Invoke-NativeProcess -FileName 'git.exe' -Arguments @('check-ignore','--quiet','--',$EnvFile) -Stage 'Git ignored-file check' -TimeoutSeconds 15 -Quiet
    if ($ignoredResult.ExitCode -ne 0) { Set-SafeFailure 'Staging configuration' 'staging env file is not confirmed ignored by Git'; throw 'Staging configuration check failed.' }

    $envLines = Get-Content -LiteralPath $EnvFile
    $required = @('SPRINT52_STAGING_CONFIRMATION','STAGING_DB_HOST','STAGING_DB_NAME','STAGING_DB_CONTAINER','STAGING_DB_VOLUME','STAGING_DB_RUNTIME_USER','STAGING_DOCKER_NETWORK','EDGE_HOST_PORT','PRIVATE_SUBNET','EDGE_PRIVATE_IP','API_IMAGE','API_ALLOWED_HOSTS','STAGING_HEALTH_HOST','JWT_ISSUER','JWT_AUDIENCE','DATABASE_CONNECTION_FILE','JWT_SIGNING_KEY_FILE','DP_CERTIFICATE_FILE','DP_CERTIFICATE_PASSWORD_FILE','DP_KEYS_PATH','EDGE_CERTIFICATE_FILE','EDGE_PRIVATE_KEY_FILE','POSTGRES_CA_FILE','EDGE_IMAGE')
    $provided = @{}
    foreach ($line in $envLines) {
        if ($line -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*?)\s*$' -and $Matches[1] -notlike '#*') {
            $value = $Matches[2]
            if ($value.Length -ge 2 -and (($value.StartsWith('"') -and $value.EndsWith('"')) -or ($value.StartsWith("'") -and $value.EndsWith("'")))) { $value = $value.Substring(1, $value.Length - 2) }
            $provided[$Matches[1]] = $value
        }
    }
    $missing = @($required | Where-Object { -not $provided.ContainsKey($_) -or [string]::IsNullOrWhiteSpace($provided[$_]) })
    if ($missing.Count) { Add-Result 'Required staging configuration' 'FAIL' ('Missing variable names: ' + ($missing -join ', ')); throw 'Staging configuration is incomplete.' }
    $processSettings = @{}
    foreach ($item in Get-ChildItem Env:) { $processSettings[$item.Name] = $item.Value }
    $unexpectedOverrides = Get-UnexpectedComposeOverrides -Names (@($required) + @('EDGE_CA_FILE')) -EnvironmentSettings $processSettings
    if ($unexpectedOverrides) { Set-SafeFailure 'Staging configuration' ('unexpected process environment override names: ' + $unexpectedOverrides); throw 'Unexpected staging environment override detected.' }
    if ($provided['SPRINT52_STAGING_CONFIRMATION'] -ne 'ISOLATED_SYNTHETIC_DATABASE') { throw 'Set SPRINT52_STAGING_CONFIRMATION=ISOLATED_SYNTHETIC_DATABASE only after confirming all DB and key resources are disposable staging assets.' }
    if ($provided['API_ALLOWED_HOSTS'] -eq '*' -or $provided['EDGE_IMAGE'] -notmatch '@sha256:[a-f0-9]{64}$' -or $provided['API_IMAGE'] -notmatch '@sha256:[a-f0-9]{64}$') { throw 'Require explicit staging AllowedHosts and immutable reviewed API and edge image digests.' }
    $allowedHosts = @($provided['API_ALLOWED_HOSTS'].Split(';') | ForEach-Object { $_.Trim() })
    if ($allowedHosts -notcontains $provided['STAGING_HEALTH_HOST'] -or $provided['STAGING_HEALTH_HOST'] -notmatch '^[A-Za-z0-9.-]+$') { throw 'STAGING_HEALTH_HOST must be a DNS host listed in API_ALLOWED_HOSTS.' }
    if ($provided['DP_KEYS_PATH'].ToLowerInvariant().Contains('production') -or $provided['DP_KEYS_PATH'].ToLowerInvariant().Contains('prod_') -or $provided['DP_KEYS_PATH'].ToLowerInvariant().Contains('prod-') -or $provided['DATABASE_CONNECTION_FILE'].ToLowerInvariant().Contains('production') -or $provided['DATABASE_CONNECTION_FILE'].ToLowerInvariant().Contains('prod_') -or $provided['DATABASE_CONNECTION_FILE'].ToLowerInvariant().Contains('prod-')) { throw 'Production-like paths are forbidden; select isolated staging paths.' }

    foreach ($name in $required | Where-Object { $_ -match '_FILE$' -or $_ -eq 'POSTGRES_CA_FILE' }) {
        $path = $provided[$name]
        if (-not (Test-FullyQualifiedWindowsPath $path) -or -not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required staged secret file missing for variable name $name." }
    }
    $edgeProbeCa = Get-Sprint52EdgeProbeCaPath -Settings $provided
    if (-not (Test-FullyQualifiedWindowsPath $provided['DP_KEYS_PATH']) -or -not (Test-Path -LiteralPath $provided['DP_KEYS_PATH'] -PathType Container)) { throw 'DP_KEYS_PATH must be an existing isolated persistent directory.' }
    $dbConn = (Get-Content -LiteralPath $provided['DATABASE_CONNECTION_FILE'] -Raw).Trim()
    $safetyFailure = Get-Sprint52StagingSafetyFailure -Settings $provided -ConnectionString $dbConn
    if ($safetyFailure) { Set-SafeFailure 'Staging resource isolation' $safetyFailure; throw 'Staging resource isolation failed.' }
    $script:StagingPort = [int]$provided['EDGE_HOST_PORT']
    if ($dbConn -notmatch '(?i)(?:^|;)\s*Ssl\s*Mode\s*=\s*VerifyFull\s*(?:;|$)' -or $dbConn -match '(?i)(localhost|127\.0\.0\.1|production|prod[-_])') { throw 'Staging runtime connection must use an isolated staging host and SslMode=VerifyFull.' }
    $dbHostMatch = [Regex]::Match($dbConn, '(?i)(?:^|;)Host\s*=\s*([^;]+)')
    $dbNameMatch = [Regex]::Match($dbConn, '(?i)(?:^|;)(?:Database|Initial Catalog)\s*=\s*([^;]+)')
    $safeHost = $provided['STAGING_DB_HOST'].Trim().ToLowerInvariant()
    $safeDatabase = $provided['STAGING_DB_NAME'].Trim().ToLowerInvariant()
    $actualDbHost = $dbHostMatch.Groups[1].Value.Trim().Trim([char]'"').ToLowerInvariant()
    $actualDbName = $dbNameMatch.Groups[1].Value.Trim().Trim([char]'"').ToLowerInvariant()
    if (-not $dbHostMatch.Success -or -not $dbNameMatch.Success -or $actualDbHost -ne $safeHost -or $actualDbName -ne $safeDatabase) { throw 'The runtime connection host/database must exactly match STAGING_DB_HOST and STAGING_DB_NAME.' }
    if ($safeHost -notlike '*sprint52*' -or $safeDatabase -notlike '*sprint52*') { throw 'Staging host and database names must visibly contain sprint52.' }
    if ($dbConn -match '(?i)Include Error Detail\s*=\s*true') { throw 'Staging connection must suppress detailed database errors.' }
    Add-Result 'Synthetic isolated staging inputs' 'PASS' 'Required paths and DB TLS mode validated; values withheld'

    # Preserve production service settings but use the isolated DB network/CA and image tag.
    $caFileYaml = ConvertTo-Json -InputObject $provided['POSTGRES_CA_FILE'] -Compress
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $script:ImageOverride) | Out-Null
    @(
        'services:', '  api:', "    image: $script:ImageTag", '    build: .', '    environment:', "      Proxy__KnownProxies__0: $($provided['EDGE_PRIVATE_IP'])", '    secrets:', '      - source: sprint52_postgres_ca', '        target: postgres-ca.crt',
        'networks:', '  private:', '    external: true', "    name: $($provided['STAGING_DOCKER_NETWORK'])", '    ipam: !reset null',
        'secrets:', '  sprint52_postgres_ca:', "    file: $caFileYaml"
    ) | Set-Content -LiteralPath $script:ImageOverride -Encoding UTF8
    $script:ComposeFiles = @('--env-file', $EnvFile, '-f', 'compose.production.yaml', '-f', $script:ImageOverride)

    # Production compose has a fixed project subnet/address and external PostgreSQL.
    # Isolate its run-specific Docker resource namespace and fail if a resource collision exists.
    Set-Stage 'Docker resource preflight' 'Checking only the unique staging project and configured external resources...'
    $existing = Invoke-Docker @('ps','-a','--filter',"label=com.docker.compose.project=$script:Project",'--format','{{.ID}}') -Quiet -Stage 'Compose project collision check' -TimeoutSeconds 30
    if (($existing | Where-Object { $_.ToString().Trim() }).Count -gt 0) { throw "Isolated project '$script:Project' already exists. Review it manually; this script never deletes/reuses pre-existing resources." }
    $networkSubnet = Invoke-Docker @('network','inspect','--format','{{range .IPAM.Config}}{{.Subnet}}{{end}}',$provided['STAGING_DOCKER_NETWORK']) -Quiet -Stage 'Staging network inspection' -TimeoutSeconds 30
    if (($networkSubnet | Select-Object -First 1).ToString().Trim() -ne $provided['PRIVATE_SUBNET']) { throw 'The isolated staging network subnet does not match the configured Sprint 5.2 subnet.' }
    $networkLabelsJson = (Invoke-Docker @('network','inspect','--format','{{json .Labels}}',$provided['STAGING_DOCKER_NETWORK']) -Quiet -Stage 'Staging network label check' -TimeoutSeconds 30 | Select-Object -First 1).ToString().Trim()
    try { $networkLabels = ConvertFrom-Json -InputObject $networkLabelsJson -ErrorAction Stop }
    catch { Set-SafeFailure 'Staging network label check' 'could not parse inspected labels'; throw 'Staging network label check failed.' }
    if ($null -eq $networkLabels -or $networkLabels.'groudeal.sprint52.staging' -cne 'true') { Set-SafeFailure 'Staging network label check' 'required staging label is missing or false'; throw 'Staging network label check failed.' }
    $volume = Invoke-Docker @('volume','ls','--filter',"label=com.docker.compose.project=$script:Project",'--format','{{.Name}}') -Quiet -Stage 'Compose volume collision check' -TimeoutSeconds 30
    if (($volume | Where-Object { $_.ToString().Trim() }).Count -gt 0) { throw 'Isolated Compose volume already exists; inspect manually; no cleanup performed.' }

    $dbState = (Invoke-Docker @('inspect','--format','{{.State.Status}}|{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}',$provided['STAGING_DB_CONTAINER']) -Quiet -Stage 'Synthetic database state check' -TimeoutSeconds 30 | Select-Object -First 1).ToString().Trim()
    if ($dbState -ne 'running|healthy') { Set-SafeFailure 'Synthetic database preflight' 'configured database container is not running and healthy'; throw 'Synthetic database preflight failed.' }
    $networkContainersJson = (Invoke-Docker @('network','inspect','--format','{{json .Containers}}',$provided['STAGING_DOCKER_NETWORK']) -Quiet -Stage 'Staging network membership check' -TimeoutSeconds 30 | Select-Object -First 1).ToString().Trim()
    try { $networkContainers = ConvertFrom-Json -InputObject $networkContainersJson -ErrorAction Stop }
    catch { Set-SafeFailure 'Staging network membership check' 'could not parse network member identities'; throw 'Staging network isolation check failed.' }
    $membershipFailure = Get-Sprint52NetworkMembershipFailure -ContainerMap $networkContainers -ExpectedContainer $provided['STAGING_DB_CONTAINER']
    if ($membershipFailure) { Set-SafeFailure 'Staging network membership check' $membershipFailure; throw 'Staging network isolation check failed.' }
    $dbLabelsJson = (Invoke-Docker @('inspect','--format','{{json .Config.Labels}}',$provided['STAGING_DB_CONTAINER']) -Quiet -Stage 'Synthetic database label check' -TimeoutSeconds 30 | Select-Object -First 1).ToString().Trim()
    try { $dbLabels = ConvertFrom-Json -InputObject $dbLabelsJson -ErrorAction Stop }
    catch { Set-SafeFailure 'Synthetic database label check' 'could not parse inspected labels'; throw 'Synthetic database label check failed.' }
    if ($null -eq $dbLabels -or $dbLabels.'groudeal.sprint52.staging' -cne 'true') { Set-SafeFailure 'Synthetic database label check' 'required staging label is missing or false'; throw 'Synthetic database label check failed.' }
    $actualContainerName = (Invoke-Docker @('inspect','--format','{{.Name}}',$provided['STAGING_DB_CONTAINER']) -Quiet -Stage 'Synthetic database container identity check' -TimeoutSeconds 30 | Select-Object -First 1).ToString().Trim().TrimStart('/')
    if ($actualContainerName -cne $provided['STAGING_DB_CONTAINER']) { Set-SafeFailure 'Synthetic database container identity check' 'inspected container name does not match configured isolated name'; throw 'Synthetic database identity check failed.' }
    $dbMountsJson = (Invoke-Docker @('inspect','--format','{{json .Mounts}}',$provided['STAGING_DB_CONTAINER']) -Quiet -Stage 'Synthetic database volume identity check' -TimeoutSeconds 30 | Select-Object -First 1).ToString().Trim()
    try { $dbMounts = @(ConvertFrom-Json -InputObject $dbMountsJson -ErrorAction Stop) }
    catch { Set-SafeFailure 'Synthetic database volume identity check' 'could not parse inspected mounts'; throw 'Synthetic database volume check failed.' }
    $dataMount = @($dbMounts | Where-Object { $_.Destination -eq '/var/lib/postgresql/data' -and $_.Type -eq 'volume' -and $_.Name -ceq $provided['STAGING_DB_VOLUME'] })
    if ($dataMount.Count -ne 1) { Set-SafeFailure 'Synthetic database volume identity check' 'database data directory is not backed by the configured unique volume'; throw 'Synthetic database volume check failed.' }
    $dbVolumeLabelsJson = (Invoke-Docker @('volume','inspect','--format','{{json .Labels}}',$provided['STAGING_DB_VOLUME']) -Quiet -Stage 'Synthetic database volume label check' -TimeoutSeconds 30 | Select-Object -First 1).ToString().Trim()
    try { $dbVolumeLabels = ConvertFrom-Json -InputObject $dbVolumeLabelsJson -ErrorAction Stop }
    catch { Set-SafeFailure 'Synthetic database volume label check' 'could not parse inspected labels'; throw 'Synthetic database volume check failed.' }
    if ($null -eq $dbVolumeLabels -or $dbVolumeLabels.'groudeal.sprint52.staging' -cne 'true') { Set-SafeFailure 'Synthetic database volume label check' 'required staging label is missing or false'; throw 'Synthetic database volume check failed.' }
    $dbNetworksJson = (Invoke-Docker @('inspect','--format','{{json .NetworkSettings.Networks}}',$provided['STAGING_DB_CONTAINER']) -Quiet -Stage 'Synthetic database network check' -TimeoutSeconds 30 | Select-Object -First 1).ToString()
    $dbNetworks = $dbNetworksJson | ConvertFrom-Json
    $networkProperty = $dbNetworks.PSObject.Properties[$provided['STAGING_DOCKER_NETWORK']]
    if ($null -eq $networkProperty -or @($networkProperty.Value.Aliases) -notcontains $provided['STAGING_DB_HOST']) { Set-SafeFailure 'Synthetic database network check' 'configured DNS alias is not attached to the isolated network'; throw 'Synthetic database network check failed.' }

    Set-Stage 'Compose configuration' 'Validating production-like API/edge Compose configuration...'
    $config = Invoke-Compose @('config') -Quiet -Stage 'Compose configuration' -TimeoutSeconds 60
    Add-Result 'Compose configuration' 'PASS'
    $serviceNames = Invoke-Compose @('config','--services') -Quiet -Stage 'Compose service validation' -TimeoutSeconds 60
    if ($serviceNames -notcontains 'api' -or $serviceNames -notcontains 'edge') { throw 'Expected production API and edge services missing.' }
    if ($serviceNames -contains 'postgres') { throw 'Unexpected DB service in production Compose; inspect architecture before proceeding.' }
    $script:ComposeStarted = $true
    Set-Stage 'API/edge startup' 'Starting isolated API/edge (image build and pull may take several minutes)...'
    $null = Invoke-Compose @('up','--build','--detach') -Stage 'API/edge startup' -TimeoutSeconds 900
    Add-Result 'Production-like API and edge startup' 'PASS'

    $api = Wait-Healthy $script:ApiService 240
    # The production Compose edge has no Docker HEALTHCHECK. Require it to be
    # running here; the subsequent HTTPS liveness/readiness probes verify service health.
    $edge = Wait-Healthy $script:EdgeService 120 -AllowNoHealthcheck
    $ps = Invoke-Compose @('ps') -Quiet -Stage 'Container health summary' -TimeoutSeconds 30
    Add-Result 'Container status and health' 'PASS' 'API healthy; edge running; edge TLS probes verify service health inside the isolated container'
    Write-PreTlsEdgeDiagnostics $edge
    Test-HostTlsDiagnostic $provided['STAGING_HEALTH_HOST'] $edgeProbeCa '/health/live' 'Liveness endpoint'
    Test-HealthEndpoint $edge $provided['STAGING_HEALTH_HOST'] '/health/live' 200 'live' 'Liveness endpoint'
    Test-HealthEndpoint $edge $provided['STAGING_HEALTH_HOST'] '/health/ready' 200 'ready' 'Readiness endpoint (database, migrations and worker)'

    # The separately provisioned, labeled synthetic PostgreSQL container is inspected
    # only for health and network identity. Readiness confirms connectivity/migrations;
    # this script never reads DB contents or uses the migration identity.
    Add-Result 'PostgreSQL container and TLS network' 'PASS' 'Labeled healthy synthetic database shares the isolated network; readiness confirms connectivity and no pending migrations'
    Add-Result 'Migration identity and runtime privilege boundary' 'NOT VERIFIED' 'Apply migrations as the separate migration identity and review runtime role assertions as documented'
    Add-Result 'Database/worker recovery and DB outage/recovery' 'NOT VERIFIED' 'Requires operator-managed synthetic DB checks and persisted worker workload; API readiness is checked above'
    Add-Result 'ADMIN MFA RESTART VERIFICATION' 'MANUAL ACTION REQUIRED' 'Follow the exact secret-safe procedure in docs/sprint52-staging-isolation.md'
    Add-Result 'PostgreSQL restart, volume persistence, backup/restore' 'MANUAL ACTION REQUIRED' 'PostgreSQL is a separately managed isolated staging container; follow the documented volume/backup/restore drill'

    # Restart only known Compose services. This path is reached only after a fresh, isolated
    # project started by this invocation; never stop/delete other Docker resources.
    Set-Stage 'API restart' 'Restarting isolated API service...'
    $null = Invoke-Compose @('restart',$script:ApiService) -Stage 'API restart' -TimeoutSeconds 90
    $null = Wait-Healthy $script:ApiService 180
    Add-Result 'API container restart' 'PASS'
    Test-HealthEndpoint $edge $provided['STAGING_HEALTH_HOST'] '/health/live' 200 'live' 'Liveness after API restart'
    Test-HealthEndpoint $edge $provided['STAGING_HEALTH_HOST'] '/health/ready' 200 'ready' 'Readiness after API restart'
    Set-Stage 'Edge restart' 'Restarting isolated edge service...'
    $null = Invoke-Compose @('restart',$script:EdgeService) -Stage 'Edge restart' -TimeoutSeconds 90
    # The edge image has no HEALTHCHECK; restart recovery uses the same running-state
    # condition as initial startup, followed by the strict HTTPS probes below.
    $edge = Wait-Healthy $script:EdgeService 120 -AllowNoHealthcheck
    Add-Result 'Edge restart' 'PASS'
    Test-HealthEndpoint $edge $provided['STAGING_HEALTH_HOST'] '/health/live' 200 'live' 'Liveness after edge restart'
    Test-HealthEndpoint $edge $provided['STAGING_HEALTH_HOST'] '/health/ready' 200 'ready' 'Readiness after edge restart'

    # No fabricated scenario: supported repository mechanism for persisted campaign retries
    # is the PostgreSQL integration suite, including WorkerRecoveryTests / OperationsTests.
    Add-Result 'Worker restart recovery' 'NOT VERIFIED' 'Live pending-work scenario must be generated/observed by operator; repository regression suites cover persisted retry and recovery behavior'
    Add-Result 'Backup/restore' 'NOT VERIFIED' 'Requires a dump/restore drill between separate isolated PostgreSQL databases'
    Add-Result 'Full-stack restart and DB volume retention' 'NOT VERIFIED' 'Production Compose intentionally does not own the separately managed PostgreSQL volume; do not run down -v'
    Set-Stage 'Restricted log review' 'Scanning bounded API and edge logs in memory before cleanup...'
    Write-ContainerLogSecretScan -ContainerId $api -ServiceName 'API'
    Write-ContainerLogSecretScan -ContainerId $edge -ServiceName 'Edge'

    if (-not $KeepResources) {
        # Stop only the exact services started by this invocation; retain all persistent data.
        Set-Stage 'Scoped Compose cleanup' 'Cleaning only the current isolated API/edge project...'
        $null = Invoke-Compose @('down','--remove-orphans') -Stage 'Scoped Compose cleanup' -TimeoutSeconds 120
        $script:CleanupDone = $true
        Add-Result 'Isolated service cleanup' 'PASS' 'Removed only this fresh Compose project containers; external network, volumes and database were untouched'
    }
} catch {
    if (-not $script:FailureStage) { Set-SafeFailure $script:CurrentStage 'verification check failed; details withheld' }
    $safeMessage = Redact-Line $_.Exception.Message
    Add-Result $script:FailureStage 'FAIL' $safeMessage
    $script:Failures.Add($safeMessage)
} finally {
    if ($script:ComposeStarted -and -not $script:CleanupDone -and -not $KeepResources) {
        try {
            Set-Stage 'Failure cleanup' 'Cleaning only the current isolated API/edge project after failure...'
            $null = Invoke-Compose @('down','--remove-orphans') -Stage 'Failure cleanup' -TimeoutSeconds 120
            $script:CleanupDone = $true
            Add-Result 'Failure cleanup' 'PASS' 'Removed only this invocation project containers; external network, volumes and database were untouched'
        }
        catch {
            $cleanupReason = if ($script:FailureStage -eq 'Failure cleanup' -and $script:FailureReason) { $script:FailureReason } else { 'scoped cleanup command failed; details withheld' }
            Add-Result 'Failure cleanup' 'FAIL' $cleanupReason
            $script:Failures.Add('Failure cleanup failed')
        }
    }
    $timestamp = [DateTimeOffset]::Now.ToString('yyyy-MM-dd HH:mm:ss zzz')
    $header = @("Groudeal Sprint 5.2 host staging verification", "Timestamp: $timestamp", "Compose project: $script:Project", "Docker/Compose execution: host PowerShell only")
    $hasUnverified = @($script:Results | Where-Object { $_.StartsWith('NOT VERIFIED') -or $_.StartsWith('MANUAL ACTION REQUIRED') }).Count -gt 0
    $overall = if ($script:Failures.Count) { 'Overall: FAIL - automated prerequisite or runtime check failed' } elseif ($hasUnverified) { 'Overall: NOT VERIFIED - manual Sprint 5.2 checks remain' } else { 'Overall: PASS' }
    $content = ($header + $script:Results + $overall) -join [Environment]::NewLine
    Set-Content -LiteralPath $script:EvidencePath -Value $content -Encoding UTF8
    Write-Host "Sanitized evidence: $script:EvidencePath"
}

if ($script:Failures.Count -gt 0) { exit 1 }
    if (@($script:Results | Where-Object { $_.StartsWith('NOT VERIFIED') -or $_.StartsWith('MANUAL ACTION REQUIRED') }).Count -gt 0) { exit 2 }
exit 0

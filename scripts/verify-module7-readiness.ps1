# Uses one fresh backend-local native PostgreSQL cluster. Never uses an existing server.
param([switch]$FocusedOnly,[switch]$SkipFocused,[string]$PostgresBin='')
$ErrorActionPreference='Stop'
Add-Type -TypeDefinition 'using System.Runtime.InteropServices; public static class Module7ExecutionState { [DllImport("kernel32.dll", SetLastError=true)] public static extern uint SetThreadExecutionState(uint flags); }'
$root=Split-Path -Parent $PSScriptRoot
if($FocusedOnly -and $SkipFocused){throw 'FocusedOnly and SkipFocused cannot be combined.'}
if(-not $PostgresBin){$PostgresBin=Join-Path $root '.local/audit-postgres/pgsql/bin'}
foreach($tool in @('initdb.exe','pg_ctl.exe','pg_dump.exe','pg_restore.exe')){if(-not(Test-Path (Join-Path $PostgresBin $tool))){throw "Required local PostgreSQL tool missing: $tool"}}
$run=[IO.Path]::GetFullPath((Join-Path $root ('.local/module7-'+[Guid]::NewGuid().ToString('N'))))
$allowed=[IO.Path]::GetFullPath((Join-Path $root '.local'))+[IO.Path]::DirectorySeparatorChar
if(-not $run.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){throw 'Evidence must remain backend-local.'}
New-Item -ItemType Directory -Path $run | Out-Null
$data=Join-Path $run 'data';$temporary=Join-Path $run 'tmp'
New-Item -ItemType Directory -Path $temporary | Out-Null
$previous=@{}
foreach($key in @('DOTNET_ROOT','DOTNET_CLI_HOME','TEMP','TMP','GROUPBUY_TEST_POSTGRES','GROUPBUY_TEST_PG_BIN','NuGetAudit')){$previous[$key]=[Environment]::GetEnvironmentVariable($key)}
$dotnet=Join-Path $root '.local/dotnet10/dotnet.exe'
if(-not(Test-Path $dotnet)){$dotnet='dotnet'}
function Check([string]$Name,[string[]]$Arguments){
    & $dotnet @Arguments *> (Join-Path $run "$Name.log")
    $code=$LASTEXITCODE
    Get-Content (Join-Path $run "$Name.log") -Tail 8
    if($code -ne 0){throw "$Name failed; evidence: $run"}
}
function Tests([string]$Name,[string]$Project,[int]$Minimum,[string]$Filter=''){
    $arguments=@('test',$Project,'--no-build','--no-restore','-m:1','--settings','scripts/customer-authentication.runsettings',
        '--blame-hang-timeout','5m','--blame-hang-dump-type','none','--results-directory',$run,'--logger',"trx;LogFileName=$Name.trx",'--logger','console;verbosity=normal')
    if($Filter){$arguments+=@('--filter',$Filter)}
    Check $Name $arguments
    [xml]$trx=Get-Content (Join-Path $run "$Name.trx")
    $counts=$trx.SelectSingleNode('//*[local-name()="Counters"]')
    if([int]$counts.GetAttribute('passed') -lt $Minimum -or [int]$counts.GetAttribute('failed') -ne 0 -or [int]$counts.GetAttribute('notExecuted') -ne 0 -or [int]$counts.GetAttribute('aborted') -ne 0){throw "$Name has missing, failed or skipped tests."}
}
function PgControl([string[]]$Arguments,[string]$Name){
    $process=Start-Process -FilePath (Join-Path $PostgresBin 'pg_ctl.exe') -ArgumentList $Arguments -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $run "$Name-out.log") -RedirectStandardError (Join-Path $run "$Name-error.log")
    # Windows PowerShell 5 needs the native handle retained to expose ExitCode
    # after WaitForExit; otherwise a successful short-lived pg_ctl can look null.
    $nativeHandle=$process.Handle
    if(-not $process.WaitForExit(60000)){throw 'Owned PostgreSQL control timed out.'}
    $process.Refresh()
    if($null -eq $process.ExitCode){throw 'Owned PostgreSQL control exit status is unavailable.'}
    if($process.ExitCode -ne 0){throw "Owned PostgreSQL $Name failed."}
}
Push-Location $root
$baselineApi=@(Get-CimInstance Win32_Process | Where-Object {$_.Name -eq 'dotnet.exe' -and $_.CommandLine -like "*$root*GroupBuy.API.dll*"} | Select-Object -ExpandProperty ProcessId)
$baselineTls=@(Get-ChildItem (Join-Path $root '.local') -Directory -Filter 'render-tls-*' | Select-Object -ExpandProperty FullName)
try{
    if([Module7ExecutionState]::SetThreadExecutionState([uint32]2147483649) -eq 0){throw 'Temporary system sleep guard could not be acquired.'}
    $env:DOTNET_CLI_HOME=Join-Path $root '.local'
    if($dotnet -ne 'dotnet'){$env:DOTNET_ROOT=Split-Path -Parent $dotnet}
    $env:TEMP=$temporary;$env:TMP=$temporary;$env:NuGetAudit='false'
    Remove-Item Env:GROUPBUY_TEST_POSTGRES -ErrorAction SilentlyContinue
    Check '01-restore' @('restore','GroupBuy.sln','--disable-parallel','-m:1')
    Check '02-build' @('build','GroupBuy.sln','--no-restore','-m:1','-p:UseSharedCompilation=false')
    if(-not $SkipFocused){Tests '03-focused-unit' 'tests/GroupBuy.UnitTests' 38 'FullyQualifiedName~Module7_'}
    if(-not $FocusedOnly){Tests '04-unit' 'tests/GroupBuy.UnitTests' 225}
    $listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
    $listener.Start();$port=$listener.LocalEndpoint.Port;$listener.Stop()
    if($port -eq 55432){throw 'Protected endpoint excluded.'}
    $randomBytes=New-Object byte[] 32
    $random=[Security.Cryptography.RandomNumberGenerator]::Create()
    try{$random.GetBytes($randomBytes)}finally{$random.Dispose()}
    $password=[BitConverter]::ToString($randomBytes).Replace('-','')
    $passwordFile=Join-Path $run 'password.txt'
    [IO.File]::WriteAllText($passwordFile,$password)
    & (Join-Path $PostgresBin 'initdb.exe') -D $data -U module7_fixture --auth-host=scram-sha-256 --auth-local=scram-sha-256 --pwfile=$passwordFile --encoding=UTF8 --locale=C *> (Join-Path $run '05-initdb.log')
    if($LASTEXITCODE -ne 0){throw 'Fresh PostgreSQL initialization failed.'}
    Remove-Item -LiteralPath $passwordFile
    # Quoted argument paths support the existing workspace directory with spaces.
    PgControl @('-D',('"'+$data+'"'),'-l',('"'+(Join-Path $run 'postgres.log')+'"'),'-o',('"-h 127.0.0.1 -p '+$port+'"'),'-w','start') '06-start'
    $env:GROUPBUY_TEST_POSTGRES="Host=127.0.0.1;Port=$port;Database=postgres;Username=module7_fixture;Password=$password;Pooling=true"
    $env:GROUPBUY_TEST_PG_BIN=$PostgresBin
    "Fresh loopback PostgreSQL 17, port $port; protected port excluded." | Set-Content (Join-Path $run 'fixture.log')
    if(-not $SkipFocused){Tests '07-focused-integration' 'tests/GroupBuy.IntegrationTests' 22 'FullyQualifiedName~Module7_'}
    if(-not $FocusedOnly){Tests '08-integration' 'tests/GroupBuy.IntegrationTests' 381}
    Check '09-model' @('ef','migrations','has-pending-model-changes','--project','src/GroupBuy.Infrastructure','--no-build')
    # Suppress advisory CRLF conversion warnings for this invocation only. Windows
    # PowerShell otherwise promotes native stderr to an error under Stop, even
    # when Git returns success. Whitespace validation and exit checks remain on.
    git -c core.safecrlf=false diff --check *> (Join-Path $run '10-diff.log')
    if($LASTEXITCODE -ne 0){throw 'Diff whitespace check failed.'}
    $paths=@(@(git -c core.safecrlf=false diff --name-only)+@(git ls-files --others --exclude-standard) | Sort-Object -Unique)
    $secrets=0;$production=0
    foreach($path in $paths){foreach($line in Get-Content -LiteralPath $path){
        if($line -match '(-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----|AKIA[0-9A-Z]{16}|sk_live_[0-9A-Za-z]{16,}|AIza[0-9A-Za-z_-]{30,}|ghp_[0-9A-Za-z]{30,})'){$secrets++}
        if($line -match '(postgres(ql)?://[^\s]+:[^\s]+@|[a-z0-9-]+\.[a-z0-9-]+\.neon\.tech)'){$production++}
    }}
    "Changed files reviewed: $($paths.Count); secret findings: $secrets; production-connection findings: $production" | Set-Content (Join-Path $run '11-scans.log')
    if($secrets -or $production){throw 'Changed-file scan requires review.'}
    git status --short | Set-Content (Join-Path $run '12-worktree.log')
    'Requested checks passed; fixture cleanup follows.' | Set-Content (Join-Path $run 'CHECKS-PASSED.txt')
} finally {
    try{
        if($passwordFile -and (Test-Path -LiteralPath $passwordFile)){Remove-Item -LiteralPath $passwordFile}
        if(Test-Path (Join-Path $data 'postmaster.pid')){
            $resolved=[IO.Path]::GetFullPath($data)
            if(-not $resolved.StartsWith($run+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Refusing unowned PostgreSQL control.'}
            PgControl @('-D',('"'+$data+'"'),'-m','fast','-w','stop') '13-stop'
        }
        $remaining=Test-Path (Join-Path $data 'postmaster.pid')
        $remainingApi=@(Get-CimInstance Win32_Process | Where-Object {$_.Name -eq 'dotnet.exe' -and $_.CommandLine -like "*$root*GroupBuy.API.dll*" -and $baselineApi -notcontains $_.ProcessId})
        $remainingTls=@(Get-ChildItem (Join-Path $root '.local') -Directory -Filter 'render-tls-*' | Where-Object {$baselineTls -notcontains $_.FullName -and (Test-Path (Join-Path $_.FullName 'data/postmaster.pid'))})
        "Owned PostgreSQL PID file remaining: $remaining; new TLS PostgreSQL PID files remaining: $($remainingTls.Count); new backend API processes remaining: $($remainingApi.Count)" | Set-Content (Join-Path $run '14-cleanup.log')
        if($remaining -or $remainingApi.Count -or $remainingTls.Count){throw 'Fixture cleanup remains outstanding; no unrelated processes were stopped.'}
    } finally {
        [void][Module7ExecutionState]::SetThreadExecutionState([uint32]2147483648)
        'Temporary thread sleep guard released; no saved power settings changed.' | Set-Content (Join-Path $run '15-sleep-guard.log')
        foreach($key in $previous.Keys){[Environment]::SetEnvironmentVariable($key,$previous[$key])}
        Pop-Location
        Write-Output "Module 7 evidence: $run"
    }
}

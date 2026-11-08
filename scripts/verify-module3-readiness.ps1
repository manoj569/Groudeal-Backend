# Local-only wrapper around the existing complete security/regression gate.
param([string]$RunDirectory = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $RunDirectory) { $RunDirectory = Join-Path $root ('.local/module3-final-' + [Guid]::NewGuid().ToString('N')) }
& (Join-Path $PSScriptRoot 'verify-render-readiness.ps1') -RunDirectory $RunDirectory
if (-not (Test-Path (Join-Path $RunDirectory 'COMPLETE.txt'))) { throw 'Base complete gate did not finish.' }
foreach ($expectation in @(@('04-all-unit',140),@('08-all-postgres',313))) {
    [xml]$trx = Get-Content (Join-Path $RunDirectory ($expectation[0] + '.trx'))
    $counts = $trx.SelectSingleNode('//*[local-name()="Counters"]')
    if ([int]$counts.GetAttribute('passed') -ne $expectation[1] -or [int]$counts.GetAttribute('failed') -ne 0) { throw 'Module 3 complete suite count is unexpected; review the TRX.' }
}
'Module 3 complete local gate passed; no production rollout performed.' | Set-Content (Join-Path $RunDirectory 'MODULE3-COMPLETE.txt')
Get-Content (Join-Path $RunDirectory 'MODULE3-COMPLETE.txt')

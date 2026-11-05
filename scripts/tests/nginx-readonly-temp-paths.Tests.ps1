$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$nginx = Get-Content -LiteralPath (Join-Path $repositoryRoot 'deployment\nginx.conf') -Raw
$compose = Get-Content -LiteralPath (Join-Path $repositoryRoot 'compose.production.yaml') -Raw

$directives = @('client_body_temp_path','fastcgi_temp_path','proxy_temp_path','scgi_temp_path','uwsgi_temp_path')
foreach ($directive in $directives) {
    if ($nginx -notmatch ("(?m)^\s*" + [regex]::Escape($directive) + '\s+/tmp/[A-Za-z0-9_-]+;\s*$')) {
        throw "FAIL $directive must use the writable /tmp tmpfs."
    }
    Write-Output "PASS $directive uses /tmp"
}

if ($compose -notmatch '(?ms)read_only:\s*true.*?tmpfs:\s*\n\s*- /tmp:rw,noexec,nosuid,size=32m') {
    throw 'FAIL read-only edge must retain its bounded writable /tmp tmpfs.'
}
Write-Output 'PASS read-only edge has bounded /tmp tmpfs'

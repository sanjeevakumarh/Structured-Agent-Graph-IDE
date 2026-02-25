param(
    [switch]$WhatIf
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Run from repo root even when invoked inside utils
$scriptDir = Split-Path -Parent $PSCommandPath
$repoRoot  = Split-Path -Parent $scriptDir
Push-Location $repoRoot

function Remove-PathSafe {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return }
    Write-Host "Removing $Path"
    Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue -WhatIf:$WhatIf
}

try {
    $dirNames = @('bin', 'obj', 'out', 'dist', 'node_modules', 'packages', 'logs', 'coverage')
    foreach ($name in $dirNames) {
        Get-ChildItem -Path . -Recurse -Directory -Filter $name -ErrorAction SilentlyContinue |
            ForEach-Object { Remove-PathSafe $_.FullName }
    }

    Get-ChildItem -Path . -Recurse -File -Filter 'package-lock.json' -ErrorAction SilentlyContinue |
        ForEach-Object { Remove-PathSafe $_.FullName }
}
finally {
    Pop-Location
}

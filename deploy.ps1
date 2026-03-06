#Requires -Version 5.1
<#
.SYNOPSIS
    Deploys SAGIDE: installs the VS Code extension, starts the backend service,
    and verifies health.

.DESCRIPTION
    Expects build-all.ps1 to have been run first (Release binaries must exist).
    Starts the service with an absolute PromptsPath so it works from the
    bin/Release directory without path resolution issues.

.PARAMETER SkipExtension
    Skip installing the VS Code extension.

.PARAMETER SkipService
    Skip starting the backend service (useful if you only want to reinstall the extension).

.PARAMETER InstallCli
    Copy sag.exe to %USERPROFILE%\.local\bin and add it to the user PATH if not present.

.PARAMETER Background
    Start the service window-hidden (background process). Default: normal window visible.

.PARAMETER HealthCheckUrl
    Override the health check URL. Default: http://localhost:5100/api/health

.EXAMPLE
    .\deploy.ps1
    .\deploy.ps1 -SkipExtension -Background
    .\deploy.ps1 -InstallCli
#>
param(
    [switch]$SkipExtension,
    [switch]$SkipService,
    [switch]$InstallCli,
    [switch]$Background,
    [string]$HealthCheckUrl = "http://localhost:5100/api/health"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

function Step([string]$msg)  { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Ok([string]$msg)    { Write-Host "    OK   $msg" -ForegroundColor Green }
function Warn([string]$msg)  { Write-Host "    WARN $msg" -ForegroundColor Yellow }
function Err([string]$msg)   { Write-Host "`n    ERROR: $msg" -ForegroundColor Red; exit 1 }

# ---------------------------------------------------------------------------
# Prerequisites
# ---------------------------------------------------------------------------
Step "Checking prerequisites..."
$missing = @()
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { $missing += 'dotnet' }
if (-not (Get-Command node   -ErrorAction SilentlyContinue)) { $missing += 'node' }
if (-not (Get-Command npm    -ErrorAction SilentlyContinue)) { $missing += 'npm' }
if ($missing.Count -gt 0) {
    Err "Missing required tools: $($missing -join ', '). Install them and retry."
}
Ok "dotnet, node, npm found"

# ---------------------------------------------------------------------------
# Paths
# ---------------------------------------------------------------------------
$serviceExe  = "$root\src\SAGIDE.Service\bin\Release\net9.0\SAGIDE.Service.exe"
$serviceDir  = "$root\src\SAGIDE.Service\bin\Release\net9.0"
$promptsPath = "$root\prompts"
$cliExe      = "$root\tools\cli\sag\bin\Release\net9.0\sag.exe"

# Find the most recently built VSIX
$vsixFiles = Get-ChildItem "$root\src\vscode-extension\*.vsix" -ErrorAction SilentlyContinue |
             Sort-Object LastWriteTime -Descending
$vsixPath  = if ($vsixFiles) { $vsixFiles[0].FullName } else { $null }

# ---------------------------------------------------------------------------
# 1. Install VS Code extension
# ---------------------------------------------------------------------------
if (-not $SkipExtension) {
    Step "Installing VS Code extension..."
    if (-not $vsixPath) {
        Err "No .vsix found in src/vscode-extension/ — run build-all.ps1 first"
    }
    Write-Host "    VSIX: $(Split-Path $vsixPath -Leaf)"
    & code --install-extension $vsixPath --force
    if ($LASTEXITCODE -ne 0) {
        Warn "code returned exit code $LASTEXITCODE — verify 'code' is in your PATH"
    } else {
        Ok "Extension installed — reload VS Code (Ctrl+Shift+P → 'Reload Window')"
    }
}

# ---------------------------------------------------------------------------
# 2. Install CLI (optional)
# ---------------------------------------------------------------------------
if ($InstallCli) {
    Step "Installing sag CLI..."
    if (-not (Test-Path $cliExe)) {
        Err "sag.exe not found at $cliExe — run build-all.ps1 first"
    }

    $cliDir = "$env:USERPROFILE\.local\bin"
    if (-not (Test-Path $cliDir)) {
        New-Item -ItemType Directory $cliDir -Force | Out-Null
    }

    Copy-Item $cliExe "$cliDir\sag.exe" -Force
    Ok "Copied sag.exe to $cliDir"

    # Add to user PATH if not already there
    $userPath = [Environment]::GetEnvironmentVariable("PATH", "User")
    if ($userPath -notlike "*$cliDir*") {
        [Environment]::SetEnvironmentVariable("PATH", "$userPath;$cliDir", "User")
        Ok "Added $cliDir to user PATH (restart terminal to take effect)"
    } else {
        Ok "$cliDir is already in user PATH"
    }
}

# ---------------------------------------------------------------------------
# 3. Start backend service
# ---------------------------------------------------------------------------
if (-not $SkipService) {
    Step "Starting SAGIDE.Service..."
    if (-not (Test-Path $serviceExe)) {
        Err "SAGIDE.Service.exe not found at $serviceExe — run build-all.ps1 first"
    }

    # Kill any existing instance
    $existing = Get-Process -Name "SAGIDE.Service" -ErrorAction SilentlyContinue
    if ($existing) {
        Write-Host "    Stopping existing service instance..."
        $existing | Stop-Process -Force
        Start-Sleep -Milliseconds 800
    }

    # Start the service
    # Pass PromptsPath as an absolute override so the service finds prompts/
    # regardless of its working directory.
    $windowStyle = if ($Background) { 'Hidden' } else { 'Normal' }
    $proc = Start-Process -FilePath $serviceExe `
        -WorkingDirectory $serviceDir `
        -ArgumentList "--SAGIDE:PromptsPath=`"$promptsPath`"" `
        -WindowStyle $windowStyle `
        -PassThru

    Write-Host "    PID: $($proc.Id) | Window: $windowStyle"

    # ---------------------------------------------------------------------------
    # 4. Health check (wait up to 20 seconds)
    # ---------------------------------------------------------------------------
    Write-Host "    Waiting for service to become healthy..." -NoNewline
    $healthy = $false
    for ($i = 0; $i -lt 20; $i++) {
        Start-Sleep -Seconds 1
        Write-Host "." -NoNewline
        try {
            $resp = Invoke-WebRequest $HealthCheckUrl -UseBasicParsing -TimeoutSec 2 -ErrorAction Stop
            if ($resp.StatusCode -eq 200) { $healthy = $true; break }
        } catch { }
    }
    Write-Host ""

    if ($healthy) {
        Ok "Service healthy at $HealthCheckUrl"
    } else {
        Warn "Service did not respond within 20s"
        Warn "Check the service window for errors, or run: Invoke-WebRequest $HealthCheckUrl"
    }
}

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
Step "Deploy complete."
Write-Host @"

  Endpoints:
    Health    : GET  http://localhost:5100/api/health
    Dashboard : GET  http://localhost:5100/
    Tasks API : GET  http://localhost:5100/api/tasks
    Prompts   : GET  http://localhost:5100/api/prompts

  VS Code   : Reload VS Code window (Ctrl+Shift+P -> 'Reload Window')
  CLI test  : sag health   (if InstallCli was used or sag.exe is in PATH)
"@

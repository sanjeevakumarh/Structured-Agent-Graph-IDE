#Requires -Version 5.1
<#
.SYNOPSIS
    End-to-end test pipeline: build → start service → smoke tests → teardown.

.DESCRIPTION
    Orchestrates the full  build → deploy → smoke-test  loop:

      1.  (Optional) Build SAGIDE.Service in Release configuration.
      2.  If no -BaseUrl is given, start the service with an isolated temporary
          SQLite database so tests never touch production data.
      3.  Wait for the health endpoint to become responsive.
      4.  Dot-source Test-SmokeTests.ps1 and run all smoke tests.
      5.  Print a colour-coded pass/fail summary identical to
          Invoke-CodexValidation.ps1.
      6.  Stop the service and delete the temp database (unless -KeepRunning).
      7.  Exit 0 if all tests pass; exit 1 if any fail.

    Reuses the validation-script result pattern:
        [pscustomobject]@{ Name = "..."; Passed = $true/$false; Details = "..." }

.PARAMETER Build
    Rebuild SAGIDE.Service (Release) before starting.
    Uses build-all.ps1 with -SkipTests -SkipExtension -SkipLogseq.

.PARAMETER BaseUrl
    Base URL of an already-running service instance.
    When provided the script skips service start/stop entirely.
    Example: -BaseUrl http://localhost:5100

.PARAMETER TimeoutSeconds
    Seconds to wait for the health check to succeed after starting the service.
    Default: 30.

.PARAMETER KeepRunning
    Do not stop the service after tests complete.
    Useful when pointing at an existing instance (-BaseUrl) or for post-test
    inspection.

.PARAMETER Background
    Start the service as a hidden background window (no console visible).
    Default: normal window (visible).

.PARAMETER ServiceConfig
    .NET build configuration to use when locating the service executable.
    Default: Release.

.EXAMPLE
    # Full pipeline: build, start, test, teardown
    .\utils\Invoke-EndToEndTest.ps1 -Build

.EXAMPLE
    # Smoke-test a service that is already running (e.g. after deploy.ps1)
    .\utils\Invoke-EndToEndTest.ps1 -BaseUrl http://localhost:5100 -KeepRunning

.EXAMPLE
    # Run from build-all.ps1 after a normal build
    .\utils\Invoke-EndToEndTest.ps1
#>
param(
    [switch]$Build,
    [string]$BaseUrl         = "",
    [int]$TimeoutSeconds     = 30,
    [switch]$KeepRunning,
    [switch]$Background,
    [string]$ServiceConfig   = "Release"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Repo root is one level above utils/
$root        = Split-Path $PSScriptRoot -Parent
$serviceExe  = "$root\src\SAGIDE.Service\bin\$ServiceConfig\net9.0\SAGIDE.Service.exe"
$serviceDir  = "$root\src\SAGIDE.Service\bin\$ServiceConfig\net9.0"
$promptsPath = "$root\prompts"

function Step([string]$msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Ok([string]$msg)   { Write-Host "    OK  $msg" -ForegroundColor Green }
function Warn([string]$msg) { Write-Host "    WARN $msg" -ForegroundColor Yellow }
function Err([string]$msg)  { Write-Host "`n    ERROR: $msg" -ForegroundColor Red; exit 1 }

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$serviceProc  = $null   # Process object — set only when we start it
$tempDbPath   = $null   # Temp DB path — set only when we start the service

# ── Phase 1: Build (optional) ─────────────────────────────────────────────────
if ($Build) {
    Step "Building SAGIDE.Service ($ServiceConfig)..."
    $buildScript = "$PSScriptRoot\build-all.ps1"
    if (-not (Test-Path $buildScript)) { Err "build-all.ps1 not found at $buildScript" }

    $buildArgs = @('-SkipTests', '-SkipExtension', '-SkipLogseq')
    if ($ServiceConfig -eq 'Debug') { $buildArgs += '-Debug' }

    & $buildScript @buildArgs
    if ($LASTEXITCODE -ne 0) { Err "Build failed (exit $LASTEXITCODE)" }
    Ok "Build succeeded"
}

# ── Phase 2: Start service (unless caller supplies BaseUrl) ───────────────────
if ([string]::IsNullOrWhiteSpace($BaseUrl)) {

    if (-not (Test-Path $serviceExe)) {
        Err "SAGIDE.Service.exe not found at $serviceExe`n       Run build-all.ps1 first, or pass -Build."
    }

    # Unique temp database so tests never touch real data
    $tempDbPath = [System.IO.Path]::Combine(
        [System.IO.Path]::GetTempPath(),
        "sagide-e2e-$([datetime]::UtcNow.ToString('yyyyMMddHHmmss')).db"
    )

    Step "Starting SAGIDE.Service (isolated temp DB)..."
    Write-Host "    Exe : $serviceExe"
    Write-Host "    DB  : $tempDbPath"
    Write-Host "    Prompts: $promptsPath"

    # Kill any existing instance (frees the port)
    $existing = Get-Process -Name "SAGIDE.Service" -ErrorAction SilentlyContinue
    if ($existing) {
        Write-Host "    Stopping existing service instance (PID $($existing.Id))..."
        $existing | Stop-Process -Force
        Start-Sleep -Milliseconds 800
    }

    $windowStyle = if ($Background) { 'Hidden' } else { 'Normal' }
    $serviceProc = Start-Process `
        -FilePath $serviceExe `
        -WorkingDirectory $serviceDir `
        -ArgumentList `
            "--SAGIDE:Database:Path=`"$tempDbPath`"",
            "--SAGIDE:PromptsPath=`"$promptsPath`"" `
        -WindowStyle $windowStyle `
        -PassThru

    Write-Host "    PID: $($serviceProc.Id) | Window: $windowStyle"
    $BaseUrl = "http://localhost:5100"

    # ── Phase 3: Wait for health ──────────────────────────────────────────────
    $healthUrl = "$BaseUrl/api/health"
    Write-Host "    Waiting for health ($healthUrl)..." -NoNewline

    $healthy = $false
    for ($i = 0; $i -lt $TimeoutSeconds; $i++) {
        Start-Sleep -Seconds 1
        Write-Host "." -NoNewline
        try {
            $resp = Invoke-WebRequest $healthUrl -UseBasicParsing -TimeoutSec 2 -ErrorAction Stop
            if ($resp.StatusCode -eq 200) { $healthy = $true; break }
        } catch { }
    }
    Write-Host ""

    if ($healthy) {
        Ok "Service healthy at $healthUrl"
    } else {
        # Give up — tear down and exit
        if ($serviceProc -and -not $serviceProc.HasExited) {
            $serviceProc | Stop-Process -Force
        }
        Err "Service did not become healthy within $TimeoutSeconds seconds"
    }

} else {
    # Caller pointed us at a running instance — verify it is reachable first
    Step "Verifying service at $BaseUrl ..."
    $healthUrl = "$BaseUrl/api/health"
    try {
        $resp = Invoke-WebRequest $healthUrl -UseBasicParsing -TimeoutSec 5 -ErrorAction Stop
        if ($resp.StatusCode -ne 200) { Err "Health endpoint returned $($resp.StatusCode)" }
    } catch {
        Err "Service not reachable at $healthUrl — $($_.Exception.Message)"
    }
    Ok "Service reachable"
}

# ── Phase 4: Run smoke tests ──────────────────────────────────────────────────
Step "Running smoke tests against $BaseUrl ..."

. "$PSScriptRoot\validation\Test-SmokeTests.ps1"
$results = Test-SmokeTests -BaseUrl $BaseUrl

# ── Phase 5: Print results ────────────────────────────────────────────────────
Write-Host ""
Write-Host "Smoke test results" -ForegroundColor Cyan
Write-Host ("-" * 62)
foreach ($r in $results) {
    $color  = if ($r.Passed) { 'Green' } else { 'Red' }
    $status = if ($r.Passed) { 'OK  ' } else { 'FAIL' }
    Write-Host ("[{0}] {1,-38}  {2}" -f $status, $r.Name, $r.Details) -ForegroundColor $color
}
Write-Host ("-" * 62)

$passed = @($results | Where-Object { $_.Passed })
$failed = @($results | Where-Object { -not $_.Passed })
$stopwatch.Stop()

$color = if ($failed.Count -eq 0) { 'Green' } else { 'Red' }
Write-Host ("Passed: {0}  Failed: {1}  ({2} total, {3:mm\:ss} elapsed)" -f `
    $passed.Count, $failed.Count, $results.Count,
    $stopwatch.Elapsed) -ForegroundColor $color

# ── Phase 6: Teardown ─────────────────────────────────────────────────────────
if (-not $KeepRunning -and $serviceProc) {
    Step "Stopping service (PID $($serviceProc.Id))..."
    try {
        $serviceProc | Stop-Process -Force -ErrorAction SilentlyContinue
        $serviceProc.WaitForExit(5000) | Out-Null
    } catch { }
    Ok "Service stopped"

    if ($tempDbPath -and (Test-Path $tempDbPath)) {
        # Give SQLite connection pool a moment to release the file handle
        Start-Sleep -Milliseconds 500
        try {
            Remove-Item $tempDbPath -Force -ErrorAction SilentlyContinue
            # SQLite also creates a -wal and -shm file
            Remove-Item "$tempDbPath-wal"  -Force -ErrorAction SilentlyContinue
            Remove-Item "$tempDbPath-shm"  -Force -ErrorAction SilentlyContinue
            Ok "Temp database removed"
        } catch {
            Warn "Could not delete temp DB: $tempDbPath — remove manually"
        }
    }
} elseif ($KeepRunning -and $serviceProc) {
    Warn "Service left running (PID $($serviceProc.Id)) — stop manually when done"
    if ($tempDbPath) {
        Warn "Temp DB at: $tempDbPath — remove manually"
    }
}

if ($failed.Count -gt 0) { exit 1 }

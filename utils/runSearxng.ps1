#Requires -Version 5.1
<#
.SYNOPSIS
    Starts a SearXNG container with JSON + HTML output formats enabled.
.DESCRIPTION
    - Starts Docker Desktop if it is not already running
    - On first run: boots container with default settings, copies settings.yml
      out, patches only the formats block, then restarts with the patched file
    - On subsequent runs: reuses the already-patched settings.yml
    - Exposes SearXNG on http://localhost:8888
#>

$ContainerName = "searxng"
$HostPort      = 8888
$TmpDir        = Join-Path $env:TEMP "searxng-config"
$SettingsFile  = Join-Path $TmpDir   "settings.yml"

# ── Ensure Docker Desktop is running ──────────────────────────────────────────
function Wait-Docker {
    $dockerExe = "${env:ProgramFiles}\Docker\Docker\Docker Desktop.exe"

    if (Get-Process "Docker Desktop" -ErrorAction SilentlyContinue) { return }

    if (-not (Test-Path $dockerExe)) {
        Write-Error "Docker Desktop not found at '$dockerExe'. Please install it first."
        exit 1
    }

    Write-Host "Docker Desktop is not running. Starting it..."
    Start-Process $dockerExe

    Write-Host "Waiting for Docker Desktop to become ready (up to 60 s)..."
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 3
        if (Get-Process "Docker Desktop" -ErrorAction SilentlyContinue) {
            Start-Sleep -Seconds 5
            Write-Host "Docker Desktop is ready."
            return
        }
        Write-Host -NoNewline "."
    }

    Write-Error "Docker Desktop did not start within 60 seconds."
    exit 1
}

# ── Patch only the formats block in settings.yml ──────────────────────────────
function Set-Formats($path) {
    $lines     = Get-Content $path
    $result    = [System.Collections.Generic.List[string]]::new()
    $inFormats = $false

    foreach ($line in $lines) {
        if ($line -match '^\s+formats:') {
            $result.Add($line)
            $result.Add('    - html')
            $result.Add('    - json')
            $inFormats = $true
        } elseif ($inFormats -and $line -match '^\s+-\s+') {
            # skip original format entries — replaced above
        } else {
            $inFormats = $false
            $result.Add($line)
        }
    }

    $result | Set-Content -Encoding UTF8 $path
    Write-Host "Formats patched in: $path"
}

# ── Main ──────────────────────────────────────────────────────────────────────
Wait-Docker

# Remove any existing container so we start clean
$existing = docker ps -a --filter "name=^${ContainerName}$" --format "{{.Names}}" 2>$null
if ($existing -eq $ContainerName) {
    Write-Host "Removing existing container '$ContainerName'..."
    docker rm -f $ContainerName | Out-Null
}

New-Item -ItemType Directory -Force -Path $TmpDir | Out-Null

# First run: boot with defaults, copy settings.yml out, stop, patch
if (-not (Test-Path $SettingsFile)) {
    Write-Host "No settings.yml found — fetching defaults from container..."

    $cmd = "docker run -d --name $ContainerName searxng/searxng:latest"
    Write-Host "Running: $cmd"
    docker run -d --name $ContainerName searxng/searxng:latest

    Write-Host "Waiting 5 s for container to initialise..."
    Start-Sleep -Seconds 5

    Write-Host "Copying settings.yml from container..."
    docker cp "${ContainerName}:/etc/searxng/settings.yml" $SettingsFile

    Write-Host "Stopping init container..."
    docker rm -f $ContainerName | Out-Null

    Set-Formats $SettingsFile
}

# Start container with patched settings
$cmd = "docker run -d -p ${HostPort}:8080 --name $ContainerName -v `"${TmpDir}:/etc/searxng:ro`" searxng/searxng:latest"
Write-Host "Running: $cmd"

docker run -d `
    -p "${HostPort}:8080" `
    --name $ContainerName `
    -v "${TmpDir}:/etc/searxng:ro" `
    searxng/searxng:latest

if ($LASTEXITCODE -eq 0) {
    Write-Host "SearXNG started."
    Write-Host "  Browse : http://localhost:${HostPort}"
    Write-Host "  JSON   : http://localhost:${HostPort}/search?q=test&format=json"
} else {
    Write-Error "docker run failed (exit $LASTEXITCODE)"
    exit 1
}

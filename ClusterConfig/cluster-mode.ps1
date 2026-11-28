# PowerShell version of cluster-mode.sh
# Usage: ./cluster-mode.ps1 {general|general-abliterated|video|video-abliterated|status} [--dry-run] [--node NAME]

param(
    [Parameter(Mandatory=$false, Position=0)]
    [ValidateSet('general','general-abliterated','video','video-abliterated','status')]
    $Mode = 'status',

    [switch]$DryRun,
    [string]$Node
)

# --- OS guard ---------------------------------------------------------------
# This script is a Windows port of cluster-mode.sh. The --remote flag is only
# needed on non-macOS systems, so abort early if someone tries to run it on
# macOS (where the original shell script should be used instead).
if ($IsMacOS) {
    Write-Error "This script is a Windows port. Use cluster-mode.sh on macOS."
    exit 1
}

# --- Build inventory command -------------------------------------------------
# Splat (@) expands the array into separate arguments for the external 'bash'
# command, matching the bash behavior of "${inventory[@]}" on line 22.
$inventory = @('inventory', '--roles')
if (-not $DryRun) {
    $inventory += '--remote'
}
if ($DryRun) {
    Write-Host "Offline preview using this checkout's Windows config"
}

# Locate the shared launcher
$setupPath = Join-Path $PSScriptRoot 'common\setup.sh'
if (-not (Test-Path $setupPath)) {
    Write-Error "setup.sh not found at $setupPath"
    exit 1
}

# Get list of members (node<TAB>role) from common/setup.sh
$membersRaw = & bash $setupPath --modes @inventory
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to obtain inventory from setup.sh"
    exit 1
}

# Split lines, ignore empty lines
$members = $membersRaw -split "`n" | Where-Object { $_ -match '\S' }

# --- Validate requested node ------------------------------------------------
if ($Node) {
    $found = $false
    foreach ($line in $members) {
        $parts = $line -split "`t"
        if ($parts[0].Trim() -eq $Node) { $found = $true; break }
    }
    if (-not $found) {
        Write-Error "${Node} is outside the enabled inventory"
        exit 2
    }
}

# --- Process each member ----------------------------------------------------
foreach ($line in $members) {
    $parts = $line -split "`t"
    if ($parts.Count -lt 2) { continue }

    $node = $parts[0].Trim()
    $role = $parts[1].Trim()

    if ([string]::IsNullOrWhiteSpace($node)) { continue }
    if ($Node -and $node -ne $Node) { continue }

    switch ("$role`: $Mode") {
        "gateway:status" { }
        "edge:status"    { }
        "gateway:*"      {
            Write-Host "$node: LiteLLM router"
            continue
        }
    }

    # Build arguments for the per-node call
    $args = @($Mode)
    if ($Mode -ne 'status') {
        # For non-status modes we need to use the generic worker signature.
        # The worker resolves runtime and catalog from its own YAML.
        $args = @('general', '--profile', $Mode, '--quiesced')
    }

    # Assemble the final command line for setup.sh
    $cmd = @('bash', $setupPath, '--modes') + $args + @('--node', $node)
    if ($DryRun) { $cmd += '--dry-run' }
    $cmd += '--remote'

    $cmdString = $cmd -join ' '
    if ($DryRun) {
        Write-Host "[Dry-run] $cmdString"
    } else {
        & $cmd
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Command failed for node $node"
            # Continue to next node rather than aborting the whole script
        }
    }
}

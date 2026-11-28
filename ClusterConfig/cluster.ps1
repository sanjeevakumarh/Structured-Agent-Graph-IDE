# Manage the same ClusterConfig checkout on all nodes using passwordless SSH.
# Usage: .\cluster.ps1 health | validate | setup | benchmark | calibrate
# Local settings: config/cluster.local.json (Git-ignored).
# Copy config/cluster.example.json to that path and set SSH targets and checkout path.
# SSH targets may be configured SSH aliases or user@host; no credentials in Git.
param(
    [ValidateSet('health','validate','setup','benchmark','calibrate')]
    [string]$Action = 'health',
    [switch]$Yes,
    [string]$OnlyNode
)
# Read local settings relative to this script, not the current working directory.
$ConfigPath = Join-Path $PSScriptRoot 'config/cluster.local.json'
if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
    throw "Missing $ConfigPath. Copy config/cluster.example.json to config/cluster.local.json and edit it."
}
try {
    $Local = Get-Content -LiteralPath $ConfigPath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
} catch {
    throw "Invalid local cluster configuration: $($_.Exception.Message)"
}

# Quote the remote checkout path while preserving expansion of the remote HOME.
function Quote-RemotePath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path -match '[\x00-\x1f\x7f]') {
        throw 'repo must be a nonempty remote directory path without control characters.'
    }
    if ($Path.StartsWith('~/')) {
        return '"$HOME"/' + "'" + $Path.Substring(2).Replace("'", "'\''") + "'"
    }
    if ($Path.StartsWith('/')) {
        return "'" + $Path.Replace("'", "'\''") + "'"
    }
    throw 'repo must be an absolute remote path or start with ~/.'
}
$Repo = Quote-RemotePath $Local.repo

# Logical profile names are portable; machine/user mappings are local only.
# Keep the gateway last: the preflight distributes its key to the three workers.
$SSH = @('-o','BatchMode=yes','-o','StrictHostKeyChecking=yes','-o','ConnectTimeout=8','-o','ServerAliveInterval=30','-o','ServerAliveCountMax=3')
$InventoryScript = Join-Path $PSScriptRoot 'common/inventory.py'
$InventoryPython = 'python'
$InventoryArgs = @($InventoryScript)
$Gateway = if (Get-Command $InventoryPython -ErrorAction SilentlyContinue) {
    & $InventoryPython @InventoryArgs 2>$null
}
if (-not $Gateway -and (Get-Command uv -ErrorAction SilentlyContinue)) {
    $InventoryPython = 'uv'
    $InventoryArgs = @('run','--offline','--no-project','--with','pyyaml','--python','3.12','python',$InventoryScript)
    $Gateway = & $InventoryPython @InventoryArgs
}
if ($LASTEXITCODE -ne 0 -or $Gateway -notmatch '^[a-zA-Z0-9][a-zA-Z0-9_-]*$') { throw 'Cannot discover gateway from local profiles (Python + PyYAML required).' }
$MacTarget = $Local.targets.$Gateway
if ($MacTarget -notmatch '^(?:[A-Za-z0-9_][A-Za-z0-9_.-]*@)?[A-Za-z0-9][A-Za-z0-9_.-]*$') { throw 'Invalid gateway SSH target.' }
# Membership lives on the gateway. Reading the YAML works across launcher versions.
$GatewayYaml = & ssh @SSH -T $MacTarget "cd $Repo && cat 'config/$Gateway.yaml'"
if ($LASTEXITCODE -ne 0) { throw 'Cannot read the gateway YAML over SSH.' }
$Rows = @($GatewayYaml | & $InventoryPython @InventoryArgs --gateway-yaml)
if ($LASTEXITCODE -ne 0) { throw 'Cannot read inventory from the gateway checkout.' }
$Roles = [ordered]@{}
foreach ($Row in $Rows) {
    if ($Row -notmatch '^([A-Za-z0-9][A-Za-z0-9_-]*)\t(worker|edge|gateway)$') { throw 'Invalid inventory response.' }
    $Roles.Add($Matches[1], $Matches[2])
}
$Members = @($Roles.Keys)
$NodeNames = @($Members | Where-Object { $Roles[$_] -ne 'gateway' }) + @($Members | Where-Object { $Roles[$_] -eq 'gateway' })
if ($OnlyNode) {
    if ($OnlyNode -notin $Members) { throw "$OnlyNode is disabled in the Mac inventory." }
    if ($Action -eq 'setup') { throw 'Use a node setup script for individual provisioning.' }
    $NodeNames = @($OnlyNode)
}
if (-not $NodeNames.Count) { Write-Host 'No enabled nodes.'; return }
$Nodes = foreach ($Name in $NodeNames) {
    $Target = $Local.targets.$Name
    if ($Target -isnot [string] -or $Target -notmatch '^(?:[A-Za-z0-9_][A-Za-z0-9_.-]*@)?[A-Za-z0-9][A-Za-z0-9_.-]*$') {
        throw "Missing or invalid SSH target for '$Name' in $ConfigPath. Use an SSH alias or user@host."
    }
    @{Name=$Name; Target=$Target}
}
$RemoteEnv = 'export LC_ALL=C SYSTEMD_COLORS=0 SYSTEMD_PAGER=cat; '
$Option = if ($Action -eq 'setup') { '--install' } else { "--$Action" }
if ($Yes -and $Action -in @('setup','calibrate','benchmark')) { $Option += ' --yes' }
if ($Action -eq 'setup') {
    # Verify every checkout before destructive changes on any node.
    foreach ($Node in $Nodes) {
        $Version = & ssh @SSH -T $Node.Target "$RemoteEnv cd $Repo && bash common/setup.sh $($Node.Name) --version"
        if ($LASTEXITCODE -ne 0 -or (($Version -join ' ') -notmatch 'local-ai-cluster\s+v?0\.11\b')) {
            throw "Setup aborted: $($Node.Name) does not have v0.11. Update its checkout first."
        }
    }
    # Preserve existing gateway keys and distribute the worker key privately.
    & ssh @SSH -T $MacTarget "$RemoteEnv cd $Repo && bash common/setup.sh $Gateway --init-keys"
    if ($LASTEXITCODE -ne 0) { throw 'Gateway key initialization failed.' }
    $WorkerKey = & ssh @SSH -T $MacTarget 'cat ~/inference/config/worker.key'
    if ($LASTEXITCODE -ne 0 -or ($WorkerKey -join '') -notmatch '^sk-[A-Za-z0-9_-]{32,}$') { throw 'Could not read valid worker key.' }
    try {
        foreach ($Node in @($Nodes | Where-Object { $Roles[$_.Name] -eq 'worker' })) {
            $WorkerKey | & ssh @SSH -T $Node.Target 'umask 077; mkdir -p ~/inference/config && chmod 700 ~/inference/config && cat > ~/inference/config/worker.key && chmod 600 ~/inference/config/worker.key'
            if ($LASTEXITCODE -ne 0) { throw "Key distribution failed: $($Node.Name)" }
        }
    } finally { Remove-Variable WorkerKey -ErrorAction SilentlyContinue }
}
$Results = foreach ($Node in $Nodes) {
    if ($Roles[$Node.Name] -eq 'gateway' -and $Action -in @('benchmark','calibrate')) {
        [pscustomobject]@{Node=$Node.Name; Status='N/A'; ExitCode=0}; continue
    }
    Write-Host "`n=== $($Node.Name): $Action ===" -ForegroundColor Cyan
    $Command = "$RemoteEnv cd $Repo && bash common/setup.sh $($Node.Name) $Option"
    $TTY = if ($Action -in @('setup','benchmark','calibrate')) { '-tt' } else { '-T' }
    & ssh @SSH $TTY $Node.Target $Command | Out-Host
    $Code = $LASTEXITCODE
    [pscustomobject]@{Node=$Node.Name; Status=if ($Code -eq 0) {'PASS'} elseif ($Action -eq 'health' -and $Roles[$Node.Name] -eq 'gateway' -and $Code -eq 3) {'DEGRADED'} else {'FAIL'}; ExitCode=$Code}
}
$Results | Format-Table -AutoSize
if ($Results.Status -contains 'FAIL') { exit 1 }
if ($Results.Status -contains 'DEGRADED') { exit 3 }

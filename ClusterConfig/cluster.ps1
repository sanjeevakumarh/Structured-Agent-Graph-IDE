# Manage the same ClusterConfig checkout on all nodes using passwordless SSH.
# Usage: .\cluster.ps1 health | validate | setup | benchmark | calibrate
# Local settings: config/cluster.local.json (Git-ignored).
# Copy config/cluster.example.json to that path and set SSH targets and checkout path.
# SSH targets may be configured SSH aliases or user@host; no credentials in Git.
param(
    [ValidateSet('health','validate','setup','benchmark','calibrate')]
    [string]$Action = 'health',
    [switch]$Yes
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
$NodeNames = @('gmini','p520','z820','nano','ventunoq','macmini')
$Nodes = foreach ($Name in $NodeNames) {
    $Target = $Local.targets.$Name
    if ($Target -isnot [string] -or $Target -notmatch '^(?:[A-Za-z0-9_][A-Za-z0-9_.-]*@)?[A-Za-z0-9][A-Za-z0-9_.-]*$') {
        throw "Missing or invalid SSH target for '$Name' in $ConfigPath. Use an SSH alias or user@host."
    }
    @{Name=$Name; Target=$Target}
}
$SSH = @('-o','BatchMode=yes','-o','StrictHostKeyChecking=yes','-o','ConnectTimeout=8','-o','ServerAliveInterval=30','-o','ServerAliveCountMax=3')
$RemoteEnv = 'export LC_ALL=C SYSTEMD_COLORS=0 SYSTEMD_PAGER=cat; '
$Option = if ($Action -eq 'setup') { '--install' } else { "--$Action" }
if ($Yes -and $Action -in @('setup','calibrate','benchmark')) { $Option += ' --yes' }
if ($Action -eq 'setup') {
    # Verify every checkout before destructive changes on any node.
    foreach ($Node in $Nodes) {
        $Version = & ssh @SSH -T $Node.Target "$RemoteEnv cd $Repo && bash setup-$($Node.Name).sh --version"
        if ($LASTEXITCODE -ne 0 -or (($Version -join ' ') -notmatch 'local-ai-cluster\s+v?0\.11\b')) {
            throw "Setup aborted: $($Node.Name) does not have v0.11. Update its checkout first."
        }
    }
    # Preserve existing gateway keys and distribute the worker key privately.
    & ssh @SSH -T $Nodes[-1].Target "$RemoteEnv cd $Repo && bash setup-macmini.sh --init-keys"
    if ($LASTEXITCODE -ne 0) { throw 'Gateway key initialization failed.' }
    $WorkerKey = & ssh @SSH -T $Nodes[-1].Target 'cat ~/inference/config/worker.key'
    if ($LASTEXITCODE -ne 0 -or ($WorkerKey -join '') -notmatch '^sk-[A-Za-z0-9_-]{32,}$') { throw 'Could not read valid worker key.' }
    try {
        foreach ($Node in $Nodes[0..2]) {
            $WorkerKey | & ssh @SSH -T $Node.Target 'umask 077; mkdir -p ~/inference/config && chmod 700 ~/inference/config && cat > ~/inference/config/worker.key && chmod 600 ~/inference/config/worker.key'
            if ($LASTEXITCODE -ne 0) { throw "Key distribution failed: $($Node.Name)" }
        }
    } finally { Remove-Variable WorkerKey -ErrorAction SilentlyContinue }
}
$Results = foreach ($Node in $Nodes) {
    if ($Node.Name -eq 'macmini' -and $Action -in @('benchmark','calibrate')) {
        [pscustomobject]@{Node=$Node.Name; Status='N/A'; ExitCode=0}; continue
    }
    Write-Host "`n=== $($Node.Name): $Action ===" -ForegroundColor Cyan
    $Command = "$RemoteEnv cd $Repo && bash setup-$($Node.Name).sh $Option"
    $TTY = if ($Action -in @('setup','benchmark','calibrate')) { '-tt' } else { '-T' }
    & ssh @SSH $TTY $Node.Target $Command | Out-Host
    $Code = $LASTEXITCODE
    [pscustomobject]@{Node=$Node.Name; Status=if ($Code -eq 0) {'PASS'} else {'FAIL'}; ExitCode=$Code}
}
$Results | Format-Table -AutoSize
if ($Results.Status -contains 'FAIL') { exit 1 }

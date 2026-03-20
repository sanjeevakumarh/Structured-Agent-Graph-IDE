[CmdletBinding()]
param()

function Test-NodeNpm {
    $result = [pscustomobject]@{ Name = "Node/npm"; Passed = $false; Details = "" }
    $node = Get-Command node -ErrorAction SilentlyContinue
    $npm = Get-Command npm -ErrorAction SilentlyContinue
    if (-not $node) {
        $result.Details = "node not on PATH"
        return $result
    }
    if (-not $npm) {
        $result.Details = "npm not on PATH"
        return $result
    }

    try {
        $nodeVerText = node --version
    } catch {
        $result.Details = "node --version failed: $_"
        return $result
    }
    try {
        $npmVerText = npm --version
    } catch {
        $result.Details = "npm --version failed: $_"
        return $result
    }

    $nodeMin = [version]"20.0.0"
    $npmMin = [version]"10.0.0"
    $nodeVer = $null
    $npmVer = $null
    [void][version]::TryParse($nodeVerText.TrimStart('v'), [ref]$nodeVer)
    [void][version]::TryParse($npmVerText.Trim(), [ref]$npmVer)

    if (-not $nodeVer -or $nodeVer -lt $nodeMin) {
        $result.Details = "node $nodeVerText < 20"
        return $result
    }
    if (-not $npmVer -or $npmVer -lt $npmMin) {
        $result.Details = "npm $npmVerText < 10"
        return $result
    }

    $result.Passed = $true
    $result.Details = "node $nodeVerText, npm $npmVerText ok"
    return $result
}

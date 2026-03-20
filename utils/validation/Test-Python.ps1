[CmdletBinding()]
param()

function Test-Python {
    param([string]$Command = "python")

    $result = [pscustomobject]@{ Name = "Python"; Passed = $false; Details = "" }
    $cmd = Get-Command $Command -ErrorAction SilentlyContinue
    if (-not $cmd) {
        $result.Details = "$Command not on PATH"
        return $result
    }
    try {
        $ver = & $Command --version 2>&1
    } catch {
        $result.Details = "$Command --version failed: $_"
        return $result
    }
    $result.Passed = $true
    $result.Details = $ver.Trim()
    return $result
}
